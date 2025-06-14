using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
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
    private FolderWatcher? _watcher;

    // 転送処理用のチャンネル
    private readonly Channel<TransferItem> _channel = Channel.CreateUnbounded<TransferItem>();

    // DI された各種オプションを受け取る
    public Worker(IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry, IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services, ILogger<Worker> logger)
    {
        _watch = watch.Value;
        _transfer = transfer.Value;
        _retry = retry.Value;
        _hash = hash.Value;
        _cleanup = cleanup.Value;
        _services = services;
        _logger = logger;
    }

    // バックグラウンド処理の本体
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 設定に応じて FTP または SFTP クライアントを生成
        using IFileTransferClient client = _transfer.Mode.ToLower() == "sftp"
            ? new SftpClientWrapper(_transfer, _services.GetRequiredService<ILogger<SftpClientWrapper>>())
            : new AsyncFtpClientWrapper(_transfer, _services.GetRequiredService<ILogger<AsyncFtpClientWrapper>>());

        // 再試行付きの転送キューを開始
        var queueLogger = _services.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(_channel, _retry, queueLogger, _transfer.Concurrency);
        var queueTask = queue.StartAsync(async (item, token) =>
        {
            // 各転送処理の識別子
            var id = Guid.NewGuid();
            if (item.Action == TransferAction.Upload)
            {
                var remotePath = Path.Combine(_transfer.RemotePath, Path.GetFileName(item.Path)).Replace('\\', '/');
                _logger.LogInformation("[{Id}] Uploading {File} to {Remote}", id, item.Path, remotePath);
                await client.UploadAsync(item.Path, remotePath, token);
                var remoteHash = await client.GetRemoteHashAsync(remotePath, _hash.Algorithm, token);
                var localHash = await HashUtil.ComputeHashAsync(item.Path, _hash.Algorithm, token);
                if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[{Id}] Verified hash for {File}", id, item.Path);
                    if (_cleanup.DeleteAfterVerify)
                    {
                        File.Delete(item.Path);
                        _logger.LogInformation("[{Id}] Deleted {File}", id, item.Path);
                    }
                }
                else
                {
                    _logger.LogError("[{Id}] Hash mismatch for {File}", id, item.Path);
                }
            }
            else
            {
                var localPath = Path.Combine(_watch.Path, Path.GetFileName(item.Path));
                _logger.LogInformation("[{Id}] Downloading {Remote} to {Local}", id, item.Path, localPath);
                await client.DownloadAsync(item.Path, localPath, token);
                var remoteHash = await client.GetRemoteHashAsync(item.Path, _hash.Algorithm, token);
                var localHash = await HashUtil.ComputeHashAsync(localPath, _hash.Algorithm, token);
                if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[{Id}] Verified hash for {File}", id, item.Path);
                }
                else
                {
                    _logger.LogError("[{Id}] Hash mismatch for {File}", id, item.Path);
                }
            }
        }, stoppingToken);

        // アップロード処理が有効な場合はフォルダ監視を開始
        if (_transfer.Direction is "put" or "both")
        {
            _watcher = new FolderWatcher(_watch, _channel.Writer);
        }

        // ダウンロード処理が有効な場合はリモート一覧を取得
        if (_transfer.Direction is "get" or "both")
        {
            var files = await client.ListFilesAsync(_transfer.RemotePath, stoppingToken);
            foreach (var f in files)
            {
                _channel.Writer.TryWrite(new TransferItem(f, TransferAction.Download));
            }
        }

        // キューの完了を待機
        await queueTask;
    }

    public override void Dispose()
    {
        // フォルダ監視の後始末
        _watcher?.Dispose();
        base.Dispose();
    }
}
