using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VSDebuggerMCP
{
    internal static class BundleSignature
    {
        public static string Compute(string root, IEnumerable<string> files)
        {
            var records = new List<string>();
            foreach (string file in files)
            {
                string relative = BundlePathPolicy.GetRelativePath(root, file).Replace("\\", "/");
                using (var stream = File.OpenRead(file))
                using (var fileSha = SHA256.Create())
                {
                    string digest = BitConverter.ToString(fileSha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    records.Add(relative + ":" + digest);
                }
            }
            records.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", records));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
