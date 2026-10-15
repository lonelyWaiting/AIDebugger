using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VSDebuggerMCP.Tests
{
    [TestClass]
    public class DebugSessionTrackerTests
    {
        [TestMethod]
        public void ModeEventEstablishesEpochBeforeFirstContextObservation()
        {
            var events = new EnvDTE.DebuggerEvents();
            var dte = new EnvDTE80.DTE2(events);
            using (var tracker = new DebugSessionTracker(dte, null))
            {
                tracker.ObserveDebuggerContext("break:pid:thread:old-frame");
                events.Run();
                long runEpoch = tracker.ContextEpoch;
                tracker.ObserveDebuggerContext("run:pid:thread:");
                Assert.AreEqual(runEpoch, tracker.ContextEpoch);
                events.Break();
                long stopEpoch = tracker.ContextEpoch;
                tracker.ObserveDebuggerContext("break:pid:thread:new-frame");
                Assert.AreEqual(stopEpoch, tracker.ContextEpoch);
                Assert.AreEqual(1L, tracker.BreakSequence);
            }
        }

        [TestMethod]
        public void ActualContextChangeWithinOneStopStillInvalidatesPinnedEpoch()
        {
            var events = new EnvDTE.DebuggerEvents();
            using (var tracker = new DebugSessionTracker(new EnvDTE80.DTE2(events), null))
            {
                events.Break();
                tracker.ObserveDebuggerContext("break:pid:thread:frame-one");
                long pinned = tracker.ContextEpoch;
                tracker.ObserveDebuggerContext("break:pid:thread:frame-two");
                Assert.AreEqual(pinned + 1, tracker.ContextEpoch);
                Assert.AreEqual(1L, tracker.BreakSequence);
            }
        }
    }
}

// CPU fixture surfaces only the tracker-owned event/state inputs. It does not
// implement IDE marshalling, native exception settings or debugger control.
namespace EnvDTE
{
    public enum dbgDebugMode { dbgDesignMode, dbgBreakMode, dbgRunMode }
    public enum dbgEventReason { dbgEventReasonBreakpoint, dbgEventReasonGo }
    public enum dbgExecutionAction { dbgExecutionActionDefault }
    public delegate void BreakHandler(dbgEventReason reason, ref dbgExecutionAction action);
    public sealed class DebuggerEvents
    {
        public event Action<dbgEventReason> OnEnterRunMode;
        public event BreakHandler OnEnterBreakMode;
        public event Action<dbgEventReason> OnEnterDesignMode;
        public void Run() { OnEnterRunMode?.Invoke(dbgEventReason.dbgEventReasonGo); }
        public void Design() { OnEnterDesignMode?.Invoke(dbgEventReason.dbgEventReasonGo); }
        public void Break()
        {
            var action = dbgExecutionAction.dbgExecutionActionDefault;
            OnEnterBreakMode?.Invoke(dbgEventReason.dbgEventReasonBreakpoint, ref action);
        }
    }
}
namespace EnvDTE80
{
    public sealed class DTE2
    {
        public sealed class EventCollection
        {
            public EnvDTE.DebuggerEvents DebuggerEvents { get; set; }
        }
        public sealed class DebuggerState
        {
            public EnvDTE.dbgDebugMode CurrentMode { get; set; }
        }
        public EventCollection Events { get; }
        public DebuggerState Debugger { get; } = new DebuggerState();
        public DTE2(EnvDTE.DebuggerEvents events)
        {
            Events = new EventCollection { DebuggerEvents = events };
        }
    }
}
namespace Microsoft.VisualStudio.Shell
{
    public static class ThreadHelper { public static void ThrowIfNotOnUIThread() { } }
}
