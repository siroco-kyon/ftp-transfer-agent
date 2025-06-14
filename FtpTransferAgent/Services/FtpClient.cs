using FluentFTP;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
            await _client.Connect(ct);
        }
    }

    // ファイルを一時名でアップロードしてからリネーム
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var tempPath = remotePath + ".tmp";
        await _client.UploadFile(localPath, tempPath, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct);
        await _client.Rename(tempPath, remotePath, ct);
    }

    // ダウンロードも一時ファイル経由で行う
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var temp = localPath + ".tmp";
        await _client.DownloadFile(temp, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
        File.Move(temp, localPath, true);
    }

    // リモートファイルのハッシュ値を取得
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        FtpHashAlgorithm alg = algorithm.ToUpper() switch
        {
            "SHA256" => FtpHashAlgorithm.SHA256,
            _ => FtpHashAlgorithm.MD5
        };
        var hash = await _client.GetChecksum(remotePath, alg, ct);
        return hash?.Value ?? string.Empty;
    }

    // 指定ディレクトリのファイル一覧を取得
    public async Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var listing = await _client.GetListing(remotePath, ct);
        return listing.Where(i => i.Type == FtpObjectType.File).Select(i => i.FullName);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        await _client.DeleteFile(remotePath, ct);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
