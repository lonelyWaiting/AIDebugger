using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VSDebuggerMCP.Services
{
    /// <summary>
    /// Bounded, read-only mapper for the memory streams embedded in a Windows
    /// minidump.  It deliberately accepts only the dump selected by the active
    /// Visual Studio session; callers must not use it as an arbitrary file reader.
    /// </summary>
    internal sealed class MinidumpMemoryReader : IDisposable
    {
        private const uint MinidumpSignature = 0x504D444D; // "MDMP"
        private const uint MemoryListStreamType = 5;
        private const uint Memory64ListStreamType = 9;
        private const int MinidumpHeaderSize = 32;
        private const int DirectoryEntrySize = 12;
        private const int MemoryListDescriptorSize = 16;
        private const int Memory64DescriptorSize = 16;
        private const int MaxDirectoryEntries = 4096;
        private const int MaxMemoryRanges = 1_000_000;

        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private readonly List<MinidumpMemoryRange> _ranges;

        private MinidumpMemoryReader(FileStream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            _ranges = ReadRanges(out string source);
            Source = source;
            TotalMappedBytes = SumMappedBytes(_ranges);
        }

        internal string Source { get; }

        internal ulong TotalMappedBytes { get; }

        internal IReadOnlyList<MinidumpMemoryRange> Ranges => _ranges;

        internal static MinidumpMemoryReader Open(string dumpPath)
        {
            if (string.IsNullOrWhiteSpace(dumpPath))
            {
                throw new ArgumentException("A minidump path is required.", nameof(dumpPath));
            }

            var stream = new FileStream(
                dumpPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            try
            {
                return new MinidumpMemoryReader(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal byte[] Read(ulong address, int maxBytes)
        {
            if (maxBytes <= 0)
            {
                return Array.Empty<byte>();
            }

            int rangeIndex = FindRangeIndex(address);
            if (rangeIndex < 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new List<byte>(maxBytes);
            ulong currentAddress = address;
            while (rangeIndex < _ranges.Count && bytes.Count < maxBytes)
            {
                MinidumpMemoryRange range = _ranges[rangeIndex];
                if (currentAddress < range.StartAddress || currentAddress >= range.EndAddress)
                {
                    break;
                }

                ulong offset = currentAddress - range.StartAddress;
                ulong remainingRangeBytes = range.Size - offset;
                int requested = (int)Math.Min(
                    (ulong)(maxBytes - bytes.Count),
                    remainingRangeBytes);
                byte[] portion = ReadRange(range, offset, requested);
                if (portion.Length == 0)
                {
                    break;
                }

                bytes.AddRange(portion);
                if (portion.Length < requested)
                {
                    break;
                }

                currentAddress += (ulong)portion.Length;
                rangeIndex++;
                if (rangeIndex >= _ranges.Count ||
                    _ranges[rangeIndex].StartAddress != currentAddress)
                {
                    break;
                }
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// Searches only virtual-address ranges physically present in this dump.
        /// The caller owns the byte and result budgets; the returned range cursor
        /// is sufficient to resume without treating unmapped virtual gaps as data.
        /// </summary>
        internal MinidumpMemorySearchPage Search(
            byte[] pattern,
            int maxResults,
            int scanBytes,
            int rangeIndex,
            ulong rangeOffset,
            int continuationCarryBytes = 0)
        {
            if (pattern == null || pattern.Length == 0)
            {
                throw new ArgumentException("A non-empty search pattern is required.", nameof(pattern));
            }
            if (maxResults <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            }
            if (scanBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(scanBytes));
            }
            if (continuationCarryBytes < 0 || continuationCarryBytes >= pattern.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(continuationCarryBytes));
            }
            if (rangeIndex < 0 || rangeIndex > _ranges.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(rangeIndex));
            }
            if (rangeIndex == _ranges.Count)
            {
                if (rangeOffset != 0 || continuationCarryBytes != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(rangeOffset));
                }
                return new MinidumpMemorySearchPage(_ranges.Count, rangeIndex, rangeOffset);
            }
            if (rangeOffset > _ranges[rangeIndex].Size)
            {
                throw new ArgumentOutOfRangeException(nameof(rangeOffset));
            }

            var result = new MinidumpMemorySearchPage(_ranges.Count, rangeIndex, rangeOffset);
            int currentRangeIndex = rangeIndex;
            ulong currentRangeOffset = rangeOffset;
            int remainingBudget = scanBytes;
            var carry = new List<byte>(Math.Max(0, pattern.Length - 1));
            ulong previousChunkEndAddress = 0;
            bool hasPreviousChunk = false;
            ulong nextCandidateAddress = 0;
            bool hasNextCandidateAddress = false;
            if (continuationCarryBytes > 0)
            {
                ulong carryStartAddress;
                List<byte> restoredCarry;
                if (!TryReadContinuationCarry(
                    continuationCarryBytes,
                    ref currentRangeIndex,
                    ref currentRangeOffset,
                    out carryStartAddress,
                    out restoredCarry))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(continuationCarryBytes),
                        "The continuation overlap is not present in contiguous mapped memory.");
                }

                carry = restoredCarry;
                previousChunkEndAddress = checked(carryStartAddress + (ulong)carry.Count);
                hasPreviousChunk = true;
                nextCandidateAddress = carryStartAddress;
                hasNextCandidateAddress = true;
            }

            while (currentRangeIndex < _ranges.Count && remainingBudget > 0)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                MinidumpMemoryRange range = _ranges[currentRangeIndex];
                if (currentRangeOffset >= range.Size)
                {
                    currentRangeIndex++;
                    currentRangeOffset = 0;
                    continue;
                }

                ulong chunkAddress = range.StartAddress + currentRangeOffset;
                bool contiguousWithPreviousChunk = hasPreviousChunk &&
                    previousChunkEndAddress == chunkAddress;
                if (!contiguousWithPreviousChunk)
                {
                    carry.Clear();
                    hasNextCandidateAddress = false;
                }

                int requested = (int)Math.Min(
                    (ulong)Math.Min(MemorySearchChunkBytes, remainingBudget),
                    range.Size - currentRangeOffset);
                result.BytesAttempted += requested;
                byte[] chunk = ReadRange(range, currentRangeOffset, requested);
                if (chunk.Length == 0)
                {
                    result.Partial = true;
                    result.Error = "Could not read a mapped minidump memory chunk.";
                    SetSearchContinuation(
                        result,
                        currentRangeIndex,
                        currentRangeOffset,
                        carry.Count,
                        previousChunkEndAddress);
                    return result;
                }

                currentRangeOffset += (ulong)chunk.Length;
                previousChunkEndAddress = checked(chunkAddress + (ulong)chunk.Length);
                hasPreviousChunk = true;
                result.BytesSearched += chunk.Length;
                remainingBudget -= chunk.Length;

                int previousCarryCount = carry.Count;
                ulong combinedStartAddress = chunkAddress - (ulong)previousCarryCount;
                var combined = new List<byte>(previousCarryCount + chunk.Length);
                combined.AddRange(carry);
                combined.AddRange(chunk);
                int candidateEndExclusive = Math.Max(0, combined.Count - pattern.Length + 1);
                int candidateStart = 0;
                if (hasNextCandidateAddress && nextCandidateAddress > combinedStartAddress)
                {
                    ulong candidateOffset = nextCandidateAddress - combinedStartAddress;
                    candidateStart = candidateOffset >= (ulong)candidateEndExclusive
                        ? candidateEndExclusive
                        : (int)candidateOffset;
                }

                for (int candidate = candidateStart;
                    candidate < candidateEndExclusive;
                    candidate++)
                {
                    if (!MatchesAt(combined, candidate, pattern))
                    {
                        continue;
                    }

                    ulong matchAddress = combinedStartAddress + (ulong)candidate;
                    result.Matches.Add(matchAddress);
                    if (result.Matches.Count >= maxResults)
                    {
                        result.Truncated = true;
                        result.Partial = true;
                        SetContinuationAtAddress(result, checked(matchAddress + 1));
                        return result;
                    }
                }

                nextCandidateAddress = combinedStartAddress + (ulong)candidateEndExclusive;
                hasNextCandidateAddress = true;
                int carryCount = Math.Min(pattern.Length - 1, combined.Count);
                carry = carryCount == 0
                    ? new List<byte>()
                    : combined.Skip(combined.Count - carryCount).Take(carryCount).ToList();

                if (chunk.Length < requested)
                {
                    result.Partial = true;
                    result.Error = "Minidump memory chunk ended before its mapped length.";
                    SetSearchContinuation(
                        result,
                        currentRangeIndex,
                        currentRangeOffset,
                        carry.Count,
                        previousChunkEndAddress);
                    return result;
                }
            }

            SetSearchContinuation(
                result,
                currentRangeIndex,
                currentRangeOffset,
                carry.Count,
                previousChunkEndAddress);
            result.Partial = result.HasMore;
            return result;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _stream?.Dispose();
        }

        private List<MinidumpMemoryRange> ReadRanges(out string source)
        {
            source = null;
            if (_stream.Length < MinidumpHeaderSize)
            {
                throw new InvalidDataException("Minidump is shorter than the fixed header.");
            }

            _stream.Position = 0;
            if (_reader.ReadUInt32() != MinidumpSignature)
            {
                throw new InvalidDataException("File does not have a minidump signature.");
            }

            _reader.ReadUInt32(); // Version
            uint streamCount = _reader.ReadUInt32();
            uint directoryRva = _reader.ReadUInt32();
            _reader.ReadUInt32(); // Checksum
            _reader.ReadUInt32(); // Timestamp
            _reader.ReadUInt64(); // Flags

            if (streamCount == 0 || streamCount > MaxDirectoryEntries || directoryRva == 0)
            {
                throw new InvalidDataException("Minidump stream directory is invalid.");
            }

            EnsureFileRange(directoryRva, checked((ulong)streamCount * DirectoryEntrySize));
            var directories = new List<MinidumpDirectory>((int)streamCount);
            _stream.Position = directoryRva;
            for (uint index = 0; index < streamCount; index++)
            {
                ThrowIfCancellationRequested(index);
                directories.Add(new MinidumpDirectory
                {
                    StreamType = _reader.ReadUInt32(),
                    DataSize = _reader.ReadUInt32(),
                    Rva = _reader.ReadUInt32(),
                });
            }

            MinidumpDirectory memory64 = directories.FirstOrDefault(
                entry => entry.StreamType == Memory64ListStreamType);
            if (memory64.StreamType == Memory64ListStreamType)
            {
                source = "memory64_list";
                return NormalizeRanges(ReadMemory64Ranges(memory64));
            }

            MinidumpDirectory memoryList = directories.FirstOrDefault(
                entry => entry.StreamType == MemoryListStreamType);
            if (memoryList.StreamType == MemoryListStreamType)
            {
                source = "memory_list";
                return NormalizeRanges(ReadMemoryListRanges(memoryList));
            }

            throw new InvalidDataException("Minidump does not contain a memory-list stream.");
        }

        private List<MinidumpMemoryRange> ReadMemory64Ranges(MinidumpDirectory directory)
        {
            if (directory.DataSize < 16 || directory.Rva == 0)
            {
                throw new InvalidDataException("Minidump Memory64List stream is invalid.");
            }

            EnsureFileRange(directory.Rva, directory.DataSize);
            _stream.Position = directory.Rva;
            ulong rangeCount = _reader.ReadUInt64();
            ulong baseFileOffset = _reader.ReadUInt64();
            if (rangeCount > MaxMemoryRanges)
            {
                throw new InvalidDataException("Minidump Memory64List range count exceeds the safety limit.");
            }

            ulong tableBytes = checked(16UL + rangeCount * Memory64DescriptorSize);
            if (tableBytes > directory.DataSize)
            {
                throw new InvalidDataException("Minidump Memory64List stream is truncated.");
            }

            var ranges = new List<MinidumpMemoryRange>((int)rangeCount);
            ulong dataFileOffset = baseFileOffset;
            for (ulong index = 0; index < rangeCount; index++)
            {
                ThrowIfCancellationRequested(index);
                ulong startAddress = _reader.ReadUInt64();
                ulong size = _reader.ReadUInt64();
                if (size == 0)
                {
                    continue;
                }

                EnsureAddressRange(startAddress, size);
                EnsureFileRange(dataFileOffset, size);
                ranges.Add(new MinidumpMemoryRange(startAddress, size, dataFileOffset));
                dataFileOffset = checked(dataFileOffset + size);
            }
            return ranges;
        }

        private List<MinidumpMemoryRange> ReadMemoryListRanges(MinidumpDirectory directory)
        {
            if (directory.DataSize < 4 || directory.Rva == 0)
            {
                throw new InvalidDataException("Minidump MemoryList stream is invalid.");
            }

            EnsureFileRange(directory.Rva, directory.DataSize);
            _stream.Position = directory.Rva;
            uint rangeCount = _reader.ReadUInt32();
            if (rangeCount > MaxMemoryRanges)
            {
                throw new InvalidDataException("Minidump MemoryList range count exceeds the safety limit.");
            }

            ulong tableBytes = checked(4UL + (ulong)rangeCount * MemoryListDescriptorSize);
            if (tableBytes > directory.DataSize)
            {
                throw new InvalidDataException("Minidump MemoryList stream is truncated.");
            }

            var ranges = new List<MinidumpMemoryRange>((int)rangeCount);
            for (uint index = 0; index < rangeCount; index++)
            {
                ThrowIfCancellationRequested(index);
                ulong startAddress = _reader.ReadUInt64();
                uint size = _reader.ReadUInt32();
                uint fileOffset = _reader.ReadUInt32();
                if (size == 0)
                {
                    continue;
                }

                EnsureAddressRange(startAddress, size);
                EnsureFileRange(fileOffset, size);
                ranges.Add(new MinidumpMemoryRange(startAddress, size, fileOffset));
            }
            return ranges;
        }

        private static List<MinidumpMemoryRange> NormalizeRanges(IEnumerable<MinidumpMemoryRange> ranges)
        {
            List<MinidumpMemoryRange> ordered = ranges
                .OrderBy(range => range.StartAddress)
                .ToList();
            for (int index = 1; index < ordered.Count; index++)
            {
                if (ordered[index].StartAddress < ordered[index - 1].EndAddress)
                {
                    throw new InvalidDataException("Minidump memory ranges overlap and cannot be mapped safely.");
                }
            }
            return ordered;
        }

        private byte[] ReadRange(MinidumpMemoryRange range, ulong offset, int requestedBytes)
        {
            if (requestedBytes <= 0 || offset >= range.Size)
            {
                return Array.Empty<byte>();
            }

            int count = (int)Math.Min((ulong)requestedBytes, range.Size - offset);
            ulong fileOffset = checked(range.FileOffset + offset);
            EnsureFileRange(fileOffset, (ulong)count);
            _stream.Position = checked((long)fileOffset);
            byte[] buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }
            if (total == buffer.Length)
            {
                return buffer;
            }
            return buffer.Take(total).ToArray();
        }

        private int FindRangeIndex(ulong address)
        {
            int low = 0;
            int high = _ranges.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                MinidumpMemoryRange range = _ranges[middle];
                if (address < range.StartAddress)
                {
                    high = middle - 1;
                }
                else if (address >= range.EndAddress)
                {
                    low = middle + 1;
                }
                else
                {
                    return middle;
                }
            }
            return -1;
        }

        private static bool MatchesAt(IReadOnlyList<byte> bytes, int offset, byte[] pattern)
        {
            for (int index = 0; index < pattern.Length; index++)
            {
                if (bytes[offset + index] != pattern[index])
                {
                    return false;
                }
            }
            return true;
        }

        private void SetContinuationAtAddress(MinidumpMemorySearchPage result, ulong address)
        {
            int index = FindRangeIndex(address);
            if (index >= 0)
            {
                SetContinuation(result, index, address - _ranges[index].StartAddress);
                return;
            }

            // A match can end exactly at a range boundary.  Resume at the next
            // mapped range rather than inventing an address inside an unmapped gap.
            for (int candidate = 0; candidate < _ranges.Count; candidate++)
            {
                if (_ranges[candidate].StartAddress >= address)
                {
                    SetContinuation(result, candidate, 0);
                    return;
                }
            }
            SetContinuation(result, _ranges.Count, 0);
        }

        /// <summary>
        /// Preserves the trailing bytes required to evaluate the first candidate
        /// of the next bounded page without serializing dump data into the cursor.
        /// The cursor points at the start of that tail and records its length;
        /// the same trusted dump is used to reconstruct it on resume.
        /// </summary>
        private void SetSearchContinuation(
            MinidumpMemorySearchPage result,
            int rangeIndex,
            ulong rangeOffset,
            int carryBytes,
            ulong carryEndAddress)
        {
            int nextRangeIndex = rangeIndex;
            ulong nextRangeOffset = rangeOffset;
            if (!NormalizeContinuationPosition(ref nextRangeIndex, ref nextRangeOffset))
            {
                SetContinuation(result, _ranges.Count, 0);
                return;
            }

            ulong nextAddress = checked(_ranges[nextRangeIndex].StartAddress + nextRangeOffset);
            if (carryBytes > 0 && carryEndAddress == nextAddress)
            {
                ulong carryStartAddress = checked(carryEndAddress - (ulong)carryBytes);
                int carryRangeIndex = FindRangeIndex(carryStartAddress);
                if (carryRangeIndex >= 0)
                {
                    result.HasMore = true;
                    result.NextRangeIndex = carryRangeIndex;
                    result.NextRangeOffset = carryStartAddress - _ranges[carryRangeIndex].StartAddress;
                    result.NextCarryBytes = carryBytes;
                    return;
                }
            }

            SetContinuation(result, nextRangeIndex, nextRangeOffset);
        }

        private void SetContinuation(
            MinidumpMemorySearchPage result,
            int rangeIndex,
            ulong rangeOffset)
        {
            if (!NormalizeContinuationPosition(ref rangeIndex, ref rangeOffset))
            {
                result.HasMore = false;
                result.NextRangeIndex = null;
                result.NextRangeOffset = null;
                result.NextCarryBytes = 0;
                return;
            }

            result.HasMore = true;
            result.NextRangeIndex = rangeIndex;
            result.NextRangeOffset = rangeOffset;
            result.NextCarryBytes = 0;
        }

        private bool NormalizeContinuationPosition(ref int rangeIndex, ref ulong rangeOffset)
        {
            while (rangeIndex < _ranges.Count && rangeOffset >= _ranges[rangeIndex].Size)
            {
                rangeIndex++;
                rangeOffset = 0;
            }

            return rangeIndex < _ranges.Count;
        }

        private bool TryReadContinuationCarry(
            int carryBytes,
            ref int rangeIndex,
            ref ulong rangeOffset,
            out ulong carryStartAddress,
            out List<byte> carry)
        {
            carryStartAddress = 0;
            carry = new List<byte>(carryBytes);
            if (rangeIndex < 0 || rangeIndex >= _ranges.Count ||
                rangeOffset >= _ranges[rangeIndex].Size)
            {
                return false;
            }

            carryStartAddress = checked(_ranges[rangeIndex].StartAddress + rangeOffset);
            while (carry.Count < carryBytes)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
                MinidumpMemoryRange range = _ranges[rangeIndex];
                int requested = (int)Math.Min(
                    (ulong)(carryBytes - carry.Count),
                    range.Size - rangeOffset);
                byte[] bytes = ReadRange(range, rangeOffset, requested);
                if (bytes.Length != requested)
                {
                    return false;
                }

                carry.AddRange(bytes);
                rangeOffset += (ulong)requested;
                if (carry.Count == carryBytes)
                {
                    return true;
                }

                int nextRangeIndex = rangeIndex + 1;
                if (nextRangeIndex >= _ranges.Count ||
                    range.EndAddress != _ranges[nextRangeIndex].StartAddress)
                {
                    return false;
                }

                rangeIndex = nextRangeIndex;
                rangeOffset = 0;
            }

            return true;
        }

        private void EnsureFileRange(ulong offset, ulong size)
        {
            if (offset > long.MaxValue || size > long.MaxValue ||
                offset > (ulong)_stream.Length ||
                size > (ulong)_stream.Length - offset)
            {
                throw new InvalidDataException("Minidump references bytes outside the dump file.");
            }
        }

        private static void EnsureAddressRange(ulong address, ulong size)
        {
            if (size > ulong.MaxValue - address)
            {
                throw new InvalidDataException("Minidump memory range overflows the virtual address space.");
            }
        }

        private static ulong SumMappedBytes(IEnumerable<MinidumpMemoryRange> ranges)
        {
            ulong total = 0;
            foreach (MinidumpMemoryRange range in ranges)
            {
                total = checked(total + range.Size);
            }
            return total;
        }

        private static void ThrowIfCancellationRequested(ulong index)
        {
            if ((index & 0xFF) == 0)
            {
                DebuggerRequestContext.ThrowIfCancellationOrDeadlineExceeded();
            }
        }

        private const int MemorySearchChunkBytes = 4096;

        private struct MinidumpDirectory
        {
            internal uint StreamType { get; set; }
            internal uint DataSize { get; set; }
            internal uint Rva { get; set; }
        }
    }

    internal sealed class MinidumpMemoryRange
    {
        internal MinidumpMemoryRange(ulong startAddress, ulong size, ulong fileOffset)
        {
            StartAddress = startAddress;
            Size = size;
            FileOffset = fileOffset;
        }

        internal ulong StartAddress { get; }

        internal ulong Size { get; }

        internal ulong EndAddress => StartAddress + Size;

        internal ulong FileOffset { get; }
    }

    internal sealed class MinidumpMemorySearchPage
    {
        internal MinidumpMemorySearchPage(int rangeCount, int startRangeIndex, ulong startRangeOffset)
        {
            RangeCount = rangeCount;
            StartRangeIndex = startRangeIndex;
            StartRangeOffset = startRangeOffset;
        }

        internal int RangeCount { get; }

        internal int StartRangeIndex { get; }

        internal ulong StartRangeOffset { get; }

        internal List<ulong> Matches { get; } = new List<ulong>();

        internal int BytesSearched { get; set; }

        internal int BytesAttempted { get; set; }

        internal bool Partial { get; set; }

        internal bool Truncated { get; set; }

        internal bool HasMore { get; set; }

        internal int? NextRangeIndex { get; set; }

        internal ulong? NextRangeOffset { get; set; }

        internal int NextCarryBytes { get; set; }

        internal string Error { get; set; }
    }
}
