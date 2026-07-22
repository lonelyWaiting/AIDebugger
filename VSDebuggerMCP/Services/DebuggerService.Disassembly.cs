using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using EnvDTE;
using EnvDTE80;
using EnvDTE100;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP
{
    public partial class DebuggerService
    {
        #region Disassembly

        /// <summary>
        /// Get disassembly from the exact current instruction pointer using Iced.
        /// Preceding x86/x64 boundaries are not guessed without a trusted symbol boundary.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Captures only scalar debugger state on UI and performs memory decoding off-thread.")]
        public DisassemblyResult GetDisassembly(int linesBefore = 20, int linesAfter = 20)
        {
            TargetMachineArchitecture architecture = DetectTargetArchitecture();
            ThrowIfDisassemblyArchitectureUnsupported(architecture);
            DisassemblyCaptureHeader header = ExecuteOnMainThread(
                () => CaptureDisassemblyHeader(architecture));
            if (header == null || header.InstructionPointer == 0)
            {
                return new DisassemblyResult { Error = "Could not determine instruction pointer" };
            }
            int before = Math.Max(0, Math.Min(linesBefore, 256));
            int after = Math.Max(0, Math.Min(linesAfter, 256));
            DisassemblyResult result = BuildDisassemblyFromMemory(
                header.InstructionPointer,
                Math.Max(1, after + 1),
                header);
            result.FunctionName = header.FunctionName;
            result.SourceFile = string.Empty;
            result.PrecedingInstructionsStatus = before > 0
                ? "unavailable_without_trusted_instruction_boundary"
                : "not_requested";
            return result;
        }

        /// <summary>
        /// Get disassembly starting at a specific address using the Iced library.
        /// Automatically handles thread context issues by trying to switch to user code thread if needed.
        /// </summary>
        [SuppressMessage("Usage", "VSTHRD010", Justification = "Captures only scalar debugger state on UI and performs memory decoding off-thread.")]
        public DisassemblyResult GetDisassemblyAt(string address, int count = 50)
        {
            if (!TryParseAddressLiteral(address, out ulong baseAddress))
            {
                return new DisassemblyResult { Error = "Invalid address literal" };
            }
            TargetMachineArchitecture architecture = DetectTargetArchitecture();
            ThrowIfDisassemblyArchitectureUnsupported(architecture);
            DisassemblyCaptureHeader header = ExecuteOnMainThread(
                () => CaptureDisassemblyHeader(architecture));
            if (header == null)
            {
                return new DisassemblyResult { Error = "Not in break mode" };
            }
            return BuildDisassemblyFromMemory(baseAddress, Math.Max(1, Math.Min(count, 256)), header);
        }

        private DisassemblyCaptureHeader CaptureDisassemblyHeader(
            TargetMachineArchitecture architecture)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var debugger = Debugger;
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return null;
            }
            bool is64Bit = architecture == TargetMachineArchitecture.X64;
            EnvDTE.Expression ip = debugger.GetExpression(
                is64Bit ? "@rip" : "@eip", false, 250);
            return new DisassemblyCaptureHeader
            {
                Architecture = architecture,
                Is64Bit = is64Bit,
                InstructionPointer = ip != null && ip.IsValidValue ? ParseAddress(ip.Value) : 0,
                FunctionName = debugger.CurrentStackFrame?.FunctionName,
                SnapshotToken = CreateDebuggerSnapshotToken(debugger)
            };
        }

        [SuppressMessage("Usage", "VSTHRD010", Justification = "ReadMemory self-marshals bounded validation/fallback slices; decoding is off-thread.")]
        private DisassemblyResult BuildDisassemblyFromMemory(
            ulong baseAddress,
            int count,
            DisassemblyCaptureHeader header)
        {
            string address = header.Is64Bit ? $"0x{baseAddress:X16}" : $"0x{baseAddress:X8}";
            string currentAddress = header.Is64Bit
                ? $"0x{header.InstructionPointer:X16}"
                : $"0x{header.InstructionPointer:X8}";
            int requestedBytes = Math.Min(4096, checked(count * 15));
            MemoryResult memory = ReadMemory(address, requestedBytes);
            var result = new DisassemblyResult
            {
                CurrentAddress = currentAddress,
                StartAddress = address,
                Architecture = TargetArchitecturePolicy.ToProtocolName(header.Architecture),
                ArchitectureStatus = "supported",
                Is64Bit = header.Is64Bit,
                RequestedInstructions = count,
                RequestedBytes = requestedBytes,
            };
            if (string.Equals(memory.Status, "unstable", StringComparison.Ordinal) ||
                !IsDisassemblySnapshotCurrent(header))
            {
                result.Error = "Debugger context changed before disassembly decoding; captured bytes were discarded";
                return result;
            }
            if (memory.Bytes == null || memory.Bytes.Count == 0)
            {
                result.Error = memory.Error ?? $"Could not read memory at {address}";
                return result;
            }
            byte[] memoryBytes = memory.Bytes.ToArray();
            List<DisassembledInstruction> instructions = _disasmService.Disassemble(
                memoryBytes,
                baseAddress,
                header.Is64Bit,
                count);
            foreach (var instruction in instructions)
            {
                result.Instructions.Add(new DisassemblyInstruction
                {
                    Address = instruction.AddressHex,
                    Opcode = instruction.Bytes,
                    Instruction = instruction.FullInstruction,
                    IsCurrent = instruction.Address == header.InstructionPointer,
                    Mnemonic = instruction.Mnemonic,
                    Operands = instruction.Operands,
                    IsCall = instruction.IsCall,
                    IsJump = instruction.IsJump,
                    IsReturn = instruction.IsReturn,
                    BranchTarget = instruction.BranchTarget?.ToString("X"),
                    Length = instruction.Length
                });
            }
            result.TotalInstructions = result.Instructions.Count;
            result.BytesRead = memory.BytesRead;
            if (!IsDisassemblySnapshotCurrent(header))
            {
                result.Instructions.Clear();
                result.TotalInstructions = 0;
                result.Error = "Debugger context changed during disassembly decoding; decoded instructions were discarded";
                return result;
            }
            if (result.Instructions.Count > 0)
            {
                DisassemblyInstruction last = result.Instructions[result.Instructions.Count - 1];
                ulong lastAddress = ParseAddress(last.Address);
                if (lastAddress > 0 && last.Length > 0)
                    result.NextAddress = header.Is64Bit
                        ? $"0x{lastAddress + (ulong)last.Length:X16}"
                        : $"0x{lastAddress + (ulong)last.Length:X8}";
            }
            result.Truncated = result.Instructions.Count >= count;
            result.HasMore = result.Truncated || memory.HasMore;
            result.Partial = memory.Partial || result.Instructions.Count < count;
            if (memory.Partial && string.IsNullOrEmpty(result.Error)) result.Error = memory.Error;
            if (result.Instructions.Count < count && string.IsNullOrEmpty(result.Error))
                result.Error = $"Decoded {result.Instructions.Count} of {count} requested instructions from {memory.BytesRead} bytes.";
            return result;
        }

        private bool IsDisassemblySnapshotCurrent(DisassemblyCaptureHeader header)
        {
            string current = ExecuteOnMainThread(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return CreateDebuggerSnapshotToken(Debugger);
            });
            return header != null && !string.IsNullOrEmpty(header.SnapshotToken) &&
                string.Equals(header.SnapshotToken, current, StringComparison.Ordinal);
        }

        private sealed class DisassemblyCaptureHeader
        {
            internal TargetMachineArchitecture Architecture { get; set; }
            internal bool Is64Bit { get; set; }
            internal ulong InstructionPointer { get; set; }
            internal string FunctionName { get; set; }
            internal string SnapshotToken { get; set; }
        }

        private static void ThrowIfDisassemblyArchitectureUnsupported(
            TargetMachineArchitecture architecture)
        {
            if (TargetArchitecturePolicy.IsIcedSupported(architecture)) return;
            bool unknown = architecture == TargetMachineArchitecture.Unknown;
            throw new DebuggerStateRequirementException(
                unknown ? "TARGET_ARCHITECTURE_UNKNOWN" : "ARCHITECTURE_NOT_SUPPORTED",
                unknown
                    ? "Target architecture could not be proven; disassembly was not guessed."
                    : $"Iced disassembly supports x86/x64, not {TargetArchitecturePolicy.ToProtocolName(architecture)}.");
        }

        /// <summary>
        /// Optimized memory reading - reads in larger chunks to reduce IPC calls.
        /// Uses 4-byte reads instead of single-byte reads for better performance.
        /// </summary>
        /// <summary>
        /// Parse unsigned int from debugger expression value.
        /// </summary>
        private uint? ParseUInt(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            value = value.Trim();

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                {
                    return hex;
                }
                return null;
            }

            if (uint.TryParse(value, out uint result))
                return result;

            return null;
        }

        /// <summary>
        /// Parse an address string to a ulong value.
        /// Handles both hex (with 0x prefix) and decimal formats.
        /// </summary>
        private ulong ParseAddress(string addrStr)
        {
            if (string.IsNullOrEmpty(addrStr))
                return 0;

            addrStr = addrStr.Trim();

            // Handle hex with 0x prefix
            if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string hexPart = addrStr.Substring(2);
                // Handle addresses with extra info like "0x7ff712b45a94 {Module!Function}"
                int spaceIdx = hexPart.IndexOf(' ');
                if (spaceIdx > 0)
                    hexPart = hexPart.Substring(0, spaceIdx);

                if (ulong.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out ulong hexAddr))
                {
                    return hexAddr;
                }
            }

            // Try to parse as decimal first (VS debugger often returns decimal values)
            if (ulong.TryParse(addrStr, out ulong decAddr))
            {
                return decAddr;
            }

            // Fallback: try to parse as pure hex without prefix (for edge cases)
            if (ulong.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out ulong fallbackAddr))
            {
                return fallbackAddr;
            }

            return 0;
        }

        private string CreateDebuggerSnapshotToken(Debugger5 debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugger == null || debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return string.Empty;
            }

            int processId = 0;
            int threadId = 0;
            string functionName = "";
            string moduleName = "";
            string frameIdentity = "";

            try
            {
                processId = debugger.CurrentProcess?.ProcessID ?? 0;
            }
            catch { }

            try
            {
                threadId = debugger.CurrentThread?.ID ?? 0;
            }
            catch { }

            try
            {
                var frame = debugger.CurrentStackFrame;
                if (frame != null)
                {
                    functionName = frame.FunctionName ?? "";
                    moduleName = frame.Module ?? "";
                    IntPtr unknown = IntPtr.Zero;
                    try
                    {
                        unknown = Marshal.GetIUnknownForObject(frame);
                        frameIdentity = unknown.ToInt64().ToString("X16");
                    }
                    finally
                    {
                        if (unknown != IntPtr.Zero) Marshal.Release(unknown);
                    }
                }
            }
            catch { }

            // Snapshot identity must remain O(1) and observation-only. Evaluating
            // @rip/@eip can invoke the debug engine and exceed a query deadline;
            // tracker generations already invalidate run, break and frame changes.
            return $"{_sessionTracker.Generation}:{_sessionTracker.BreakSequence}:" +
                $"{_sessionTracker.ContextEpoch}:{processId}:{threadId}:{frameIdentity}:" +
                $"{functionName}:{moduleName}";
        }

        private byte[] ReadProcessMemoryContiguous(IntPtr processHandle, ulong startAddress, int requestedSize)
        {
            if (processHandle == IntPtr.Zero || requestedSize <= 0)
            {
                return Array.Empty<byte>();
            }
            var bytes = new List<byte>(requestedSize);
                int offset = 0;
                UIntPtr mbiSize = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

                while (offset < requestedSize)
                {
                    ulong currentAddress = startAddress + (ulong)offset;
                    if (currentAddress > long.MaxValue)
                    {
                        break;
                    }

                    MEMORY_BASIC_INFORMATION mbi;
                    IntPtr queryResult = VirtualQueryEx(processHandle, new IntPtr((long)currentAddress), out mbi, mbiSize);
                    if (queryResult == IntPtr.Zero)
                    {
                        break;
                    }

                    long regionSize = mbi.RegionSize.ToInt64();
                    if (regionSize <= 0)
                    {
                        break;
                    }

                    ulong regionBase = unchecked((ulong)mbi.BaseAddress.ToInt64());
                    ulong regionEnd = regionBase + (ulong)regionSize;
                    if (currentAddress < regionBase || currentAddress >= regionEnd)
                    {
                        break;
                    }

                    int chunkSize = (int)Math.Min((ulong)(requestedSize - offset), regionEnd - currentAddress);
                    if (chunkSize <= 0)
                    {
                        break;
                    }

                    if (!IsReadableMemoryProtection(mbi.State, mbi.Protect))
                    {
                        break;
                    }

                    var chunk = new byte[chunkSize];
                    IntPtr bytesReadPtr;
                    bool success = ReadProcessMemory(
                        processHandle,
                        new IntPtr((long)currentAddress),
                        chunk,
                        (UIntPtr)chunkSize,
                        out bytesReadPtr);

                    if (!success)
                    {
                        break;
                    }

                    int bytesRead = (int)Math.Min(chunkSize, Math.Max(0, bytesReadPtr.ToInt64()));
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        bytes.Add(chunk[i]);
                    }

                    offset += bytesRead;
                    if (bytesRead < chunkSize)
                    {
                        break;
                    }
                }

            return bytes.ToArray();
        }

        private bool IsReadableMemoryProtection(uint state, uint protect)
        {
            if (state != MEM_COMMIT)
            {
                return false;
            }

            if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0)
            {
                return false;
            }

            uint readableMask =
                PAGE_READONLY |
                PAGE_READWRITE |
                PAGE_WRITECOPY |
                PAGE_EXECUTE_READ |
                PAGE_EXECUTE_READWRITE |
                PAGE_EXECUTE_WRITECOPY;
            return (protect & readableMask) != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer,
            UIntPtr nSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer,
            UIntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}
