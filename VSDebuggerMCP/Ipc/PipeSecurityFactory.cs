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
                    PipeAccessRights.Synchronize |
                    // Windows requires FILE_CREATE_PIPE_INSTANCE for every
                    // additional server stream that joins the same named-pipe
                    // listener pool. The current user is already the explicit
                    // trust boundary and the DACL remains protected, so grant
                    // only that user the right needed to create pool slots.
                    PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

            return security;
        }
    }
}
