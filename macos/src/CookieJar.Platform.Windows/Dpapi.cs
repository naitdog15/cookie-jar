using System;
using System.Runtime.InteropServices;

namespace CookieJar.Platform.Windows
{
    /// <summary>
    /// DPAPI wrapper (current-user scope, caller-supplied entropy) called through crypt32.
    /// Same flags and description as src/Dpapi.cs so blobs are interchangeable. Error messages never include data.
    /// </summary>
    internal static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;
        private const string Description = "RobloxMultiLauncher";

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description,
            IntPtr entropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
            IntPtr entropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] plain, byte[] entropy)
        {
            return Run(plain, entropy, true);
        }

        public static byte[] Unprotect(byte[] sealedBytes, byte[] entropy)
        {
            return Run(sealedBytes, entropy, false);
        }

        private static DataBlob ToBlob(byte[] data)
        {
            DataBlob blob = new DataBlob();
            blob.cbData = data.Length;
            blob.pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            DataBlob input = ToBlob(data);
            DataBlob ent = new DataBlob();
            IntPtr entPtr = IntPtr.Zero;
            if (entropy != null)
            {
                ent = ToBlob(entropy);
                entPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DataBlob)));
                Marshal.StructureToPtr(ent, entPtr, false);
            }
            try
            {
                DataBlob output;
                bool ok = protect
                    ? CryptProtectData(ref input, Description, entPtr, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, entPtr, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (!ok)
                {
                    throw new InvalidOperationException("DPAPI " + (protect ? "encrypt" : "decrypt")
                        + " failed (error " + Marshal.GetLastWin32Error() + ")");
                }
                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                LocalFree(output.pbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (entPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ent.pbData);
                    Marshal.FreeHGlobal(entPtr);
                }
            }
        }
    }
}
