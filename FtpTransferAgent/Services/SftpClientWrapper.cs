using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FtpTransferAgent.Services;

public class SftpClientWrapper : IFileTransferClient, IDisposable
{
    private readonly SftpClient _client;
    private readonly ILogger<SftpClientWrapper> _logger;

    public SftpClientWrapper(TransferOptions options, ILogger<SftpClientWrapper> logger)
    {
        _logger = logger;
        _client = new SftpClient(options.Host, options.Port, options.Username, options.Password);
    }

    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect();
        }
    }

    public Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        EnsureConnected();
        using var fs = File.OpenRead(localPath);
        var temp = remotePath + ".tmp";
        _client.UploadFile(fs, temp, true);
        _client.RenameFile(temp, remotePath);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        EnsureConnected();
        var temp = localPath + ".tmp";
        using var fs = File.Create(temp);
        _client.DownloadFile(remotePath, fs);
        fs.Close();
        File.Move(temp, localPath, true);
        return Task.CompletedTask;
    }

    public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct)
    {
        EnsureConnected();
        using var ms = new MemoryStream();
        _client.DownloadFile(remotePath, ms);
        ms.Position = 0;
        using System.Security.Cryptography.HashAlgorithm hasher = algorithm.ToUpper() == "SHA256" ? System.Security.Cryptography.SHA256.Create() : System.Security.Cryptography.MD5.Create();
        var hash = hasher.ComputeHash(ms);
        return Task.FromResult(BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant());
    }

    public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        EnsureConnected();
        var files = _client.ListDirectory(remotePath)
            .Where(f => !f.IsDirectory && !f.IsSymbolicLink)
            .Select(f => f.FullName);
        return Task.FromResult((IEnumerable<string>)files.ToArray());
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
