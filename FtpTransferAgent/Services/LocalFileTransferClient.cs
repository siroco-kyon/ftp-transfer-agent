using System.Collections.Generic;
using System.IO;
using System.Linq;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Services;

/// <summary>
/// ローカル / UNC (SMB 共有) のファイルシステムに対して転送を行うクライアント。
///
/// <para>
/// FTP / SFTP のような通信プロトコルではなく、OS のファイル I/O で完結する。宛先 (<c>RemotePath</c>) には
/// ローカルの絶対パス (例: <c>D:\out</c>) や UNC パス (例: <c>\\server\share\incoming</c>) を指定する。
/// UNC への書き込みは OS が SMB を透過処理するため、本クラスは SMB1/CIFS を一切実装・有効化しない
/// (使われるのは OS が交渉するモダンな SMB)。
/// </para>
///
/// <para>
/// 既存ツールが CIFS で行っていた「ローカル → LAN 共有フォルダへの書き込み」を、本ツールの
/// アトミック転送 (一時名 → リネーム) とハッシュ検証付きで置き換えるための実装。
/// 現状は put (アップロード) 方向のみを正式サポートする (起動時バリデーションで get + local を拒否)。
/// </para>
///
/// <para>
/// 接続を持たないため <see cref="IFileTransferClient"/> としてはステートレスで、ワーカー間で安全に
/// 再利用できる (各操作はユニークな一時名を使い、互いに干渉しない)。
/// </para>
/// </summary>
public sealed class LocalFileTransferClient : IFileTransferClient
{
    private readonly ILogger<LocalFileTransferClient> _logger;

    // 大容量でもメモリを使い切らないストリームコピー用バッファ
    private const int CopyBufferSize = 81920;

    // options は将来の拡張 (権限・属性保持など) のために受け取るが、現状はパスを引数で受けるため未使用。
    public LocalFileTransferClient(DestinationOptions options, ILogger<LocalFileTransferClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// ローカルファイルを宛先 (ローカル / UNC) へコピーする。
    /// 一時名へ書き込んでから本番名へリネームすることで、相手のバッチが書きかけを拾わないようにする。
    /// </summary>
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        var destination = Normalize(remotePath);
        EnsureParentDirectory(destination);

        // 一意な一時名で衝突防止 (SFTP/FTP ラッパーと同じ規約)
        var temp = $"{destination}.tmp.{Guid.NewGuid():N}";
        _logger.LogDebug("Local upload: {LocalPath} -> temp={TempPath}", localPath, temp);

        try
        {
            ct.ThrowIfCancellationRequested();
            await CopyFileAsync(localPath, temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 同一ボリュームなら一瞬のリネーム。別ボリュームへの UNC でも File.Move が面倒を見る。
            // overwrite:true で既存ファイルを置き換える。
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            // リネーム前に失敗した場合、一時ファイルを残さない
            TryDelete(temp);
            throw;
        }

        if (!File.Exists(destination))
        {
            throw new IOException($"Local move completed without error but destination file not found: {destination}.");
        }
        _logger.LogDebug("Local upload confirmed at: {Destination}", destination);
    }

    /// <summary>
    /// 宛先 (ローカル / UNC) のファイルをローカルへコピーする。こちらも一時名経由。
    /// </summary>
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        var source = Normalize(remotePath);
        EnsureParentDirectory(localPath);
        var temp = $"{localPath}.tmp.{Guid.NewGuid():N}";

        try
        {
            ct.ThrowIfCancellationRequested();
            await CopyFileAsync(source, temp, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            File.Move(temp, localPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// 宛先ファイルのハッシュ値を計算する。ローカルファイルを直接読むため "サーバーコマンド" の概念はない。
    /// </summary>
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        if (useServerCommand)
        {
            _logger.LogDebug("Server-side hash command is not applicable for local transfers. Using local calculation for {Algorithm}.", algorithm);
        }
        return await HashUtil.ComputeHashAsync(Normalize(remotePath), algorithm, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 指定ディレクトリ配下のファイル一覧を返す。存在しないディレクトリは
    /// FTP/SFTP 実装と挙動を揃えて <see cref="DirectoryNotFoundException"/> とする。
    /// </summary>
    public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
    {
        var dir = Normalize(remotePath);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"Local directory not found: {dir}");
        }

        var option = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files = Directory.EnumerateFiles(dir, "*", option).ToList();
        return Task.FromResult(files);
    }

    public Task<bool> ExistsAsync(string remotePath, CancellationToken ct)
        => Task.FromResult(File.Exists(Normalize(remotePath)));

    public Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        File.Delete(Normalize(remotePath));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // 接続を持たないため後始末は不要
    }

    // パス区切りを実行 OS のものへ正規化する (Worker.BuildRemotePath は OS ネイティブで組むが、
    // 直接呼ばれるケースやテストでの混在入力にも備える)。UNC 先頭の "\\" は維持される。
    private static string Normalize(string path)
        => path.Replace('/', Path.DirectorySeparatorChar);

    private static void EnsureParentDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var src = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
            FileOptions.Asynchronous);
        await src.CopyToAsync(dst, CopyBufferSize, ct).ConfigureAwait(false);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to delete temp file {Path}: {Error}", path, ex.Message);
        }
    }
}
