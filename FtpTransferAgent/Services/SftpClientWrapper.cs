using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace FtpTransferAgent.Services;

/// <summary>
/// SSH.NET を利用した SFTP クライアントのラッパー
/// </summary>
public class SftpClientWrapper : IFileTransferClient, IDisposable
{
    private readonly SftpClient _client;
    private readonly ILogger<SftpClientWrapper> _logger;

    // テスト用に既存の SftpClient を渡せるようにする
    public SftpClientWrapper(TransferOptions options, ILogger<SftpClientWrapper> logger, SftpClient? client = null)
    {
        _logger = logger;

        if (client != null)
        {
            _client = client;
            return;
        }

        if (!string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(options.Password))
            {
                methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));
            }
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.Username, keyFile));

            var conn = new ConnectionInfo(options.Host, options.Port, options.Username, methods.ToArray());
            _client = new SftpClient(conn);
        }
        else
        {
            var password = options.Password ?? throw new ArgumentNullException(nameof(options.Password));
            _client = new SftpClient(options.Host, options.Port, options.Username, password);
        }
    }

    // 接続されていなければ接続を確立
    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect();
        }
    }

    // ファイルを一時名でアップロードしてからリネーム
    public Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        EnsureConnected();
        using var fs = File.OpenRead(localPath);
        var temp = remotePath + ".tmp";
        _client.UploadFile(fs, temp, true);
        _client.RenameFile(temp, remotePath);
        return Task.CompletedTask;
    }

    // ダウンロードも一時ファイル経由で行う
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

    // リモートファイルのハッシュ値を取得
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

    // 指定ディレクトリのファイル一覧を取得
    public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct)
    {
        EnsureConnected();
        var files = _client.ListDirectory(remotePath)
            .Where(f => !f.IsDirectory && !f.IsSymbolicLink)
            .Select(f => f.FullName);
        return Task.FromResult((IEnumerable<string>)files.ToArray());
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        EnsureConnected();
        _client.DeleteFile(remotePath);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
