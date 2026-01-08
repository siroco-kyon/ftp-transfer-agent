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
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

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

            var conn = new ConnectionInfo(options.Host, options.Port, options.Username, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            };
            _client = new SftpClient(conn);
            _client.OperationTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
        else
        {
            var password = options.Password ?? throw new ArgumentNullException(nameof(options.Password));
            var conn = new ConnectionInfo(options.Host, options.Port, options.Username, new PasswordAuthenticationMethod(options.Username, password))
            {
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            };
            _client = new SftpClient(conn);
            _client.OperationTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
    }

    // 接続されていなければ接続を確立
    private async Task EnsureConnectedAsync()
    {
        if (!_client.IsConnected)
        {
            await Task.Run(() => _client.Connect()).ConfigureAwait(false);
        }
    }

    // 同期版は既存コード互換性のため保持
    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect();
        }
    }

    // リモートディレクトリが存在しなければ作成
    private void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        if (_client.Exists(dir))
        {
            return;
        }
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!_client.Exists(current))
            {
                _client.CreateDirectory(current);
            }
        }
    }

    // ファイルを一時名でアップロードしてからリネーム
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync().ConfigureAwait(false);
        EnsureDirectory(remotePath);

        // 一意な一時ファイル名で衝突防止
        var temp = $"{remotePath}.tmp.{Guid.NewGuid():N}";

        await using var fs = File.OpenRead(localPath);
        _client.UploadFile(fs, temp, true);
        if (_client.Exists(remotePath))
        {
            _client.DeleteFile(remotePath);
        }
        _client.RenameFile(temp, remotePath);
    }

    // ダウンロードも一時ファイル経由で行う
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync().ConfigureAwait(false);
        var temp = $"{localPath}.tmp.{Guid.NewGuid():N}";

        await using (var fs = File.Create(temp))
        {
            _client.DownloadFile(remotePath, fs);
        }

        File.Move(temp, localPath, true);
    }

    // リモートファイルのハッシュ値を取得（ストリーミング処理でメモリ効率化）
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        await EnsureConnectedAsync().ConfigureAwait(false);

        // SFTPプロトコルではサーバーサイドハッシュコマンドが標準サポートされていないため
        // useServerCommandパラメータが指定されている場合は警告ログを出力
        if (useServerCommand)
        {
            _logger.LogDebug("Server-side hash command is not supported in SFTP protocol. Using local calculation for {Algorithm}", algorithm);
        }

        // 大容量ファイルの場合はストリーミング処理
        using var stream = _client.OpenRead(remotePath);
        return await HashUtil.ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
    }

    // 指定ディレクトリのファイル一覧を取得
    public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
    {
        EnsureConnected();
        
        if (!includeSubdirectories)
        {
            var files = _client.ListDirectory(remotePath)
                .Where(f => !f.IsDirectory && !f.IsSymbolicLink)
                .Select(f => f.FullName);
            return Task.FromResult((IEnumerable<string>)files.ToArray());
        }
        
        // サブディレクトリを含む再帰的な検索
        var allFiles = new List<string>();
        ListFilesRecursive(remotePath, allFiles);
        return Task.FromResult((IEnumerable<string>)allFiles);
    }
    
    private void ListFilesRecursive(string currentPath, List<string> allFiles)
    {
        var entries = _client.ListDirectory(currentPath);
        
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory && !entry.IsSymbolicLink)
            {
                allFiles.Add(entry.FullName);
            }
            else if (entry.IsDirectory && entry.Name != "." && entry.Name != "..")
            {
                ListFilesRecursive(entry.FullName, allFiles);
            }
        }
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
