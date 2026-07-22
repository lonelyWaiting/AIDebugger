using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Applies resource containment after the AppContainer process is created suspended.
    /// </summary>
    internal sealed class ManagedInspectionJob : IDisposable
    {
        private const uint JobObjectLimitProcessMemory = 0x00000100;
        private const uint JobObjectLimitJobMemory = 0x00000200;
        private const uint JobObjectLimitProcessTime = 0x00000002;
        private const uint JobObjectLimitActiveProcess = 0x00000008;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int ExtendedLimitInformationClass = 9;
        private static readonly UIntPtr ProcessMemoryLimit = new UIntPtr(256UL * 1024UL * 1024UL);
        private static readonly UIntPtr JobMemoryLimit = new UIntPtr(384UL * 1024UL * 1024UL);

        private readonly SafeJobHandle _handle;

        private ManagedInspectionJob(SafeJobHandle handle)
        {
            _handle = handle;
        }

        internal static ManagedInspectionJob CreateAndAssign(Process process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create managed-inspection Job Object");
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose |
                        JobObjectLimitProcessMemory |
                        JobObjectLimitJobMemory |
                        JobObjectLimitProcessTime |
                        JobObjectLimitActiveProcess,
                    PerProcessUserTimeLimit = TimeSpan.FromSeconds(45).Ticks,
                    ActiveProcessLimit = 1,
                },
                ProcessMemoryLimit = ProcessMemoryLimit,
                JobMemoryLimit = JobMemoryLimit,
            };
            int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure managed-inspection Job Object");
                }
                if (!AssignProcessToJobObject(handle, process.Handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to apply managed-inspection resource containment");
                }
                return new ManagedInspectionJob(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle() : base(true) { }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
