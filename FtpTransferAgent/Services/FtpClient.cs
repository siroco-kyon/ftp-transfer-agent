using FluentFTP;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentFTP.Exceptions;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Services;

/// <summary>
/// FluentFTP を利用した FTP クライアントのラッパー
/// </summary>
public class AsyncFtpClientWrapper : IFileTransferClient, IDisposable
{
    // 失敗時の一時ファイル掃除に許す時間。相手が停止している場合に掃除自体が
    // 転送単位のタイムアウトを超えてブロックしないよう区切る
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    private readonly AsyncFtpClient _client;
    private readonly ILogger<AsyncFtpClientWrapper> _logger;
    private readonly DestinationOptions _options;

    // 宛先削除後のリネーム失敗で意図的に残した一時ファイル (宛先パス → 一時パス一覧)。
    // Polly の再試行は毎回別の一時名を使うため、成功した時点で掃除しないとサーバに
    // 重複ファイルが残り続ける。接続はプールから排他的に貸し出されるためロック不要。
    private readonly Dictionary<string, List<string>> _retainedTempPaths = new(StringComparer.Ordinal);

    // テスト用に既存の AsyncFtpClient を渡せるようオーバーロードを追加
    public AsyncFtpClientWrapper(DestinationOptions options, ILogger<AsyncFtpClientWrapper> logger, AsyncFtpClient? client = null)
    {
        _logger = logger;
        _options = options;
        _client = client ?? new AsyncFtpClient(options.Host, options.Username, options.Password, options.Port);

        // タイムアウト設定を適用
        if (client == null)
        {
            _client.Config.ConnectTimeout = options.TimeoutSeconds * 1000;
            _client.Config.ReadTimeout = options.TimeoutSeconds * 1000;
            _client.Config.DataConnectionConnectTimeout = options.TimeoutSeconds * 1000;
            _client.Config.DataConnectionReadTimeout = options.TimeoutSeconds * 1000;

            // 接続をワーカー間で再利用する際、制御接続がアイドルで切断されないようにする。
            // FTP は NOOP デーモンで指定間隔ごとに NOOP を送り、アイドル切断を秒数どおり防ぐ。
            // Noop(マスタースイッチ)を有効化しないと NoopInterval だけでは NOOP は送られない。
            // 併せて OS の TCP KeepAlive も有効化する。
            if (options.KeepAliveSeconds > 0)
            {
                _client.Config.Noop = true;
                _client.Config.NoopInterval = options.KeepAliveSeconds * 1000;
                _client.Config.SocketKeepAlive = true;
            }
        }
    }

    // 接続されていなければ接続を確立
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            await _client.Connect(ct).ConfigureAwait(false);
        }
    }

    // リモートディレクトリが存在しなければ作成
    private async Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        if (!await _client.DirectoryExists(dir, ct).ConfigureAwait(false))
        {
            await _client.CreateDirectory(dir, true, ct).ConfigureAwait(false);
        }
    }

    // ファイルを一時名でアップロードしてからリネーム
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await EnsureDirectoryAsync(remotePath, ct).ConfigureAwait(false);

        // 一意な一時ファイル名で衝突防止
        var tempPath = $"{remotePath}.tmp.{Guid.NewGuid():N}";

        // フォールバック経路で宛先を削除済みかどうか。true の場合、失敗しても一時ファイルを
        // 消してはならない (旧ファイルと新ファイルの両方を失うため)
        var destinationRemoved = new StrongBox<bool>(false);

        try
        {
            // FluentFTP は 4xx/5xx 応答やデータ接続断でも例外を投げず FtpStatus.Failed を返すため、
            // 戻り値を必ず検査する。これを怠るとサーバに保存されていないのに成功扱いとなり、
            // Cleanup.DeleteAfterVerify によってローカル原本が削除される
            var status = await _client.UploadFile(localPath, tempPath, FtpRemoteExists.NoCheck, true, FtpVerify.None, null, ct).ConfigureAwait(false);
            if (status != FtpStatus.Success)
            {
                throw new TransferFailedException(
                    $"FTP upload did not complete successfully (status={status}): {localPath} -> {remotePath}");
            }

            await RenameOverwriteAsync(tempPath, remotePath, destinationRemoved, ct).ConfigureAwait(false);

            await VerifyUploadedAsync(localPath, remotePath, ct).ConfigureAwait(false);

            // 過去の試行で残した一時ファイルは、宛先が正しく作られた時点で不要になる
            await CleanupRetainedTempFilesAsync(remotePath).ConfigureAwait(false);
        }
        catch
        {
            if (destinationRemoved.Value)
            {
                // 宛先を削除した後にリネームが失敗した状態。一時ファイルを消すと復旧不能になるため
                // 意図的に残し、手動復旧できるようパスをログに出す。
                // 再試行が成功したら CleanupRetainedTempFilesAsync が掃除する
                if (!_retainedTempPaths.TryGetValue(remotePath, out var retained))
                {
                    retained = new List<string>();
                    _retainedTempPaths[remotePath] = retained;
                }
                retained.Add(tempPath);

                _logger.LogError(
                    "FTP rename failed after the existing destination file was removed. The uploaded data is retained at {TempPath} and must be renamed to {RemotePath} manually if the retry does not recover it.",
                    tempPath, remotePath);
            }
            else
            {
                // 一時ファイルがリモートに蓄積しないよう削除を試みる。
                // 失敗要因がキャンセルでも掃除できるよう元のトークンは使わないが、
                // CancellationToken.None にすると相手が停止した際に掃除自体が
                // Transfer.TransferTimeoutSeconds を超えてブロックし得るため、時間を区切る
                using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
                try { await _client.DeleteFile(tempPath, cleanupCts.Token).ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// 宛先削除後のリネーム失敗で残した一時ファイルを掃除する。
    /// 再試行が成功して宛先が正しく作られた後に呼ぶこと (それまでは唯一のデータなので消せない)。
    /// </summary>
    private async Task CleanupRetainedTempFilesAsync(string remotePath)
    {
        if (!_retainedTempPaths.TryGetValue(remotePath, out var retained))
        {
            return;
        }

        _retainedTempPaths.Remove(remotePath);
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        foreach (var path in retained)
        {
            try
            {
                await _client.DeleteFile(path, cleanupCts.Token).ConfigureAwait(false);
                _logger.LogInformation("Removed the temporary file retained by a previous failed rename: {TempPath}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not remove the temporary file retained by a previous failed rename: {TempPath} ({Error})", path, ex.Message);
            }
        }
    }

    /// <summary>
    /// 一時ファイルを宛先名へリネームする。RNTO で既存ファイルを上書きできるサーバでは
    /// 原子的に置き換わる。上書きを拒否するサーバのみ削除 + リネームにフォールバックし、
    /// 宛先を削除したことを <paramref name="destinationRemoved"/> で呼び出し元へ伝える。
    /// </summary>
    private async Task RenameOverwriteAsync(string tempPath, string remotePath, StrongBox<bool> destinationRemoved, CancellationToken ct)
    {
        // 宛先が存在しなければ上書きの考慮は不要。存在する場合のみフォールバックを許可することで、
        // 接続断などリネーム以外の理由で失敗したときに既存の宛先を削除してしまう事故を防ぐ
        var destinationExists = await _client.FileExists(remotePath, ct).ConfigureAwait(false);

        try
        {
            await _client.Rename(tempPath, remotePath, ct).ConfigureAwait(false);
            return;
        }
        catch (FtpException ex) when (destinationExists)
        {
            // RNTO による上書きを拒否するサーバ向けのフォールバック。
            // Delete と Rename の間に障害が起きると宛先が存在しない瞬間が生じる
            _logger.LogDebug("FTP rename over an existing file failed, falling back to delete+rename: {Error}", ex.Message);
        }

        await _client.DeleteFile(remotePath, ct).ConfigureAwait(false);
        destinationRemoved.Value = true;
        await _client.Rename(tempPath, remotePath, ct).ConfigureAwait(false);
        destinationRemoved.Value = false;
    }

    /// <summary>
    /// アップロード後に宛先ファイルの存在とサイズを確認する。
    /// ハッシュ検証を無効にしている構成でも「保存されていないのに成功扱い」を検出できるようにする。
    /// </summary>
    private async Task VerifyUploadedAsync(string localPath, string remotePath, CancellationToken ct)
    {
        if (!_options.VerifyUploadedFileExists)
        {
            return;
        }

        // SIZE 非対応のサーバは -1 を返す。その場合は存在確認までに留める
        var remoteSize = await _client.GetFileSize(remotePath, -1, ct).ConfigureAwait(false);
        if (remoteSize < 0)
        {
            if (!await _client.FileExists(remotePath, ct).ConfigureAwait(false))
            {
                throw new TransferFailedException(
                    $"FTP upload completed without error but the destination file was not found: {remotePath}");
            }
            _logger.LogDebug("FTP upload confirmed at (existence only, SIZE unsupported): {RemotePath}", remotePath);
            return;
        }

        var localSize = new FileInfo(localPath).Length;
        if (remoteSize != localSize)
        {
            throw new TransferFailedException(
                $"FTP upload size mismatch for {remotePath}: local={localSize} bytes, remote={remoteSize} bytes");
        }
        _logger.LogDebug("FTP upload confirmed at: {RemotePath} ({Size} bytes)", remotePath, remoteSize);
    }

    // ダウンロードも一時ファイル経由で行う
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var temp = $"{localPath}.tmp.{Guid.NewGuid():N}";
        try
        {
            // アップロードと同様、FluentFTP は転送中断や 4xx/5xx 応答を戻り値で通知する。
            // 戻り値を検査しないと途中までのファイルを正常扱いで配置してしまう
            var status = await _client.DownloadFile(temp, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, ct).ConfigureAwait(false);
            if (status != FtpStatus.Success)
            {
                throw new TransferFailedException(
                    $"FTP download did not complete successfully (status={status}): {remotePath} -> {localPath}");
            }
            File.Move(temp, localPath, true);
        }
        catch
        {
            // File.Move 失敗時に一時ファイルが残らないよう削除する
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    // リモートファイルのハッシュ値を取得
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        if (useServerCommand)
        {
            try
            {
                // サーバーサイドハッシュコマンドを試行
                var serverHash = await TryGetServerHashAsync(remotePath, algorithm, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(serverHash))
                {
                    return serverHash;
                }
            }
            catch (FluentFTP.Exceptions.FtpException ex)
            {
                // サーバーサイドハッシュコマンドがサポートされていない場合
                _logger.LogDebug("Server hash command not supported for {Algorithm}: {Error}", algorithm, ex.Message);
            }
            catch (Exception ex)
            {
                // その他のエラーでサーバーサイドハッシュが失敗した場合
                _logger.LogWarning("Server hash calculation failed, falling back to local calculation: {Error}", ex.Message);
            }
        }

        // ローカルでハッシュを計算
        await using var stream = await _client.OpenRead(remotePath, FtpDataType.Binary, 0, true, ct).ConfigureAwait(false);
        var result = await HashUtil.ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<string?> TryGetServerHashAsync(string remotePath, string algorithm, CancellationToken ct)
    {
        try
        {
            // FluentFTPのGetChecksum機能を使用
            var hashType = algorithm.ToUpperInvariant() switch
            {
                "MD5" => FtpHashAlgorithm.MD5,
                "SHA256" => FtpHashAlgorithm.SHA256,
                "SHA512" => FtpHashAlgorithm.SHA512,
                _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}")
            };

            var checksum = await _client.GetChecksum(remotePath, hashType, ct).ConfigureAwait(false);
            return checksum?.Value;
        }
        catch
        {
            return null;
        }
    }

    // 指定ディレクトリのファイル一覧を取得
    public async Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        // FTP サーバの多くは存在しないディレクトリの LIST に空応答を返すため、
        // RemotePath の設定ミスが「0 件成功」として見逃されないよう明示的に確認する
        // (SFTP 側は例外で失敗するので挙動を揃える)。ルートはチェック不要。
        if (!string.IsNullOrEmpty(remotePath) && remotePath != "/" && remotePath != "."
            && !await _client.DirectoryExists(remotePath, ct).ConfigureAwait(false))
        {
            throw new DirectoryNotFoundException($"Remote directory not found: {remotePath}");
        }

        if (!includeSubdirectories)
        {
            var listing = await _client.GetListing(remotePath, ct).ConfigureAwait(false);
            return listing.Where(i => i.Type == FtpObjectType.File).Select(i => i.FullName);
        }

        // サブディレクトリを含む再帰的な検索
        var allFiles = new List<string>();
        await ListFilesRecursiveAsync(remotePath, allFiles, ct).ConfigureAwait(false);
        return allFiles;
    }

    private async Task ListFilesRecursiveAsync(string currentPath, List<string> allFiles, CancellationToken ct)
    {
        var listing = await _client.GetListing(currentPath, ct).ConfigureAwait(false);

        foreach (var item in listing)
        {
            if (item.Type == FtpObjectType.File)
            {
                allFiles.Add(item.FullName);
            }
            else if (item.Type == FtpObjectType.Directory)
            {
                await ListFilesRecursiveAsync(item.FullName, allFiles, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> ExistsAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await _client.FileExists(remotePath, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _client.DeleteFile(remotePath, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
