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
    // 設定値全体を保持。ホスト鍵検証で利用するため。
    private readonly TransferOptions _options;

    // テスト用に既存の SftpClient を渡せるようにする
    public SftpClientWrapper(TransferOptions options, ILogger<SftpClientWrapper> logger, SftpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _options = options;

        if (client != null)
        {
            // テストや特殊なケースで既存のSftpClientを受け取った場合でも
            // 安全なホスト鍵検証が機能するように、ここでイベントを登録する。
            _client = client;
            AttachHostKeyValidation();
            // 接続タイムアウトも設定しておく（呼び出し元で設定済みなら上書きにはならない）
            try
            {
                _client.OperationTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            }
            catch
            {
                // OperationTimeoutが設定できない場合は無視する
            }
        }
        else
        {
            // 接続情報を構築し、弱いホスト鍵アルゴリズムや鍵交換アルゴリズムを除外する
            ConnectionInfo conn;
            if (!string.IsNullOrEmpty(options.PrivateKeyPath))
            {
                var methods = new List<AuthenticationMethod>();
                if (!string.IsNullOrEmpty(options.Password))
                {
                    methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));
                    logger.LogInformation("SFTP auth configured: PrivateKey + Password (host={Host}, port={Port}, user={User}, key={Key})",
                        options.Host, options.Port, options.Username, options.PrivateKeyPath);
                }
                else
                {
                    logger.LogInformation("SFTP auth configured: PrivateKey only (host={Host}, port={Port}, user={User}, key={Key})",
                        options.Host, options.Port, options.Username, options.PrivateKeyPath);
                }
                var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(options.PrivateKeyPath)
                    : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(options.Username, keyFile));

                conn = new ConnectionInfo(options.Host, options.Port, options.Username, methods.ToArray())
                {
                    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
                };
            }
            else
            {
                var password = options.Password ?? throw new ArgumentNullException(nameof(options.Password));
                logger.LogInformation("SFTP auth configured: Password only (host={Host}, port={Port}, user={User})",
                    options.Host, options.Port, options.Username);
                conn = new ConnectionInfo(options.Host, options.Port, options.Username, new PasswordAuthenticationMethod(options.Username, password))
                {
                    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
                };
            }

            // 安全性を高めるため、弱い鍵交換方式やホスト鍵アルゴリズムを除外
            ConfigureConnectionSecurity(conn);
            _client = new SftpClient(conn);
            // サーバのホスト鍵指紋を検証・記録するハンドラを登録
            AttachHostKeyValidation();
            _client.OperationTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }
    }

    // 接続されていなければ接続を確立
    private async Task EnsureConnectedAsync()
    {
        if (!_client.IsConnected)
        {
            await Task.Run(() => _client.Connect()).ConfigureAwait(false);
            LogConnectionEstablished();
        }
    }

    // 同期版は既存コード互換性のため保持
    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect();
            LogConnectionEstablished();
        }
    }

    // SFTP接続確立時の詳細情報をログに出力する
    private void LogConnectionEstablished()
    {
        var info = _client.ConnectionInfo;
        _logger.LogInformation(
            "SFTP session established: Host={Host}, Port={Port}, User={User}, " +
            "ServerVersion={ServerVersion}, KeyExchange={Kex}, Encryption={Enc}, Hmac={Hmac}",
            info.Host,
            info.Port,
            info.Username,
            info.ServerVersion,
            info.CurrentKeyExchangeAlgorithm,
            info.CurrentClientEncryption,
            info.CurrentClientHmacAlgorithm);
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
        _logger.LogDebug("SFTP upload: {LocalPath} -> temp={TempPath}", localPath, temp);

        await using var fs = File.OpenRead(localPath);
        _client.UploadFile(fs, temp, true);
        _logger.LogDebug("SFTP UploadFile completed. Temp exists: {Exists}", _client.Exists(temp));

        if (_client.Exists(remotePath))
        {
            _logger.LogDebug("SFTP: Remote file exists, deleting before rename: {RemotePath}", remotePath);
            _client.DeleteFile(remotePath);
        }
        _client.RenameFile(temp, remotePath);

        // RenameFile 後の存在確認：失敗した場合は一時ファイルが残ったままになるため明示的にエラーとする
        if (!_client.Exists(remotePath))
        {
            throw new InvalidOperationException(
                $"SFTP RenameFile completed without error but destination file not found: {remotePath}. Temp file may remain at: {temp}");
        }
        _logger.LogDebug("SFTP upload confirmed at: {RemotePath}", remotePath);
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

    // リモートファイルのハッシュ値を取得（一時ファイル経由でダウンロードして計算）
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        await EnsureConnectedAsync().ConfigureAwait(false);

        // SFTPプロトコルではサーバーサイドハッシュコマンドが標準サポートされていないため
        // useServerCommandパラメータが指定されている場合は警告ログを出力
        if (useServerCommand)
        {
            _logger.LogDebug("Server-side hash command is not supported in SFTP protocol. Using local calculation for {Algorithm}", algorithm);
        }

        // SSH.NET の SftpFileStream は ReadAsync(Memory<byte>, CancellationToken) との互換性が
        // 保証されないため、一時ローカルファイルにダウンロードしてからハッシュを計算する。
        // これにより確実にリモートの実データを検証できる。
        var tempFile = Path.GetTempFileName();
        try
        {
            await using (var fs = File.OpenWrite(tempFile))
            {
                _client.DownloadFile(remotePath, fs);
            }
            return await HashUtil.ComputeHashAsync(tempFile, algorithm, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
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

    /// <summary>
    /// 接続情報から弱いホスト鍵アルゴリズムおよび SHA-1 ベースの鍵交換方式を除去する。
    /// </summary>
    /// <param name="conn">接続情報オブジェクト</param>
    private static void ConfigureConnectionSecurity(ConnectionInfo conn)
    {
        if (conn == null)
        {
            return;
        }
        // HostKeyAlgorithms は IOrderedDictionary であるため、Remove(object key) を使用する。
        try { conn.HostKeyAlgorithms.Remove("ssh-rsa"); } catch { }
        try { conn.HostKeyAlgorithms.Remove("ssh-dss"); } catch { }

        // SHA-1 ベースの鍵交換方式を削除
        var weakKex = new[]
        {
            "diffie-hellman-group-exchange-sha1",
            "diffie-hellman-group14-sha1",
            "diffie-hellman-group1-sha1"
        };
        foreach (var algo in weakKex)
        {
            try { conn.KeyExchangeAlgorithms.Remove(algo); } catch { }
        }
    }

    /// <summary>
    /// SftpClient にホスト鍵検証ハンドラを登録する。
    /// 期待される指紋が設定されている場合は照合し、一致しない場合は接続を拒否する。
    /// 未設定の場合は受信した指紋をログに出力して信頼する。
    /// </summary>
    private void AttachHostKeyValidation()
    {
        // SftpClient のホスト鍵受信イベントにハンドラを追加
        _client.HostKeyReceived += (sender, e) =>
        {
            // 受信した指紋 (MD5) を16進数表記に変換
            var received = BitConverter.ToString(e.FingerPrint).Replace("-", "").ToLowerInvariant();

            // TransferOptions.HostKeyFingerprint から期待値を直接取得
            var expected = _options.HostKeyFingerprint;

            if (!string.IsNullOrEmpty(expected))
            {
                // コロンやハイフンを除去して比較用に整形
                expected = expected.Replace(":", "").Replace("-", "").ToLowerInvariant();
                if (!string.Equals(expected, received, StringComparison.OrdinalIgnoreCase))
                {
                    // 指紋が一致しない場合は信頼せず接続を拒否
                    e.CanTrust = false;
                    _logger.LogError("Host key fingerprint mismatch. Expected {Expected}, but got {Received}", expected, received);
                    return;
                }
                e.CanTrust = true;
                _logger.LogInformation("Host key fingerprint verified: {Fingerprint}", received);
            }
            else
            {
                // 期待値が無い場合は受信した指紋をログ出力し、そのまま信頼
                // HostKeyFingerprint を設定することで MITM 攻撃を防止できます
                _logger.LogWarning("HostKeyFingerprint is not configured. Trusting server key without verification: {Fingerprint}", received);
                e.CanTrust = true;
            }
        };
    }
}
