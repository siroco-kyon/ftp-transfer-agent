using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FtpTransferAgent.Services;

/// <summary>同名の新世代があるとき、配信済みの旧世代を失わずに保管する。</summary>
public sealed class CompletedDeliveryArchive
{
    private readonly string _root;
    private readonly string _retry;

    public CompletedDeliveryArchive(string stateDirectory, string retryDirectory)
    {
        _root = Path.Combine(stateDirectory, "completed-files");
        _retry = LocalPathIdentity.Normalize(retryDirectory);
    }

    public void Archive(string relativePath, IReadOnlyList<string> files, Action<string> clearMarkers)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var entries = files.Where(File.Exists).Select(file => new Entry(
            Path.GetRelativePath(_retry, file), Fingerprint(file))).ToArray();
        var manifest = new Manifest(relativePath, entries);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pending.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest));
        File.Move(path + ".tmp", path);
        Complete(directory, manifest, clearMarkers);
    }

    public IReadOnlyList<string> Recover(Action<string> clearMarkers)
    {
        var completed = new List<string>();
        if (!Directory.Exists(_root)) return completed;
        foreach (var directory in Directory.GetDirectories(_root))
        {
            var path = Path.Combine(directory, "pending.json");
            if (!File.Exists(path)) continue;
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path))
                ?? throw new IOException($"Invalid archive manifest: {path}");
            Complete(directory, manifest, clearMarkers);
            completed.Add(manifest.RelativePath);
        }
        return completed;
    }

    private void Complete(string directory, Manifest manifest, Action<string> clearMarkers)
    {
        foreach (var entry in manifest.Entries)
        {
            var source = SafePath(_retry, entry.RelativePath);
            var target = SafePath(Path.Combine(directory, "files"), entry.RelativePath);
            if (File.Exists(target))
            {
                if (Fingerprint(target) != entry.Hash) throw new IOException($"Archived file changed: {target}");
                if (File.Exists(source))
                {
                    if (Fingerprint(source) != entry.Hash) throw new IOException($"Retry file changed during archiving: {source}");
                    File.Delete(source);
                }
                continue;
            }
            if (!File.Exists(source) || Fingerprint(source) != entry.Hash)
                throw new IOException($"Retry file missing or changed during archiving: {source}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
        }
        // 保管記録を残す。途中停止しても pending.json から再開できる。
        clearMarkers(manifest.RelativePath);
        File.Move(Path.Combine(directory, "pending.json"), Path.Combine(directory, "completed.json"));
    }

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(segment => segment is ".." or "."))
            throw new IOException($"Unsafe archive path: {relative}");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!LocalPathIdentity.Contains(root, full)) throw new IOException($"Unsafe archive path: {relative}");
        for (var current = full; LocalPathIdentity.Contains(root, current); current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Archive path contains a reparse point: {current}");
            if (LocalPathIdentity.Normalize(current) == LocalPathIdentity.Normalize(root)) break;
        }
        return full;
    }

    private static string Fingerprint(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public sealed record Entry(string RelativePath, string Hash);
    public sealed record Manifest(string RelativePath, Entry[] Entries);
}
