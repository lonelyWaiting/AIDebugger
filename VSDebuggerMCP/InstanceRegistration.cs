using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace VSDebuggerMCP
{
    internal sealed class InstanceRegistration : IDisposable
    {
        private readonly string _registrationPath;
        private readonly string _registrationDirectory;
        private readonly string _serverInstanceId;
        private readonly Action<string> _log;
        private bool _disposed;

        public InstanceRegistration(
            string protocol,
            string pipeName,
            string controlPipeName,
            string recoveryPipeName,
            int processId,
            long processStartTimeUtcTicks,
            string serverInstanceId,
            string[] capabilities,
            Action<string> log)
        {
            _serverInstanceId = serverInstanceId;
            _log = log ?? (_ => { });
            AuthToken = CreateAuthToken();

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!BundlePathPolicy.IsPlainDirectory(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATA is missing or is a reparse point");
            }
            string aidbRoot = EnsureOwnerOnlyChildDirectory(localAppData, "AIDebugger");
            _registrationDirectory = EnsureOwnerOnlyChildDirectory(aidbRoot, "instances");
            _registrationPath = Path.GetFullPath(Path.Combine(_registrationDirectory, pipeName + ".json"));
            if (!string.Equals(
                Path.GetDirectoryName(_registrationPath),
                _registrationDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Instance registration path escaped its trusted directory");
            }

            var payload = new RegistrationPayload
            {
                Protocol = protocol,
                PipeName = pipeName,
                ControlPipeName = controlPipeName,
                RecoveryPipeName = recoveryPipeName,
                ProcessId = processId,
                ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
                ServerInstanceId = serverInstanceId,
                AuthToken = AuthToken,
                Capabilities = capabilities ?? new string[0],
            };
            WriteAtomicallyOwnerOnly(
                _registrationPath,
                _registrationDirectory,
                new JavaScriptSerializer().Serialize(payload));
        }

        public string AuthToken { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try
            {
                if (!BundlePathPolicy.IsPlainDirectory(_registrationDirectory) ||
                    !BundlePathPolicy.IsPlainFile(_registrationPath) ||
                    !string.Equals(
                        Path.GetDirectoryName(Path.GetFullPath(_registrationPath)),
                        _registrationDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string json = File.ReadAllText(_registrationPath, Encoding.UTF8);
                var current = new JavaScriptSerializer().Deserialize<RegistrationPayload>(json);
                if (string.Equals(current?.ServerInstanceId, _serverInstanceId, StringComparison.Ordinal))
                {
                    if (BundlePathPolicy.IsPlainFile(_registrationPath))
                    {
                        File.Delete(_registrationPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Failed to remove instance registration: {ex.GetType().Name}");
            }
        }

        private static string CreateAuthToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes);
        }

        private static string EnsureOwnerOnlyChildDirectory(string parentPath, string childName)
        {
            if (string.IsNullOrWhiteSpace(childName) ||
                childName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                childName.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                childName == "." || childName == "..")
            {
                throw new ArgumentException("A direct child directory name is required", nameof(childName));
            }
            string parent = Path.GetFullPath(parentPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!BundlePathPolicy.IsPlainDirectory(parent))
            {
                throw new InvalidOperationException("Instance registration parent is missing or is a reparse point");
            }
            string path = Path.GetFullPath(Path.Combine(parent, childName));
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Instance registration directory is not a direct child");
            }

            var sid = GetCurrentUserSid();
            var security = CreateOwnerOnlyDirectorySecurity(sid);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path, security);
            }
            if (!BundlePathPolicy.IsPlainDirectory(parent) || !BundlePathPolicy.IsPlainDirectory(path))
            {
                throw new InvalidOperationException("Instance registration directory is a reparse point");
            }
            new DirectoryInfo(path).SetAccessControl(security);
            if (!BundlePathPolicy.IsPlainDirectory(parent) || !BundlePathPolicy.IsPlainDirectory(path))
            {
                throw new InvalidOperationException("Instance registration directory changed during ACL setup");
            }
            return path;
        }

        private static DirectorySecurity CreateOwnerOnlyDirectorySecurity(SecurityIdentifier sid)
        {
            var security = new DirectorySecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        private static void WriteAtomicallyOwnerOnly(string path, string trustedDirectory, string content)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(trustedDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                !BundlePathPolicy.IsPlainDirectory(fullDirectory))
            {
                throw new InvalidOperationException("Instance registration path is outside its trusted directory");
            }
            FileAttributes existingAttributes;
            if (TryGetAttributes(fullPath, out existingAttributes) &&
                (existingAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException("Existing instance registration is not a plain file");
            }

            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content ?? string.Empty);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (!BundlePathPolicy.IsPlainDirectory(fullDirectory) ||
                    !BundlePathPolicy.IsPlainFile(temporaryPath))
                {
                    throw new InvalidOperationException("Instance registration staging path is unsafe");
                }
                var sid = GetCurrentUserSid();
                var security = new FileSecurity();
                security.SetOwner(sid);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                new FileInfo(temporaryPath).SetAccessControl(security);

                if (TryGetAttributes(fullPath, out existingAttributes) &&
                    (existingAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidOperationException("Instance registration changed to an unsafe path");
                }
                if (File.Exists(fullPath))
                {
                    string backupPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".bak";
                    try
                    {
                        File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
                    }
                    finally
                    {
                        if (File.Exists(backupPath)) File.Delete(backupPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
                if (!BundlePathPolicy.IsPlainDirectory(fullDirectory) ||
                    !BundlePathPolicy.IsPlainFile(fullPath))
                {
                    throw new InvalidOperationException("Instance registration write did not produce a plain file");
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            attributes = 0;
            return false;
        }

        private static SecurityIdentifier GetCurrentUserSid()
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid == null)
            {
                throw new InvalidOperationException("Unable to determine current Windows user SID");
            }
            return sid;
        }

        private sealed class RegistrationPayload
        {
            public string Protocol { get; set; }
            public string PipeName { get; set; }
            public string ControlPipeName { get; set; }
            public string RecoveryPipeName { get; set; }
            public int ProcessId { get; set; }
            public long ProcessStartTimeUtcTicks { get; set; }
            public string ServerInstanceId { get; set; }
            public string AuthToken { get; set; }
            public string[] Capabilities { get; set; }
        }
    }
}
