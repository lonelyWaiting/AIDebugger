namespace VSDebuggerMCP.Services
{
    internal enum TargetMachineArchitecture
    {
        Unknown,
        X86,
        X64,
        Arm32,
        Arm64,
        Ia64,
    }

    internal static class TargetArchitecturePolicy
    {
        internal static TargetMachineArchitecture FromImageFileMachine(ushort machine)
        {
            switch (machine)
            {
                case 0x014c: return TargetMachineArchitecture.X86;
                case 0x8664: return TargetMachineArchitecture.X64;
                case 0x01c4: return TargetMachineArchitecture.Arm32;
                case 0xaa64: return TargetMachineArchitecture.Arm64;
                case 0x0200: return TargetMachineArchitecture.Ia64;
                default: return TargetMachineArchitecture.Unknown;
            }
        }

        internal static TargetMachineArchitecture FromMinidumpProcessorArchitecture(ushort architecture)
        {
            switch (architecture)
            {
                case 0: return TargetMachineArchitecture.X86;
                case 5: return TargetMachineArchitecture.Arm32;
                case 6: return TargetMachineArchitecture.Ia64;
                case 9: return TargetMachineArchitecture.X64;
                case 12: return TargetMachineArchitecture.Arm64;
                default: return TargetMachineArchitecture.Unknown;
            }
        }

        internal static bool? Is64Bit(TargetMachineArchitecture architecture)
        {
            switch (architecture)
            {
                case TargetMachineArchitecture.X86:
                case TargetMachineArchitecture.Arm32:
                    return false;
                case TargetMachineArchitecture.X64:
                case TargetMachineArchitecture.Arm64:
                case TargetMachineArchitecture.Ia64:
                    return true;
                default:
                    return null;
            }
        }

        internal static bool IsIcedSupported(TargetMachineArchitecture architecture)
        {
            return architecture == TargetMachineArchitecture.X86 ||
                   architecture == TargetMachineArchitecture.X64;
        }

        internal static string ToProtocolName(TargetMachineArchitecture architecture)
        {
            switch (architecture)
            {
                case TargetMachineArchitecture.X86: return "x86";
                case TargetMachineArchitecture.X64: return "x64";
                case TargetMachineArchitecture.Arm32: return "arm32";
                case TargetMachineArchitecture.Arm64: return "arm64";
                case TargetMachineArchitecture.Ia64: return "ia64";
                default: return "unknown";
            }
        }
    }
}
