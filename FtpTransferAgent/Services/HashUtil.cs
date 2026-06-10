using System.Security.Cryptography;
using System.IO;

namespace FtpTransferAgent.Services;

/// <summary>
/// ファイルのハッシュ値を計算するユーティリティ
/// </summary>
public static class HashUtil
{
    private static HashAlgorithmName ResolveAlgorithm(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "MD5" => HashAlgorithmName.MD5,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}. Only MD5, SHA256, and SHA512 are supported.")
        };
    }

    // ファイルサイズに応じてバッファサイズを調整
    private static int GetBufferSize(Stream stream)
    {
        var streamLength = stream.CanSeek ? stream.Length : 0;
        return streamLength switch
        {
            < 1024 * 1024 => 8192,         // 1MB未満: 8KB
            < 10 * 1024 * 1024 => 32768,   // 10MB未満: 32KB
            < 100 * 1024 * 1024 => 131072, // 100MB未満: 128KB
            _ => 262144                    // 100MB以上: 256KB
        };
    }

    // ストリームからハッシュ値を計算
    public static async Task<string> ComputeHashAsync(Stream stream, string algorithm, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(ResolveAlgorithm(algorithm));

        var buffer = new byte[GetBufferSize(stream)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // ファイルパスを受け取ってハッシュ値を計算
    public static async Task<string> ComputeHashAsync(string path, string algorithm, CancellationToken ct)
    {
        // FileOptions.Asynchronous を指定しないと ReadAsync が同期 I/O のスレッドプール
        // ラップになるため、真の非同期 I/O + 順次アクセスヒントで開く
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
    }

    // 同期ストリームからハッシュ値を計算（ReadAsync が使えないストリーム向け）
    public static string ComputeHashSync(Stream stream, string algorithm)
    {
        using var hasher = IncrementalHash.CreateHash(ResolveAlgorithm(algorithm));

        var buffer = new byte[GetBufferSize(stream)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
