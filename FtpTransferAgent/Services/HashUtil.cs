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
            "MD5" => MD5.Create(),
            "SHA256" => SHA256.Create(),
            "SHA512" => SHA512.Create(),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}. Only MD5, SHA256, and SHA512 are supported.")
        };

        // ファイルサイズに応じてバッファサイズを調整
        var streamLength = stream.CanSeek ? stream.Length : 0;
        var bufferSize = streamLength switch
        {
            < 1024 * 1024 => 8192,       // 1MB未満: 8KB
            < 10 * 1024 * 1024 => 32768,  // 10MB未満: 32KB
            < 100 * 1024 * 1024 => 131072, // 100MB未満: 128KB
            _ => 262144                    // 100MB以上: 256KB
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

    // 同期ストリームからハッシュ値を計算（ReadAsync が使えないストリーム向け）
    // SftpFileStream のように ReadAsync の互換性が不明なストリームに対して使用する。
    // 呼び出し元は Task.Run 内で呼ぶこと。
    public static string ComputeHashSync(Stream stream, string algorithm)
    {
        using HashAlgorithm hasher = algorithm.ToUpper() switch
        {
            "MD5" => MD5.Create(),
            "SHA256" => SHA256.Create(),
            "SHA512" => SHA512.Create(),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}. Only MD5, SHA256, and SHA512 are supported.")
        };

        // CanSeek の場合はファイルサイズに応じてバッファサイズを調整
        var streamLength = stream.CanSeek ? stream.Length : 0;
        var bufferSize = streamLength switch
        {
            < 1024 * 1024 => 8192,
            < 10 * 1024 * 1024 => 32768,
            < 100 * 1024 * 1024 => 131072,
            _ => 262144
        };

        var buffer = new byte[bufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.TransformBlock(buffer, 0, read, null, 0);
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hashBytes = hasher.Hash ?? throw new InvalidOperationException("Hash computation failed");
        return BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}
