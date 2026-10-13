using System.Diagnostics;
using System.Globalization;
using Heartbeat.Observers.Sources;
using Heartbeat.Observers.Sources.Local;

namespace Heartbeat.Model.Tests;

public sealed class LocalSourceTests
{
    [Test]
    public async Task GitReadsNativeCommitTimesOrderedParentsAndRefKinds()
    {
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init", "-b", "main");
            await Git(root, "commit", "--allow-empty", "-m", "base");
            await Git(root, "checkout", "-b", "side");
            await Git(root, "commit", "--allow-empty", "-m", "side");
            var side = await Git(root, "rev-parse", "HEAD");
            await Git(root, "checkout", "main");
            await Git(root, "commit", "--allow-empty", "-m", "main");
            var main = await Git(root, "rev-parse", "HEAD");
            await Git(root, "merge", "--no-ff", "side", "-m", "merge");
            await Git(root, "tag", "light");
            await Git(root, "tag", "-a", "annotated", "-m", "tag body");
            var captured = await Read(new GitSource(), new(root, "repository-a", "capture-a", DateTimeOffset.UtcNow));
            var commit = captured.Single(r => r.Kind == "git-commit");
            var native = await Git(root, "cat-file", "commit", "HEAD");
            await Assert.That(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(commit.Properties.GetProperty("rawCommitBase64").GetString()!)).TrimEnd()).IsEqualTo(native);
            await Assert.That(commit.Properties.GetProperty("authorEpoch").GetInt64()).IsEqualTo(1700000000L);
            await Assert.That(commit.Properties.GetProperty("committerEpoch").GetInt64()).IsEqualTo(1700003600L);
            var parents = captured.Where(r => r.Kind == "git-parent-occurrence").ToArray();
            await Assert.That(parents.Length).IsEqualTo(2);
            await Assert.That(parents[0].References["parentId"].NativeKey).IsEqualTo(main);
            await Assert.That(parents[1].References["parentId"].NativeKey).IsEqualTo(side);
            var refs = captured.Where(r => r.Kind == "git-ref").ToArray();
            await Assert.That(refs.Single(r => r.NativeKey == "refs/tags/light").Properties.GetProperty("objectType").GetString()).IsEqualTo("commit");
            await Assert.That(refs.Single(r => r.NativeKey == "refs/tags/annotated").Properties.GetProperty("objectType").GetString()).IsEqualTo("tag");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FileCapturePreservesBytesLinkIdentityAndDoesNotTraverseSymlinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "original.bin");
            await File.WriteAllBytesAsync(path, [65, 0, 66, 255]);
            var time = DateTimeOffset.UtcNow;
            var first = (await Read(new FileSource(), new(path, "host", "one", time))).Single();
            await Assert.That(first.Properties.GetProperty("contentBase64").GetString()).IsEqualTo("QQBC/w==");
            var moved = Path.Combine(root, "renamed.bin");
            File.Move(path, moved);
            var second = (await Read(new FileSource(), new(moved, "host", "two", time))).Single();
            if (first.Properties.GetProperty("birthTimeText").ValueKind != System.Text.Json.JsonValueKind.Null)
                await Assert.That(second.NativeKey).IsEqualTo(first.NativeKey);
            var hard = Path.Combine(root, "hard.bin");
            await Command("ln", ["--", moved, hard], root);
            var linked = (await Read(new FileSource(), new(hard, "host", "three", time))).Single();
            await Assert.That(linked.Properties.GetProperty("linkCount").GetInt64()).IsEqualTo(2L);
            var symlink = Path.Combine(root, "symbolic");
            File.CreateSymbolicLink(symlink, "missing-target");
            var link = (await Read(new FileSource(), new(symlink, "host", "four", time))).Single();
            await Assert.That(link.Properties.GetProperty("isSymbolicLink").GetBoolean()).IsTrue();
            await Assert.That(link.Properties.GetProperty("contentBase64").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
            await Assert.That(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(link.Properties.GetProperty("linkTargetBase64").GetString()!))).IsEqualTo("missing-target");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FileDevicesAndInvalidPidFailWithoutCapture()
    {
        if (!OperatingSystem.IsLinux()) return;
        var rejectedFile = false; var rejectedPid = false;
        try { await Read(new FileSource(), new("/dev/null", "host", "one")); }
        catch (InvalidDataException) { rejectedFile = true; }
        try { await Read(new ProcessSource(), new("not-a-pid", "host", "one")); }
        catch (InvalidDataException) { rejectedPid = true; }
        await Assert.That(rejectedFile).IsTrue();
        await Assert.That(rejectedPid).IsTrue();
    }

    private static async Task<List<SourceRecord>> Read(IRecordSource source, SourceReadContext context)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in source.ReadAsync(context, CancellationToken.None)) records.Add(record);
        return records;
    }
    private static Task<string> Git(string root, params string[] arguments) => Command("git",
        ["-c", "user.name=Source Fixture", "-c", "user.email=source@example.invalid", "-c", "core.hooksPath=/dev/null", .. arguments], root);
    private static async Task<string> Command(string name, string[] arguments, string root)
    {
        var start = new ProcessStartInfo(name) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_AUTHOR_DATE"] = "1700000000 +0000";
        start.Environment["GIT_COMMITTER_DATE"] = "1700003600 +0000";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(name + ": " + await stderr);
        return (await stdout).TrimEnd();
    }
}
