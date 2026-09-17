using System;
using System.IO;
using System.Threading;
using CodeWicket.Core;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Attachment retention (issue #118 follow-up). <b>Size is the only thing that reclaims here</b> —
    /// unlike the log and diff-scratch sweeps this otherwise mirrors, there is deliberately no age
    /// rule, because this directory holds conversation CONTENT rather than scratch and conversations
    /// are kept indefinitely.
    /// </summary>
    [Collection(StoragePathCollection.Name)]
    public class AttachmentRetentionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "cwkt-retention-" + Guid.NewGuid().ToString("N"));

        public AttachmentRetentionTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        private string Write(string name, int bytes, TimeSpan age)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }

        [Fact]
        public void AnAncientFileSurvivesWhenThereIsNoAgeRule()
        {
            // The behaviour change: an attachment from a year ago is still part of its conversation,
            // and the conversation has not expired, so neither has it.
            var old = Write("ancient.png", 1024, TimeSpan.FromDays(400));

            RetentionSweep.Run(_dir, maxAge: null, budgetBytes: 10 * 1024 * 1024);

            Assert.True(File.Exists(old), "no age rule means age alone must never delete");
        }

        [Fact]
        public void TheBudGetStillEvictsOldestFirst()
        {
            var oldest = Write("a.png", 4096, TimeSpan.FromDays(30));
            var newest = Write("b.png", 4096, TimeSpan.FromMinutes(1));

            // Room for one of them.
            RetentionSweep.Run(_dir, maxAge: null, budgetBytes: 5000);

            Assert.False(File.Exists(oldest), "over budget, the oldest goes first");
            Assert.True(File.Exists(newest));
        }

        [Fact]
        public void NothingIsDeletedWhileTheDirectoryFits()
        {
            var kept = Write("a.png", 1024, TimeSpan.FromDays(365));

            var result = RetentionSweep.Run(_dir, maxAge: null, budgetBytes: 10 * 1024 * 1024);

            Assert.True(File.Exists(kept));
            Assert.Equal(0, result.Deleted);
        }

        [Fact]
        public void TheAgeRuleStillWorksForTheCallersThatWantOne()
        {
            // The logs and the diff scratch still pass an age, and making it optional must not have
            // quietly disabled it for them.
            var stale = Write("old.log", 16, TimeSpan.FromDays(30));
            var fresh = Write("new.log", 16, TimeSpan.FromMinutes(5));

            RetentionSweep.Run(_dir, TimeSpan.FromDays(7), budgetBytes: 10 * 1024 * 1024);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
        }

        [Fact]
        public void TheConfiguredLimitIsClampedUpSoATypoCannotEmptyTheDirectory()
        {
            // A hand-edited 0 would mean "keep nothing" and wipe the folder on the next window open.
            // Clamping up is the safe direction, and the floor is above one paste.
            var config = Path.Combine(_dir, "config.json");
            File.WriteAllText(config, "{\"attachmentStorageLimitMb\": 0}");
            ExtensionConfig.RedirectTo(config);
            try
            {
                Assert.Equal(
                    AttachmentStore.MinimumLimitMb * 1024L * 1024L, AttachmentStore.BudgetBytes);
            }
            finally
            {
                ExtensionConfig.RedirectTo(null!);
            }
        }

        [Fact]
        public void TheConfiguredLimitIsHonouredWhenItIsSensible()
        {
            var config = Path.Combine(_dir, "config.json");
            File.WriteAllText(config, "{\"attachmentStorageLimitMb\": 250}");
            ExtensionConfig.RedirectTo(config);
            try
            {
                Assert.Equal(250 * 1024L * 1024L, AttachmentStore.BudgetBytes);
            }
            finally
            {
                ExtensionConfig.RedirectTo(null!);
            }
        }
    }
}
