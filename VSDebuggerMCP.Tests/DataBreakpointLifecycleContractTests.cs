using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSDebuggerMCP.Protocol;

namespace VSDebuggerMCP.Tests
{
    [TestClass]
    public class DataBreakpointLifecycleContractTests
    {
        [TestMethod]
        public void DeleteDataBreakpointIsAWatchpointOnlyCommand()
        {
            DebugMethodDescriptor descriptor = DebugMethodCatalog.Get("delete_data_breakpoint");

            Assert.IsNotNull(descriptor);
            Assert.AreEqual("command", descriptor.Kind);
            Assert.AreEqual("debug.breakpoint.write", descriptor.Capability);
            Assert.AreEqual("any", descriptor.RequiredState);
            CollectionAssert.AreEqual(
                new[] { "dataBreakpointId" },
                descriptor.RequiredParameters.ToArray());
            CollectionAssert.Contains(
                descriptor.AllowedParameters.ToArray(),
                "breakpointLookupBudgetMs");
            Assert.IsFalse(descriptor.RequiredCapabilities.Contains("debug.evaluate.unsafe"));
        }

        [TestMethod]
        public void BreakpointInfoExposesOwnedDataBreakpointIdSeparatelyFromTag()
        {
            PropertyInfo property = typeof(BreakpointInfo).GetProperty(
                nameof(BreakpointInfo.DataBreakpointId));

            Assert.IsNotNull(property);
            DataMemberAttribute attribute = property.GetCustomAttribute<DataMemberAttribute>();
            Assert.IsNotNull(attribute);
            Assert.AreEqual("dataBreakpointId", attribute.Name);
            Assert.IsFalse(attribute.EmitDefaultValue);
        }

        [TestMethod]
        public void FailedDataBreakpointConditionCleanupPreservesTheOwnedIdentifier()
        {
            string root = FindRepositoryRoot();
            string source = File.ReadAllText(Path.Combine(
                root,
                "VSDebuggerMCP",
                "Services",
                "DebuggerService.Breakpoints.cs"));
            int conditionFailure = source.IndexOf(
                "Data breakpoint condition was not applied",
                System.StringComparison.Ordinal);

            Assert.IsTrue(conditionFailure > 0);
            string precedingBlock = source.Substring(
                System.Math.Max(0, conditionFailure - 1000),
                conditionFailure - System.Math.Max(0, conditionFailure - 1000));
            StringAssert.Contains(precedingBlock, "result.BreakpointInfo = breakpointInfo;");
        }

        private static string FindRepositoryRoot()
        {
            string current = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "AIDebugger.sln")))
                {
                    return current;
                }
                current = Directory.GetParent(current)?.FullName;
            }
            Assert.Fail("Repository root was not found.");
            return null;
        }
    }
}
