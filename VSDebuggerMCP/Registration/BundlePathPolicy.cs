using System;
using System.Collections.Generic;
using System.IO;

namespace VSDebuggerMCP
{
    internal static class BundlePathPolicy
    {
        private static readonly HashSet<string> VsToolModuleFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__init__.py",
                "capability_tools.py",
                "vs_instance_tools.py",
                "multi_vs_tools.py",
                "snapshot_tools.py",
                "native_managed_tools.py",
                "control_tools.py",
                "breakpoint_tools.py",
                "memory_tools.py",
            };

        public static string EnsurePlainChildDirectory(string parentDirectory, string childName)
        {
            if (string.IsNullOrWhiteSpace(childName) ||
                childName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                childName.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                childName == "." || childName == "..")
            {
                throw new ArgumentException("A direct child directory name is required", nameof(childName));
            }

            string parent = NormalizeRoot(parentDirectory);
            if (!Directory.Exists(parent) ||
                (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Owned bundle parent is missing or is a reparse point");
            }
            string child = Path.GetFullPath(Path.Combine(parent, childName));
            if (!string.Equals(Path.GetDirectoryName(child), parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Owned bundle directory is not a direct child");
            }
            Directory.CreateDirectory(child);
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Owned bundle directory is a reparse point");
            }
            return child;
        }

        public static bool IsPlainDirectory(string directory)
        {
            try
            {
                string full = NormalizeRoot(directory);
                return Directory.Exists(full) &&
                    (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static bool IsPlainFile(string file)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(Path.GetFullPath(file));
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static void DeletePlainTree(string directory)
        {
            string root = NormalizeRoot(directory);
            if (!Directory.Exists(root)) return;
            if (!IsPlainDirectory(root))
                throw new InvalidOperationException("Refusing to delete a reparse-point bundle root");

            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string current = pending.Pop();
                EnsureNoReparsePoint(root, current);
                foreach (string entry in Directory.GetFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Refusing to delete a bundle tree containing a reparse point");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        EnsureNoReparsePoint(root, entry);
                        directories.Add(entry);
                        pending.Push(entry);
                    }
                    else
                    {
                        EnsureNoReparsePoint(root, entry);
                        files.Add(entry);
                    }
                }
            }

            foreach (string file in files)
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidOperationException("Bundle entry changed during safe deletion");
                File.Delete(file);
            }
            directories.Sort((left, right) => right.Length.CompareTo(left.Length));
            foreach (string child in directories)
            {
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidOperationException("Bundle directory changed during safe deletion");
                Directory.Delete(child, false);
            }
            if (!IsPlainDirectory(root))
                throw new InvalidOperationException("Bundle root changed during safe deletion");
            Directory.Delete(root, false);
        }

        public static void CopyVsBundleFiles(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            if ((File.GetAttributes(targetDir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("VS MCP staging root is a reparse point");
            }
            foreach (string file in GetVsBundleFiles(sourceDir))
            {
                string relativePath = GetRelativePath(sourceDir, file);
                string destination = ResolveDestination(targetDir, relativePath);
                string destinationParent = Path.GetDirectoryName(destination);
                Directory.CreateDirectory(destinationParent);
                EnsureNoReparsePoint(targetDir, destinationParent);
                if (File.Exists(destination) &&
                    (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("VS MCP staging file is a reparse point");
                }
                File.Copy(file, destination, true);
                EnsureNoReparsePoint(targetDir, destination);
            }
        }

        public static string[] GetVsBundleFiles(string sourceDir)
        {
            var files = new List<string>();
            string root = NormalizeRoot(sourceDir);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("VS MCP bundle root is a reparse point");
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string current = pending.Pop();
                foreach (string directory in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    string relative = GetRelativePath(root, directory).Replace("\\", "/");
                    if (!IsVsRuntimeDirectory(relative)) continue;
                    EnsureNoReparsePoint(root, directory);
                    pending.Push(directory);
                }
                foreach (string file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    EnsureNoReparsePoint(root, file);
                    string relative = GetRelativePath(root, file).Replace("\\", "/");
                    if (IsVsBundleRelativePath(relative)) files.Add(file);
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files.ToArray();
        }

        public static bool IsValidVsBundleTree(string bundleDir, bool allowBundleMetadata)
        {
            try
            {
                string root = NormalizeRoot(bundleDir);
                if (!Directory.Exists(root) ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count != 0)
                {
                    string current = pending.Pop();
                    foreach (string directory in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                        string relative = GetRelativePath(root, directory).Replace("\\", "/");
                        if (!IsVsRuntimeDirectory(relative)) return false;
                        pending.Push(directory);
                    }
                    foreach (string file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                        string relative = GetRelativePath(root, file).Replace("\\", "/");
                        bool metadata = relative.Equals(".bundle-signature", StringComparison.OrdinalIgnoreCase) ||
                            relative.Equals(".bundle-version", StringComparison.OrdinalIgnoreCase);
                        if (!(allowBundleMetadata && metadata) && !IsVsBundleRelativePath(relative)) return false;
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public static bool IsVsBundleRelativePath(string relative)
        {
            string normalized = (relative ?? string.Empty).Replace("\\", "/");
            string fileName = Path.GetFileName(normalized);
            string[] parts = normalized.Split('/');
            return
                parts.Length == 2 && parts[0].Equals("tool_modules", StringComparison.OrdinalIgnoreCase) &&
                    VsToolModuleFiles.Contains(fileName) ||
                parts.Length == 3 && parts[0].Equals("vendor", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Equals("wheels", StringComparison.OrdinalIgnoreCase) &&
                    (fileName.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) ||
                parts.Length == 1 && IsVsRuntimeRootFile(fileName);
        }

        public static bool IsVsRuntimeRootFile(string fileName)
        {
            switch ((fileName ?? string.Empty).ToLowerInvariant())
            {
                case "vs_server.py":
                case "vs_launcher.py":
                case "vs_bridge.py":
                case "vs_errors.py":
                case "output_models.py":
                case "tool_context.py":
                case "tool_runtime.py":
                case "debug_method_manifest.json":
                case "bootstrap.py":
                case "requirements.txt":
                case "vs_session.py":
                case "vs_mcp_app.py":
                case "vs_tool_registry.py":
                case "vs_trust.py":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsVsRuntimeDirectory(string relative)
        {
            string normalized = (relative ?? string.Empty).Replace("\\", "/").Trim('/');
            return normalized.Equals("tool_modules", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("vendor", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("vendor/wheels", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetRelativePath(string rootPath, string fullPath)
        {
            string root = NormalizeRoot(rootPath);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Bundle file is outside the expected source directory");
            }
            return full.Substring(rootPrefix.Length);
        }

        public static string ResolveDestination(string targetRoot, string relativePath)
        {
            string root = NormalizeRoot(targetRoot);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(rootPrefix, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Bundle destination is outside the staging directory");
            }
            return destination;
        }

        public static void EnsureNoReparsePoint(string sourceRoot, string file)
        {
            string root = NormalizeRoot(sourceRoot);
            string rootPrefix = root + Path.DirectorySeparatorChar;
            string current = Path.GetFullPath(file);
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("VS MCP bundle path escaped its source directory");
            }

            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("VS MCP bundle contains a reparse point");
                }
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current) ||
                    (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
                     !current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("VS MCP bundle path escaped its source directory");
                }
            }
        }

        private static string NormalizeRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("Root path is required", nameof(rootPath));
            }
            return Path.GetFullPath(rootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }
}
