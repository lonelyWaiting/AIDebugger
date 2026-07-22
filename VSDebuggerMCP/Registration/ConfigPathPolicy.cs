using System;
using System.Collections.Generic;
using System.IO;

namespace VSDebuggerMCP
{
    internal static class ConfigPathPolicy
    {
        public static string EnsureSafeFilePath(string path, bool createDirectory)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Config path has no parent directory");
            }

            if (createDirectory)
            {
                EnsurePlainDirectoryTree(directory);
            }
            else
            {
                ValidatePlainDirectoryTree(directory);
            }
            ValidateFinalFileEntry(fullPath);
            return fullPath;
        }

        public static string EnsurePlainDirectoryTree(string directory)
        {
            string fullDirectory = NormalizeDirectory(directory);
            string root = Path.GetPathRoot(fullDirectory);
            if (string.IsNullOrEmpty(root) || !BundlePathPolicy.IsPlainDirectory(root))
            {
                throw new InvalidOperationException("Config directory root is missing or is a reparse point");
            }

            var missingOrExisting = new Stack<string>();
            string current = fullDirectory;
            while (!string.Equals(current, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                missingOrExisting.Push(Path.GetFileName(current));
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current))
                {
                    throw new InvalidOperationException("Config directory escaped its filesystem root");
                }
            }

            current = root;
            while (missingOrExisting.Count != 0)
            {
                string component = missingOrExisting.Pop();
                string child = Path.GetFullPath(Path.Combine(current, component));
                FileAttributes attributes;
                if (TryGetAttributes(child, out attributes))
                {
                    if ((attributes & FileAttributes.Directory) == 0 ||
                        (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Config directory contains a non-directory or reparse point");
                    }
                }
                else
                {
                    Directory.CreateDirectory(child);
                    if (!BundlePathPolicy.IsPlainDirectory(child))
                    {
                        throw new InvalidOperationException("Config directory creation produced a reparse point");
                    }
                }
                current = child;
            }

            ValidatePlainDirectoryTree(fullDirectory);
            return fullDirectory;
        }

        public static void ValidatePlainDirectoryTree(string directory)
        {
            string current = NormalizeDirectory(directory);
            string root = Path.GetPathRoot(current);
            if (string.IsNullOrEmpty(root))
            {
                throw new InvalidOperationException("Config directory has no filesystem root");
            }
            while (true)
            {
                if (!BundlePathPolicy.IsPlainDirectory(current))
                {
                    throw new InvalidOperationException("Config directory is missing or contains a reparse point");
                }
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current))
                {
                    throw new InvalidOperationException("Config directory escaped its filesystem root");
                }
            }
        }

        public static void ValidateFinalFileEntry(string path)
        {
            string fullPath = Path.GetFullPath(path);
            FileAttributes attributes;
            if (TryGetAttributes(fullPath, out attributes) &&
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException("Config file is a directory or reparse point");
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

        private static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Directory is required", nameof(directory));
            }
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
