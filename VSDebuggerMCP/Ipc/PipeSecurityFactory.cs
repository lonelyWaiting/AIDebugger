using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VSDebuggerMCP.Ipc
{
    internal static class PipeSecurityFactory
    {
        internal static PipeSecurity Create()
        {
            var currentUserSid = WindowsIdentity.GetCurrent().User;
            if (currentUserSid == null)
            {
                throw new InvalidOperationException("Unable to determine the current Windows user SID");
            }

            var security = new PipeSecurity();
            security.SetOwner(currentUserSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(
                currentUserSid,
                PipeAccessRights.ReadWrite |
                    PipeAccessRights.Synchronize,
                AccessControlType.Allow));

            return security;
        }
    }
}
