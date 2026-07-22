using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSDebuggerMCP.Ipc;
using VSDebuggerMCP.Protocol;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP.Tests
{
    [TestClass]
    public class ProtocolInfrastructureTests
    {
        [TestMethod]
        public void JsoncUpsertPreservesUnownedContentAndComments()
        {
            const string original = "{\n  // retained\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"Other\": {\"command\":\"other\"}\n  }\n}";
            string updated = JsonConfigEditor.UpsertOwnedEntry(
                original,
                "mcpServers",
                "AIDebugger-VS",
                "{\"command\":\"python\"}");

            StringAssert.Contains(updated, "// retained");
            StringAssert.Contains(updated, "\"theme\": \"dark\"");
            StringAssert.Contains(updated, "\"Other\": {\"command\":\"other\"}");
            StringAssert.Contains(updated, "\"AIDebugger-VS\": {\"command\":\"python\"}");
        }

        [TestMethod]
        public void FreshVsRuntimeStagingIncludesAndImportsOutputModels()
        {
            string repoRoot = FindRepositoryRoot();
            string source = Path.Combine(repoRoot, "mcp_server");
            string staging = Path.Combine(Path.GetTempPath(), "aidb-vs-runtime-" + Guid.NewGuid().ToString("N"));
            try
            {
                BundlePathPolicy.CopyVsBundleFiles(source, staging);
                string outputModels = Path.Combine(staging, "output_models.py");
                Assert.IsTrue(File.Exists(outputModels), "output_models.py was omitted from the VS runtime bundle");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import sys; sys.path.insert(0, r'" +
                        staging.Replace("'", "''") +
                        "'); import bootstrap; bootstrap.ensure_deps(); import output_models; print('output_models import ok')\"",
                    WorkingDirectory = staging,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                startInfo.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
                using (Process process = Process.Start(startInfo))
                {
                    Assert.IsTrue(process.WaitForExit(30000), "Python import smoke test timed out");
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    Assert.AreEqual(0, process.ExitCode, output + error);
                    StringAssert.Contains(output, "output_models import ok");
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        private static string FindRepositoryRoot()
        {
            string[] starts =
            {
                Environment.CurrentDirectory,
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(typeof(ProtocolInfrastructureTests).Assembly.Location),
            };
            foreach (string start in starts.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "AIDebugger.sln")))
                    {
                        return directory.FullName;
                    }
                    directory = directory.Parent;
                }
            }
            throw new DirectoryNotFoundException("Could not locate AIDebugger.sln from the test output directory");
        }

        [TestMethod]
        public void JsoncUpsertReplacesOnlyOwnedValueAndSupportsTrailingComma()
        {
            const string original = "{\n  \"mcpServers\": {\n    \"AIDebugger-VS\": {\"command\":\"old\"},\n  },\n  \"keep\": 7\n}";
            string updated = JsonConfigEditor.UpsertOwnedEntry(
                original,
                "mcpServers",
                "AIDebugger-VS",
                "{\"command\":\"new\"}");

            Assert.IsFalse(updated.Contains("\"command\":\"old\""));
            StringAssert.Contains(updated, "\"command\":\"new\"");
            StringAssert.Contains(updated, "\"keep\": 7");
        }

        [TestMethod]
        public void JsoncEditorAcceptsCommentsAndTrailingCommasAtEveryContainerLevel()
        {
            const string original = "{\n  /* root */\n  \"items\": [1, 2,],\n  \"mcpServers\": {\n    // retained\n    \"Other\": {\"args\":[true, null,],},\n  },\n}";
            string updated = JsonConfigEditor.UpsertOwnedEntry(
                original, "mcpServers", "AIDebugger-VS", "{\"command\":\"python\"}");
            StringAssert.Contains(updated, "/* root */");
            StringAssert.Contains(updated, "// retained");
            StringAssert.Contains(updated, "\"items\": [1, 2,]");
            StringAssert.Contains(updated, "\"AIDebugger-VS\"");
        }

        [TestMethod]
        public void JsoncEditorRejectsMalformedUnownedNodesBeforeEditing()
        {
            foreach (string document in new[]
            {
                "{\"unowned\": false true}",
                "{\"unowned\": [1 2]}",
                "{\"unowned\": 01}",
                "{\"unowned\": \"bad\\q\"}",
                "{\"unowned\": {}, \"unowned\": []}",
            })
            {
                AssertFormatException(() => JsonConfigEditor.UpsertOwnedEntry(
                    document, "mcpServers", "AIDebugger-VS", "{}"));
            }
        }

        [TestMethod]
        public void JsoncRemoveDeletesOnlyOwnedEntry()
        {
            const string original = "{\n  \"mcpServers\": {\n    \"AIDebugger-VS\": {},\n    \"Other\": {}\n  }\n}";
            string updated = JsonConfigEditor.RemoveOwnedEntry(
                original,
                "mcpServers",
                "AIDebugger-VS",
                out bool removed);

            Assert.IsTrue(removed);
            Assert.IsFalse(updated.Contains("AIDebugger-VS"));
            StringAssert.Contains(updated, "\"Other\": {}");
        }

        [TestMethod]
        public void JsoncRemoveLastEntryPreservesPreviousInlineCommentAndValidCommaPlacement()
        {
            const string original = "{\n  \"mcpServers\": {\n    \"Other\": {}, // keep this comment\n    \"AIDebugger-VS\": {}\n  }\n}";
            string updated = JsonConfigEditor.RemoveOwnedEntry(
                original,
                "mcpServers",
                "AIDebugger-VS",
                out bool removed);

            Assert.IsTrue(removed);
            StringAssert.Contains(updated, "// keep this comment");
            Assert.IsFalse(updated.Contains("\"Other\": {},"));
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer()
                .DeserializeObject(JsonConfigEditor.StripComments(updated));
            Assert.IsNotNull(parsed);
        }

        [TestMethod]
        public void JsoncEditorRejectsDuplicateOwnedContainerIncludingEscapedKey()
        {
            const string document = "{\n  \"mcpServers\": {},\n  \"mcp\\u0053ervers\": {}\n}";
            try
            {
                JsonConfigEditor.UpsertOwnedEntry(document, "mcpServers", "AIDebuggerVS", "{}");
                Assert.Fail("Duplicate semantic container key was accepted");
            }
            catch (FormatException) { }
        }

        [TestMethod]
        public void JsoncEditorRejectsDuplicateOwnedEntryIncludingEscapedKey()
        {
            const string document = "{\n  \"mcpServers\": {\n    \"AIDebuggerVS\": {},\n    \"AIDebugger\\u0056S\": {}\n  }\n}";
            try
            {
                JsonConfigEditor.RemoveOwnedEntry(document, "mcpServers", "AIDebuggerVS", out _);
                Assert.Fail("Duplicate semantic owned key was accepted");
            }
            catch (FormatException) { }
        }

        [TestMethod]
        public void StrictRequestEnvelopeRejectsUnknownDuplicateAndNullObjects()
        {
            const string valid = "{\"protocol\":\"aidb/2\",\"id\":\"id\",\"method\":\"ping\",\"params\":{},\"meta\":{\"authToken\":\"x\",\"deadlineUnixMs\":1,\"kind\":\"query\",\"capability\":\"server.control\",\"clientProcessId\":1,\"clientProcessStartTimeUtcTicks\":1,\"target\":{\"processId\":1,\"processStartTimeUtcTicks\":1,\"serverInstanceId\":\"x\",\"debugSessionGeneration\":0,\"breakSequence\":0,\"contextEpoch\":0}}}";
            StrictJsonRequestValidator.Validate(valid);
            AssertFormatException(() => StrictJsonRequestValidator.Validate(
                valid.Insert(valid.Length - 1, ",\"unexpected\":true")));
            AssertFormatException(() => StrictJsonRequestValidator.Validate(
                valid.Replace("\"id\":\"id\"", "\"id\":\"id\",\"\\u0069d\":\"other\"")));
            AssertFormatException(() => StrictJsonRequestValidator.Validate(
                valid.Replace("\"params\":{}", "\"params\":null")));
            AssertFormatException(() => StrictJsonRequestValidator.Validate(
                valid.Replace("\"contextEpoch\":0", "\"contextEpoch\":0,\"future\":1")));
            foreach (string invalidNumber in new[]
            {
                "NaN", "Infinity", "-Infinity", "01", "+1", "1.", "1e", "1e9999",
                "18446744073709551616", "-9223372036854775809",
                "1" + new string('0', StrictJsonRequestValidator.MaxNumberTokenLength),
            })
            {
                AssertFormatException(() => StrictJsonRequestValidator.Validate(
                    valid.Replace("\"deadlineUnixMs\":1", "\"deadlineUnixMs\":" + invalidNumber)));
            }
            foreach (char invalidWhitespace in new[] { '\u000b', '\u000c', '\u00a0', '\u2028' })
            {
                AssertFormatException(() => StrictJsonRequestValidator.Validate(
                    valid.Replace("\"protocol\":", "\"protocol\":" + invalidWhitespace)));
            }

            string deeplyNested = new string('[', StrictJsonRequestValidator.MaxNestingDepth + 2) +
                "0" + new string(']', StrictJsonRequestValidator.MaxNestingDepth + 2);
            AssertFormatException(() => StrictJsonRequestValidator.Validate(deeplyNested));
            string deeplyNestedObject = string.Concat(Enumerable.Repeat("{\"x\":", StrictJsonRequestValidator.MaxNestingDepth + 2)) +
                "0" + new string('}', StrictJsonRequestValidator.MaxNestingDepth + 2);
            AssertFormatException(() => StrictJsonRequestValidator.Validate(deeplyNestedObject));
        }

        [TestMethod]
        public void ResponseSerializerRejectsNonFiniteNumbers()
        {
            foreach (object invalidNumber in new object[]
            {
                float.NaN,
                float.PositiveInfinity,
                float.NegativeInfinity,
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
            })
            {
                var response = DebugResponse.Success(
                    Guid.NewGuid().ToString(),
                    new Dictionary<string, object> { ["value"] = invalidNumber });
                try
                {
                    DebugResponseJsonSerializer.Serialize(response);
                    Assert.Fail("Non-finite JSON number was serialized");
                }
                catch (System.Runtime.Serialization.SerializationException) { }
            }
        }

        [TestMethod]
        public void CancelRequestCannotCancelItsOwnControlRequest()
        {
            string requestId = Guid.NewGuid().ToString("D");
            Assert.IsTrue(ControlRequestValidator.IsSelfCancellation(requestId, requestId));
            Assert.IsFalse(ControlRequestValidator.IsSelfCancellation(
                requestId,
                Guid.NewGuid().ToString("D")));
        }

        [TestMethod]
        public void ProcessLaunchBoundaryMarksStartedBeforeIdentityCaptureFailure()
        {
            bool markedStarted = false;
            PostLaunchCaptureResult<string> capture = ProcessLaunchBoundary.CaptureAfterStart<object, string>(
                new object(),
                () => markedStarted = true,
                _ => throw new InvalidOperationException("identity unavailable"));

            Assert.IsTrue(markedStarted);
            Assert.IsTrue(capture.ProcessStarted);
            Assert.IsInstanceOfType<InvalidOperationException>(capture.CaptureError);
            Assert.IsNull(capture.Value);
        }

        private static void AssertFormatException(Action action)
        {
            try
            {
                action();
                Assert.Fail("Expected FormatException");
            }
            catch (FormatException) { }
        }

        [TestMethod]
        public async Task FrameCodecRoundTripsStrictUtf8()
        {
            using (var stream = new MemoryStream())
            {
                var writer = new LengthPrefixedFrameCodec(stream);
                await writer.WriteFrameAsync("调试-ok", 1024, CancellationToken.None);
                stream.Position = 0;
                var reader = new LengthPrefixedFrameCodec(stream);
                Assert.AreEqual("调试-ok", await reader.ReadFrameAsync(1024, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task FrameCodecRejectsOversizedAndMalformedUtf8Frames()
        {
            using (var oversized = new MemoryStream(new byte[] { 0, 0, 4, 0 }))
            {
                var codec = new LengthPrefixedFrameCodec(oversized);
                await Assert.ThrowsAsync<IOException>(() =>
                    codec.ReadFrameAsync(100, CancellationToken.None));
            }

            using (var malformed = new MemoryStream(new byte[] { 0, 0, 0, 2, 0xC3, 0x28 }))
            {
                var codec = new LengthPrefixedFrameCodec(malformed);
                await Assert.ThrowsAsync<DecoderFallbackException>(() =>
                    codec.ReadFrameAsync(100, CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task FrameCodecPreservesConsecutiveFramesOnOneConnection()
        {
            using (var stream = new MemoryStream())
            {
                var writer = new LengthPrefixedFrameCodec(stream);
                await writer.WriteFrameAsync("first", 1024, CancellationToken.None);
                await writer.WriteFrameAsync("second", 1024, CancellationToken.None);
                stream.Position = 0;

                var reader = new LengthPrefixedFrameCodec(stream);
                Assert.AreEqual("first", await reader.ReadFrameAsync(1024, CancellationToken.None));
                Assert.AreEqual("second", await reader.ReadFrameAsync(1024, CancellationToken.None));
                Assert.IsNull(await reader.ReadFrameAsync(1024, CancellationToken.None));
            }
        }

        [TestMethod]
        [Timeout(5000)]
        public async Task OwnerOnlyNamedPipeTransfersConsecutiveBoundedFrames()
        {
            string pipeName = "AIDebuggerTest_" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                PipeSecurityFactory.Create()))
            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                Task accept = Task.Factory.FromAsync(
                    server.BeginWaitForConnection,
                    server.EndWaitForConnection,
                    null);
                client.Connect(3000);
                await accept;

                var clientCodec = new LengthPrefixedFrameCodec(client);
                var serverCodec = new LengthPrefixedFrameCodec(server);
                await clientCodec.WriteFrameAsync("request-one", 1024, CancellationToken.None);
                await clientCodec.WriteFrameAsync("request-two", 1024, CancellationToken.None);
                Assert.AreEqual("request-one", await serverCodec.ReadFrameAsync(1024, CancellationToken.None));
                Assert.AreEqual("request-two", await serverCodec.ReadFrameAsync(1024, CancellationToken.None));

                await serverCodec.WriteFrameAsync("response", 1024, CancellationToken.None);
                Assert.AreEqual("response", await clientCodec.ReadFrameAsync(1024, CancellationToken.None));
            }
        }

        [TestMethod]
        public void PipeSecurityDefaultsToOwnerOnlyAndProtectedDacl()
        {
            string previous = Environment.GetEnvironmentVariable("AIDB_PIPE_SECURITY");
            try
            {
                Environment.SetEnvironmentVariable("AIDB_PIPE_SECURITY", null);
                var security = PipeSecurityFactory.Create();
                var currentSid = WindowsIdentity.GetCurrent().User;
                Assert.IsTrue(security.AreAccessRulesProtected);
                Assert.AreEqual(currentSid, security.GetOwner(typeof(SecurityIdentifier)));

                var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
                Assert.AreEqual(1, rules.Count);
                var rule = (System.IO.Pipes.PipeAccessRule)rules[0];
                Assert.AreEqual(currentSid, rule.IdentityReference);
                Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
                var required = System.IO.Pipes.PipeAccessRights.ReadWrite |
                    System.IO.Pipes.PipeAccessRights.Synchronize;
                Assert.AreEqual(required, rule.PipeAccessRights & required);
                Assert.AreEqual((System.IO.Pipes.PipeAccessRights)0,
                    rule.PipeAccessRights & System.IO.Pipes.PipeAccessRights.CreateNewInstance);
                Assert.AreEqual((System.IO.Pipes.PipeAccessRights)0,
                    rule.PipeAccessRights & System.IO.Pipes.PipeAccessRights.ChangePermissions);
                Assert.AreEqual((System.IO.Pipes.PipeAccessRights)0,
                    rule.PipeAccessRights & System.IO.Pipes.PipeAccessRights.TakeOwnership);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIDB_PIPE_SECURITY", previous);
            }
        }

        [TestMethod]
        public async Task PipeSecurityAllowsClientIoButDeniesSameNameServerCreation()
        {
            string pipeName = "AIDebuggerTest_" + Guid.NewGuid().ToString("N");
            PipeSecurity security = PipeSecurityFactory.Create();
            using (var first = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security))
            using (var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                Task accept = Task.Factory.FromAsync(
                    first.BeginWaitForConnection,
                    first.EndWaitForConnection,
                    null);
                client.Connect(3000);
                await accept;
                Assert.IsTrue(client.IsConnected);

                Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
                    new NamedPipeServerStream(
                        pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, security));
            }
        }

        [TestMethod]
        public void IntegrityBoundaryRejectsLowerIntegrityClients()
        {
            Assert.IsTrue(ProcessIntegrityValidator.IsClientIntegrityAllowed(0x2000, 0x2000));
            Assert.IsTrue(ProcessIntegrityValidator.IsClientIntegrityAllowed(0x2000, 0x3000));
            Assert.IsFalse(ProcessIntegrityValidator.IsClientIntegrityAllowed(0x3000, 0x2000));
            Assert.IsFalse(ProcessIntegrityValidator.IsClientIntegrityAllowed(0x2000, 0x1000));
            using (var current = System.Diagnostics.Process.GetCurrentProcess())
            {
                Assert.IsTrue(
                    ProcessIntegrityValidator.TryValidateClientAgainstCurrentProcess(current.Id, out string error),
                    error);
            }
        }

        [TestMethod]
        public void UiOperationAdmissionHasExactlyOneWinner()
        {
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                var admission = new UiOperationAdmission();
                var barrier = new Barrier(3);
                bool started = false;
                bool cancelled = false;
                Task start = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    started = admission.TryStart();
                });
                Task cancel = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    cancelled = admission.TryCancelBeforeStart();
                });
                barrier.SignalAndWait();
                Task.WaitAll(start, cancel);

                Assert.AreNotEqual(started, cancelled);
                Assert.AreEqual(started, admission.Started);
            }
        }

        [TestMethod]
        public void TargetArchitecturePolicyDoesNotCollapseArmOrUnknownIntoX86X64()
        {
            Assert.AreEqual(
                TargetMachineArchitecture.X86,
                TargetArchitecturePolicy.FromImageFileMachine(0x014c));
            Assert.AreEqual(
                TargetMachineArchitecture.X64,
                TargetArchitecturePolicy.FromImageFileMachine(0x8664));
            Assert.AreEqual(
                TargetMachineArchitecture.Arm64,
                TargetArchitecturePolicy.FromImageFileMachine(0xaa64));
            Assert.AreEqual(
                TargetMachineArchitecture.Arm32,
                TargetArchitecturePolicy.FromMinidumpProcessorArchitecture(5));
            Assert.AreEqual(
                TargetMachineArchitecture.Unknown,
                TargetArchitecturePolicy.FromMinidumpProcessorArchitecture(ushort.MaxValue));
            Assert.IsFalse(TargetArchitecturePolicy.IsIcedSupported(TargetMachineArchitecture.Arm64));
            Assert.IsFalse(TargetArchitecturePolicy.IsIcedSupported(TargetMachineArchitecture.Unknown));
            Assert.IsNull(TargetArchitecturePolicy.Is64Bit(TargetMachineArchitecture.Unknown));
            Assert.AreEqual("arm64", TargetArchitecturePolicy.ToProtocolName(TargetMachineArchitecture.Arm64));
        }

        [TestMethod]
        public void RegisterCapturePolicyUsesArchitectureSpecificXmmSets()
        {
            IReadOnlyList<string> x86 = RegisterCapturePolicy.BuildRegisterNames(TargetMachineArchitecture.X86);
            IReadOnlyList<string> x64 = RegisterCapturePolicy.BuildRegisterNames(TargetMachineArchitecture.X64);

            Assert.AreEqual(8, x86.Count(name => name.StartsWith("xmm", StringComparison.Ordinal)));
            Assert.IsFalse(x86.Contains("xmm8"));
            Assert.AreEqual(16, x64.Count(name => name.StartsWith("xmm", StringComparison.Ordinal)));
            Assert.IsTrue(x64.Contains("xmm15"));
        }

        [TestMethod]
        public void RegisterHexFormattingUsesTheTargetRegisterWidthAndNeverEchoesDecimal()
        {
            Assert.AreEqual(
                "0xFFFFFFFF",
                RegisterCapturePolicy.FormatScalarValueAsHex(
                    TargetMachineArchitecture.X86, "eax", "-1"));
            Assert.AreEqual(
                "0xFFFFFFFFFFFFFFFF",
                RegisterCapturePolicy.FormatScalarValueAsHex(
                    TargetMachineArchitecture.X64, "rax", "18446744073709551615"));
            Assert.AreEqual(
                "0xFFFF",
                RegisterCapturePolicy.FormatScalarValueAsHex(
                    TargetMachineArchitecture.X64, "cs", "65535"));
            Assert.IsNull(RegisterCapturePolicy.FormatScalarValueAsHex(
                TargetMachineArchitecture.X86, "eax", "4294967296"));
            Assert.IsNull(RegisterCapturePolicy.FormatScalarValueAsHex(
                TargetMachineArchitecture.X86, "eax", "-4294967296"));
            Assert.IsNull(RegisterCapturePolicy.FormatScalarValueAsHex(
                TargetMachineArchitecture.X64, "rax", "not-a-number"));
            Assert.IsNull(RegisterCapturePolicy.FormatScalarValueAsHex(
                TargetMachineArchitecture.X64, "xmm0", "123"));
        }

        [TestMethod]
        public void RegisterCapturePolicyReportsUnavailableAndDerivesActualCategories()
        {
            IReadOnlyList<string> requested = RegisterCapturePolicy.BuildRegisterNames(TargetMachineArchitecture.X86);
            var result = new RegistersResult
            {
                Is64Bit = false,
                Registers = new List<RegisterInfo>
                {
                    new RegisterInfo { Name = "EAX", Category = "General Purpose" },
                    new RegisterInfo { Name = "EIP", Category = "Instruction Pointer" },
                },
            };
            string[] unavailable = requested.Where(name => name != "eax" && name != "eip").ToArray();

            RegisterCapturePolicy.Complete(result, requested, unavailable, true, null);

            Assert.AreEqual("partial", result.Status);
            Assert.AreEqual("CORE_REGISTERS_UNAVAILABLE", result.ErrorCode);
            Assert.IsTrue(result.Partial);
            Assert.AreEqual("stable", result.SnapshotStatus);
            Assert.AreEqual(requested.Count, result.RequestedRegisterCount);
            Assert.AreEqual(2, result.CapturedRegisterCount);
            CollectionAssert.AreEqual(
                new[] { "General Purpose", "Instruction Pointer" },
                result.AvailableCategories);
            CollectionAssert.Contains(result.UnavailableRegisters, "ESP");
            CollectionAssert.DoesNotContain(result.UnavailableRegisters, "EIP");
        }

        [TestMethod]
        public void RegisterCapturePolicyDoesNotReportAnEmptyOrMixedSnapshotAsComplete()
        {
            IReadOnlyList<string> requested = RegisterCapturePolicy.BuildRegisterNames(TargetMachineArchitecture.X64);
            var unavailable = new RegistersResult { Is64Bit = true };
            RegisterCapturePolicy.Complete(unavailable, requested, requested, true, null);
            Assert.AreEqual("unavailable", unavailable.Status);
            Assert.AreEqual("REGISTERS_UNAVAILABLE", unavailable.ErrorCode);
            Assert.IsTrue(unavailable.Partial);
            Assert.AreEqual(0, unavailable.AvailableCategories.Count);

            var unstable = new RegistersResult
            {
                Is64Bit = true,
                Registers = new List<RegisterInfo>
                {
                    new RegisterInfo { Name = "RIP", Category = "Instruction Pointer" },
                    new RegisterInfo { Name = "RSP", Category = "Stack" },
                },
            };
            RegisterCapturePolicy.Complete(
                unstable,
                requested,
                requested.Where(name => name != "rip" && name != "rsp").ToArray(),
                false,
                null);
            Assert.AreEqual("unstable", unstable.Status);
            Assert.AreEqual("STALE_DEBUG_CONTEXT", unstable.ErrorCode);
            Assert.AreEqual("unstable", unstable.SnapshotStatus);
        }

        [TestMethod]
        public void CompositeSnapshotPolicyPropagatesPartialAndUnstableChildren()
        {
            Assert.IsFalse(SnapshotCompletenessPolicy.IsIncomplete(new RegistersResult
            {
                Status = "complete",
                SnapshotStatus = "stable",
            }));
            Assert.IsTrue(SnapshotCompletenessPolicy.IsIncomplete(new RegistersResult
            {
                Status = "partial",
                SnapshotStatus = "stable",
            }));
            var bestEffortModules = new ModulesResult
            {
                SnapshotStable = false,
                SnapshotStatus = "best_effort",
            };
            Assert.IsTrue(SnapshotCompletenessPolicy.IsIncomplete(bestEffortModules));
            Assert.IsTrue(SnapshotCompletenessPolicy.IsUnstable(bestEffortModules));
            var truncatedOutput = new OutputWindowResult
            {
                Partial = true,
                Truncated = true,
                SnapshotStable = true,
                SnapshotStatus = "stable",
            };
            Assert.IsTrue(SnapshotCompletenessPolicy.IsIncomplete(truncatedOutput));
            Assert.IsFalse(SnapshotCompletenessPolicy.IsUnstable(truncatedOutput));
            Assert.IsTrue(SnapshotCompletenessPolicy.IsIncomplete(new DisassemblyResult
            {
                Partial = true,
            }));
            Assert.IsTrue(SnapshotCompletenessPolicy.IsIncomplete(new BreakpointsResult
            {
                HasMore = true,
                SnapshotStable = true,
                SnapshotStatus = "ok",
            }));
        }

        [TestMethod]
        public void CommandResultStoreRetainsAtMostOnceTombstonesAfterResponseEviction()
        {
            DateTime now = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
            var store = new CommandResultStore(2, TimeSpan.FromMinutes(1), () => now, tombstoneCapacity: 4);
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("one", "fingerprint-a", out _, out CommandReservationLease one));
            store.Complete(one, "response-a");

            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("one", "fingerprint-a", out string response, out _));
            Assert.AreEqual("response-a", response);
            Assert.AreEqual(CommandReservationStatus.Conflict,
                store.ReserveOrLookup("one", "fingerprint-b", out _, out _));

            now = now.AddMinutes(2);
            Assert.AreEqual(CommandReservationStatus.ResultEvicted,
                store.ReserveOrLookup("one", "fingerprint-a", out _, out _));

            foreach (string id in new[] { "two", "three", "four" })
            {
                Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                    store.ReserveOrLookup(id, id, out _, out CommandReservationLease lease));
                store.Complete(lease, "response-" + id);
            }
            Assert.AreEqual(CommandReservationStatus.ResultEvicted,
                store.ReserveOrLookup("two", "two", out _, out _));
            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("three", "three", out _, out _));
            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("four", "four", out _, out _));
            Assert.AreEqual(CommandReservationStatus.CapacityExceeded,
                store.ReserveOrLookup("five", "five", out _, out _));
            Assert.AreEqual(CommandReservationStatus.ResultEvicted,
                store.ReserveOrLookup("one", "fingerprint-a", out _, out _));
        }

        [TestMethod]
        public void AnyAuthenticatedLaneCanRecoverButCannotChangeAnExistingCommand()
        {
            var store = new CommandResultStore(
                8,
                TimeSpan.FromMinutes(10));
            Assert.AreEqual(
                CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("old", "request", "fingerprint", out _, out CommandReservationLease lease));

            Assert.AreEqual(
                CommandReservationStatus.InProgress,
                store.ReserveOrLookup("new", "request", "fingerprint", out _, out _));
            Assert.AreEqual(
                CommandReservationStatus.Conflict,
                store.ReserveOrLookup("new", "request", "changed", out _, out _));

            store.Complete(lease, "definitive");
            Assert.AreEqual(
                CommandReservationStatus.Cached,
                store.ReserveOrLookup("new", "request", "fingerprint", out string cached, out _));
            Assert.AreEqual("definitive", cached);
            Assert.IsTrue(store.TryGetById("new", "request", out string queried, out _, out bool evicted));
            Assert.AreEqual("definitive", queried);
            Assert.IsFalse(evicted);
        }

        [TestMethod]
        public void ActiveRequestRecoveryRequiresTheOriginalClientGenerationToBeDead()
        {
            const string requestId = "019f0000-0000-7000-8000-000000000001";
            var expected = new object();
            var requests = new Dictionary<string, object>
            {
                [ClientLaneRecovery.CreateRequestKey("123:456", requestId)] = expected,
            };

            Assert.IsFalse(ClientLaneRecovery.TryFindDeadLaneRequest(
                requests, "789:999", requestId, _ => true, out _, out _));
            Assert.IsTrue(ClientLaneRecovery.TryFindDeadLaneRequest(
                requests, "789:999", requestId, _ => false, out string recoveredKey, out object recovered));
            Assert.AreSame(expected, recovered);
            Assert.AreEqual(ClientLaneRecovery.CreateRequestKey("123:456", requestId), recoveredKey);
        }

        [TestMethod]
        public void CommandResultStoreUsesOneServerGlobalRequestIdNamespace()
        {
            var store = new CommandResultStore(
                responseCapacity: 2,
                responseTtl: TimeSpan.FromMinutes(1),
                tombstoneCapacity: 2);
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("100:111", "shared", "a", out _, out CommandReservationLease shared));
            Assert.AreEqual(CommandReservationStatus.InProgress,
                store.ReserveOrLookup("200:222", "shared", "a", out _, out _));
            Assert.AreEqual(CommandReservationStatus.Conflict,
                store.ReserveOrLookup("200:222", "shared", "changed", out _, out _));
            store.Complete(shared, "a");
            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("200:222", "shared", "a", out string cached, out _));
            Assert.AreEqual("a", cached);
        }

        [TestMethod]
        public void CommandResultStoreRejectsNewCommandsAtTombstoneCapacity()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new CommandResultStore(
                1,
                TimeSpan.FromMinutes(10),
                () => now,
                tombstoneCapacity: 2);

            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("future-lane", "future", "f", out _, out CommandReservationLease future));
            store.Complete(future, "future-result");
            now = now.AddSeconds(1);
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("normal-lane", "normal", "n", out _, out CommandReservationLease normal));
            store.Complete(normal, "normal-result");

            now = now.AddSeconds(1);
            Assert.AreEqual(CommandReservationStatus.CapacityExceeded,
                store.ReserveOrLookup("normal-lane", "next", "n2", out _, out _));
            Assert.IsTrue(store.TryGetById("future-lane", "future", out _, out _, out _));
            Assert.IsTrue(store.TryGetById("normal-lane", "normal", out _, out _, out _));
        }

        [TestMethod]
        public void CommandResultStoreNeverEvictsLateCompletionTombstones()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new CommandResultStore(
                1,
                TimeSpan.FromMinutes(10),
                () => now,
                tombstoneCapacity: 2);

            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("late-lane", "old", "old-fingerprint", out _, out CommandReservationLease late));
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("other-lane", "other", "other-fingerprint", out _, out CommandReservationLease other));
            store.Complete(other, "other-result");
            now = now.AddMinutes(1);
            store.Complete(late, "late-result");

            Assert.AreEqual(CommandReservationStatus.CapacityExceeded,
                store.ReserveOrLookup("third-lane", "third", "third-fingerprint", out _, out _));
            Assert.IsTrue(store.TryGetById("other-lane", "other", out _, out _, out _));
            Assert.IsTrue(store.TryGetById("late-lane", "old", out string response, out _, out _));
            Assert.AreEqual("late-result", response);
        }

        [TestMethod]
        public void CommandResultStoreConcurrentReservationHasSingleOwner()
        {
            var store = new CommandResultStore(8, TimeSpan.FromMinutes(1));
            var barrier = new Barrier(3);
            var statuses = new CommandReservationStatus[2];
            var leases = new CommandReservationLease[2];
            Task[] tasks = new Task[2];
            for (int index = 0; index < tasks.Length; index++)
            {
                int capture = index;
                tasks[index] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    statuses[capture] = store.ReserveOrLookup(
                        "same-id", "same-fingerprint", out _, out leases[capture]);
                });
            }
            barrier.SignalAndWait();
            Task.WaitAll(tasks);

            Assert.AreEqual(1, Array.FindAll(statuses, s => s == CommandReservationStatus.ReservedByCaller).Length);
            Assert.AreEqual(1, Array.FindAll(statuses, s => s == CommandReservationStatus.InProgress).Length);
            CommandReservationLease owner = Array.Find(leases, lease => lease != null);
            var forged = new CommandReservationLease
            {
                RequestId = owner.RequestId,
                Fingerprint = owner.Fingerprint,
                Token = new object(),
            };
            store.ReleaseReservation(forged);
            store.Complete(owner, "completed-once");
            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("same-id", "same-fingerprint", out string cached, out _));
            Assert.AreEqual("completed-once", cached);
        }

        [TestMethod]
        public void CommandResultStoreEvictsBodiesByUtf8ByteBudgetWithoutDroppingTombstones()
        {
            var store = new CommandResultStore(
                responseCapacity: 8,
                responseTtl: TimeSpan.FromMinutes(1),
                tombstoneCapacity: 16,
                responseByteCapacity: 10);
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("one", "one", out _, out CommandReservationLease one));
            store.Complete(one, "12345678");
            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("two", "two", out _, out CommandReservationLease two));
            store.Complete(two, "abcdefgh");

            Assert.AreEqual(CommandReservationStatus.ResultEvicted,
                store.ReserveOrLookup("one", "one", out _, out _));
            Assert.AreEqual(CommandReservationStatus.Cached,
                store.ReserveOrLookup("two", "two", out string retained, out _));
            Assert.AreEqual("abcdefgh", retained);

            Assert.AreEqual(CommandReservationStatus.ReservedByCaller,
                store.ReserveOrLookup("large", "large", out _, out CommandReservationLease large));
            store.Complete(large, "01234567890");
            Assert.AreEqual(CommandReservationStatus.ResultEvicted,
                store.ReserveOrLookup("large", "large", out _, out _));
        }

        [TestMethod]
        public void MethodCatalogDefinesRiskAndControlSemantics()
        {
            Assert.AreEqual("command", DebugMethodCatalog.Get("continue").Kind);
            Assert.AreEqual("debug.execution.control", DebugMethodCatalog.Get("continue").Capability);
            CollectionAssert.AreEquivalent(
                new[] { "debug.execution.control", "debug.memory.read", "debug.read" },
                new System.Collections.Generic.List<string>(
                    DebugMethodCatalog.Get("continue_and_capture_breakpoint").RequiredCapabilities));
            Assert.AreEqual("query", DebugMethodCatalog.Get("capture_debug_bundle_safe").Kind);
            Assert.AreEqual("debug.read", DebugMethodCatalog.Get("capture_debug_bundle_safe").Capability);
            CollectionAssert.AreEquivalent(
                new[] { "debug.execution.control", "debug.read" },
                new System.Collections.Generic.List<string>(
                    DebugMethodCatalog.Get("continue_and_wait_for_break_safe").RequiredCapabilities));
            Assert.IsTrue(DebugMethodCatalog.Get("inspect_managed_type").OfflineInspection);
            CollectionAssert.Contains(
                new System.Collections.Generic.List<string>(DebugMethodCatalog.Get("inspect_managed_type").RequiredParameters),
                "typeName");
            Assert.AreEqual("debug.memory.read", DebugMethodCatalog.Get("read_memory").Capability);
            Assert.AreEqual("command", DebugMethodCatalog.Get("resolve_address").Kind);
            Assert.AreEqual("debug.evaluate.unsafe", DebugMethodCatalog.Get("resolve_address").Capability);
            foreach (string unsafeInspection in new[] { "get_locals", "get_full_context", "get_exception" })
            {
                Assert.AreEqual("command", DebugMethodCatalog.Get(unsafeInspection).Kind);
                Assert.IsTrue(DebugMethodCatalog.Get(unsafeInspection).RequiresStableDebugContext);
            }
            Assert.AreEqual("debug.context.write", DebugMethodCatalog.Get("switch_stack_frame").Capability);
            CollectionAssert.AreEquivalent(
                new[] { "debug.context.write", "debug.execution.control" },
                DebugMethodCatalog.Get("set_next_statement").RequiredCapabilities.ToArray());
            Assert.IsFalse(DebugMethodCatalog.Get("inspect_managed_assembly").RequiresStableDebugSession);
            Assert.IsTrue(DebugMethodCatalog.Get("get_locals").RequiresStableDebugSession);
            Assert.AreEqual("paused", DebugMethodCatalog.Get("continue").RequiredState);
            Assert.AreEqual("running", DebugMethodCatalog.Get("break").RequiredState);
            Assert.AreEqual("debugging", DebugMethodCatalog.Get("stop").RequiredState);
            foreach (string waitMethod in new[]
            {
                "continue_and_wait_for_break",
                "continue_and_wait_for_break_safe",
                "continue_and_capture_native",
                "continue_and_capture_breakpoint",
            })
            {
                Assert.AreEqual("debugging", DebugMethodCatalog.Get(waitMethod).RequiredState);
                Assert.IsTrue(DebugMethodCatalog.Get(waitMethod).RequiresStableDebugContext);
            }
            Assert.IsTrue(DebugMethodCatalog.Get("cancel_request").ControlPlane);
            Assert.AreEqual("command", DebugMethodCatalog.Get("cancel_request").Kind);
            Assert.IsTrue(DebugMethodCatalog.Get("continue_and_wait_for_break").LongRunningWait);
            Assert.IsFalse(DebugMethodCatalog.Get("continue").LongRunningWait);
            Assert.IsTrue(DebugMethodCatalog.Get("get_debug_status").ControlPipeEligible);
            Assert.IsTrue(DebugMethodCatalog.Get("break").ControlPipeEligible);
            Assert.IsTrue(DebugMethodCatalog.Get("stop").ControlPipeEligible);
            Assert.IsTrue(DebugMethodCatalog.Get("detach").ControlPipeEligible);
            Assert.IsFalse(DebugMethodCatalog.Get("continue").ControlPipeEligible);
            Assert.IsTrue(DebugMethodCatalog.Get("get_debug_status").SessionRefresh);
            Assert.IsNull(DebugMethodCatalog.Get("does_not_exist"));
        }

        [TestMethod]
        public void WaitTimeoutsRemainStructuredCommandResults()
        {
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new ContinueAndWaitResult { TimedOut = true, IssuedContinue = true }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new NativeContinueResult { TimedOut = true, IssuedContinue = true }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new BreakpointCaptureResult { TimedOut = true, IssuedContinue = true }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new ProcessDebugResult
                {
                    Success = false,
                    ProcessStarted = true,
                    ProcessId = 1234,
                    ErrorCode = "ATTACH_DISCOVERY_FAILED",
                }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new BreakpointOperationResult
                {
                    Success = false,
                    CommandIssued = true,
                    AffectedCount = 3,
                    ErrorCode = "OUTCOME_UNKNOWN",
                }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new ProcessDebugResult
                {
                    Success = false,
                    CommandIssued = true,
                    ProcessStarted = true,
                    ProcessId = 4321,
                    ProcessStartTimeUtcTicks = 638000000000000000,
                    ErrorCode = "OUTCOME_UNKNOWN",
                    Outcome = "process_started_attach_outcome_unknown",
                }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new BreakpointOperationResult
                {
                    Success = false,
                    CommandIssued = true,
                    AffectedCount = 3,
                    ErrorCode = "POST_COMMAND_OBSERVATION_FAILED",
                }));
            Assert.IsFalse(OperationResultPolicy.ShouldReturnStructuredResult(
                new ContinueAndWaitResult { TimedOut = false }));
            Assert.IsFalse(OperationResultPolicy.ShouldReturnStructuredResult(
                new DebugControlResult { Success = false }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new DebugControlResult
                {
                    Success = false,
                    UiNavigationIssued = true,
                    ErrorCode = "OUTCOME_UNKNOWN",
                }));
            Assert.IsTrue(OperationResultPolicy.ShouldReturnStructuredResult(
                new ProcessDebugResult
                {
                    Success = false,
                    UiNavigationIssued = true,
                    ErrorCode = "DEBUG_OPERATION_FAILED",
                    Outcome = "ui_prepared_command_not_issued",
                }));
            Assert.AreEqual(
                "COMMAND_NOT_STARTED",
                OperationResultPolicy.NormalizeFailureCode(
                    new DebugControlResult
                    {
                        Success = false,
                        CommandIssued = false,
                        ErrorCode = "OUTCOME_UNKNOWN",
                    },
                    "OUTCOME_UNKNOWN"));
            Assert.AreEqual(
                "OUTCOME_UNKNOWN",
                OperationResultPolicy.NormalizeFailureCode(
                    new DebugControlResult
                    {
                        Success = false,
                        CommandIssued = true,
                        ErrorCode = "OUTCOME_UNKNOWN",
                    },
                    "OUTCOME_UNKNOWN"));
            Assert.AreEqual(
                "OUTCOME_UNKNOWN",
                OperationResultPolicy.NormalizeFailureCode(
                    new DebugControlResult
                    {
                        Success = false,
                        UiNavigationIssued = true,
                        ErrorCode = "OUTCOME_UNKNOWN",
                    },
                    "OUTCOME_UNKNOWN"));
        }

        [TestMethod]
        public void OversizedCommandResponsePreservesKnownOutcomeFields()
        {
            var details = CommandResponseCompactor.ExtractOutcome(
                "{\"status\":\"ok\",\"result\":{\"success\":false," +
                "\"commandIssued\":true,\"outcome\":\"created_original_probe_failed\"," +
                "\"affectedCount\":1,\"large\":\"ignored\"}}",
                "set_breakpoint",
                5000000);

            Assert.AreEqual(true, details["commandCompleted"]);
            Assert.AreEqual(true, details["responseTruncated"]);
            Assert.AreEqual(true, details["commandIssued"]);
            Assert.AreEqual("created_original_probe_failed", details["outcome"]);
            Assert.AreEqual(1, details["affectedCount"]);
            Assert.IsFalse(details.ContainsKey("large"));
        }

        [TestMethod]
        public void RequestContextTracksAndRebasesBreakIdentity()
        {
            using (DebuggerRequestContext.Push(
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
                "request",
                "command",
                7,
                true,
                11,
                13,
                true,
                CancellationToken.None))
            {
                Assert.AreEqual(7L, DebuggerRequestContext.ExpectedDebugSessionGeneration);
                Assert.AreEqual(11L, DebuggerRequestContext.ExpectedBreakSequence);
                Assert.AreEqual(13L, DebuggerRequestContext.ExpectedContextEpoch);
                Assert.IsTrue(DebuggerRequestContext.RequiresStableDebugContext);
                DebuggerRequestContext.RebaseDebuggerIdentity(8, 12, 14);
                Assert.AreEqual(8L, DebuggerRequestContext.ExpectedDebugSessionGeneration);
                Assert.AreEqual(12L, DebuggerRequestContext.ExpectedBreakSequence);
                Assert.AreEqual(14L, DebuggerRequestContext.ExpectedContextEpoch);
            }
        }

        [TestMethod]
        public void ReadOnlyBundleCannotRequestEvaluationLikeComponents()
        {
            DebugMethodDescriptor safeBundle = DebugMethodCatalog.Get("capture_debug_bundle_safe");
            var allowed = new System.Collections.Generic.List<string>(safeBundle.AllowedParameters);
            CollectionAssert.DoesNotContain(allowed, "expressions");
            CollectionAssert.DoesNotContain(allowed, "includeLocals");
            CollectionAssert.DoesNotContain(allowed, "includeRegisters");
            CollectionAssert.DoesNotContain(allowed, "includeException");
            Assert.AreEqual("debug.evaluate.unsafe", DebugMethodCatalog.Get("get_exception").Capability);
            var registerBundleCapabilities = DebugMethodCatalog.GetRequiredCapabilities(
                DebugMethodCatalog.Get("capture_debug_bundle"),
                new System.Collections.Generic.Dictionary<string, object> { ["includeRegisters"] = true });
            CollectionAssert.Contains(
                new System.Collections.Generic.List<string>(registerBundleCapabilities),
                "debug.memory.read");
        }

        [TestMethod]
        public void ActiveRequestCancelAndCompleteAreRaceSafe()
        {
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                var request = new ActiveRequest
                {
                    Cancellation = new CancellationTokenSource(),
                    Kind = "query",
                    EnqueuedUtc = DateTime.UtcNow,
                };
                Parallel.Invoke(
                    () => request.TryCancel(),
                    () => request.Complete());
                Assert.IsFalse(request.TryCancel());
            }
        }

        [TestMethod]
        public void RequestSchemaRejectsUnknownAndMissingParametersAndAppliesMethodDeadline()
        {
            var request = new DebugRequest
            {
                Method = "read_memory",
                Params = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["address"] = "0x1000",
                    ["szie"] = 16,
                },
                Meta = new DebugRequestMetadata
                {
                    Kind = "query",
                    Capability = "debug.memory.read",
                    DeadlineUnixMs = 0,
                },
            };

            var unknown = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(request, 1000));
            Assert.AreEqual("INVALID_ARGUMENT", unknown.Code);
            StringAssert.Contains(unknown.Message, "params.szie");

            request.Params.Remove("szie");
            DebugMethodDescriptor descriptor = DebugRequestSchemaValidator.Validate(request, 1000);
            Assert.AreEqual(1000 + descriptor.DefaultDeadlineMs, request.Meta.DeadlineUnixMs);

            request.Params.Clear();
            var missing = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(request, 1000));
            Assert.AreEqual("INVALID_ARGUMENT", missing.Code);
            StringAssert.Contains(missing.Message, "params.address");
        }

        [TestMethod]
        public void RequestSchemaBoundsBothModuleSnapshotTokenKinds()
        {
            DebugRequest Request(string key, string value) => new DebugRequest
            {
                Method = "get_modules",
                Params = new Dictionary<string, object> { [key] = value },
                Meta = new DebugRequestMetadata
                {
                    Kind = "query",
                    Capability = "debug.read",
                },
            };

            foreach (string key in new[] { "moduleSnapshotId", "scanContinuationToken" })
            {
                var blank = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                    DebugRequestSchemaValidator.Validate(Request(key, "   "), 1000, validateDeadline: false));
                Assert.AreEqual("INVALID_ARGUMENT", blank.Code);
                var oversized = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                    DebugRequestSchemaValidator.Validate(Request(key, new string('x', 129)), 1000, validateDeadline: false));
                Assert.AreEqual("INVALID_ARGUMENT", oversized.Code);
                DebugRequestSchemaValidator.Validate(
                    Request(key, new string('x', 128)), 1000, validateDeadline: false);
            }
        }

        [TestMethod]
        public void RequestSchemaRejectsAmbiguousAttachAndIgnoredLaunchArguments()
        {
            DebugRequest Request(string method, string capability, Dictionary<string, object> parameters) =>
                new DebugRequest
                {
                    Method = method,
                    Params = parameters,
                    Meta = new DebugRequestMetadata { Kind = "command", Capability = capability },
                };

            foreach (var parameters in new[]
            {
                new Dictionary<string, object>(),
                new Dictionary<string, object> { ["processId"] = 42, ["processName"] = "target.exe" },
                new Dictionary<string, object> { ["processName"] = "   " },
            })
            {
                var error = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                    DebugRequestSchemaValidator.Validate(
                        Request("attach_process", "debug.session.write", parameters), 1000));
                Assert.AreEqual("INVALID_ARGUMENT", error.Code);
            }

            foreach (var parameters in new[]
            {
                new Dictionary<string, object> { ["arguments"] = "--ignored" },
                new Dictionary<string, object> { ["executablePath"] = "crash.dmp", ["attach"] = false },
                new Dictionary<string, object> { ["executablePath"] = "   " },
                new Dictionary<string, object> { ["executablePath"] = "app.exe", ["workingDirectory"] = "   " },
            })
            {
                var error = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                    DebugRequestSchemaValidator.Validate(
                        Request("start_debugging", "debug.session.write", parameters), 1000));
                Assert.AreEqual("INVALID_ARGUMENT", error.Code);
            }
        }

        [TestMethod]
        public void SearchPatternParserPreservesTextEncodingAndRejectsAmbiguity()
        {
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("中文"),
                SearchPatternParser.Parse("中文", "text"));
            CollectionAssert.AreEqual(
                new byte[] { 0x41, 0x42 },
                SearchPatternParser.Parse("41 42", "auto"));
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes("4142"),
                SearchPatternParser.Parse("4142", "auto"));

            Assert.ThrowsExactly<DebuggerParameterValidationException>(() =>
                SearchPatternParser.Parse("中文", "ascii"));
            Assert.ThrowsExactly<DebuggerParameterValidationException>(() =>
                SearchPatternParser.Parse("ABC", "hex"));
            Assert.ThrowsExactly<DebuggerParameterValidationException>(() =>
                SearchPatternParser.Parse("GG", "bytes"));

            var request = new DebugRequest
            {
                Method = "search_memory",
                Params = new Dictionary<string, object>
                {
                    ["startAddress"] = "0x1000",
                    ["pattern"] = "GG",
                    ["patternFormat"] = "hex",
                },
                Meta = new DebugRequestMetadata { Kind = "query", Capability = "debug.memory.read" },
            };
            var error = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(request, 1000));
            Assert.AreEqual("INVALID_ARGUMENT", error.Code);
        }

        [TestMethod]
        public async Task DebugSessionActorExecutesFifoAndRejectsExpiredQueuedWork()
        {
            using (var actor = new DebugSessionActor(4))
            using (var releaseFirst = new ManualResetEventSlim(false))
            {
                var order = new List<int>();
                Task<int> first = actor.EnqueueAsync(() =>
                {
                    lock (order) order.Add(1);
                    releaseFirst.Wait(TimeSpan.FromSeconds(2));
                    return 1;
                }, DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(), CancellationToken.None);

                SpinWait.SpinUntil(() => { lock (order) return order.Count == 1; }, 1000);
                Task<int> second = actor.EnqueueAsync(() =>
                {
                    lock (order) order.Add(2);
                    return 2;
                }, DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(), CancellationToken.None);
                Task<int> expired = actor.EnqueueAsync(() => 3,
                    DateTimeOffset.UtcNow.AddMilliseconds(50).ToUnixTimeMilliseconds(),
                    CancellationToken.None);

                await Task.Delay(100);
                releaseFirst.Set();
                Assert.AreEqual(1, await first);
                Assert.AreEqual(2, await second);
                await Assert.ThrowsExactlyAsync<MainThreadDispatchTimeoutException>(async () => await expired);
                lock (order) CollectionAssert.AreEqual(new[] { 1, 2 }, order);
                actor.Stop();
                await actor.Completion;
            }
        }

        [TestMethod]
        public async Task DebugSessionActorStopBetweenSlotAndAdmissionCannotOrphanWork()
        {
            using (var admissionReached = new ManualResetEventSlim(false))
            using (var releaseAdmission = new ManualResetEventSlim(false))
            using (var actor = new DebugSessionActor(
                1,
                () =>
                {
                    admissionReached.Set();
                    releaseAdmission.Wait();
                }))
            {
                Task<int> enqueue = Task.Run(() => actor.EnqueueAsync(
                    () => 42,
                    DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(),
                    CancellationToken.None));
                Assert.IsTrue(admissionReached.Wait(TimeSpan.FromSeconds(2)));
                actor.Stop();
                releaseAdmission.Set();

                try
                {
                    await enqueue;
                    Assert.Fail("Work admitted after actor stop");
                }
                catch (OperationCanceledException) { }
                await actor.Completion;
                Assert.IsTrue(enqueue.IsCompleted);
            }
        }

        [TestMethod]
        public async Task DebugSessionActorDisposeWaitsForAllCapacityWaitersToExit()
        {
            using (var running = new ManualResetEventSlim(false))
            using (var releaseRunning = new ManualResetEventSlim(false))
            {
                int admissions = 0;
                using (var secondQueued = new ManualResetEventSlim(false))
                {
                    var actor = new DebugSessionActor(
                        1,
                        () =>
                        {
                            if (Interlocked.Increment(ref admissions) == 2) secondQueued.Set();
                        });
                    Task<int> first = Task.Run(() => actor.EnqueueAsync(
                        () =>
                        {
                            running.Set();
                            releaseRunning.Wait();
                            return 1;
                        },
                        DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(),
                        CancellationToken.None));
                    Assert.IsTrue(running.Wait(TimeSpan.FromSeconds(2)));
                    Task<int> second = Task.Run(() => actor.EnqueueAsync(
                        () => 2,
                        DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(),
                        CancellationToken.None));
                    Assert.IsTrue(secondQueued.Wait(TimeSpan.FromSeconds(2)));

                    Task<int>[] waiters = Enumerable.Range(0, 4)
                        .Select(index => Task.Run(() => actor.EnqueueAsync(
                            () => 10 + index,
                            DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds(),
                            CancellationToken.None)))
                        .ToArray();
                    await Task.Delay(50);
                    actor.Dispose();

                    Task allWaiters = Task.WhenAll(waiters.Select(async waiter =>
                    {
                        try
                        {
                            await waiter;
                            Assert.Fail("Capacity waiter was admitted after actor disposal");
                        }
                        catch (OperationCanceledException) { }
                    }));
                    Assert.AreSame(allWaiters, await Task.WhenAny(allWaiters, Task.Delay(2000)));
                    releaseRunning.Set();
                    Assert.AreEqual(1, await first);
                    try
                    {
                        await second;
                        Assert.Fail("Queued work executed after actor disposal");
                    }
                    catch (RequestCancelledNotStartedException) { }
                    await actor.Completion;
                }
            }
        }

        [TestMethod]
        public async Task SplitPhaseWaitAllowsStatusAndBreakButRejectsConflictingMutation()
        {
            var gate = new LongRunningDebugOperationGate();
            Assert.IsTrue(gate.TryAcquire("wait-id", "continue_and_wait_for_break", out LongRunningDebugOperationLease lease));
            using (lease)
            using (var breakObserved = new ManualResetEventSlim(false))
            using (var actor = new DebugSessionActor(4))
            {
                Task<bool> waitOutsideActor = Task.Run(() => breakObserved.Wait(TimeSpan.FromSeconds(3)));
                Assert.IsFalse(gate.TryGetConflict("get_debug_status", "query", out _));
                Assert.IsFalse(gate.TryGetConflict("break", "command", out _));
                Assert.IsTrue(gate.TryGetConflict("continue", "command", out string activeMethod));
                Assert.AreEqual("continue_and_wait_for_break", activeMethod);
                Assert.IsFalse(gate.TryAcquire("second", "continue_and_capture_native", out _));
                Assert.IsFalse(gate.TryAcquireCommand(
                    "continue", "command", out _, out string atomicConflict));
                Assert.AreEqual("continue_and_wait_for_break", atomicConflict);
                Assert.IsTrue(gate.TryAcquireCommand("break", "command", out LongRunningDebugOperationLease interruptLease, out _));
                Assert.IsNull(interruptLease);
                Assert.IsTrue(gate.TryAcquireCommand("cancel_request", "command", out LongRunningDebugOperationLease cancelLease, out _));
                Assert.IsNull(cancelLease);

                int status = await actor.EnqueueAsync(
                    () => 7,
                    DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
                    CancellationToken.None);
                Assert.AreEqual(7, status);
                await actor.EnqueueAsync(
                    () =>
                    {
                        breakObserved.Set();
                        return true;
                    },
                    DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
                    CancellationToken.None);
                Assert.IsTrue(await waitOutsideActor);
                actor.Stop();
                await actor.Completion;
            }
            Assert.IsFalse(gate.TryGetConflict("continue", "command", out _));
        }

        [TestMethod]
        public void LongWaitAdmissionIsAtomicAgainstAlreadyAdmittedCommands()
        {
            var gate = new LongRunningDebugOperationGate();
            Assert.IsTrue(gate.TryAcquireCommand(
                "continue", "command", out LongRunningDebugOperationLease commandLease, out _));
            Assert.IsNotNull(commandLease);
            Assert.IsFalse(gate.TryAcquire("wait", "continue_and_wait_for_break", out _));

            commandLease.Dispose();
            Assert.IsTrue(gate.TryAcquire(
                "wait", "continue_and_wait_for_break", out LongRunningDebugOperationLease waitLease));
            Assert.IsFalse(gate.TryAcquireCommand("step_over", "command", out _, out _));
            waitLease.Dispose();

            Assert.IsTrue(gate.TryAcquireCommand(
                "step_over", "command", out LongRunningDebugOperationLease afterLease, out _));
            afterLease.Dispose();
        }

        [TestMethod]
        public void CommandDispatchFailureIsUnknownAfterActorEntry()
        {
            Assert.AreEqual(
                "OUTCOME_UNKNOWN",
                RequestOutcomePolicy.ClassifyNotStartedFailure(true, true, "DEADLINE_EXCEEDED_NOT_STARTED"));
            Assert.AreEqual(
                "DEADLINE_EXCEEDED_NOT_STARTED",
                RequestOutcomePolicy.ClassifyNotStartedFailure(true, false, "DEADLINE_EXCEEDED_NOT_STARTED"));
            Assert.AreEqual(
                "CANCELLED_NOT_STARTED",
                RequestOutcomePolicy.ClassifyNotStartedFailure(false, true, "CANCELLED_NOT_STARTED"));
        }

        [TestMethod]
        public void UnsafeEvaluationBoundaryMarksOnlyActualEvaluationAndInvalidatesOnce()
        {
            int invalidations = 0;
            int sideEffects = 0;
            using (DebuggerRequestContext.Push(
                DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds(),
                "evaluation-test",
                "command",
                1,
                true,
                2,
                3,
                true,
                CancellationToken.None,
                mayEvaluateUserCode: true,
                onSideEffectIssued: () => sideEffects++))
            {
                // Preflight/empty-expression paths do not cross the evaluator boundary.
                Assert.IsFalse(DebuggerRequestContext.SideEffectIssued);
                Assert.AreEqual(0, invalidations);

                Assert.IsTrue(DebuggerRequestContext.MarkUnsafeEvaluationIssued(() => invalidations++));
                Assert.IsTrue(DebuggerRequestContext.SideEffectIssued);
                Assert.AreEqual(1, invalidations);
                Assert.AreEqual(1, sideEffects);

                Assert.IsTrue(DebuggerRequestContext.MarkUnsafeEvaluationIssued(() => invalidations++));
                Assert.AreEqual(1, invalidations);
                Assert.AreEqual(1, sideEffects);
            }
        }

        [TestMethod]
        public void RequestSchemaRejectsInvalidOutputRegex()
        {
            var request = new DebugRequest
            {
                Method = "search_output",
                Params = new Dictionary<string, object>
                {
                    ["pattern"] = "(",
                    ["isRegex"] = true,
                },
                Meta = new DebugRequestMetadata { Kind = "query", Capability = "debug.read" },
            };
            var error = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(request, 1000));
            Assert.AreEqual("INVALID_ARGUMENT", error.Code);
        }

        [TestMethod]
        public void RequestSchemaAcceptsBoundedOutputCursorAndStepWaitTimeout()
        {
            var outputRequest = new DebugRequest
            {
                Method = "search_output",
                Params = new Dictionary<string, object>
                {
                    ["pattern"] = "error",
                    ["startLine"] = 10,
                    ["endCharacter"] = 2000,
                    ["expectedCaptureStartCharacter"] = 1000,
                },
                Meta = new DebugRequestMetadata { Kind = "query", Capability = "debug.read" },
            };
            DebugRequestSchemaValidator.Validate(outputRequest, 1000);

            var stepRequest = new DebugRequest
            {
                Method = "step_over",
                Params = new Dictionary<string, object> { ["waitTimeoutMs"] = 60000 },
                Meta = new DebugRequestMetadata { Kind = "command", Capability = "debug.execution.control" },
            };
            DebugRequestSchemaValidator.Validate(stepRequest, 1000);

            outputRequest.Params["expectedCaptureStartCharacter"] = -2;
            Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(outputRequest, 1000));
        }

        [TestMethod]
        public void CommandSchemaRejectsTypesRangesAndLongDeadlineBeforeAdmission()
        {
            DebugRequest Request(object lineNumber) => new DebugRequest
            {
                Method = "set_breakpoint",
                Params = new Dictionary<string, object>
                {
                    ["filePath"] = "target.cpp",
                    ["lineNumber"] = lineNumber,
                },
                Meta = new DebugRequestMetadata
                {
                    Kind = "command",
                    Capability = "debug.breakpoint.write",
                    DeadlineUnixMs = 1000 + (31L * 60L * 1000L),
                },
            };

            var wrongType = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(Request("42"), 1000, validateDeadline: false));
            Assert.AreEqual("INVALID_ARGUMENT", wrongType.Code);

            var outOfRange = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(Request(0), 1000, validateDeadline: false));
            Assert.AreEqual("INVALID_ARGUMENT", outOfRange.Code);

            DebugRequest valid = Request(42);
            DebugMethodDescriptor descriptor = DebugRequestSchemaValidator.Validate(valid, 1000, validateDeadline: false);
            var horizon = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.ValidateDeadlineHorizon(valid, 1000, descriptor));
            Assert.AreEqual("INVALID_ARGUMENT", horizon.Code);
            valid.Meta.DeadlineUnixMs = 1000 + descriptor.DefaultDeadlineMs;
            DebugRequestSchemaValidator.ValidateDeadlineHorizon(valid, 1000, descriptor);

            valid.Params["condition"] = new Dictionary<string, object>();
            var nested = Assert.ThrowsExactly<DebugRequestSchemaException>(() =>
                DebugRequestSchemaValidator.Validate(valid, 1000, validateDeadline: false));
            Assert.AreEqual("INVALID_ARGUMENT", nested.Code);
        }

        [TestMethod]
        public void PlainBreakpointsDoNotRequireUnsafeEvaluationButConditionsDo()
        {
            DebugMethodDescriptor descriptor = DebugMethodCatalog.Get("set_breakpoint");
            CollectionAssert.AreEqual(
                new[] { "debug.breakpoint.write" },
                DebugMethodCatalog.GetRequiredCapabilities(
                    descriptor,
                    new Dictionary<string, object> { ["filePath"] = "x.cpp", ["lineNumber"] = 1 }).ToArray());
            CollectionAssert.AreEqual(
                new[] { "debug.breakpoint.write", "debug.evaluate.unsafe" },
                DebugMethodCatalog.GetRequiredCapabilities(
                    descriptor,
                    new Dictionary<string, object>
                    {
                        ["filePath"] = "x.cpp", ["lineNumber"] = 1, ["condition"] = "value > 0",
                    }).ToArray());
        }

        [TestMethod]
        public void CompositeWaitMethodsAllowCaptureGraceBeyondThirtyMinutes()
        {
            foreach (string method in new[]
            {
                "continue_and_wait_for_break", "continue_and_wait_for_break_safe",
                "continue_and_capture_native", "continue_and_capture_breakpoint",
            })
            {
                Assert.IsTrue(DebugMethodCatalog.Get(method).MaxDeadlineMs >= 35 * 60 * 1000, method);
            }
        }

        [TestMethod]
        public void VsBundleAllowlistRejectsUnexpectedPythonModules()
        {
            Assert.IsTrue(BundlePathPolicy.IsVsBundleRelativePath("tool_modules/capability_tools.py"));
            Assert.IsTrue(BundlePathPolicy.IsVsBundleRelativePath("tool_modules/vs_instance_tools.py"));
            Assert.IsFalse(BundlePathPolicy.IsVsBundleRelativePath("tool_modules/instance_tools.py"));
            Assert.IsFalse(BundlePathPolicy.IsVsBundleRelativePath("tool_modules/rider_tools.py"));
            Assert.IsFalse(BundlePathPolicy.IsVsBundleRelativePath("tool_modules/unexpected.py"));
        }

        [TestMethod]
        public void AuthenticodeTrustRejectsUnsignedFiles()
        {
            string file = Path.GetTempFileName();
            try
            {
                Assert.IsFalse(AuthenticodeTrustValidator.IsTrustedFileSignedBy(
                    file, "Python Software Foundation"));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [TestMethod]
        public void TomlEditorIgnoresHeadersInsideMultilineValuesAndArrays()
        {
            string content =
                "note = \"\"\"\n[mcp_servers.AIDebuggerVS]\ntext\n\"\"\"\n" +
                "literal = '''\n[mcp_servers.AIDebuggerVS]\ntext\n'''\n" +
                "values = [\n  \"x\",\n  [1, 2],\n]\n" +
                "[other]\nx = 1\n";
            Assert.IsFalse(TomlConfigEditor.HasSection(content, "mcp_servers.AIDebuggerVS"));
            Assert.AreEqual(content, TomlConfigEditor.RemoveSection(content, "mcp_servers.AIDebuggerVS"));
            TomlConfigEditor.RejectUnsupportedOwnedAssignments(
                content, "mcp_servers.AIDebuggerVS", "mcp_servers.AIDebugger");

            string withOwned = content + "[mcp_servers.AIDebuggerVS]\ncommand = \"python\"\n[tail]\ny = 2\n";
            string removed = TomlConfigEditor.RemoveSection(withOwned, "mcp_servers.AIDebuggerVS");
            StringAssert.Contains(removed, "note = \"\"\"");
            StringAssert.Contains(removed, "[tail]");
            Assert.IsFalse(TomlConfigEditor.HasSection(removed, "mcp_servers.AIDebuggerVS"));
        }

        [TestMethod]
        public void CanonicalJsonIgnoresObjectPropertyOrderButPreservesArrayOrder()
        {
            var first = new System.Collections.Generic.Dictionary<string, object>
            {
                ["z"] = new object[] { 2, 1 },
                ["a"] = true,
            };
            var second = new System.Collections.Generic.Dictionary<string, object>
            {
                ["a"] = true,
                ["z"] = new object[] { 2, 1 },
            };
            Assert.AreEqual(CanonicalJson.Serialize(first), CanonicalJson.Serialize(second));
            Assert.AreNotEqual(
                CanonicalJson.Serialize(first),
                CanonicalJson.Serialize(new System.Collections.Generic.Dictionary<string, object>
                {
                    ["a"] = true,
                    ["z"] = new object[] { 1, 2 },
                }));
        }

        [TestMethod]
        public void BuildErrorCodeExtractionRecognizesCompilerLinkerAndAnalyzerCodes()
        {
            Assert.AreEqual("CS1001", BuildErrorCodeParser.Extract("error CS1001: identifier expected"));
            Assert.AreEqual("LNK2001", BuildErrorCodeParser.Extract("fatal error LNK2001 unresolved external"));
            Assert.AreEqual("CA1822", BuildErrorCodeParser.Extract("warning CA1822: mark member static"));
            Assert.AreEqual(string.Empty, BuildErrorCodeParser.Extract("ordinary diagnostic text"));
        }

        [TestMethod]
        public void BundlePathPolicyRejectsSiblingPrefixesAndTraversal()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidb_bundle_" + Guid.NewGuid().ToString("N"));
            string child = Path.Combine(root, "tool_modules", "sample.py");
            Assert.AreEqual(
                Path.Combine("tool_modules", "sample.py"),
                BundlePathPolicy.GetRelativePath(root, child));

            string sibling = Path.Combine(root + "_sibling", "sample.py");
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                BundlePathPolicy.GetRelativePath(root, sibling));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                BundlePathPolicy.ResolveDestination(root, Path.Combine("..", "escape.py")));
        }

        [TestMethod]
        public void VsBundleSignatureMatchesCrossLanguageFixture()
        {
            string fixture = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "VsBundleSignature");
            Assert.AreEqual(
                "3fd3d920468408e15d6356ba7cd450c20903e500e1385b431ecac5223db6b79e",
                BundleSignature.Compute(fixture, BundlePathPolicy.GetVsBundleFiles(fixture)));
        }

        [TestMethod]
        public void VsBundleTreeRejectsCachesAndUnexpectedDirectories()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidb_bundle_tree_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "vs_server.py"), "VALUE = 1\n");
                File.WriteAllText(Path.Combine(root, ".bundle-version"), "1.0.0.0");
                File.WriteAllText(Path.Combine(root, ".bundle-signature"), new string('a', 64));
                string modules = Path.Combine(root, "tool_modules");
                Directory.CreateDirectory(modules);
                File.WriteAllText(Path.Combine(modules, "__init__.py"), string.Empty);

                Assert.IsTrue(BundlePathPolicy.IsValidVsBundleTree(root, true));

                string cache = Path.Combine(modules, "__pycache__");
                Directory.CreateDirectory(cache);
                File.WriteAllBytes(Path.Combine(cache, "__init__.pyc"), new byte[] { 1 });
                Assert.IsFalse(BundlePathPolicy.IsValidVsBundleTree(root, true));
                Directory.Delete(cache, true);

                string unexpected = Path.Combine(root, "json");
                Directory.CreateDirectory(unexpected);
                Assert.IsFalse(BundlePathPolicy.IsValidVsBundleTree(root, true));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void OwnedBundleDirectoryMustBeAPlainDirectChild()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidb_owned_root_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string child = BundlePathPolicy.EnsurePlainChildDirectory(root, "mcp_server_vs");
                Assert.AreEqual(Path.Combine(root, "mcp_server_vs"), child);
                Assert.IsTrue(BundlePathPolicy.IsPlainDirectory(child));
                Assert.ThrowsExactly<ArgumentException>(() =>
                    BundlePathPolicy.EnsurePlainChildDirectory(root, Path.Combine("..", "escape")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void IsPlainFileRejectsDirectoriesAndMissingEntries()
        {
            string root = Path.Combine(Path.GetTempPath(), "aidb_plain_file_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string file = Path.Combine(root, "marker");
                File.WriteAllText(file, "active");
                Assert.IsTrue(BundlePathPolicy.IsPlainFile(file));
                Assert.IsFalse(BundlePathPolicy.IsPlainFile(root));
                Assert.IsFalse(BundlePathPolicy.IsPlainFile(Path.Combine(root, "missing")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void SafeBundleDeletionRejectsNestedJunctions()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows junction behavior");
            string root = Path.Combine(Path.GetTempPath(), "aidb_delete_tree_" + Guid.NewGuid().ToString("N"));
            string outside = Path.Combine(Path.GetTempPath(), "aidb_delete_outside_" + Guid.NewGuid().ToString("N"));
            string junction = Path.Combine(root, "nested");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "must-survive.txt"), "owned elsewhere");
                var create = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /c mklink /J \"" + junction + "\" \"" + outside + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                create.WaitForExit();
                Assert.AreEqual(0, create.ExitCode, "Failed to create the junction fixture");

                Assert.ThrowsExactly<InvalidOperationException>(() => BundlePathPolicy.DeletePlainTree(root));
                Assert.IsTrue(File.Exists(Path.Combine(outside, "must-survive.txt")));
            }
            finally
            {
                if (Directory.Exists(junction)) Directory.Delete(junction, false);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                if (Directory.Exists(outside)) Directory.Delete(outside, true);
            }
        }

        [TestMethod]
        public void ResponseSerializerPreservesDictionaryObjectsOnTheWire()
        {
            var errorResponse = new DebugResponse
            {
                Protocol = "aidb/2",
                Id = Guid.NewGuid().ToString(),
                Status = "error",
                Error = new ErrorInfo
                {
                    Code = "RESOURCE_LIMIT_EXCEEDED",
                    Message = "too large",
                    Details = new Dictionary<string, object>
                    {
                        ["maxBytes"] = 16,
                        ["target"] = new Dictionary<string, object> { ["processId"] = 42 },
                    },
                },
            };
            var serializer = new JavaScriptSerializer();
            var errorEnvelope = (Dictionary<string, object>)serializer.DeserializeObject(
                DebugResponseJsonSerializer.Serialize(errorResponse));
            var error = (Dictionary<string, object>)errorEnvelope["error"];
            var details = (Dictionary<string, object>)error["details"];
            Assert.AreEqual(16, details["maxBytes"]);
            Assert.IsInstanceOfType<Dictionary<string, object>>(details["target"]);

            var cachedEnvelope = new Dictionary<string, object>
            {
                ["protocol"] = "aidb/2",
                ["id"] = Guid.NewGuid().ToString(),
                ["status"] = "ok",
                ["result"] = new Dictionary<string, object> { ["success"] = true },
            };
            var statusResponse = new DebugResponse
            {
                Protocol = "aidb/2",
                Id = Guid.NewGuid().ToString(),
                Status = "ok",
                Result = new CommandRequestStatusResult
                {
                    RequestId = Guid.NewGuid().ToString(),
                    State = "completed",
                    Response = cachedEnvelope,
                },
            };
            var statusEnvelope = (Dictionary<string, object>)serializer.DeserializeObject(
                DebugResponseJsonSerializer.Serialize(statusResponse));
            var status = (Dictionary<string, object>)statusEnvelope["result"];
            var response = (Dictionary<string, object>)status["response"];
            Assert.AreEqual("aidb/2", response["protocol"]);
            Assert.IsInstanceOfType<Dictionary<string, object>>(response["result"]);
        }

        [TestMethod]
        public void ResponseSerializerOmitsUnavailableSourceCoordinates()
        {
            var response = new DebugResponse
            {
                Protocol = "aidb/2",
                Id = Guid.NewGuid().ToString(),
                Status = "ok",
                Result = new CallStackResult
                {
                    Frames = new List<StackFrameInfo>
                    {
                        new StackFrameInfo
                        {
                            Index = 0,
                            FunctionName = "UnknownFrame",
                            SourceLocationStatus = "unavailable",
                        },
                    },
                },
            };

            var serializer = new JavaScriptSerializer();
            var envelope = (Dictionary<string, object>)serializer.DeserializeObject(
                DebugResponseJsonSerializer.Serialize(response));
            var result = (Dictionary<string, object>)envelope["result"];
            var frames = (object[])result["frames"];
            var frame = (Dictionary<string, object>)frames[0];

            Assert.AreEqual("unavailable", frame["sourceLocationStatus"]);
            Assert.IsFalse(frame.ContainsKey("filePath"));
            Assert.IsFalse(frame.ContainsKey("lineNumber"));
            Assert.IsFalse(frame.ContainsKey("columnNumber"));
            Assert.IsFalse(frame.ContainsKey("lineNumberReliable"));

            var pathOnly = new DebugResponse
            {
                Protocol = "aidb/2",
                Id = Guid.NewGuid().ToString(),
                Status = "ok",
                Result = new DebugStatusResult
                {
                    IsDebugging = true,
                    IsPaused = true,
                    CurrentLocation = new LocationInfo
                    {
                        FilePath = @"C:\\src\\Program.cs",
                        FunctionName = "Main",
                        SourceLocationStatus = "path_only",
                    },
                },
            };
            envelope = (Dictionary<string, object>)serializer.DeserializeObject(
                DebugResponseJsonSerializer.Serialize(pathOnly));
            result = (Dictionary<string, object>)envelope["result"];
            var location = (Dictionary<string, object>)result["currentLocation"];
            Assert.AreEqual("path_only", location["sourceLocationStatus"]);
            Assert.IsFalse(location.ContainsKey("lineNumber"));
            Assert.IsFalse(location.ContainsKey("columnNumber"));
            Assert.IsFalse(location.ContainsKey("lineNumberReliable"));
        }

        [TestMethod]
        public void RequestCompletionIdentityIsAnImmutableRebasedSnapshot()
        {
            DebuggerIdentitySnapshot captured;
            using (DebuggerRequestContext.Push(
                0,
                Guid.NewGuid().ToString(),
                "command",
                2,
                true,
                3,
                4,
                true,
                CancellationToken.None))
            {
                DebuggerRequestContext.RebaseDebuggerIdentity(5, 6, 7);
                captured = DebuggerRequestContext.CaptureIdentity();
                DebuggerRequestContext.RebaseDebuggerIdentity(8, 9, 10);
            }
            Assert.AreEqual(5, captured.DebugSessionGeneration);
            Assert.AreEqual(6, captured.BreakSequence);
            Assert.AreEqual(7, captured.ContextEpoch);
            Assert.IsNull(DebuggerRequestContext.CaptureIdentity());
        }

        [TestMethod]
        public void UnsafeEvaluationContextInvalidationIsRequestScopedAndIdempotent()
        {
            using (DebuggerRequestContext.Push(
                0,
                Guid.NewGuid().ToString(),
                "query",
                2,
                true,
                3,
                4,
                true,
                CancellationToken.None,
                mayEvaluateUserCode: true))
            {
                int invalidations = 0;
                Assert.IsTrue(DebuggerRequestContext.MarkUnsafeEvaluationIssued(() => invalidations++));
                Assert.IsTrue(DebuggerRequestContext.MarkUnsafeEvaluationIssued(() => invalidations++));
                Assert.AreEqual(1, invalidations);
                DebuggerRequestContext.RebaseDebuggerIdentity(2, 3, 5);
                Assert.AreEqual(5, DebuggerRequestContext.CaptureCompletionIdentity().ContextEpoch);
            }

            using (DebuggerRequestContext.Push(
                0,
                Guid.NewGuid().ToString(),
                "query",
                2,
                true,
                3,
                4,
                true,
                CancellationToken.None))
            {
                Assert.IsFalse(DebuggerRequestContext.MarkUnsafeEvaluationIssued(null));
            }
        }

        [TestMethod]
        public void ManagedInspectionHelperRunsInsideAppContainerWithBrokeredFileHandle()
        {
            string root = FindRepositoryRoot();
            string configuration = Directory.Exists(Path.Combine(
                root, "VSDebuggerMCP", "ManagedInspectionHelper", "bin", "Debug"))
                ? "Debug"
                : "Release";
            string helper = Path.Combine(
                root, "VSDebuggerMCP", "ManagedInspectionHelper", "bin", configuration,
                "ManagedInspectionHelper.exe");
            string assembly = Path.Combine(
                root, "tests", "fixtures", "ManagedInspectionEdgeCases", "bin", configuration,
                "ManagedInspectionEdgeCases.dll");
            Assert.IsTrue(File.Exists(helper), helper);
            Assert.IsTrue(File.Exists(assembly), assembly);

            using (ManagedInspectionProcess contained = ManagedInspectionProcess.Start(helper, assembly))
            {
                Assert.IsTrue(IsAppContainerProcess(contained.Process));
                var stdout = contained.StandardOutput.ReadToEndAsync();
                var stderr = contained.StandardError.ReadToEndAsync();
                string escapedPath = new JavaScriptSerializer().Serialize(assembly);
                contained.StandardInput.Write(
                    "{\"Operation\":\"inspect_managed_assembly\",\"AssemblyPath\":" +
                    escapedPath + ",\"MaxTypes\":4,\"MaxMethodsPerType\":2}");
                contained.StandardInput.Close();
                Assert.IsTrue(contained.Process.WaitForExit(30000), "Contained helper timed out");
                Assert.IsTrue(Task.WaitAll(new Task[] { stdout, stderr }, 5000), "Contained helper streams did not close");
                Assert.AreEqual(0, contained.Process.ExitCode, stderr.Result);
                StringAssert.Contains(stdout.Result, "appcontainer_managed_metadata_and_il_read_only");
            }
        }

        private static bool IsAppContainerProcess(Process process)
        {
            const uint TokenQuery = 0x0008;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process.Handle, TokenQuery, out token))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                int value = 0;
                int returned;
                if (!GetTokenInformation(token, 29, ref value, sizeof(int), out returned))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return value != 0;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandleForTest(token);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            ref int tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
        private static extern bool CloseHandleForTest(IntPtr handle);

        [TestMethod]
        public async Task ClientSetupSingleFlightRejectsOverlapAndCancelsForShutdown()
        {
            using (var operation = new CancelableSingleFlight())
            {
                var started = new TaskCompletionSource<bool>();
                var cancelled = new TaskCompletionSource<bool>();
                bool first = operation.TryStart(async token =>
                {
                    started.SetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled.SetResult(true);
                        throw;
                    }
                }, out Task firstTask);
                await started.Task;

                bool second = operation.TryStart(_ => Task.CompletedTask, out Task sameTask);
                Assert.IsTrue(first);
                Assert.IsFalse(second);
                Assert.AreSame(firstTask, sameTask);

                Task shutdownTask = operation.CancelAndGetTaskAsync();
                await cancelled.Task;
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await shutdownTask);
            }
        }

        [TestMethod]
        public void OutputContentBudgetMatchesWireJsonEncodingAndPreservesUnicodeScalars()
        {
            const string sample = "\0\"\\\b\t\n\f\r中文😀";
            int expectedBytes = Encoding.UTF8.GetByteCount(new JavaScriptSerializer().Serialize(sample));
            Assert.AreEqual(expectedBytes, OutputContentBudget.GetSerializedStringUtf8ByteCount(sample));

            string oversized = new string('界', 5000) + "😀tail";
            OutputContentSlice result = OutputContentBudget.FitSuffix(
                oversized,
                OutputContentBudget.MinSerializedContentBytes);

            Assert.IsTrue(result.OmittedLeadingCharacters > 0);
            Assert.IsTrue(result.SerializedContentBytes <= OutputContentBudget.MinSerializedContentBytes);
            Assert.IsTrue(result.Content.EndsWith("😀tail", StringComparison.Ordinal));
            Assert.IsFalse(result.Content.Length > 0 && char.IsLowSurrogate(result.Content[0]));
            Assert.AreEqual(
                Encoding.UTF8.GetByteCount(new JavaScriptSerializer().Serialize(result.Content)),
                result.SerializedContentBytes);
        }

        [TestMethod]
        public void OutputTailPagingKeepsOriginalLineEndingsAndExactOffset()
        {
            const string content = "one\r\ntwo\nthree\rfour";
            int start = OutputContentBudget.FindTailStart(content, 2);
            Assert.AreEqual(content.IndexOf("three", StringComparison.Ordinal), start);
            Assert.AreEqual("three\rfour", content.Substring(start));
            Assert.AreEqual(0, OutputContentBudget.FindTailStart(content, 4));
        }

        [TestMethod]
        public void OutputContentBudgetSearchesOnlyUtf16ScalarBoundaries()
        {
            Assert.AreEqual(2, OutputContentBudget.FindSuffixStartForPayloadBudget("😀", 3));
            Assert.AreEqual(0, OutputContentBudget.FindSuffixStartForPayloadBudget("😀", 4));
            Assert.AreEqual(1, OutputContentBudget.FindSuffixStartForPayloadBudget("x😀tail", 8));
            Assert.AreEqual(2, OutputContentBudget.FindSuffixStartForPayloadBudget("😀😀", 4));

            string unpaired = "\uD800x";
            Assert.AreEqual(1, OutputContentBudget.FindSuffixStartForPayloadBudget(unpaired, 1));
            Assert.AreEqual(0, OutputContentBudget.FindSuffixStartForPayloadBudget(unpaired, 4));
        }

        [TestMethod]
        public void OutputSearchBudgetBoundsManyLargeMatchesAndContextNodes()
        {
            var budget = new OutputSearchBudget();
            string bounded = OutputSearchBudget.BoundLine(new string('x', 100000));
            Assert.IsTrue(bounded.Length <= OutputSearchBudget.MaxLineCharacters + 1);

            var context = Enumerable.Range(0, OutputSearchBudget.MaxContextNodesPerMatch)
                .Select(index => "L" + index + ": " + bounded)
                .ToList();
            int accepted = 0;
            while (budget.TryConsumeMatch(bounded, context)) accepted++;

            Assert.IsTrue(accepted > 0);
            Assert.IsTrue(accepted < 10000);
            Assert.IsTrue(budget.SerializedBytes <= OutputSearchBudget.MaxSerializedResultBytes);
            Assert.IsTrue(budget.Nodes <= OutputSearchBudget.MaxResultNodes);
        }

        [TestMethod]
        public void CallStackCaptureBudgetBoundsFrameStringsAndTotalResponseBytes()
        {
            var budget = new CallStackCaptureBudget();
            int accepted = 0;
            while (true)
            {
                var frame = new StackFrameInfo
                {
                    FunctionName = new string('f', 100000) + "😀",
                    FilePath = new string('p', 100000),
                    Module = new string('m', 100000),
                    Context = new DebugFrameContext { FrameId = new string('i', 100000) },
                };
                if (!budget.TryConsume(frame)) break;
                accepted++;
                Assert.IsTrue(frame.FunctionName.Length <= CallStackCaptureBudget.MaxFrameStringCharacters + 1);
                Assert.IsFalse(char.IsLowSurrogate(frame.FunctionName[0]));
            }
            Assert.IsTrue(accepted > 0);
            Assert.IsTrue(budget.ResultBytes <= CallStackCaptureBudget.MaxResultBytes);
        }

        [TestMethod]
        public void VariableCaptureBudgetBoundsEscapedStringsWithoutSplittingSurrogates()
        {
            var budget = new VariableCaptureBudget(32, 5000);
            Assert.IsTrue(budget.TryEnterNode());
            string hostile = new string('\0', 100000) + "😀";
            string bounded = budget.CaptureString(hostile, "value", out bool truncated);

            Assert.IsTrue(truncated);
            Assert.IsTrue(budget.IsIncomplete);
            Assert.IsTrue(budget.SerializedBytes <= VariableCaptureBudget.MaxSerializedBytes);
            Assert.IsTrue(
                OutputContentBudget.GetSerializedStringUtf8ByteCount(bounded) <=
                VariableCaptureBudget.MaxFieldSerializedBytes);
            Assert.IsFalse(bounded.Length > 0 && char.IsHighSurrogate(bounded[bounded.Length - 1]));

            string prefix = OutputContentBudget.FitPrefix("a😀b", 7, out bool prefixTruncated);
            Assert.AreEqual("a😀", prefix);
            Assert.IsTrue(prefixTruncated);
        }

        [TestMethod]
        public void CompositeCaptureBudgetAccountsComponentsLargerThanDefaultSerializerLimit()
        {
            var budget = new CompositeCaptureBudget();
            var large = new { content = new string('x', 2200000) };

            Assert.IsTrue(budget.TryConsumeComponent("large", large));
            Assert.IsFalse(budget.TryConsumeComponent("second", large));
            Assert.IsTrue(budget.IsExhausted);
            Assert.IsTrue(budget.ResultBytes <= CompositeCaptureBudget.MaxResultBytes);
        }

        [TestMethod]
        public void ExceptionCaptureBudgetBoundsFieldsReasonsAndAggregateBytes()
        {
            var budget = new ExceptionCaptureBudget();
            Assert.IsTrue(budget.TryEnterNode());
            string captured = budget.CaptureString(
                new string('x', 1000000),
                "stackTrace",
                out bool truncated);
            for (int index = 0; index < 20; index++)
                budget.AddReason(new string('e', 100000));

            Assert.IsTrue(truncated);
            Assert.IsTrue(budget.IsIncomplete);
            Assert.IsTrue(
                OutputContentBudget.GetSerializedStringUtf8ByteCount(captured) <=
                ExceptionCaptureBudget.MaxFieldBytes);
            Assert.IsTrue(budget.SerializedBytes <= ExceptionCaptureBudget.MaxSerializedBytes);
        }

        [TestMethod]
        public void CollectionCaptureBudgetsBoundHostileFieldsAndAggregateBytes()
        {
            string hostile = new string('x', 100000);

            var moduleBudget = new ModuleCaptureBudget();
            var module = new ModuleInfo { Name = hostile, Path = hostile };
            Assert.IsTrue(moduleBudget.TryConsume(module));
            Assert.IsTrue(module.Truncated);
            Assert.IsTrue(moduleBudget.ResultBytes <= ModuleCaptureBudget.MaxResultBytes);

            var breakpointBudget = new BreakpointCaptureBudget();
            var breakpoint = new BreakpointInfo { Name = hostile, FilePath = hostile };
            BreakpointCaptureBudget.BoundFields(breakpoint);
            Assert.IsTrue(breakpointBudget.TryConsume(breakpoint));
            Assert.IsTrue(breakpoint.Truncated);
            Assert.IsTrue(breakpointBudget.ResultBytes <= BreakpointCaptureBudget.MaxResultBytes);

            var threadBudget = new ThreadCaptureBudget();
            var frame = new StackFrameInfo { FunctionName = hostile, FilePath = hostile };
            Assert.IsTrue(threadBudget.TryConsumeFrame(frame));
            Assert.IsTrue(frame.Truncated);
            Assert.IsTrue(threadBudget.ResultBytes <= ThreadCaptureBudget.MaxResultBytes);

            var buildBudget = new BuildErrorCaptureBudget();
            var buildItem = new BuildErrorItem
            {
                Description = BuildErrorCaptureBudget.BoundDescription(hostile, out bool descriptionTruncated),
                FilePath = BuildErrorCaptureBudget.BoundPath(hostile, out bool pathTruncated),
                Project = BuildErrorCaptureBudget.BoundProject(hostile, out bool projectTruncated),
            };
            Assert.IsTrue(descriptionTruncated && pathTruncated && projectTruncated);
            Assert.IsTrue(buildBudget.TryConsume(buildItem));
            Assert.IsTrue(buildBudget.ResultBytes <= BuildErrorCaptureBudget.MaxResultBytes);
        }

        [TestMethod]
        public void ImmutableModuleSnapshotPagesAdvanceWithoutDuplicatesOrGaps()
        {
            var snapshot = new[]
            {
                new ModuleInfo { Name = "one", Path = @"C:\one.dll" },
                new ModuleInfo { Name = "two", Path = @"C:\two.dll" },
                new ModuleInfo { Name = "three", Path = @"C:\three.dll" },
            };
            ModuleSnapshotPage first = ModuleSnapshotPager.Build(
                snapshot, 0, 1, new ModuleCaptureBudget());
            ModuleSnapshotPage second = ModuleSnapshotPager.Build(
                snapshot, first.NextIndex, 1, new ModuleCaptureBudget());
            ModuleSnapshotPage third = ModuleSnapshotPager.Build(
                snapshot, second.NextIndex, 1, new ModuleCaptureBudget());

            Assert.AreEqual(1, first.NextIndex);
            Assert.AreEqual(2, second.NextIndex);
            Assert.AreEqual(3, third.NextIndex);
            Assert.IsTrue(first.HasMore);
            Assert.IsTrue(second.HasMore);
            Assert.IsFalse(third.HasMore);
            CollectionAssert.AreEqual(
                new[] { "one", "two", "three" },
                new[] { first.Modules[0].Name, second.Modules[0].Name, third.Modules[0].Name });
        }

        [TestMethod]
        public void TomlOwnedSectionEditorHandlesQuotedTablesAndRejectsInlineAliases()
        {
            const string quoted =
                "# keep\n" +
                "[mcp_servers.\"AIDebuggerVS\"]\ncommand = \"old\"\n\n" +
                "[mcp_servers.'AIDebuggerVS'.env]\nAIDB_PREFERRED_IDE = \"vs\"\n\n" +
                "[unowned]\nvalue = 1\n";
            Assert.IsTrue(TomlConfigEditor.HasSection(quoted, "mcp_servers.AIDebuggerVS"));
            string updated = TomlConfigEditor.RemoveSection(quoted, "mcp_servers.AIDebuggerVS.env");
            updated = TomlConfigEditor.RemoveSection(updated, "mcp_servers.AIDebuggerVS");
            StringAssert.Contains(updated, "# keep");
            StringAssert.Contains(updated, "[unowned]");
            Assert.IsFalse(TomlConfigEditor.HasSection(updated, "mcp_servers.AIDebuggerVS"));

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                TomlConfigEditor.RejectUnsupportedOwnedAssignments(
                    "mcp_servers.AIDebuggerVS = { command = \"python\" }\n",
                    "mcp_servers.AIDebuggerVS"));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                TomlConfigEditor.RejectUnsupportedOwnedAssignments(
                    "[mcp_servers]\n\"AIDebuggerVS\" = { command = \"python\" }\n",
                    "mcp_servers.AIDebuggerVS"));
        }
    }
}
