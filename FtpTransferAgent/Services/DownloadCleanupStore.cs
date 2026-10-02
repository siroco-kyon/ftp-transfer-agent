using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Services;

/// <summary>取得済みファイルの削除を、ENDの有無にかかわらず次回バッチで再開する。</summary>
public sealed class DownloadCleanupStore
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Record> _pending = new(StringComparer.Ordinal);

    public DownloadCleanupStore(string watchPath, TransferOptions transfer)
    {
        var endpoint = JsonSerializer.Serialize(new[] { transfer.Mode, transfer.Host, transfer.Port.ToString(), transfer.Username, transfer.RemotePath });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..16];
        _directory = Path.Combine(DeliveryStateStore.ResolveStateDirectory(transfer.StateDirectory, watchPath), "download-cleanup", key);
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(file))
                ?? throw new IOException($"Invalid download cleanup record: {file}");
            if (string.IsNullOrWhiteSpace(record.Path) || string.IsNullOrWhiteSpace(record.Hash) || record.EndFiles is null)
                throw new IOException($"Invalid download cleanup record: {file}");
            _pending[record.Path] = record;
        }
    }

    public IEnumerable<Record> Pending => _pending.Values;
    public bool TryGet(string path, out Record? record) => _pending.TryGetValue(path, out record);

    public void Save(Record record)
    {
        Directory.CreateDirectory(_directory);
        var path = RecordPath(record.Path);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        _pending[record.Path] = record;
    }

    public void Remove(string path)
    {
        File.Delete(RecordPath(path));
        _pending.TryRemove(path, out _);
    }

    private string RecordPath(string path) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json");

    public sealed record EndFile(string Path, string Hash)
    {
        public bool Downloaded { get; set; }
        public bool Deleted { get; set; }
    }

    public sealed record Record(string Path, string Hash, EndFile[] EndFiles, bool TransferEndFiles)
    {
        public bool DataDeleted { get; set; }
    }
}
