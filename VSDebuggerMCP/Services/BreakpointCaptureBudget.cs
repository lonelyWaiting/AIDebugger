using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Services
{
    internal sealed class BreakpointCaptureBudget
    {
        internal const int MaxResultBytes = 3 * 1024 * 1024;
        internal const int MaxFieldBytes = 64 * 1024;
        private const int InitialEnvelopeBytes = 1024;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        internal int ResultBytes { get; private set; } = InitialEnvelopeBytes;

        internal static void BoundFields(BreakpointInfo item)
        {
            if (item == null) return;
            bool truncated = false;
            item.Name = Bound(item.Name, ref truncated);
            item.FilePath = Bound(item.FilePath, ref truncated);
            item.FunctionName = Bound(item.FunctionName, ref truncated);
            item.Condition = Bound(item.Condition, ref truncated);
            item.ConditionType = Bound(item.ConditionType, ref truncated);
            item.HitCountType = Bound(item.HitCountType, ref truncated);
            item.BreakpointType = Bound(item.BreakpointType, ref truncated);
            item.Tag = Bound(item.Tag, ref truncated);
            item.RequestedFilePath = Bound(item.RequestedFilePath, ref truncated);
            item.RequestedCondition = Bound(item.RequestedCondition, ref truncated);
            item.ResolvedFilePath = Bound(item.ResolvedFilePath, ref truncated);
            item.ResolvedFunctionName = Bound(item.ResolvedFunctionName, ref truncated);
            item.BindingStatus = Bound(item.BindingStatus, ref truncated);
            item.BindingVerificationStatus = Bound(item.BindingVerificationStatus, ref truncated);
            item.BindingWarning = Bound(item.BindingWarning, ref truncated);
            BoundAddress(item.TrackedAddress, ref truncated);
            foreach (NativeAddressInfo address in item.TrackedAddresses ?? new List<NativeAddressInfo>())
                BoundAddress(address, ref truncated);
            item.Truncated = truncated;
        }

        internal bool TryConsume(BreakpointInfo item)
        {
            int bytes = Encoding.UTF8.GetByteCount(_serializer.Serialize(item)) + 1;
            if (bytes > MaxResultBytes - ResultBytes) return false;
            ResultBytes += bytes;
            return true;
        }

        private static string Bound(string value, ref bool anyTruncated)
        {
            string bounded = OutputContentBudget.FitPrefix(
                value ?? string.Empty,
                MaxFieldBytes,
                out bool truncated);
            anyTruncated |= truncated;
            return bounded;
        }

        private static void BoundAddress(NativeAddressInfo address, ref bool truncated)
        {
            if (address == null) return;
            address.Expression = Bound(address.Expression, ref truncated);
            address.Status = Bound(address.Status, ref truncated);
            address.VirtualAddress = Bound(address.VirtualAddress, ref truncated);
            address.ModuleName = Bound(address.ModuleName, ref truncated);
            address.ModulePath = Bound(address.ModulePath, ref truncated);
            address.ModuleBase = Bound(address.ModuleBase, ref truncated);
            address.Rva = Bound(address.Rva, ref truncated);
            address.SectionName = Bound(address.SectionName, ref truncated);
            address.SectionRva = Bound(address.SectionRva, ref truncated);
            address.SectionOffset = Bound(address.SectionOffset, ref truncated);
            address.NearestSymbol = Bound(address.NearestSymbol, ref truncated);
            address.FunctionStatus = Bound(address.FunctionStatus, ref truncated);
            address.FunctionError = Bound(address.FunctionError, ref truncated);
            address.Error = Bound(address.Error, ref truncated);
            if (address.Function == null) return;
            address.Function.StartAddress = Bound(address.Function.StartAddress, ref truncated);
            address.Function.StartRva = Bound(address.Function.StartRva, ref truncated);
            address.Function.EndAddress = Bound(address.Function.EndAddress, ref truncated);
            address.Function.EndRva = Bound(address.Function.EndRva, ref truncated);
            address.Function.AddressOffset = Bound(address.Function.AddressOffset, ref truncated);
            address.Function.NearestSymbol = Bound(address.Function.NearestSymbol, ref truncated);
            address.Function.Confidence = Bound(address.Function.Confidence, ref truncated);
            address.Function.Reason = Bound(address.Function.Reason, ref truncated);
        }
    }
}
