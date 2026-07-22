using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VSDebuggerMCP.Ipc
{
    internal static class AuthenticodeTrustValidator
    {
        private static readonly Guid GenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static bool IsTrustedFileSignedBy(string filePath, string expectedSubjectFragment)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(expectedSubjectFragment))
                return false;
            if (!VerifyEmbeddedSignature(filePath)) return false;
            try
            {
                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath)))
                {
                    return certificate.Subject.IndexOf(
                        expectedSubjectFragment, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static bool VerifyEmbeddedSignature(string filePath)
        {
            IntPtr pathPointer = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(filePath);
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    FilePath = pathPointer,
                };
                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                var trustData = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 0,
                    ProviderFlags = 0x00001000,
                    UiContext = 0,
                };
                Guid action = GenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
            }
            catch (Exception ex) when (
                ex is ArgumentException || ex is ExternalException || ex is DllNotFoundException)
            {
                return false;
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
                if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            internal uint StructSize;
            internal IntPtr FilePath;
            internal IntPtr FileHandle;
            internal IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            internal uint StructSize;
            internal IntPtr PolicyCallbackData;
            internal IntPtr SipClientData;
            internal uint UiChoice;
            internal uint RevocationChecks;
            internal uint UnionChoice;
            internal IntPtr FileInfo;
            internal uint StateAction;
            internal IntPtr StateData;
            internal IntPtr UrlReference;
            internal uint ProviderFlags;
            internal uint UiContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [In] ref Guid actionId,
            [In] ref WinTrustData trustData);
    }
}
