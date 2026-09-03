using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
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
    private readonly DestinationOptions _options;
    // posix-rename 拡張のサポート状況 (接続先サーバごと)。null = 未判定
    private bool? _posixRenameSupported;

    // 存在を確認 (または作成) 済みのリモートディレクトリ。SFTP の Exists は
    // REALPATH + LSTAT の 2 往復を要するため、アップロードごとの再確認を省略して
    // 高レイテンシ回線での往復数を削減する。転送失敗時はエントリを無効化し、
    // リモート側でディレクトリが消された場合もリトライで再作成できるようにする。
    // (この接続は同時に 1 ワーカーしか使用しないため排他制御は不要)
    private readonly HashSet<string> _verifiedRemoteDirs = new();

    // 宛先削除後のリネーム失敗で意図的に残した一時ファイルの記録。再試行では接続を借り直す
    // (壊れていれば別インスタンスになる) ため、記録は宛先単位で共有する必要がある。
    private readonly RetainedTempFileRegistry? _retainedTempFiles;

    // テスト用に既存の SftpClient を渡せるようにする
    public SftpClientWrapper(DestinationOptions options, ILogger<SftpClientWrapper> logger, SftpClient? client = null, RetainedTempFileRegistry? retainedTempFiles = null)
    {
        _retainedTempFiles = retainedTempFiles;
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

        // 1 書き込み要求 (SSH_FXP_WRITE) あたりのバッファサイズ。実際のチャンクサイズは
        // サーバが告知するチャネルパケット上限との min になる (OpenSSH 系は 32KB)。
        _client.BufferSize = (uint)(options.BufferSizeKB * 1024);

        // 接続をワーカー間で再利用する際、アイドルタイムアウトでサーバに切断されないよう
        // 定期的に KeepAlive を送る（0 で無効）。
        if (options.KeepAliveSeconds > 0)
        {
            try
            {
                _client.KeepAliveInterval = TimeSpan.FromSeconds(options.KeepAliveSeconds);
            }
            catch
            {
                // KeepAliveInterval が設定できない場合は無視する
            }
        }
    }

    // 接続されていなければ接続を確立
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(ct).ConfigureAwait(false);
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
    private async Task EnsureDirectoryAsync(string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        if (_verifiedRemoteDirs.Contains(dir))
        {
            return;
        }
        if (await _client.ExistsAsync(dir, ct).ConfigureAwait(false))
        {
            _verifiedRemoteDirs.Add(dir);
            return;
        }

        foreach (var current in GetDirectoryCreationPaths(dir))
        {
            if (await _client.ExistsAsync(current, ct).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await _client.CreateDirectoryAsync(current, ct).ConfigureAwait(false);
            }
            catch (SshException)
            {
                // 並列ワーカーが同じディレクトリを同時に作成すると Exists -> Create の間で
                // 競合して失敗することがある。作成後に存在していれば成功として扱う。
                bool exists;
                try
                {
                    exists = await _client.ExistsAsync(current, ct).ConfigureAwait(false);
                }
                catch
                {
                    exists = false;
                }

                if (!exists)
                {
                    throw;
                }
            }
        }

        _verifiedRemoteDirs.Add(dir);
    }

    private static IReadOnlyList<string> GetDirectoryCreationPaths(string directory)
    {
        var normalized = directory.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/" || normalized == ".")
        {
            return Array.Empty<string>();
        }

        var isAbsolute = normalized.StartsWith("/", StringComparison.Ordinal);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>(parts.Length);
        var current = string.Empty;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(current))
            {
                current = isAbsolute ? "/" + part : part;
            }
            else
            {
                current += "/" + part;
            }

            paths.Add(current);
        }

        return paths;
    }

    // ファイルを一時名でアップロードしてからリネーム
    public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await EnsureDirectoryAsync(remotePath, ct).ConfigureAwait(false);

        var dir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        try
        {
            await UploadCoreAsync(localPath, remotePath, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException) when (!string.IsNullOrEmpty(dir) && _verifiedRemoteDirs.Remove(dir))
        {
            // 存在確認済みのはずのディレクトリが外部要因で消されている。キャッシュを無効化した上で
            // 再作成し、このアップロードを 1 回だけやり直す (キャッシュ導入前の毎回確認と同じ回復性を保つ)。
            _logger.LogWarning("Remote directory {Dir} disappeared. Recreating it and retrying the upload once: {RemotePath}", dir, remotePath);
            await EnsureDirectoryAsync(remotePath, ct).ConfigureAwait(false);
            await UploadCoreAsync(localPath, remotePath, ct).ConfigureAwait(false);
        }
        catch
        {
            // 転送失敗時はディレクトリキャッシュを無効化する (リモート側でディレクトリが
            // 消された場合にリトライで再作成できるようにするため)
            if (!string.IsNullOrEmpty(dir))
            {
                _verifiedRemoteDirs.Remove(dir);
            }
            throw;
        }
    }

    // 一時名でアップロード → 宛先名へリネーム → (設定に応じて) 存在確認
    private async Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct)
    {
        // 一意な一時ファイル名で衝突防止
        var temp = $"{remotePath}.tmp.{Guid.NewGuid():N}";
        _logger.LogDebug("SFTP upload: {LocalPath} -> temp={TempPath}", localPath, temp);

        // フォールバック経路で宛先を削除済みかどうか。true の場合、失敗しても一時ファイルを
        // 消してはならない (旧ファイルと新ファイルの両方を失うため)
        var destinationRemoved = new StrongBox<bool>(false);

        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var fs = File.OpenRead(localPath))
            {
                // UploadFile は SFTP 要求をパイプライン化するため大容量でも高速。
                // SSH.NET にキャンセル対応の同等 API が無いため、転送中のキャンセルは次のチェックまで遅延する
                _client.UploadFile(fs, temp, true);
            }
            ct.ThrowIfCancellationRequested();

            await RenameOverwriteAsync(temp, remotePath, destinationRemoved, ct).ConfigureAwait(false);
        }
        catch
        {
            if (destinationRemoved.Value)
            {
                // 宛先を削除した後にリネームが失敗した状態。一時ファイルを消すと復旧不能になるため
                // 意図的に残し、手動復旧できるようパスをログに出す。
                // 再試行が成功したら CleanupRetainedTempFiles が掃除する
                _retainedTempFiles?.Retain(remotePath, temp);

                _logger.LogError(
                    "SFTP rename failed after the existing destination file was removed. The uploaded data is retained at {TempPath} and must be renamed to {RemotePath} manually if the retry does not recover it.",
                    temp, remotePath);
            }
            else
            {
                // リネーム失敗時にリモートの一時ファイルが蓄積しないよう削除を試みる
                try { if (_client.Exists(temp)) _client.DeleteFile(temp); } catch { }
            }
            throw;
        }

        // リネーム後の存在確認。リネームの成功応答 (SSH_FX_OK) 自体がサーバによる完了確認の
        // ため冗長だが、従来動作の互換のため既定で実施する。高レイテンシ回線では
        // VerifyUploadedFileExists=false で 1 ファイルあたり 2 往復を節約できる。
        if (_options.VerifyUploadedFileExists)
        {
            if (!await _client.ExistsAsync(remotePath, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"SFTP RenameFile completed without error but destination file not found: {remotePath}.");
            }
            _logger.LogDebug("SFTP upload confirmed at: {RemotePath}", remotePath);
        }

        // 過去の試行で残した一時ファイルは、宛先が正しく作られた時点で不要になる
        CleanupRetainedTempFiles(remotePath);
    }

    /// <summary>
    /// 宛先削除後のリネーム失敗で残した一時ファイルを掃除する。
    /// 再試行が成功して宛先が正しく作られた後に呼ぶこと (それまでは唯一のデータなので消せない)。
    /// </summary>
    private void CleanupRetainedTempFiles(string remotePath)
    {
        var retained = _retainedTempFiles?.TakeRetained(remotePath);
        if (retained is null || retained.Count == 0)
        {
            return;
        }

        foreach (var path in retained)
        {
            try
            {
                if (_client.Exists(path))
                {
                    _client.DeleteFile(path);
                }
                _logger.LogInformation("Removed the temporary file retained by a previous failed rename: {TempPath}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not remove the temporary file retained by a previous failed rename: {TempPath} ({Error})", path, ex.Message);
            }
        }
    }

    /// <summary>
    /// 一時ファイルを宛先名へリネームする。posix-rename 拡張が使えるサーバでは
    /// 宛先が存在していても原子的に置き換える (既存ファイルが消失する瞬間を作らない)。
    /// 拡張未対応のサーバのみ従来の削除 + リネームにフォールバックする。
    /// </summary>
    private async Task RenameOverwriteAsync(string tempPath, string remotePath, StrongBox<bool> destinationRemoved, CancellationToken ct)
    {
        if (_posixRenameSupported != false)
        {
            try
            {
                _client.RenameFile(tempPath, remotePath, isPosix: true);
                _posixRenameSupported = true;
                return;
            }
            catch (NotSupportedException ex)
            {
                // サーバが posix-rename 拡張を告知していない (SSH.NET が送信前にクライアント側で
                // 検出する)。この接続では恒久的に未対応とみなしフォールバックする。
                _posixRenameSupported = false;
                _logger.LogDebug("posix-rename is not supported by the server, falling back to delete+rename: {Error}", ex.Message);
            }
            catch (SshException ex) when (SafeExists(tempPath))
            {
                // サーバ側の失敗応答。一時的な失敗 (ビジー等) と実装上の非対応を区別できないため、
                // このファイルに限り削除 + リネームでフォールバックし、posix-rename 自体は次の
                // ファイルでも引き続き試す (一時失敗を恒久的な非対応と誤判定して以降の転送の
                // 原子性を失わないため)。
                // (接続断など本物の障害では SafeExists も失敗し、ここには入らず元の例外が伝播する)
                _logger.LogDebug("posix-rename failed, falling back to delete+rename for this file: {Error}", ex.Message);
            }
        }

        // フォールバック: 非原子的な置換。Delete と Rename の間に障害が起きると
        // 宛先ファイルが存在しない瞬間が生じる (posix-rename 未対応サーバの制約)
        if (await _client.ExistsAsync(remotePath, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("SFTP: Remote file exists, deleting before rename: {RemotePath}", remotePath);
            await _client.DeleteFileAsync(remotePath, ct).ConfigureAwait(false);
            destinationRemoved.Value = true;
        }
        await _client.RenameFileAsync(tempPath, remotePath, ct).ConfigureAwait(false);
        destinationRemoved.Value = false;
    }

    private bool SafeExists(string path)
    {
        try
        {
            return _client.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    // ダウンロードも一時ファイル経由で行う
    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var temp = $"{localPath}.tmp.{Guid.NewGuid():N}";

        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var fs = File.Create(temp))
            {
                // DownloadFile は SFTP 要求をパイプライン化するため大容量でも高速。
                // 転送中のキャンセルは次のチェックまで遅延する
                _client.DownloadFile(remotePath, fs);
            }
            ct.ThrowIfCancellationRequested();

            File.Move(temp, localPath, true);
        }
        catch
        {
            // File.Move 失敗時に一時ファイルが残らないよう削除する
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    // リモートファイルのハッシュ値を取得（ストリーミングで計算し、一時ファイルへのダウンロード不要）
    public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        // SFTPプロトコルではサーバーサイドハッシュコマンドが標準サポートされていないため
        // useServerCommandパラメータが指定されている場合は警告ログを出力
        if (useServerCommand)
        {
            _logger.LogDebug("Server-side hash command is not supported in SFTP protocol. Using local calculation for {Algorithm}", algorithm);
        }

        // SftpFileStream は ReadAsync(byte[], int, int, CancellationToken) をネイティブ実装している
        // (SSH.NET 2025.0.0) ため、キャンセル可能な非同期ストリーミングでハッシュを計算できる。
        await using var stream = await _client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        return await HashUtil.ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
    }

    // 指定ディレクトリのファイル一覧を取得
    public async Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var allFiles = new List<string>();
        await ListFilesCoreAsync(remotePath, allFiles, includeSubdirectories, ct).ConfigureAwait(false);
        return allFiles;
    }

    private async Task ListFilesCoreAsync(string currentPath, List<string> allFiles, bool recursive, CancellationToken ct)
    {
        await foreach (var entry in _client.ListDirectoryAsync(currentPath, ct).ConfigureAwait(false))
        {
            if (!entry.IsDirectory && !entry.IsSymbolicLink)
            {
                allFiles.Add(entry.FullName);
            }
            else if (recursive && entry.IsDirectory && entry.Name != "." && entry.Name != "..")
            {
                await ListFilesCoreAsync(entry.FullName, allFiles, recursive, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> ExistsAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await _client.ExistsAsync(remotePath, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string remotePath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await _client.DeleteFileAsync(remotePath, ct).ConfigureAwait(false);
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
    /// "SHA256:" プレフィックス付きの場合は OpenSSH 形式の SHA-256 指紋として、
    /// それ以外は従来の MD5 16 進指紋として比較する。
    /// 未設定の場合は受信した指紋をログに出力して信頼する。
    /// </summary>
    private void AttachHostKeyValidation()
    {
        // SftpClient のホスト鍵受信イベントにハンドラを追加
        _client.HostKeyReceived += (sender, e) =>
        {
            var expected = _options.HostKeyFingerprint;

            if (string.IsNullOrEmpty(expected))
            {
                // 期待値が無い場合は受信した指紋をログ出力し、そのまま信頼
                // HostKeyFingerprint を設定することで MITM 攻撃を防止できます
                _logger.LogWarning("HostKeyFingerprint is not configured. Trusting server key without verification: SHA256:{Sha256} (MD5: {Md5})",
                    e.FingerPrintSHA256, FormatMd5Fingerprint(e.FingerPrint));
                e.CanTrust = true;
                return;
            }

            expected = expected.Trim();
            if (expected.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            {
                // OpenSSH (ssh-keygen -lf) 形式: パディング無し base64。base64 は大文字小文字を区別する
                var expectedSha = expected.Substring("SHA256:".Length).Trim().TrimEnd('=');
                var receivedSha = e.FingerPrintSHA256.TrimEnd('=');
                if (!string.Equals(expectedSha, receivedSha, StringComparison.Ordinal))
                {
                    e.CanTrust = false;
                    _logger.LogError("Host key fingerprint mismatch. Expected SHA256:{Expected}, but got SHA256:{Received}", expectedSha, receivedSha);
                    return;
                }
                e.CanTrust = true;
                _logger.LogInformation("Host key fingerprint verified: SHA256:{Fingerprint}", receivedSha);
                return;
            }

            // 後方互換: MD5 16 進指紋 (コロンやハイフン区切りを許容)
            var received = FormatMd5Fingerprint(e.FingerPrint);
            var normalizedExpected = expected.Replace(":", "").Replace("-", "").ToLowerInvariant();
            if (!string.Equals(normalizedExpected, received, StringComparison.OrdinalIgnoreCase))
            {
                // 指紋が一致しない場合は信頼せず接続を拒否
                e.CanTrust = false;
                _logger.LogError("Host key fingerprint mismatch. Expected {Expected}, but got {Received}", normalizedExpected, received);
                return;
            }
            e.CanTrust = true;
            _logger.LogInformation("Host key fingerprint verified: {Fingerprint}", received);
        };
    }

    private static string FormatMd5Fingerprint(byte[] fingerprint) =>
        BitConverter.ToString(fingerprint).Replace("-", "").ToLowerInvariant();
}
