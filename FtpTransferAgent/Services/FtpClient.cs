using FluentFTP;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Services;

public class AsyncFtpClientWrapper : IFileTransferClient, IDisposable
{
    private readonly AsyncFtpClient _client;
    private readonly ILogger<AsyncFtpClientWrapper> _logger;

    public AsyncFtpClientWrapper(TransferOptions options, ILogger<AsyncFtpClientWrapper> logger)
    {
        _logger = logger;
        _client = new AsyncFtpClient(options.Host, options.Username, options.Password, options.Port);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            await _client.Connect(ct);
        }
    }

    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var tempPath = remotePath + ".tmp";
        await _client.UploadFile(localPath, tempPath, FtpRemoteExists.Overwrite, true, FtpVerify.None, null, ct);
        await _client.Rename(tempPath, remotePath, ct);
    }

    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var temp = localPath + ".tmp";
        await _client.DownloadFile(temp, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
        File.Move(temp, localPath, true);
    }

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

    public async Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var listing = await _client.GetListing(remotePath, ct);
        return listing.Where(i => i.Type == FtpObjectType.File).Select(i => i.FullName);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
