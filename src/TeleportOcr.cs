// TeleportOcr.cs
//
// Reads a short line of text from a small bitmap with the Windows built-in OCR engine
// (Windows.Media.Ocr.OcrEngine). C# 5, no compile-time WinRT reference.
//
// WHY LATE BINDING: csc.exe (.NET Framework 4) cannot reference the WinRT metadata on Windows 10/11.
// C:\Windows\System32\WinMetadata\Windows.winmd does not exist any more (it is split into
// Windows.Foundation.winmd, Windows.Media.winmd, ...), and referencing those fails with CS0012
// ("defined in an assembly that is not referenced: Windows, Version=255.255.255.255") because
// System.Runtime.WindowsRuntime.dll expects the unified "Windows" assembly. So the WinRT types are
// resolved at run time with Type.GetType("<type>, Windows, ContentType=WindowsRuntime") and used through
// reflection (statics, generic async interfaces) and dynamic (plain runtime classes). Async operations
// are polled through IAsyncInfo.Status (no await, no AsTask). Verified to work from this csc build.
//
// Public API
//   TeleportOcr.IsAvailable        true when an OCR engine could be created (language packs installed).
//   TeleportOcr.Language           engine language tag (e.g. "en-GB") or null.
//   TeleportOcr.LastError          last failure text or null.
//   TeleportOcr.ReadLine(Bitmap)   recognised text of the whole bitmap (lines joined by a space) or "".
//   TeleportOcr.PrepareNameCrop(Bitmap crop)   upscales and binarises a name crop for OCR (caller disposes).
//
// OCR can run on any thread. One OCR call at a time (a lock), a few tens of milliseconds each.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace RbxMulti
{
    public static class TeleportOcr
    {
        private const string Asm = ", Windows, ContentType=WindowsRuntime, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null";

        private static readonly object Gate = new object();
        private static bool _tried;
        private static dynamic _engine;
        private static string _language;
        private static string _lastError;

        public static string LastError { get { return _lastError; } }

        public static string Language
        {
            get { EnsureEngine(); return _language; }
        }

        public static bool IsAvailable
        {
            get { EnsureEngine(); return _engine != null; }
        }

        private static Type T(string name)
        {
            Type t = Type.GetType(name + Asm, false);
            if (t == null) throw new InvalidOperationException("WinRT type not found: " + name);
            return t;
        }

        private static void EnsureEngine()
        {
            lock (Gate)
            {
                if (_tried) return;
                _tried = true;
                try
                {
                    object eng = T("Windows.Media.Ocr.OcrEngine").GetMethod("TryCreateFromUserProfileLanguages").Invoke(null, null);
                    if (eng == null)
                    {
                        // No user-profile language has an OCR pack: try English explicitly.
                        Type lang = T("Windows.Globalization.Language");
                        object en = Activator.CreateInstance(lang, new object[] { "en-US" });
                        eng = T("Windows.Media.Ocr.OcrEngine").GetMethod("TryCreateFromLanguage").Invoke(null, new object[] { en });
                    }
                    if (eng == null)
                    {
                        _lastError = "No OCR language pack installed";
                        return;
                    }
                    _engine = eng;
                    _language = (string)((dynamic)eng).RecognizerLanguage.LanguageTag;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    _engine = null;
                }
            }
        }

        // Polls an IAsyncOperation / IAsyncAction until it completes. resultType null = IAsyncAction.
        private static object Wait(object op, string resultType)
        {
            PropertyInfo st = T("Windows.Foundation.IAsyncInfo").GetProperty("Status");
            int n = 0;
            while ((int)st.GetValue(op, null) == 0)
            {
                Thread.Sleep(4);
                if (++n > 2500) throw new TimeoutException("OCR async operation timed out");
            }
            if ((int)st.GetValue(op, null) != 1) throw new InvalidOperationException("OCR async status " + st.GetValue(op, null));
            if (resultType == null) return null;
            Type g = T("Windows.Foundation.IAsyncOperation`1").MakeGenericType(T(resultType));
            return g.GetMethod("GetResults").Invoke(op, null);
        }

        /// <summary>Recognised text of the whole bitmap, lines joined by a single space. Empty when nothing was read.</summary>
        public static string ReadLine(Bitmap bmp)
        {
            if (bmp == null) return "";
            EnsureEngine();
            if (_engine == null) return "";
            byte[] png;
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            lock (Gate)
            {
                try
                {
                    dynamic stream = Activator.CreateInstance(T("Windows.Storage.Streams.InMemoryRandomAccessStream"));
                    dynamic writer = Activator.CreateInstance(T("Windows.Storage.Streams.DataWriter"), new object[] { stream });
                    writer.WriteBytes(png);
                    Wait(writer.StoreAsync(), null);
                    writer.DetachStream();
                    stream.Seek(0UL);
                    Type iras = T("Windows.Storage.Streams.IRandomAccessStream");
                    dynamic decoder = Wait(T("Windows.Graphics.Imaging.BitmapDecoder").GetMethod("CreateAsync", new Type[] { iras }).Invoke(null, new object[] { stream }),
                        "Windows.Graphics.Imaging.BitmapDecoder");
                    object bgra = Enum.ToObject(T("Windows.Graphics.Imaging.BitmapPixelFormat"), 87);       // Bgra8
                    object premul = Enum.ToObject(T("Windows.Graphics.Imaging.BitmapAlphaMode"), 0);        // Premultiplied
                    dynamic opBmp = decoder.GetSoftwareBitmapAsync((dynamic)bgra, (dynamic)premul);
                    dynamic soft = Wait((object)opBmp, "Windows.Graphics.Imaging.SoftwareBitmap");
                    dynamic result = Wait(_engine.RecognizeAsync(soft), "Windows.Media.Ocr.OcrResult");
                    StringBuilder sb = new StringBuilder();
                    foreach (dynamic line in result.Lines)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append((string)line.Text);
                    }
                    try { stream.Dispose(); } catch (Exception) { }
                    return sb.ToString().Trim();
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    return "";
                }
            }
        }

        /// <summary>
        /// Prepares a crop of coloured text on a flat background for OCR: scales it up (bicubic), converts to
        /// grey, stretches the contrast so the background is white and the text black, and adds a margin.
        /// The caller owns the returned bitmap.
        /// </summary>
        public static Bitmap PrepareNameCrop(Bitmap crop, int scale)
        {
            return PrepareNameCrop(crop, scale, 0);
        }

        /// <summary>mode 0: soft (bright pixels pushed to white), 1: hard black/white threshold, 2: contrast stretch only.</summary>
        public static Bitmap PrepareNameCrop(Bitmap crop, int scale, int mode)
        {
            if (scale < 1) scale = 1;
            int w = crop.Width * scale;
            int h = crop.Height * scale;
            Bitmap big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(crop, new Rectangle(0, 0, w, h), new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            }

            BitmapData d = big.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int n = w * h;
                byte[] buf = new byte[n * 4];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                int[] hist = new int[256];
                byte[] lum = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int l = (buf[i * 4 + 2] * 77 + buf[i * 4 + 1] * 151 + buf[i * 4] * 28) >> 8;
                    lum[i] = (byte)l;
                    hist[l]++;
                }
                // Background = most frequent bright level region (the mode), text = dark tail (5th percentile).
                int bg = 255; int best = -1;
                for (int v = 0; v < 256; v++)
                {
                    int s = 0;
                    for (int k = Math.Max(0, v - 3); k <= Math.Min(255, v + 3); k++) s += hist[k];
                    if (s > best) { best = s; bg = v; }
                }
                int acc = 0; int lo = 0;
                int target = (int)(n * 0.02);
                for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= target) { lo = v; break; } }
                if (lo > bg - 30) lo = Math.Max(0, bg - 30);
                int span = Math.Max(1, bg - lo);
                for (int i = 0; i < n; i++)
                {
                    int v = (lum[i] - lo) * 255 / span;
                    if (v < 0) v = 0; if (v > 255) v = 255;
                    // Push towards a clean black-on-white image but keep a little anti-aliasing.
                    if (mode == 1) v = v > 150 ? 255 : 0;
                    else if (mode == 0 && v > 200) v = 255;
                    byte b = (byte)v;
                    buf[i * 4] = b; buf[i * 4 + 1] = b; buf[i * 4 + 2] = b; buf[i * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, d.Scan0, buf.Length);
            }
            finally
            {
                big.UnlockBits(d);
            }

            int pad = 12 * scale / 2;
            Bitmap padded = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format32bppArgb);
            using (Graphics g2 = Graphics.FromImage(padded))
            {
                g2.Clear(Color.White);
                g2.DrawImageUnscaled(big, pad, pad);
            }
            big.Dispose();
            return padded;
        }
    }
}
