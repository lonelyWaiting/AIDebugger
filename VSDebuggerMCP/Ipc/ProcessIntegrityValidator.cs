using System;
using System.Runtime.InteropServices;

namespace VSDebuggerMCP.Ipc
{
    /// <summary>
    /// Enforces the Windows mandatory-integrity boundary in addition to the pipe DACL.
    /// A same-user medium-integrity process must not drive an elevated Visual Studio.
    /// </summary>
    internal static class ProcessIntegrityValidator
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenIntegrityLevel = 25;

        internal static bool IsClientIntegrityAllowed(uint serverIntegrityRid, uint clientIntegrityRid)
        {
            return clientIntegrityRid >= serverIntegrityRid;
        }

        internal static bool TryValidateClientAgainstCurrentProcess(int clientProcessId, out string error)
        {
            error = null;
            if (clientProcessId <= 0)
            {
                error = "Named-pipe client PID is invalid";
                return false;
            }

            if (!TryGetCurrentProcessIntegrityRid(out uint serverRid))
            {
                error = "Unable to determine the Visual Studio process integrity level";
                return false;
            }
            if (!TryGetProcessIntegrityRid(clientProcessId, out uint clientRid))
            {
                error = "Unable to determine the named-pipe client integrity level";
                return false;
            }
            if (!IsClientIntegrityAllowed(serverRid, clientRid))
            {
                error = $"Client integrity level 0x{clientRid:X} is lower than Visual Studio integrity level 0x{serverRid:X}";
                return false;
            }
            return true;
        }

        internal static bool TryGetCurrentProcessIntegrityRid(out uint integrityRid)
        {
            return TryGetIntegrityRidForProcessHandle(GetCurrentProcess(), out integrityRid);
        }

        internal static bool IsCurrentProcessElevated()
        {
            return TryGetCurrentProcessIntegrityRid(out uint rid) && rid >= 0x3000;
        }

        private static bool TryGetProcessIntegrityRid(int processId, out uint integrityRid)
        {
            integrityRid = 0;
            IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return TryGetIntegrityRidForProcessHandle(processHandle, out integrityRid);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static bool TryGetIntegrityRidForProcessHandle(IntPtr processHandle, out uint integrityRid)
        {
            integrityRid = 0;
            if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
            {
                return false;
            }
            try
            {
                GetTokenInformation(tokenHandle, TokenIntegrityLevel, IntPtr.Zero, 0, out int requiredBytes);
                if (requiredBytes <= 0)
                {
                    return false;
                }

                IntPtr buffer = Marshal.AllocHGlobal(requiredBytes);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, requiredBytes, out _))
                    {
                        return false;
                    }

                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    if (sid == IntPtr.Zero || !IsValidSid(sid))
                    {
                        return false;
                    }
                    IntPtr countPointer = GetSidSubAuthorityCount(sid);
                    if (countPointer == IntPtr.Zero)
                    {
                        return false;
                    }
                    byte subAuthorityCount = Marshal.ReadByte(countPointer);
                    if (subAuthorityCount == 0)
                    {
                        return false;
                    }
                    IntPtr ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
                    if (ridPointer == IntPtr.Zero)
                    {
                        return false;
                    }
                    integrityRid = unchecked((uint)Marshal.ReadInt32(ridPointer));
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsValidSid(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);
    }
}
