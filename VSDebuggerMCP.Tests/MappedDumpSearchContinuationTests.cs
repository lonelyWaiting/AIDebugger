using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VSDebuggerMCP.Services;

namespace VSDebuggerMCP.Tests
{
    [TestClass]
    public class MappedDumpSearchContinuationTests
    {
        private static readonly byte[] Pattern = { 0x11, 0x22, 0x33, 0x44 };

        [TestMethod]
        public void CursorRoundTripsForItsOriginalBinding()
        {
            string cursor = MappedDumpSearchContinuationCodec.Create(
                3,
                64,
                2,
                "dump-a",
                "snapshot-a",
                Pattern);

            Assert.IsTrue(MappedDumpSearchContinuationCodec.TryDecode(
                cursor,
                Pattern.Length,
                "dump-a",
                "snapshot-a",
                Pattern,
                out int rangeIndex,
                out ulong rangeOffset,
                out int carryBytes,
                out string error));
            Assert.AreEqual(3, rangeIndex);
            Assert.AreEqual(64UL, rangeOffset);
            Assert.AreEqual(2, carryBytes);
            Assert.IsNull(error);
            Assert.IsTrue(cursor.Length <= 512);
        }

        [TestMethod]
        public void CursorRejectsDifferentDumpSnapshotOrSameLengthPattern()
        {
            string cursor = MappedDumpSearchContinuationCodec.Create(
                0,
                8,
                1,
                "dump-a",
                "snapshot-a",
                Pattern);
            byte[] sameLengthDifferentPattern = { 0xAA, 0xBB, 0xCC, 0xDD };

            AssertBindingMismatch(cursor, "dump-b", "snapshot-a", Pattern);
            AssertBindingMismatch(cursor, "dump-a", "snapshot-b", Pattern);
            AssertBindingMismatch(cursor, "dump-a", "snapshot-a", sameLengthDifferentPattern);
        }

        [TestMethod]
        public void CursorRejectsInvalidCarryLengthAtCreation()
        {
            try
            {
                MappedDumpSearchContinuationCodec.Create(
                    0,
                    0,
                    Pattern.Length,
                    "dump-a",
                    "snapshot-a",
                    Pattern);
                Assert.Fail("Expected an invalid carry length to be rejected.");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        private static void AssertBindingMismatch(
            string cursor,
            string dumpIdentity,
            string snapshotToken,
            byte[] pattern)
        {
            Assert.IsFalse(MappedDumpSearchContinuationCodec.TryDecode(
                cursor,
                pattern.Length,
                dumpIdentity,
                snapshotToken,
                pattern,
                out _,
                out _,
                out _,
                out string error));
            StringAssert.Contains(error, "does not match");
        }
    }
}
