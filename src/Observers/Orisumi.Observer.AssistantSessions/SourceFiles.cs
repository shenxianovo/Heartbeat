using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace Orisumi.Observers.AssistantSessions;

internal sealed record SourceFile(string Path, string Product, SessionInfo? Location = null, bool IsIndex = false);
internal sealed record FileProgress(string Identity, long Size, long Modified, long Offset, SourceContext Context);
// Oversized lines carry no bytes: the reader skips them so one huge row cannot block the rest of the file.
internal sealed record SourceLine(byte[] Bytes, long End, bool Oversized = false);

internal static class SourceFiles
{
    internal static IReadOnlyList<SourceFile> Discover(string claudeDirectory, string codexDirectory, Action<string> diagnostic)
    {
        var files = new List<SourceFile>();
        var projects = Path.Combine(claudeDirectory, "projects");
        foreach (var path in Enumerate(projects, "*.jsonl", diagnostic))
        {
            var parent = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(parent) == "subagents" && Path.GetFileName(path).StartsWith("agent-", StringComparison.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(path)[6..];
                var owner = Path.GetFileName(Path.GetDirectoryName(parent))!;
                var location = new SessionInfo("claude-code", id, ParentId: owner);
                var metadata = Path.ChangeExtension(path, ".meta.json");
                try
                {
                    if (File.Exists(metadata))
                    {
                        using var json = JsonDocument.Parse(File.ReadAllBytes(metadata));
                        location = location with { Title = json.RootElement.Text("description"), AgentName = json.RootElement.Text("agentType"),
                            SpawnCallId = json.RootElement.Text("toolUseId") };
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                { diagnostic("Subagent metadata unavailable: " + metadata + ": " + exception.Message); }
                files.Add(new(path, "claude-code", location));
            }
            else if (Path.GetDirectoryName(parent) == projects)
                files.Add(new(path, "claude-code", new("claude-code", Path.GetFileNameWithoutExtension(path))));
        }
        foreach (var directory in new[] { "sessions", "archived_sessions" })
            foreach (var path in Enumerate(Path.Combine(codexDirectory, directory), "rollout-*", diagnostic))
                if (path.EndsWith(".jsonl", StringComparison.Ordinal) || path.EndsWith(".jsonl.zst", StringComparison.Ordinal))
                    files.Add(new(path, "codex"));
        var index = Path.Combine(codexDirectory, "session_index.jsonl");
        if (File.Exists(index)) files.Add(new(index, "codex", IsIndex: true));
        return files;
    }

    private static string[] Enumerate(string root, string pattern, Action<string> diagnostic)
    {
        if (!Directory.Exists(root)) return [];
        try { return Directory.GetFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint }); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostic("Cannot discover source directory " + root + ": " + exception.Message); return []; }
    }

    internal static async Task<string> ExpandAsync(string path, string identity, string cacheDirectory, CancellationToken token)
    {
        if (!path.EndsWith(".zst", StringComparison.Ordinal)) return path;
        Directory.CreateDirectory(cacheDirectory);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path + identity)));
        var target = Path.Combine(cacheDirectory, key + ".jsonl");
        if (File.Exists(target)) return target;
        var temporary = target + ".tmp";
        try
        {
            await using var input = File.OpenRead(path);
            await using var decompress = new DecompressionStream(input);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await decompress.CopyToAsync(output, token);
                output.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }

    internal const int MaximumLineBytes = 64 * 1024 * 1024;

    internal static async Task<IReadOnlyList<SourceLine>> ReadAsync(string path, long offset, int maximumLines, CancellationToken token,
        int maximumLineBytes = MaximumLineBytes)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        file.Seek(offset, SeekOrigin.Begin);
        var result = new List<SourceLine>();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var line = new MemoryStream();
        var position = offset;
        var bytesInSlice = 0L;
        var oversized = false;
        try
        {
            while (await file.ReadAsync(buffer.AsMemory(), token) is var count and > 0)
            {
                var begin = 0;
                for (var index = 0; index < count; index++)
                {
                    position++;
                    if (buffer[index] != (byte)'\n') continue;
                    if (oversized)
                    {
                        result.Add(new([], position, Oversized: true));
                        oversized = false;
                        begin = index + 1;
                        if (result.Count >= maximumLines) return result;
                        continue;
                    }
                    line.Write(buffer, begin, index - begin);
                    var length = (int)line.Length;
                    var bytes = line.GetBuffer();
                    if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                    result.Add(new(bytes.AsSpan(0, length).ToArray(), position));
                    bytesInSlice += length;
                    line.SetLength(0);
                    begin = index + 1;
                    if (result.Count >= maximumLines || bytesInSlice >= 512 * 1024) return result;
                }
                if (oversized) continue;
                line.Write(buffer, begin, count - begin);
                if (line.Length > maximumLineBytes)
                {
                    oversized = true;
                    line.SetLength(0);
                }
            }
            return result; // Unterminated bytes, including partial UTF-8, stay in the source until next poll.
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal static partial class FileIdentity
{
    // Read the identity of the opened file, not its path; rename/replacement must not inherit an old cursor.
    internal static unsafe string Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = stackalloc byte[512];
        new Span<byte>(buffer, 512).Clear();
        if (OperatingSystem.IsWindows())
        {
            if (GetInformation(handle, buffer) == 0) throw new IOException("Cannot read source file identity.", Marshal.GetLastPInvokeError());
            return $"{*(uint*)(buffer + 28)}:{*(uint*)(buffer + 44)}:{*(uint*)(buffer + 48)}";
        }
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacStat(handle.DangerousGetHandle().ToInt32(), buffer)
            : FStat(handle.DangerousGetHandle().ToInt32(), buffer);
        if (result != 0)
            throw new IOException("Cannot read source file identity.", Marshal.GetLastPInvokeError());
        var device = OperatingSystem.IsMacOS() ? *(uint*)buffer : *(ulong*)buffer;
        return $"{device}:{*(ulong*)(buffer + 8)}";
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int FStat(int descriptor, byte* information);
    [LibraryImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static unsafe partial int MacStat(int descriptor, byte* information);
    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static unsafe partial int GetInformation(SafeFileHandle handle, byte* information);
}
