using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Text;

namespace VSDebuggerMCP
{
    /// <summary>
    /// Provides x86/x64 disassembly using the Iced library.
    /// Supports all Intel and AMD instructions with high performance.
    /// </summary>
    public class DisassemblyService
    {
        /// <summary>
        /// Disassemble raw bytes into assembly instructions.
        /// </summary>
        /// <param name="code">Raw machine code bytes</param>
        /// <param name="baseAddress">Starting address of the code</param>
        /// <param name="is64Bit">True for 64-bit code, false for 32-bit</param>
        /// <param name="maxInstructions">Maximum instructions to decode</param>
        /// <returns>List of disassembled instructions</returns>
        public List<DisassembledInstruction> Disassemble(
            byte[] code,
            ulong baseAddress,
            bool is64Bit,
            int maxInstructions = 100)
        {
            var result = new List<DisassembledInstruction>();

            if (code == null || code.Length == 0)
                return result;

            var bitness = is64Bit ? 64 : 32;

            var codeReader = new ByteArrayCodeReader(code);
            var decoder = Iced.Intel.Decoder.Create(bitness, codeReader);
            decoder.IP = baseAddress;

            // Use NASM-style formatting (most readable)
            var formatter = new NasmFormatter();
            formatter.Options.DigitSeparator = "";
            formatter.Options.FirstOperandCharIndex = 10;
            formatter.Options.SpaceAfterOperandSeparator = true;
            formatter.Options.HexPrefix = "0x";
            formatter.Options.HexSuffix = "";
            formatter.Options.UppercaseHex = false;

            var output = new StringOutput();

            int count = 0;
            int codeOffset = 0;

            while (codeOffset < code.Length && count < maxInstructions)
            {
                var instruction = decoder.Decode();

                // Stop if we hit an invalid instruction
                if (instruction.IsInvalid)
                {
                    // Add a placeholder for the invalid byte
                    if (codeOffset < code.Length)
                    {
                        result.Add(new DisassembledInstruction
                        {
                            Address = baseAddress + (ulong)codeOffset,
                            AddressHex = is64Bit
                                ? $"0x{baseAddress + (ulong)codeOffset:X16}"
                                : $"0x{baseAddress + (ulong)codeOffset:X8}",
                            Length = 1,
                            Bytes = $"{code[codeOffset]:X2}",
                            Mnemonic = "db",
                            Operands = $"0x{code[codeOffset]:X2}",
                            FullInstruction = $"db          0x{code[codeOffset]:X2}",
                            IsCall = false,
                            IsJump = false,
                            IsReturn = false,
                            BranchTarget = null
                        });
                        codeOffset++;
                        count++;
                        decoder.IP = baseAddress + (ulong)codeOffset;
                    }
                    continue;
                }

                // Format the instruction
                output.Reset();
                formatter.Format(instruction, output);
                string fullInstruction = output.ToStringAndReset();

                // Get the raw bytes for this instruction
                int instructionOffset = (int)(instruction.IP - baseAddress);
                var bytes = new byte[instruction.Length];
                if (instructionOffset >= 0 && instructionOffset + instruction.Length <= code.Length)
                {
                    Array.Copy(code, instructionOffset, bytes, 0, instruction.Length);
                }

                // Extract mnemonic and operands
                string mnemonic = instruction.Mnemonic.ToString().ToLowerInvariant();
                string operands = GetOperandsString(fullInstruction);

                result.Add(new DisassembledInstruction
                {
                    Address = instruction.IP,
                    AddressHex = is64Bit
                        ? $"0x{instruction.IP:X16}"
                        : $"0x{instruction.IP:X8}",
                    Length = instruction.Length,
                    Bytes = FormatBytes(bytes),
                    Mnemonic = mnemonic,
                    Operands = operands,
                    FullInstruction = fullInstruction,
                    IsCall = IsCallInstruction(instruction),
                    IsJump = IsJumpInstruction(instruction),
                    IsReturn = instruction.FlowControl == FlowControl.Return,
                    BranchTarget = GetBranchTarget(instruction),
                    IsInterrupt = instruction.FlowControl == FlowControl.Interrupt,
                    IsIndirectBranch = IsIndirectBranch(instruction)
                });

                codeOffset = (int)(instruction.IP - baseAddress) + instruction.Length;
                count++;
            }

            return result;
        }

        /// <summary>
        /// Disassemble a single instruction at the given address.
        /// </summary>
        public DisassembledInstruction DisassembleSingle(byte[] code, ulong address, bool is64Bit)
        {
            var instructions = Disassemble(code, address, is64Bit, 1);
            return instructions.Count > 0 ? instructions[0] : null;
        }

        private string FormatBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";

            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.AppendFormat("{0:X2}", bytes[i]);
            }
            return sb.ToString();
        }

        private string GetOperandsString(string fullInstruction)
        {
            if (string.IsNullOrEmpty(fullInstruction))
                return "";

            // Find the first space after the mnemonic
            int spaceIndex = fullInstruction.IndexOf(' ');
            if (spaceIndex < 0)
                return "";

            return fullInstruction.Substring(spaceIndex).Trim();
        }

        private bool IsCallInstruction(Instruction instruction)
        {
            return instruction.FlowControl == FlowControl.Call ||
                   instruction.FlowControl == FlowControl.IndirectCall;
        }

        private bool IsJumpInstruction(Instruction instruction)
        {
            return instruction.FlowControl == FlowControl.ConditionalBranch ||
                   instruction.FlowControl == FlowControl.UnconditionalBranch ||
                   instruction.FlowControl == FlowControl.IndirectBranch;
        }

        private bool IsIndirectBranch(Instruction instruction)
        {
            return instruction.FlowControl == FlowControl.IndirectBranch ||
                   instruction.FlowControl == FlowControl.IndirectCall;
        }

        private ulong? GetBranchTarget(Instruction instruction)
        {
            // Check for near branch targets
            switch (instruction.Op0Kind)
            {
                case OpKind.NearBranch16:
                case OpKind.NearBranch32:
                case OpKind.NearBranch64:
                    return instruction.NearBranchTarget;
                case OpKind.FarBranch16:
                case OpKind.FarBranch32:
                    return instruction.FarBranchSelector; // Return segment:offset as single value
            }

            return null;
        }
    }

    /// <summary>
    /// Represents a disassembled instruction with all relevant metadata.
    /// </summary>
    public class DisassembledInstruction
    {
        /// <summary>
        /// The memory address of this instruction (numeric).
        /// </summary>
        public ulong Address { get; set; }

        /// <summary>
        /// The memory address formatted as hex string.
        /// </summary>
        public string AddressHex { get; set; }

        /// <summary>
        /// Length of this instruction in bytes.
        /// </summary>
        public int Length { get; set; }

        /// <summary>
        /// Raw bytes of this instruction as hex string (e.g., "48 89 5C 24 08").
        /// </summary>
        public string Bytes { get; set; }

        /// <summary>
        /// The instruction mnemonic (e.g., "mov", "call", "jmp").
        /// </summary>
        public string Mnemonic { get; set; }

        /// <summary>
        /// The instruction operands (e.g., "[rsp+8], rbx").
        /// </summary>
        public string Operands { get; set; }

        /// <summary>
        /// Full formatted instruction text (e.g., "mov [rsp+8], rbx").
        /// </summary>
        public string FullInstruction { get; set; }

        /// <summary>
        /// True if this is a CALL instruction.
        /// </summary>
        public bool IsCall { get; set; }

        /// <summary>
        /// True if this is a JMP or conditional branch instruction.
        /// </summary>
        public bool IsJump { get; set; }

        /// <summary>
        /// True if this is a RET instruction.
        /// </summary>
        public bool IsReturn { get; set; }

        /// <summary>
        /// True if this is an INT instruction.
        /// </summary>
        public bool IsInterrupt { get; set; }

        /// <summary>
        /// True if this is an indirect branch (e.g., call rax, jmp [rbx]).
        /// </summary>
        public bool IsIndirectBranch { get; set; }

        /// <summary>
        /// Target address for branch/call instructions (null if indirect or not a branch).
        /// </summary>
        public ulong? BranchTarget { get; set; }
    }

    /// <summary>
    /// Helper class for Iced formatter output.
    /// </summary>
    internal sealed class StringOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new StringBuilder();

        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);

        public void Reset() => _sb.Clear();

        public string ToStringAndReset()
        {
            var result = _sb.ToString();
            _sb.Clear();
            return result;
        }
    }
}
