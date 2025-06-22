using System.Security.Cryptography;
using System.IO;

namespace FtpTransferAgent.Services;

/// <summary>
/// ファイルのハッシュ値を計算するユーティリティ
/// </summary>
public static class HashUtil
{
    // ストリームからハッシュ値を計算
    public static async Task<string> ComputeHashAsync(Stream stream, string algorithm, CancellationToken ct)
    {
        using HashAlgorithm hasher = algorithm.ToUpper() switch
        {
            "SHA256" => SHA256.Create(),
            "SHA512" => SHA512.Create(),
            "MD5" => MD5.Create(), // 非推奨だが互換性のため保持
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}")
        };
        
        // ファイルサイズに応じてバッファサイズを調整
        var streamLength = stream.CanSeek ? stream.Length : 0;
        var bufferSize = streamLength switch
        {
            < 1024 * 1024 => 8192,      // 1MB未満: 8KB
            < 10 * 1024 * 1024 => 32768, // 10MB未満: 32KB
            _ => 81920                    // 10MB以上: 80KB
        };
        
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            hasher.TransformBlock(buffer, 0, read, null, 0);
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hashBytes = hasher.Hash ?? throw new InvalidOperationException("Hash computation failed");
        return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    // ファイルパスを受け取ってハッシュ値を計算
    public static async Task<string> ComputeHashAsync(string path, string algorithm, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
    }
}
