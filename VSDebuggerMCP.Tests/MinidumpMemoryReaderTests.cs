using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP.Tests
{
    [TestClass]
    public class MinidumpMemoryReaderTests
    {
        [TestMethod]
        public void DumpMemoryRangeCatalogIsReadOnlyAndPausedOnly()
        {
            DebugMethodDescriptor descriptor = DebugMethodCatalog.Get("get_dump_memory_ranges");

            Assert.IsNotNull(descriptor);
            Assert.AreEqual("query", descriptor.Kind);
            Assert.AreEqual("debug.memory.read", descriptor.Capability);
            Assert.AreEqual("paused", descriptor.RequiredState);
            CollectionAssert.AreEqual(
                new[] { "startIndex", "maxNodes", "budgetMs" },
                descriptor.AllowedParameters.ToArray());
        }

        [TestMethod]
        public void UnifiedMemorySearchCatalogUsesAnOptionalAddressAndOpaqueContinuation()
        {
            DebugMethodDescriptor descriptor = DebugMethodCatalog.Get("search_memory");

            Assert.IsNotNull(descriptor);
            Assert.AreEqual("query", descriptor.Kind);
            Assert.AreEqual("debug.memory.read", descriptor.Capability);
            Assert.AreEqual("paused", descriptor.RequiredState);
            CollectionAssert.AreEqual(
                new[] { "pattern", "startAddress", "maxResults", "searchSize", "patternFormat", "startOffset", "continuation" },
                descriptor.AllowedParameters.ToArray());
            CollectionAssert.AreEqual(new[] { "pattern" }, descriptor.RequiredParameters.ToArray());
            Assert.IsNull(DebugMethodCatalog.Get("search_dump_memory"));

            var request = new DebugRequest
            {
                Method = "search_memory",
                Params = new Dictionary<string, object>
                {
                    ["pattern"] = "11 22 33",
                    ["maxResults"] = 10,
                    ["searchSize"] = 4096,
                    ["patternFormat"] = "hex",
                    ["continuation"] = "eyJ2ZXJzaW9uIjoxfQ",
                },
                Meta = new DebugRequestMetadata
                {
                    Kind = "query",
                    Capability = "debug.memory.read",
                },
            };
            DebugRequestSchemaValidator.Validate(request, 1000);

            request.Params["searchSize"] = 0;
            Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(request, 1000));
        }

        [TestMethod]
        public void Memory64ListMapsVirtualAddressesAndReadsContiguousRanges()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x41, 0x42, 0x43, 0x44 }),
                new DumpRange(0x1004, new byte[] { 0x45, 0x46, 0x47 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    Assert.AreEqual("memory64_list", reader.Source);
                    Assert.AreEqual(2, reader.Ranges.Count);
                    Assert.AreEqual((ulong)7, reader.TotalMappedBytes);
                    CollectionAssert.AreEqual(
                        new byte[] { 0x42, 0x43, 0x44, 0x45, 0x46, 0x47 },
                        reader.Read(0x1001, 6));
                    CollectionAssert.AreEqual(Array.Empty<byte>(), reader.Read(0x2000, 1));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void MemoryListIsUsedWhenMemory64ListIsAbsent()
        {
            string path = WriteMemoryListDump(
                new DumpRange(0x8000, new byte[] { 0x10, 0x20, 0x30 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    Assert.AreEqual("memory_list", reader.Source);
                    Assert.AreEqual((ulong)0x8000, reader.Ranges[0].StartAddress);
                    CollectionAssert.AreEqual(
                        new byte[] { 0x10, 0x20, 0x30 },
                        reader.Read(0x8000, 4));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void OverlappingMemoryRangesAreRejectedInsteadOfChoosingAnArbitraryMapping()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x01, 0x02 }),
                new DumpRange(0x1001, new byte[] { 0x03, 0x04 }));
            try
            {
                Assert.ThrowsExactly<InvalidDataException>(() => MinidumpMemoryReader.Open(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void SearchWalksMappedRangesWithAResumeCursorWithoutCrossingVirtualGaps()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0xAA, 0x11, 0x22 }),
                new DumpRange(0x1003, new byte[] { 0x33, 0x44 }),
                new DumpRange(0x2000, new byte[] { 0x11, 0x22, 0x33 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    MinidumpMemorySearchPage first = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 5,
                        rangeIndex: 0,
                        rangeOffset: 0);
                    CollectionAssert.AreEqual(new ulong[] { 0x1001 }, first.Matches);
                    Assert.IsTrue(first.Partial);
                    Assert.IsTrue(first.HasMore);
                    Assert.AreEqual(2, first.NextRangeIndex);
                    Assert.AreEqual((ulong)0, first.NextRangeOffset);

                    MinidumpMemorySearchPage second = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 5,
                        rangeIndex: first.NextRangeIndex.Value,
                        rangeOffset: first.NextRangeOffset.Value);
                    CollectionAssert.AreEqual(new ulong[] { 0x2000 }, second.Matches);
                    Assert.IsFalse(second.Partial);
                    Assert.IsFalse(second.HasMore);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void SearchResumeKeepsMatchThatCrossesTheScanBudgetBoundary()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x00, 0x00, 0x00, 0x11, 0x22, 0x33 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    MinidumpMemorySearchPage first = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 5,
                        rangeIndex: 0,
                        rangeOffset: 0);
                    Assert.AreEqual(0, first.Matches.Count);
                    Assert.IsTrue(first.HasMore);
                    Assert.AreEqual(0, first.NextRangeIndex);
                    Assert.AreEqual((ulong)3, first.NextRangeOffset);
                    Assert.AreEqual(2, first.NextCarryBytes);

                    MinidumpMemorySearchPage second = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 5,
                        rangeIndex: first.NextRangeIndex.Value,
                        rangeOffset: first.NextRangeOffset.Value,
                        continuationCarryBytes: first.NextCarryBytes);
                    CollectionAssert.AreEqual(new ulong[] { 0x1003 }, second.Matches);
                    Assert.IsFalse(second.HasMore);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void SearchResumeKeepsOverlappingMatchesAcrossContiguousRanges()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x11, 0x22 }),
                new DumpRange(0x1002, new byte[] { 0x33, 0x11, 0x22, 0x33 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    byte[] pattern = { 0x11, 0x22, 0x33 };
                    MinidumpMemorySearchPage first = reader.Search(
                        pattern,
                        maxResults: 10,
                        scanBytes: 2,
                        rangeIndex: 0,
                        rangeOffset: 0);
                    Assert.AreEqual(0, first.Matches.Count);
                    Assert.IsTrue(first.HasMore);
                    Assert.AreEqual(0, first.NextRangeIndex);
                    Assert.AreEqual((ulong)0, first.NextRangeOffset);
                    Assert.AreEqual(2, first.NextCarryBytes);

                    MinidumpMemorySearchPage second = reader.Search(
                        pattern,
                        maxResults: 10,
                        scanBytes: 2,
                        rangeIndex: first.NextRangeIndex.Value,
                        rangeOffset: first.NextRangeOffset.Value,
                        continuationCarryBytes: first.NextCarryBytes);
                    CollectionAssert.AreEqual(new ulong[] { 0x1000 }, second.Matches);
                    Assert.IsTrue(second.HasMore);

                    MinidumpMemorySearchPage third = reader.Search(
                        pattern,
                        maxResults: 10,
                        scanBytes: 2,
                        rangeIndex: second.NextRangeIndex.Value,
                        rangeOffset: second.NextRangeOffset.Value,
                        continuationCarryBytes: second.NextCarryBytes);
                    CollectionAssert.AreEqual(new ulong[] { 0x1003 }, third.Matches);
                    Assert.IsFalse(third.HasMore);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void SearchContinuationAfterResultLimitFindsOverlappingCandidates()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x11, 0x22, 0x33, 0x11, 0x22, 0x33 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    MinidumpMemorySearchPage first = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 1,
                        scanBytes: 6,
                        rangeIndex: 0,
                        rangeOffset: 0);
                    CollectionAssert.AreEqual(new ulong[] { 0x1000 }, first.Matches);
                    Assert.IsTrue(first.Truncated);
                    Assert.IsTrue(first.HasMore);
                    Assert.AreEqual(6, first.BytesAttempted);
                    Assert.AreEqual(6, first.BytesSearched);
                    Assert.AreEqual(0, first.NextRangeIndex);
                    Assert.AreEqual((ulong)1, first.NextRangeOffset);

                    MinidumpMemorySearchPage second = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 5,
                        rangeIndex: first.NextRangeIndex.Value,
                        rangeOffset: first.NextRangeOffset.Value);
                    CollectionAssert.AreEqual(new ulong[] { 0x1003 }, second.Matches);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void SearchDoesNotTreatDisjointVirtualRangesAsContiguousMemory()
        {
            string path = WriteMemory64Dump(
                new DumpRange(0x1000, new byte[] { 0x11, 0x22 }),
                new DumpRange(0x2000, new byte[] { 0x33 }));
            try
            {
                using (MinidumpMemoryReader reader = MinidumpMemoryReader.Open(path))
                {
                    MinidumpMemorySearchPage page = reader.Search(
                        new byte[] { 0x11, 0x22, 0x33 },
                        maxResults: 10,
                        scanBytes: 3,
                        rangeIndex: 0,
                        rangeOffset: 0);
                    Assert.AreEqual(0, page.Matches.Count);
                    Assert.IsFalse(page.HasMore);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static string WriteMemory64Dump(params DumpRange[] ranges)
        {
            const uint directoryRva = 0x40;
            const uint streamRva = 0x100;
            const ulong dataRva = 0x200;
            string path = Path.Combine(Path.GetTempPath(), "aidb-minidump-memory-" + Guid.NewGuid().ToString("N") + ".dmp");
            ulong totalDataBytes = TotalDataBytes(ranges);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                stream.SetLength(checked((long)(dataRva + totalDataBytes)));
                stream.Position = 0;
                WriteHeader(writer, directoryRva);
                stream.Position = directoryRva;
                writer.Write(9U); // Memory64ListStream
                writer.Write(checked((uint)(16 + ranges.Length * 16)));
                writer.Write(streamRva);
                stream.Position = streamRva;
                writer.Write((ulong)ranges.Length);
                writer.Write(dataRva);
                foreach (DumpRange range in ranges)
                {
                    writer.Write(range.StartAddress);
                    writer.Write((ulong)range.Bytes.Length);
                }
                stream.Position = (long)dataRva;
                foreach (DumpRange range in ranges)
                {
                    writer.Write(range.Bytes);
                }
            }
            return path;
        }

        private static string WriteMemoryListDump(params DumpRange[] ranges)
        {
            const uint directoryRva = 0x40;
            const uint streamRva = 0x100;
            const uint dataRva = 0x200;
            string path = Path.Combine(Path.GetTempPath(), "aidb-minidump-memory-" + Guid.NewGuid().ToString("N") + ".dmp");
            ulong totalDataBytes = TotalDataBytes(ranges);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                stream.SetLength(checked((long)(dataRva + totalDataBytes)));
                stream.Position = 0;
                WriteHeader(writer, directoryRva);
                stream.Position = directoryRva;
                writer.Write(5U); // MemoryListStream
                writer.Write(checked((uint)(4 + ranges.Length * 16)));
                writer.Write(streamRva);
                stream.Position = streamRva;
                writer.Write((uint)ranges.Length);
                uint nextDataRva = dataRva;
                foreach (DumpRange range in ranges)
                {
                    writer.Write(range.StartAddress);
                    writer.Write((uint)range.Bytes.Length);
                    writer.Write(nextDataRva);
                    nextDataRva += (uint)range.Bytes.Length;
                }
                stream.Position = dataRva;
                foreach (DumpRange range in ranges)
                {
                    writer.Write(range.Bytes);
                }
            }
            return path;
        }

        private static void WriteHeader(BinaryWriter writer, uint directoryRva)
        {
            writer.Write(0x504D444DU); // MDMP
            writer.Write(0U); // Version
            writer.Write(1U); // NumberOfStreams
            writer.Write(directoryRva);
            writer.Write(0U); // Checksum
            writer.Write(0U); // Timestamp
            writer.Write(0UL); // Flags
        }

        private static ulong TotalDataBytes(IEnumerable<DumpRange> ranges)
        {
            ulong total = 0;
            foreach (DumpRange range in ranges)
            {
                total += (ulong)range.Bytes.Length;
            }
            return total;
        }

        private sealed class DumpRange
        {
            internal DumpRange(ulong startAddress, byte[] bytes)
            {
                StartAddress = startAddress;
                Bytes = bytes;
            }

            internal ulong StartAddress { get; }

            internal byte[] Bytes { get; }
        }
    }
}
