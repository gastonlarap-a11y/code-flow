using System.Diagnostics;
using CodeFlow.Ai;
using Xunit;

namespace CodeFlow.Tests.Ai;

/// <summary>
/// The scratch-file lifecycle that closed BUG-AI-a: creation under the temp root, recognition
/// from a built command, per-invocation deletion, and the age-gated startup sweep.
/// </summary>
public sealed class EngineScratchTests
{
    [Fact]
    public void Scratch_writers_place_prefixed_artifacts_under_the_temp_root()
    {
        var payload = EngineScratch.TryWriteOpenCodePayload("payload");
        var brief = EngineScratch.TryWriteAgyBrief("brief");

        try
        {
            Assert.NotNull(payload);
            Assert.StartsWith(Path.GetTempPath(), payload, StringComparison.Ordinal);
            Assert.StartsWith("codeflow-opencode-", Path.GetFileName(payload), StringComparison.Ordinal);
            Assert.Equal("payload", File.ReadAllText(payload));

            Assert.NotNull(brief);
            Assert.StartsWith("codeflow-agy-", Path.GetFileName(brief.Value.Directory), StringComparison.Ordinal);
            Assert.Equal("brief", File.ReadAllText(brief.Value.File));
            Assert.Equal(brief.Value.Directory, Path.GetDirectoryName(brief.Value.File));
        }
        finally
        {
            EngineScratch.TryDelete([payload!, brief!.Value.Directory]);
        }
    }

    [Fact]
    public void Collect_recognises_only_scratch_arguments_and_delete_removes_them()
    {
        var payload = EngineScratch.TryWriteOpenCodePayload("payload")!;
        var brief = EngineScratch.TryWriteAgyBrief("brief")!.Value;

        var startInfo = new ProcessStartInfo { FileName = "engine" };
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(payload);
        startInfo.ArgumentList.Add("--add-dir");
        startInfo.ArgumentList.Add(brief.Directory);
        // Arguments that must NOT be claimed: a flag, a repo path, and a temp path someone
        // else owns.
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("/Users/someone/repo/file.txt");
        startInfo.ArgumentList.Add(Path.Combine(Path.GetTempPath(), "unrelated.txt"));

        var scratch = EngineScratch.CollectFrom(startInfo);

        Assert.Equal(2, scratch.Count);
        Assert.Contains(payload, scratch);
        Assert.Contains(brief.Directory, scratch);

        EngineScratch.TryDelete(scratch);
        Assert.False(File.Exists(payload));
        Assert.False(Directory.Exists(brief.Directory));
    }

    [Fact]
    public void The_sweep_claims_old_orphans_and_leaves_young_and_foreign_entries_alone()
    {
        var root = Directory.CreateTempSubdirectory("codeflow-scratch-test-").FullName;
        try
        {
            var oldFile = Path.Combine(root, "codeflow-opencode-old.txt");
            File.WriteAllText(oldFile, "orphan");
            var oldDir = Path.Combine(root, "codeflow-agy-old");
            Directory.CreateDirectory(oldDir);
            File.WriteAllText(Path.Combine(oldDir, "brief.txt"), "orphan");

            var youngFile = Path.Combine(root, "codeflow-opencode-young.txt");
            File.WriteAllText(youngFile, "live");
            var foreign = Path.Combine(root, "someone-elses.txt");
            File.WriteAllText(foreign, "not ours");

            // Age is decided by last-write time, so the sweep needs no real waiting: the clock
            // passed in is simply far in the future for the "old" entries.
            var future = DateTime.UtcNow + EngineScratch.OrphanAge + TimeSpan.FromMinutes(1);
            File.SetLastWriteTimeUtc(youngFile, future);

            var removed = EngineScratch.SweepOrphans(root, future);

            Assert.Equal(2, removed);
            Assert.False(File.Exists(oldFile));
            Assert.False(Directory.Exists(oldDir));
            Assert.True(File.Exists(youngFile));
            Assert.True(File.Exists(foreign));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_missing_temp_root_is_not_an_error()
    {
        var removed = EngineScratch.SweepOrphans(
            Path.Combine(Path.GetTempPath(), $"codeflow-missing-{Guid.NewGuid():N}"), DateTime.UtcNow);

        Assert.Equal(0, removed);
    }
}
