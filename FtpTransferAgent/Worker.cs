using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent;

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

    private readonly Channel<TransferItem> _channel = Channel.CreateUnbounded<TransferItem>();

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using IFileTransferClient client = _transfer.Mode.ToLower() == "sftp"
            ? new SftpClientWrapper(_transfer, _services.GetRequiredService<ILogger<SftpClientWrapper>>())
            : new AsyncFtpClientWrapper(_transfer, _services.GetRequiredService<ILogger<AsyncFtpClientWrapper>>());

        var queueLogger = _services.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(_channel, _retry, queueLogger);
        var queueTask = queue.StartAsync(async (item, token) =>
        {
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

        if (_transfer.Direction is "put" or "both")
        {
            _watcher = new FolderWatcher(_watch, _channel.Writer);
        }

        if (_transfer.Direction is "get" or "both")
        {
            var files = await client.ListFilesAsync(_transfer.RemotePath, stoppingToken);
            foreach (var f in files)
            {
                _channel.Writer.TryWrite(new TransferItem(f, TransferAction.Download));
            }
        }

        await queueTask;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
