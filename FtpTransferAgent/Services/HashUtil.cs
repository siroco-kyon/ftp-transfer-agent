using System.Security.Cryptography;
using System.IO;

namespace FtpTransferAgent.Services;

/// <summary>
/// ファイルのハッシュ値を計算するユーティリティ
/// </summary>
public static class HashUtil
{
    // 指定アルゴリズムでハッシュ値を計算
    public static async Task<string> ComputeHashAsync(string path, string algorithm, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using HashAlgorithm hasher = algorithm.ToUpper() == "SHA256" ? SHA256.Create() : MD5.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            hasher.TransformBlock(buffer, 0, read, null, 0);
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }
}
