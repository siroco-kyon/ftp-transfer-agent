using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Services;

/// <summary>旧版の削除再開記録が残っている場合、警告用に件数だけを確認する。</summary>
public static class LegacyDownloadCleanupRecords
{
    public static int Count(string watchPath, TransferOptions transfer)
    {
        var endpoint = JsonSerializer.Serialize(new[] { transfer.Mode, transfer.Host, transfer.Port.ToString(), transfer.Username, transfer.RemotePath });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..16];
        var directory = Path.Combine(DeliveryStateStore.ResolveStateDirectory(transfer.StateDirectory, watchPath), "download-cleanup", key);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
    }
}
