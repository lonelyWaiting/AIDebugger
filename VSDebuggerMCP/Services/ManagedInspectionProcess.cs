using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Starts the metadata parser inside an AppContainer with no capabilities.
    /// The target assembly is provided only as an inherited read-only handle;
    /// the container receives no target path access and no network capability.
    /// </summary>
    internal sealed class ManagedInspectionProcess : IDisposable
    {
        private const string ProfileNamePrefix = "AIDebugger.ManagedInspection.";
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 0x00000001;
        private const string ProfileRegistryPath = @"Software\AIDebugger\ManagedInspectionProfiles";
        private static readonly object ProfileJournalLock = new object();
        private static DateTime _lastOrphanCleanupUtc = DateTime.MinValue;
        private static readonly IntPtr ProcThreadAttributeHandleList = new IntPtr(0x00020002);
        private static readonly IntPtr ProcThreadAttributeMitigationPolicy = new IntPtr(0x00020007);
        private static readonly IntPtr ProcThreadAttributeSecurityCapabilities = new IntPtr(0x00020009);

        private readonly AnonymousPipeServerStream _stdinPipe;
        private readonly AnonymousPipeServerStream _stdoutPipe;
        private readonly AnonymousPipeServerStream _stderrPipe;
        private readonly ManagedInspectionJob _job;
        private readonly string _profileName;

        private ManagedInspectionProcess(
            Process process,
            ManagedInspectionJob job,
            AnonymousPipeServerStream stdinPipe,
            AnonymousPipeServerStream stdoutPipe,
            AnonymousPipeServerStream stderrPipe,
            string profileName)
        {
            Process = process;
            _job = job;
            _stdinPipe = stdinPipe;
            _stdoutPipe = stdoutPipe;
            _stderrPipe = stderrPipe;
            _profileName = profileName;
            StandardInput = new StreamWriter(stdinPipe, new UTF8Encoding(false), 4096, false);
            StandardOutput = new StreamReader(stdoutPipe, Encoding.UTF8, true, 4096, true);
            StandardError = new StreamReader(stderrPipe, Encoding.UTF8, true, 4096, true);
        }

        internal Process Process { get; }
        internal StreamWriter StandardInput { get; }
        internal StreamReader StandardOutput { get; }
        internal StreamReader StandardError { get; }

        internal static ManagedInspectionProcess Start(string helperPath, string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(helperPath)) throw new ArgumentNullException(nameof(helperPath));
            if (string.IsNullOrWhiteSpace(assemblyPath)) throw new ArgumentNullException(nameof(assemblyPath));

            string profileName = ProfileNamePrefix + Guid.NewGuid().ToString("N");
            IntPtr appContainerSid = IntPtr.Zero;
            AnonymousPipeServerStream stdinPipe = null;
            AnonymousPipeServerStream stdoutPipe = null;
            AnonymousPipeServerStream stderrPipe = null;
            FileStream assembly = null;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr capabilitiesPointer = IntPtr.Zero;
            IntPtr handleListPointer = IntPtr.Zero;
            IntPtr mitigationPointer = IntPtr.Zero;
            IntPtr environmentPointer = IntPtr.Zero;
            Process process = null;
            ManagedInspectionJob job = null;
            ProcessInformation processInformation = default(ProcessInformation);
            bool created = false;
            bool handedOff = false;
            try
            {
                CleanupOrphanedProfiles();
                RegisterEphemeralProfile(profileName);
                appContainerSid = CreateEphemeralAppContainerSid(profileName);
                helperPath = StageHelperInContainer(profileName, appContainerSid, helperPath);
                stdinPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                assembly = new FileStream(
                    assemblyPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan);
                if (!SetHandleInformation(assembly.SafeFileHandle.DangerousGetHandle(), HandleFlagInherit, HandleFlagInherit))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to make the assembly handle inheritable");

                var childHandles = new[]
                {
                    stdinPipe.ClientSafePipeHandle.DangerousGetHandle(),
                    stdoutPipe.ClientSafePipeHandle.DangerousGetHandle(),
                    stderrPipe.ClientSafePipeHandle.DangerousGetHandle(),
                    assembly.SafeFileHandle.DangerousGetHandle(),
                };

                IntPtr attributeBytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref attributeBytes);
                if (attributeBytes == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size the process attribute list");
                attributeList = Marshal.AllocHGlobal(attributeBytes);
                if (!InitializeProcThreadAttributeList(attributeList, 3, 0, ref attributeBytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize the process attribute list");

                var capabilities = new SecurityCapabilities
                {
                    AppContainerSid = appContainerSid,
                    Capabilities = IntPtr.Zero,
                    CapabilityCount = 0,
                    Reserved = 0,
                };
                capabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecurityCapabilities)));
                Marshal.StructureToPtr(capabilities, capabilitiesPointer, false);
                UpdateAttribute(attributeList, ProcThreadAttributeSecurityCapabilities, capabilitiesPointer,
                    new IntPtr(Marshal.SizeOf(typeof(SecurityCapabilities))), "AppContainer capabilities");

                handleListPointer = Marshal.AllocHGlobal(IntPtr.Size * childHandles.Length);
                for (int index = 0; index < childHandles.Length; index++)
                    Marshal.WriteIntPtr(handleListPointer, index * IntPtr.Size, childHandles[index]);
                UpdateAttribute(attributeList, ProcThreadAttributeHandleList, handleListPointer,
                    new IntPtr(IntPtr.Size * childHandles.Length), "inherited handle allowlist");

                // DEP and bottom-up ASLR are compatible with the CLR JIT. Dynamic-code
                // prohibition is intentionally not requested because it would disable the JIT.
                const ulong mitigationPolicy = 0x0000000000100001UL;
                mitigationPointer = Marshal.AllocHGlobal(sizeof(ulong));
                Marshal.WriteInt64(mitigationPointer, unchecked((long)mitigationPolicy));
                UpdateAttribute(attributeList, ProcThreadAttributeMitigationPolicy, mitigationPointer,
                    new IntPtr(sizeof(ulong)), "process mitigation policy");

                string environment = CreateRestrictedEnvironmentBlock();
                environmentPointer = Marshal.StringToHGlobalUni(environment);

                var startup = new StartupInfoEx();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.StartupInfo.dwFlags = StartfUseStdHandles;
                startup.StartupInfo.hStdInput = childHandles[0];
                startup.StartupInfo.hStdOutput = childHandles[1];
                startup.StartupInfo.hStdError = childHandles[2];
                startup.AttributeList = attributeList;
                var commandLine = new StringBuilder(
                    QuoteCommandLineArgument(helperPath) + " --assembly-handle " +
                    childHandles[3].ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
                uint flags = CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
                if (!CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    flags,
                    environmentPointer,
                    null,
                    ref startup,
                    out processInformation))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "Unable to create the AppContainer inspection process (Win32 " + error + ")");
                }
                created = true;
                process = Process.GetProcessById(processInformation.ProcessId);
                job = ManagedInspectionJob.CreateAndAssign(process);
                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the contained inspection process");

                stdinPipe.DisposeLocalCopyOfClientHandle();
                stdoutPipe.DisposeLocalCopyOfClientHandle();
                stderrPipe.DisposeLocalCopyOfClientHandle();
                SetHandleInformation(assembly.SafeFileHandle.DangerousGetHandle(), HandleFlagInherit, 0);
                handedOff = true;
                return new ManagedInspectionProcess(
                    process, job, stdinPipe, stdoutPipe, stderrPipe, profileName);
            }
            catch
            {
                if (created && processInformation.Process != IntPtr.Zero)
                    TerminateProcess(processInformation.Process, 1);
                job?.Dispose();
                process?.Dispose();
                stdinPipe?.Dispose();
                stdoutPipe?.Dispose();
                stderrPipe?.Dispose();
                throw;
            }
            finally
            {
                if (processInformation.Thread != IntPtr.Zero) CloseHandle(processInformation.Thread);
                if (processInformation.Process != IntPtr.Zero) CloseHandle(processInformation.Process);
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (capabilitiesPointer != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesPointer);
                if (handleListPointer != IntPtr.Zero) Marshal.FreeHGlobal(handleListPointer);
                if (mitigationPointer != IntPtr.Zero) Marshal.FreeHGlobal(mitigationPointer);
                if (environmentPointer != IntPtr.Zero) Marshal.FreeHGlobal(environmentPointer);
                if (assembly != null)
                {
                    try { SetHandleInformation(assembly.SafeFileHandle.DangerousGetHandle(), HandleFlagInherit, 0); }
                    catch { }
                    assembly.Dispose();
                }
                if (appContainerSid != IntPtr.Zero) FreeSid(appContainerSid);
                if (!handedOff)
                    DeleteEphemeralProfile(profileName, true);
            }
        }

        public void Dispose()
        {
            // Closing the Job is the authoritative kill operation. Wait for the
            // process to actually exit before removing its ephemeral profile.
            _job.Dispose();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (!Process.HasExited && DateTime.UtcNow < deadline)
                {
                    Process.WaitForExit(25);
                }
            }
            catch { }
            try { StandardInput.Dispose(); } catch { }
            try { StandardOutput.Dispose(); } catch { }
            try { StandardError.Dispose(); } catch { }
            _stdinPipe.Dispose();
            _stdoutPipe.Dispose();
            _stderrPipe.Dispose();
            Process.Dispose();
            DeleteEphemeralProfile(_profileName, true);
        }

        private static IntPtr CreateEphemeralAppContainerSid(string profileName)
        {
            IntPtr sid;
            int result = CreateAppContainerProfile(
                profileName,
                "AIDebugger Managed Inspection",
                "No-capability container for untrusted ECMA-335 metadata",
                IntPtr.Zero,
                0,
                out sid);
            if (result != 0 || sid == IntPtr.Zero)
                throw new Win32Exception(result, "Unable to create the ephemeral managed-inspection AppContainer");
            return sid;
        }

        private static string StageHelperInContainer(string profileName, IntPtr sidPointer, string helperPath)
        {
            string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath));
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                throw new InvalidOperationException("Managed inspection helper directory is unavailable");
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Managed inspection helper directory must not be a reparse point");
            if (!string.Equals(
                Path.GetFileName(helperPath),
                "ManagedInspectionHelper.exe",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Managed inspection helper has an unexpected file name");
            }
            string[] requiredSources =
            {
                Path.Combine(sourceDirectory, "ManagedInspectionHelper.exe"),
                Path.Combine(sourceDirectory, "Mono.Cecil.dll"),
            };
            foreach (string source in requiredSources)
                EnsurePlainSourceFile(source);
            string[] sourceHashes = requiredSources.Select(ComputeSha256).ToArray();
            var sid = new SecurityIdentifier(sidPointer);
            IntPtr folderPointer = IntPtr.Zero;
            int folderResult = GetAppContainerFolderPath(sid.Value, out folderPointer);
            if (folderResult != 0 || folderPointer == IntPtr.Zero)
                throw new Win32Exception(folderResult, "Unable to resolve the ephemeral AppContainer folder");
            string containerFolder;
            try
            {
                containerFolder = Marshal.PtrToStringUni(folderPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(folderPointer);
            }
            if (string.IsNullOrWhiteSpace(containerFolder))
                throw new InvalidOperationException("The ephemeral AppContainer returned an empty storage path");

            // Apply the deny before creating the broker tree so every staged
            // child inherits it before the untrusted process can start.
            var security = Directory.GetAccessControl(containerFolder, AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.Write |
                    FileSystemRights.Delete |
                    FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions |
                    FileSystemRights.TakeOwnership,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny));
            Directory.SetAccessControl(containerFolder, security);
            var verified = Directory.GetAccessControl(containerFolder, AccessControlSections.Access | AccessControlSections.Owner);
            if (sid.Equals(verified.GetOwner(typeof(SecurityIdentifier))))
                throw new InvalidOperationException("The AppContainer must not own its storage directory");

            string brokerDirectory = Path.Combine(containerFolder, "LocalState", "Broker");
            Directory.CreateDirectory(brokerDirectory);
            for (int index = 0; index < requiredSources.Length; index++)
            {
                string destination = Path.Combine(brokerDirectory, Path.GetFileName(requiredSources[index]));
                File.Copy(requiredSources[index], destination, false);
                EnsurePlainSourceFile(destination);
                if (!string.Equals(sourceHashes[index], ComputeSha256(destination), StringComparison.Ordinal))
                    throw new InvalidOperationException("Managed inspection helper staging checksum mismatch");
            }

            string[] stagedFiles = Directory.EnumerateFiles(brokerDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] expectedFiles = { "ManagedInspectionHelper.exe", "Mono.Cecil.dll" };
            Array.Sort(expectedFiles, StringComparer.OrdinalIgnoreCase);
            if (!stagedFiles.SequenceEqual(expectedFiles, StringComparer.OrdinalIgnoreCase) ||
                Directory.EnumerateDirectories(brokerDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidOperationException("Managed inspection broker contains an unexpected payload");
            }
            VerifyContainerWriteDenied(brokerDirectory, sid);
            foreach (string stagedFile in Directory.EnumerateFiles(brokerDirectory))
                VerifyContainerWriteDenied(stagedFile, sid);

            string stagedHelper = Path.Combine(brokerDirectory, Path.GetFileName(helperPath));
            if (!File.Exists(stagedHelper))
                throw new InvalidOperationException("The managed inspection helper was not staged into the AppContainer");

            return stagedHelper;
        }

        private static void VerifyContainerWriteDenied(string path, SecurityIdentifier sid)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Access)
                : File.GetAccessControl(path, AccessControlSections.Access);
            bool denied = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Deny &&
                    sid.Equals(rule.IdentityReference) &&
                    (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete)) != 0);
            if (!denied)
                throw new InvalidOperationException("Managed inspection broker write deny was not inherited");
        }

        private static void EnsurePlainSourceFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Managed inspection dependency is missing", path);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Managed inspection dependency must be a plain file");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static void RegisterEphemeralProfile(string profileName)
        {
            lock (ProfileJournalLock)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProfileRegistryPath, true))
                {
                    if (key == null) throw new InvalidOperationException("Unable to open the managed-inspection profile journal");
                    key.SetValue(profileName, DateTime.UtcNow.Ticks, RegistryValueKind.QWord);
                    key.Flush();
                }
            }
        }

        private static void RemoveEphemeralProfileRecord(string profileName)
        {
            try
            {
                lock (ProfileJournalLock)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProfileRegistryPath, true))
                    {
                        key?.DeleteValue(profileName, false);
                        key?.Flush();
                    }
                }
            }
            catch { }
        }

        private static void CleanupOrphanedProfiles()
        {
            string[] orphanNames;
            lock (ProfileJournalLock)
            {
                DateTime now = DateTime.UtcNow;
                if (now - _lastOrphanCleanupUtc < TimeSpan.FromMinutes(1)) return;
                _lastOrphanCleanupUtc = now;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProfileRegistryPath, false))
                {
                    if (key == null) return;
                    orphanNames = key.GetValueNames()
                        .Where(IsManagedInspectionProfileName)
                        .Where(name =>
                        {
                            object raw = key.GetValue(name, 0L, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            long ticks = raw is long value ? value : 0L;
                            return ticks <= now.AddMinutes(-5).Ticks;
                        })
                        .Take(4)
                        .ToArray();
                }
            }
            // One OS call per profile and no journal lock while deleting. This
            // keeps the request path bounded even if cleanup repeatedly fails.
            foreach (string orphan in orphanNames)
                DeleteEphemeralProfile(orphan, false);
        }

        private static bool IsManagedInspectionProfileName(string profileName)
        {
            if (string.IsNullOrEmpty(profileName) || !profileName.StartsWith(ProfileNamePrefix, StringComparison.Ordinal))
                return false;
            string suffix = profileName.Substring(ProfileNamePrefix.Length);
            return suffix.Length == 32 && suffix.All(Uri.IsHexDigit);
        }

        private static bool DeleteEphemeralProfile(string profileName, bool retry)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return true;
            DateTime deadline = retry ? DateTime.UtcNow.AddSeconds(2) : DateTime.UtcNow;
            do
            {
                int result = DeleteAppContainerProfile(profileName);
                if (result == 0 ||
                    result == unchecked((int)0x80070490) || // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
                    result == unchecked((int)0x80070002) || // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
                    result == unchecked((int)0x80070003))   // HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)
                {
                    RemoveEphemeralProfileRecord(profileName);
                    return true;
                }
                if (!retry) break;
                System.Threading.Thread.Sleep(25);
            } while (DateTime.UtcNow < deadline);
            // Keep the registry value. A later invocation retries sufficiently old
            // profiles, so timeout/crash cleanup cannot leak profiles forever.
            return false;
        }

        private static void UpdateAttribute(
            IntPtr list,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            string description)
        {
            if (!UpdateProcThreadAttribute(list, 0, attribute, value, size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set " + description);
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string CreateRestrictedEnvironmentBlock()
        {
            // CreateProcess requires a sorted Unicode environment block. Keep
            // only OS/runtime location variables; never copy credentials or
            // arbitrary variables from the Visual Studio process.
            string[] allowedNames =
            {
                "ALLUSERSPROFILE", "APPDATA", "CommonProgramFiles", "CommonProgramFiles(x86)",
                "CommonProgramW6432", "ComSpec", "LOCALAPPDATA", "NUMBER_OF_PROCESSORS", "OS",
                "Path", "PATHEXT", "PROCESSOR_ARCHITECTURE", "ProgramData", "ProgramFiles",
                "ProgramFiles(x86)", "ProgramW6432", "SystemDrive", "SystemRoot", "TEMP", "TMP",
                "USERPROFILE", "WINDIR",
            };
            var variables = allowedNames
                .Select(name => new { Name = name, Value = Environment.GetEnvironmentVariable(name) })
                .Where(item => !string.IsNullOrEmpty(item.Value))
                .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
            variables["COMPlus_EnableDiagnostics"] = "0";
            return string.Join("\0", variables
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Key + "=" + item.Value)) + "\0\0";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityCapabilities
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public uint CapabilityCount;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateAppContainerProfile(
            string appContainerName,
            string displayName,
            string description,
            IntPtr capabilities,
            uint capabilityCount,
            out IntPtr appContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int GetAppContainerFolderPath(
            string appContainerSid,
            out IntPtr path);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeleteAppContainerProfile(string appContainerName);

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr sid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref IntPtr size);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
