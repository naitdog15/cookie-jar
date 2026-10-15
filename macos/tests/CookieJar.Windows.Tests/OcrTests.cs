using System;
using System.Collections.Generic;
using CookieJar.Platform;
using CookieJar.Platform.Windows;

namespace CookieJar.Tests
{
    public static class OcrTests
    {
        public static void Run()
        {
            WinOcrService ocr = new WinOcrService();

            // Decision: a null image throws ArgumentNullException (programming error, not an OCR failure).
            bool threw = false;
            string threwType = "none";
            try { ocr.Recognise(null); }
            catch (ArgumentNullException) { threw = true; threwType = "ArgumentNullException"; }
            catch (Exception ex) { threwType = ex.GetType().Name; }
            T.Check("Recognise(null) throws ArgumentNullException", threw, threwType);

            // All-black, opaque 100x40 image: no text, and no exception even if the engine is unavailable.
            byte[] black = new byte[100 * 40 * 4];
            for (int i = 3; i < black.Length; i += 4) black[i] = 255;
            RawImage blackImg = new RawImage(100, 40, 100 * 4, black);
            IReadOnlyList<OcrLine> lines = null;
            Exception failure = null;
            try { lines = ocr.Recognise(blackImg); }
            catch (Exception ex) { failure = ex; }
            T.Check("Recognise of all-black image does not throw", failure == null, failure == null ? "" : failure.GetType().Name + ": " + failure.Message);
            T.Check("Recognise of all-black image returns an empty list", lines != null && lines.Count == 0, lines == null ? "null" : "Count=" + lines.Count);

            RawImage empty = new RawImage(0, 0, 0, new byte[0]);
            IReadOnlyList<OcrLine> emptyLines = ocr.Recognise(empty);
            T.Check("Recognise of zero-size image returns an empty list", emptyLines != null && emptyLines.Count == 0);
        }
    }
}
