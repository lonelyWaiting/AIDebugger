using System;
using System.Collections.Generic;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class ModuleSnapshotPage
    {
        internal List<ModuleInfo> Modules { get; } = new List<ModuleInfo>();
        internal int NextIndex { get; set; }
        internal bool HasMore { get; set; }
        internal bool ResultBudgetExceeded { get; set; }
    }

    internal static class ModuleSnapshotPager
    {
        internal static ModuleSnapshotPage Build(
            IReadOnlyList<ModuleInfo> snapshot,
            int startIndex,
            int maxNodes,
            ModuleCaptureBudget budget)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (budget == null) throw new ArgumentNullException(nameof(budget));
            if (startIndex < 0 || startIndex > snapshot.Count)
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (maxNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxNodes));

            var page = new ModuleSnapshotPage { NextIndex = startIndex };
            int index = startIndex;
            for (; index < snapshot.Count && page.Modules.Count < maxNodes; index++)
            {
                ModuleInfo module = Clone(snapshot[index]);
                if (!budget.TryConsume(module))
                {
                    page.ResultBudgetExceeded = true;
                    break;
                }
                page.Modules.Add(module);
            }
            page.NextIndex = index;
            page.HasMore = index < snapshot.Count;
            return page;
        }

        private static ModuleInfo Clone(ModuleInfo module)
        {
            return new ModuleInfo
            {
                Name = module.Name,
                Path = module.Path,
                SymbolsLoaded = module.SymbolsLoaded,
                SymbolPath = module.SymbolPath,
                Version = module.Version,
                Timestamp = module.Timestamp,
                Address = module.Address,
                Size = module.Size,
                SizeBytes = module.SizeBytes,
                Is64Bit = module.Is64Bit,
                Architecture = module.Architecture,
                IsSystemModule = module.IsSystemModule,
                Truncated = module.Truncated,
            };
        }
    }
}
