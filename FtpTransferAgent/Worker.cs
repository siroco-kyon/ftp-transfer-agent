using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent;

// ファイル転送を実行するバックグラウンドサービス
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WatchOptions _watch;
    private readonly TransferOptions _transfer;
    private readonly RetryOptions _retry;
    private readonly HashOptions _hash;
    private readonly CleanupOptions _cleanup;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;

    // 転送処理用のチャンネル（容量制限でメモリリーク防止）
    private readonly Channel<TransferItem> _channel = Channel.CreateBounded<TransferItem>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });

    // DI された各種オプションを受け取る
    public Worker(IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry, IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services, ILogger<Worker> logger, IHostApplicationLifetime lifetime)
    {
        _watch = watch.Value;
        _transfer = transfer.Value;
        _retry = retry.Value;
        _hash = hash.Value;
        _cleanup = cleanup.Value;
        _services = services;
        _logger = logger;
        _lifetime = lifetime;
    }

    // テスト用にクライアント生成処理をオーバーライドできるようメソッド化
    protected virtual IFileTransferClient CreateClient()
    {
        return _transfer.Mode.ToLowerInvariant() switch
        {
            "sftp" => new SftpClientWrapper(_transfer, _services.GetRequiredService<ILogger<SftpClientWrapper>>()),
            "ftp" => new AsyncFtpClientWrapper(_transfer, _services.GetRequiredService<ILogger<AsyncFtpClientWrapper>>()),
            _ => throw new ArgumentException($"Unsupported transfer mode: {_transfer.Mode}")
        };
    }

    // バックグラウンド処理の本体
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 設定に応じて FTP または SFTP クライアントを生成
        using IFileTransferClient client = CreateClient();

        // 再試行付きの転送キューを開始
        var queueLogger = _services.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(_channel, _retry, queueLogger, _transfer.Concurrency);
        var queueTask = queue.StartAsync(async (item, token) =>
        {
            // 各転送処理の識別子
            var id = Guid.NewGuid();
            if (item.Action == TransferAction.Upload)
            {
                await ProcessUploadAsync(client, item, id, token).ConfigureAwait(false);
            }
            else
            {
                await ProcessDownloadAsync(client, item, id, token).ConfigureAwait(false);
            }
        }, stoppingToken);

        // アップロード処理が有効な場合は指定フォルダ内のファイルを列挙
        if (_transfer.Direction is "put" or "both")
        {
            var exts = _watch.AllowedExtensions.Select(e => e.StartsWith(".") ? e : $".{e}").ToArray();
            var option = _watch.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            try
            {
                foreach (var file in Directory.EnumerateFiles(_watch.Path, "*", option))
                {
                    if (exts.Length > 0 && !exts.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    _channel.Writer.TryWrite(new TransferItem(file, TransferAction.Upload));
                }
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogError("Watch directory not found: {Path}. Error: {Error}", _watch.Path, ex.Message);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError("Access denied to watch directory: {Path}. Error: {Error}", _watch.Path, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error enumerating files in {Path}: {Error}", _watch.Path, ex.Message);
                throw;
            }
        }

        // ダウンロード処理が有効な場合はリモート一覧を取得
        if (_transfer.Direction is "get" or "both")
        {
            try
            {
                var files = await client.ListFilesAsync(_transfer.RemotePath, stoppingToken).ConfigureAwait(false);
                foreach (var f in files)
                {
                    _channel.Writer.TryWrite(new TransferItem(f, TransferAction.Download));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error listing remote files from {RemotePath}: {Error}", _transfer.RemotePath, ex.Message);
                throw;
            }
        }

        // 書き込みを完了してすべての転送が終わるのを待機
        _channel.Writer.Complete();
        await queueTask.ConfigureAwait(false);

        // すべての処理が完了したらアプリケーションを停止
        _lifetime.StopApplication();
    }

    /// <summary>
    /// アップロード処理（確実なハッシュ検証付き）
    /// </summary>
    private async Task ProcessUploadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        var name = _transfer.PreserveFolderStructure
            ? Path.GetRelativePath(_watch.Path, item.Path)
            : Path.GetFileName(item.Path);
        var remotePath = Path.Combine(_transfer.RemotePath, name).Replace('\\', '/');

        _logger.LogInformation("[{Id}] Starting upload {File} to {Remote}", id, item.Path, remotePath);

        // 事前にローカルファイルのハッシュを計算
        var localHash = await HashUtil.ComputeHashAsync(item.Path, _hash.Algorithm, token).ConfigureAwait(false);
        _logger.LogDebug("[{Id}] Local hash calculated: {Hash}", id, localHash);

        // アップロード実行
        await client.UploadAsync(item.Path, remotePath, token).ConfigureAwait(false);
        _logger.LogInformation("[{Id}] Upload completed for {File}", id, item.Path);

        // リモートファイルのハッシュを取得して検証
        var remoteHash = await client.GetRemoteHashAsync(remotePath, _hash.Algorithm, token, false).ConfigureAwait(false);
        _logger.LogDebug("[{Id}] Remote hash calculated: {Hash}", id, remoteHash);

        if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[{Id}] Hash verification successful for {File}", id, item.Path);
            if (_cleanup.DeleteAfterVerify)
            {
                try
                {
                    File.Delete(item.Path);
                    _logger.LogInformation("[{Id}] Deleted local file {File}", id, item.Path);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning("[{Id}] Failed to delete local file {File}: {Error}", id, item.Path, ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning("[{Id}] Access denied deleting local file {File}: {Error}", id, item.Path, ex.Message);
                }
            }
        }
        else
        {
            var error = $"Hash mismatch for {item.Path}: Local={localHash}, Remote={remoteHash}";
            _logger.LogError("[{Id}] {Error}", id, error);
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// ダウンロード処理（確実なハッシュ検証付き）
    /// </summary>
    private async Task ProcessDownloadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        // パストラバーサル攻撃対策
        var fileName = Path.GetFileName(item.Path);
        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException($"Invalid file name: {item.Path}");
        }
        
        // ファイル名の安全性をチェック（パストラバーサル攻撃対策）
        var safePath = Path.Combine(_watch.Path, fileName);
        var fullPath = Path.GetFullPath(safePath);
        var watchFullPath = Path.GetFullPath(_watch.Path);
        
        if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar) && 
            !string.Equals(fullPath, watchFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsafe file path detected: {fileName}");
        }
        
        var localPath = Path.Combine(_watch.Path, fileName);

        _logger.LogInformation("[{Id}] Starting download {Remote} to {Local}", id, item.Path, localPath);

        // 事前にリモートファイルのハッシュを計算
        var remoteHash = await client.GetRemoteHashAsync(item.Path, _hash.Algorithm, token, false).ConfigureAwait(false);
        _logger.LogDebug("[{Id}] Remote hash calculated: {Hash}", id, remoteHash);

        // ダウンロード実行
        await client.DownloadAsync(item.Path, localPath, token).ConfigureAwait(false);
        _logger.LogInformation("[{Id}] Download completed for {Remote}", id, item.Path);

        // ローカルファイルのハッシュを計算して検証
        var localHash = await HashUtil.ComputeHashAsync(localPath, _hash.Algorithm, token).ConfigureAwait(false);
        _logger.LogDebug("[{Id}] Local hash calculated: {Hash}", id, localHash);

        if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[{Id}] Hash verification successful for {Remote}", id, item.Path);
            if (_cleanup.DeleteRemoteAfterDownload)
            {
                await client.DeleteAsync(item.Path, token).ConfigureAwait(false);
                _logger.LogInformation("[{Id}] Deleted remote file {Remote}", id, item.Path);
            }
        }
        else
        {
            var error = $"Hash mismatch for {item.Path}: Remote={remoteHash}, Local={localHash}";
            _logger.LogError("[{Id}] {Error}", id, error);
            throw new InvalidOperationException(error);
        }
    }

    public override void Dispose()
    {
        // 後始末
        base.Dispose();
    }
}
