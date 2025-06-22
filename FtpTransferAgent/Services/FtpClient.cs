using FluentFTP;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using FluentFTP.Exceptions;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Services;

/// <summary>
/// FluentFTP を利用した FTP クライアントのラッパー
/// </summary>
public class AsyncFtpClientWrapper : IFileTransferClient, IDisposable
{
    private readonly AsyncFtpClient _client;
    private readonly ILogger<AsyncFtpClientWrapper> _logger;

    // テスト用に既存の AsyncFtpClient を渡せるようオーバーロードを追加
    public AsyncFtpClientWrapper(TransferOptions options, ILogger<AsyncFtpClientWrapper> logger, AsyncFtpClient? client = null)
    {
        _logger = logger;
        _client = client ?? new AsyncFtpClient(options.Host, options.Username, options.Password, options.Port);
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
        
        await _client.UploadFile(localPath, tempPath, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct).ConfigureAwait(false);
        await _client.MoveFile(tempPath, remotePath, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
    }

    // ダウンロードも一時ファイル経由で行う
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var temp = $"{localPath}.tmp.{Guid.NewGuid():N}";
        await _client.DownloadFile(temp, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, ct).ConfigureAwait(false);
        File.Move(temp, localPath, true);
    }

    // リモートファイルのハッシュ値を取得（常にダウンロードして計算）
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        // サーバーのハッシュコマンドは使用せず、常にファイルをダウンロードして計算
        await using var stream = await _client.OpenRead(remotePath, FtpDataType.Binary, 0, true, ct).ConfigureAwait(false);
        var result = await HashUtil.ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
        return result;
    }

    // 指定ディレクトリのファイル一覧を取得
    public async Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var listing = await _client.GetListing(remotePath, ct).ConfigureAwait(false);
        return listing.Where(i => i.Type == FtpObjectType.File).Select(i => i.FullName);
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
