using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal static class RegisterCapturePolicy
    {
        private static readonly string[] CategoryOrder =
        {
            "General Purpose", "Instruction Pointer", "Stack", "Flags", "Segment", "SSE"
        };

        internal static IReadOnlyList<string> BuildRegisterNames(TargetMachineArchitecture architecture)
        {
            var names = architecture == TargetMachineArchitecture.X64
                ? new List<string>
                {
                    "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
                    "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rip", "rflags"
                }
                : architecture == TargetMachineArchitecture.X86
                    ? new List<string>
                    {
                        "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip", "eflags"
                    }
                    : throw new ArgumentOutOfRangeException(nameof(architecture));

            names.AddRange(new[] { "cs", "ds", "es", "fs", "gs", "ss" });
            int xmmCount = architecture == TargetMachineArchitecture.X64 ? 16 : 8;
            names.AddRange(Enumerable.Range(0, xmmCount).Select(index => "xmm" + index));
            return names;
        }

        internal static string FormatScalarValueAsHex(
            TargetMachineArchitecture architecture,
            string registerName,
            string value)
        {
            if (string.IsNullOrWhiteSpace(registerName) || string.IsNullOrWhiteSpace(value) ||
                registerName.StartsWith("xmm", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            int bitWidth;
            switch (registerName.ToLowerInvariant())
            {
                case "cs": case "ds": case "es": case "fs": case "gs": case "ss":
                    bitWidth = 16;
                    break;
                default:
                    bitWidth = architecture == TargetMachineArchitecture.X86
                        ? 32
                        : architecture == TargetMachineArchitecture.X64
                            ? 64
                            : 0;
                    break;
            }
            if (bitWidth == 0) return null;

            string normalized = value.Trim();
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string digits = normalized.Substring(2);
                if (digits.Length == 0 ||
                    !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong parsedHex) ||
                    !FitsUnsignedWidth(parsedHex, bitWidth))
                {
                    return null;
                }
                return "0x" + parsedHex.ToString("X", CultureInfo.InvariantCulture);
            }

            ulong encoded;
            if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signedValue))
            {
                encoded = unchecked((ulong)signedValue);
                if (bitWidth < 64)
                {
                    ulong mask = (1UL << bitWidth) - 1;
                    long minimumSigned = -(1L << (bitWidth - 1));
                    if (signedValue < minimumSigned) return null;
                    if (signedValue >= 0 && encoded > mask) return null;
                    encoded &= mask;
                }
            }
            else if (ulong.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong unsignedValue) &&
                FitsUnsignedWidth(unsignedValue, bitWidth))
            {
                encoded = unsignedValue;
            }
            else
            {
                return null;
            }
            return "0x" + encoded.ToString("X", CultureInfo.InvariantCulture);
        }

        private static bool FitsUnsignedWidth(ulong value, int bitWidth)
        {
            return bitWidth >= 64 || value <= ((1UL << bitWidth) - 1);
        }

        internal static void Complete(
            RegistersResult result,
            IReadOnlyCollection<string> requestedNames,
            IReadOnlyCollection<string> unavailableNames,
            bool snapshotStable,
            string captureErrors)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            requestedNames = requestedNames ?? new string[0];
            unavailableNames = unavailableNames ?? new string[0];

            result.RequestedRegisterCount = requestedNames.Count;
            result.CapturedRegisterCount = result.Registers.Count;
            result.UnavailableRegisters = unavailableNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var capturedCategories = new HashSet<string>(
                result.Registers.Select(register => register.Category),
                StringComparer.Ordinal);
            result.AvailableCategories = CategoryOrder
                .Where(capturedCategories.Contains)
                .ToList();
            result.SnapshotStatus = snapshotStable ? "stable" : "unstable";
            result.Partial = !snapshotStable || result.UnavailableRegisters.Count > 0;
            result.Error = Append(result.Error, captureErrors);

            string[] requiredCore = result.Is64Bit == true
                ? new[] { "RIP", "RSP" }
                : new[] { "EIP", "ESP" };
            string[] missingCore = requiredCore
                .Where(core => result.UnavailableRegisters.Contains(core, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (!snapshotStable)
            {
                result.Status = "unstable";
                result.ErrorCode = "STALE_DEBUG_CONTEXT";
                result.Error = Append(result.Error, "debugger context changed while registers were captured");
            }
            else if (result.CapturedRegisterCount == 0)
            {
                result.Status = "unavailable";
                result.ErrorCode = "REGISTERS_UNAVAILABLE";
                result.Error = Append(result.Error, "no requested registers were available from the active debug engine");
            }
            else if (missingCore.Length > 0)
            {
                result.Status = "partial";
                result.ErrorCode = "CORE_REGISTERS_UNAVAILABLE";
                result.Error = Append(result.Error, "required register(s) unavailable: " + string.Join(", ", missingCore));
            }
            else if (result.UnavailableRegisters.Count > 0)
            {
                result.Status = "partial";
                result.ErrorCode = "REGISTER_CAPTURE_PARTIAL";
            }
            else
            {
                result.Status = "complete";
            }
        }

        private static string Append(string current, string next)
        {
            return string.IsNullOrWhiteSpace(current)
                ? next
                : string.IsNullOrWhiteSpace(next) ? current : current + "; " + next;
        }
    }
}
