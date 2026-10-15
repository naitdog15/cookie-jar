// Ported from src/TeleportOcr.cs (Windows.Media.Ocr.OcrEngine).
//
// WHY LATE BINDING: the WinRT types are resolved by name at run time with Type.GetType and used through
// reflection, so no Windows SDK or NuGet reference is needed (and no runtime binder, which netstandard2.0
// does not carry here). Async operations are polled through IAsyncInfo.Status. Engine language selection,
// the English fallback and the poll timeout are the source's values.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CookieJar.Platform.Windows
{
    /// <summary>Windows built-in OCR. Returns an empty list when no engine or language pack is available; never throws for that.</summary>
    public sealed class WinOcrService : IOcrService
    {
        private const string Asm = ", Windows, ContentType=WindowsRuntime, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null";

        private static readonly object Gate = new object();
        private static bool _tried;
        private static object _engine;
        private static string _language;
        private static string _lastError;

        /// <summary>Last failure text, or null.</summary>
        public static string LastError { get { return _lastError; } }

        /// <summary>Engine language tag (e.g. "en-US"), or null when no engine is available.</summary>
        public static string Language
        {
            get { EnsureEngine(); return _language; }
        }

        /// <summary>
        /// Lines of the image, in image pixel coordinates. Lines with no text are skipped.
        /// Confidence is -1 because WinRT OCR gives no per-line score (OcrLine.Confidence documents -1 as "engine gives none").
        /// Throws ArgumentNullException for a null image; any OCR failure yields an empty list.
        /// </summary>
        public IReadOnlyList<OcrLine> Recognise(RawImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            List<OcrLine> lines = new List<OcrLine>();
            if (image.Width <= 0 || image.Height <= 0) return lines;
            EnsureEngine();
            if (_engine == null) return lines;

            lock (Gate)
            {
                object stream = null;
                try
                {
                    byte[] bmp = EncodeBmp(image);
                    stream = Activator.CreateInstance(T("Windows.Storage.Streams.InMemoryRandomAccessStream"));
                    object writer = Activator.CreateInstance(T("Windows.Storage.Streams.DataWriter"), new object[] { stream });
                    Call(writer, "WriteBytes", bmp);
                    Wait(Call(writer, "StoreAsync"), null);
                    Call(writer, "DetachStream");
                    Call(stream, "Seek", 0UL);
                    Type iras = T("Windows.Storage.Streams.IRandomAccessStream");
                    object decoder = Wait(T("Windows.Graphics.Imaging.BitmapDecoder").GetMethod("CreateAsync", new Type[] { iras }).Invoke(null, new object[] { stream }),
                        "Windows.Graphics.Imaging.BitmapDecoder");
                    object bgra = Enum.ToObject(T("Windows.Graphics.Imaging.BitmapPixelFormat"), 87);       // Bgra8
                    object premul = Enum.ToObject(T("Windows.Graphics.Imaging.BitmapAlphaMode"), 0);        // Premultiplied
                    object opBmp = Call(decoder, "GetSoftwareBitmapAsync", bgra, premul);
                    object soft = Wait(opBmp, "Windows.Graphics.Imaging.SoftwareBitmap");
                    object result = Wait(Call(_engine, "RecognizeAsync", soft), "Windows.Media.Ocr.OcrResult");
                    foreach (object line in (IEnumerable)Prop(result, "Lines"))
                    {
                        string text = ((string)Prop(line, "Text")).Trim();
                        if (text.Length == 0) continue;
                        lines.Add(new OcrLine(text, LineBounds(line), -1));
                    }
                    return lines;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    return new List<OcrLine>();
                }
                finally
                {
                    IDisposable d = stream as IDisposable;
                    if (d != null) { try { d.Dispose(); } catch (Exception) { } }
                }
            }
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
                    _language = (string)Prop(Prop(eng, "RecognizerLanguage"), "LanguageTag");
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

        private static object Prop(object target, string name)
        {
            return target.GetType().GetProperty(name).GetValue(target, null);
        }

        // Calls the first public instance method with that name and argument count (WinRT projections overload by type).
        private static object Call(object target, string name, params object[] args)
        {
            foreach (MethodInfo m in target.GetType().GetMethods())
            {
                if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(target, args);
            }
            throw new MissingMethodException(target.GetType().FullName, name);
        }

        // Union of the word rectangles, rounded outwards to whole pixels.
        private static ScreenRect LineBounds(object line)
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (object word in (IEnumerable)Prop(line, "Words"))
            {
                object r = Prop(word, "BoundingRect");
                double x = (double)Prop(r, "X");
                double y = (double)Prop(r, "Y");
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + (double)Prop(r, "Width"));
                bottom = Math.Max(bottom, y + (double)Prop(r, "Height"));
            }
            if (left > right) return new ScreenRect(0, 0, 0, 0);
            int l = (int)Math.Floor(left);
            int t = (int)Math.Floor(top);
            int rr = (int)Math.Ceiling(right);
            int b = (int)Math.Ceiling(bottom);
            return new ScreenRect(l, t, rr - l, b - t);
        }

        // 32bpp top-down BMP (negative height). WIC decodes it to Bgra8, so the engine gets the same SoftwareBitmap path as the source.
        private static byte[] EncodeBmp(RawImage image)
        {
            int rowBytes = image.Width * 4;
            int dataSize = rowBytes * image.Height;
            byte[] bmp = new byte[54 + dataSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            WriteInt32(bmp, 2, bmp.Length);
            WriteInt32(bmp, 10, 54);
            WriteInt32(bmp, 14, 40);
            WriteInt32(bmp, 18, image.Width);
            WriteInt32(bmp, 22, -image.Height);
            WriteInt16(bmp, 26, 1);
            WriteInt16(bmp, 28, 32);
            WriteInt32(bmp, 34, dataSize);
            for (int y = 0; y < image.Height; y++)
            {
                Buffer.BlockCopy(image.Pixels, y * image.Stride, bmp, 54 + y * rowBytes, rowBytes);
            }
            return bmp;
        }

        private static void WriteInt32(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        private static void WriteInt16(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }
    }
}
