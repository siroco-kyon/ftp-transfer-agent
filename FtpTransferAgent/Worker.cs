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

    // DI を活用してクライアントファクトリパターンを実装
    protected virtual IFileTransferClient CreateClient()
    {
        return _transfer.Mode.ToLowerInvariant() switch
        {
            "sftp" => ActivatorUtilities.CreateInstance<SftpClientWrapper>(_services, _transfer),
            "ftp" => ActivatorUtilities.CreateInstance<AsyncFtpClientWrapper>(_services, _transfer),
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

        // パフォーマンス監視タスクを開始
        var monitorTask = StartPerformanceMonitoringAsync(queue, stoppingToken);

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
                // ファイル順序を安定化し、ENDファイルが先に処理されないようにソート
                var files = Directory.EnumerateFiles(_watch.Path, "*", option)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // データファイルとENDファイルのペアを収集
                var dataFiles = new List<string>();
                var endFiles = new List<string>();

                foreach (var file in files)
                {
                    if (exts.Length > 0 && !exts.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsEndFile(file))
                    {
                        // ENDファイルは別リストに保存
                        if (_watch.TransferEndFiles)
                        {
                            endFiles.Add(file);
                        }
                        continue;
                    }

                    // ENDファイル検証が有効な場合は、対応するENDファイルの存在を確認
                    if (_watch.RequireEndFile)
                    {
                        if (!HasEndFile(file))
                        {
                            _logger.LogDebug("Skipping file {File} - no corresponding END file found", file);
                            continue;
                        }
                    }

                    dataFiles.Add(file);
                }

                // 1. まずデータファイルを転送キューに追加
                foreach (var file in dataFiles)
                {
                    _channel.Writer.TryWrite(new TransferItem(file, TransferAction.Upload));
                }

                // 2. その後でENDファイルを転送キューに追加（TransferEndFiles が true の場合）
                if (_watch.TransferEndFiles)
                {
                    foreach (var endFile in endFiles)
                    {
                        // 対応するデータファイルが存在する場合のみENDファイルを転送
                        var dataFileName = GetDataFileForEndFile(endFile);
                        if (dataFiles.Any(f => Path.GetFileNameWithoutExtension(f).Equals(dataFileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogDebug("Queueing END file {File} for transfer after data file", endFile);
                            _channel.Writer.TryWrite(new TransferItem(endFile, TransferAction.Upload));
                        }
                        else
                        {
                            _logger.LogDebug("Skipping END file {File} - corresponding data file not being transferred", endFile);
                        }
                    }
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
                var files = await client.ListFilesAsync(_transfer.RemotePath, stoppingToken, _watch.IncludeSubfolders).ConfigureAwait(false);
                
                // AllowedExtensionsが指定されている場合はフィルタを適用
                var exts = _watch.AllowedExtensions.Select(e => e.StartsWith(".") ? e : $".{e}").ToArray();
                
                foreach (var f in files)
                {
                    if (exts.Length > 0 && !exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
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

        // 最終統計情報をログ出力
        var finalStats = queue.GetStatistics();
        _logger.LogInformation("Transfer completed. Total: {Total}, Success: {Success}, Failed: {Failed}, Critical Errors: {Critical}, Success Rate: {Rate:F1}%",
            finalStats.TotalEnqueued, finalStats.TotalCompleted, finalStats.TotalFailed, finalStats.CriticalErrorCount, finalStats.SuccessRate);

        // クリティカルエラーがあれば詳細をログ出力
        var criticalExceptions = queue.GetCriticalExceptions();
        foreach (var ex in criticalExceptions)
        {
            _logger.LogError(ex, "Critical error occurred during transfer: {Message}", ex.Message);
        }

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
        var remoteHash = await client.GetRemoteHashAsync(remotePath, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
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
        string localPath;
        
        if (_transfer.PreserveFolderStructure && _watch.IncludeSubfolders)
        {
            // サブディレクトリ構造を保持する場合
            var relativePath = Path.GetRelativePath(_transfer.RemotePath, item.Path);
            
            // パストラバーサル攻撃対策
            if (relativePath.Contains("..") || Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException($"Unsafe relative path detected: {relativePath}");
            }
            
            var safePath = Path.Combine(_watch.Path, relativePath);
            var fullPath = Path.GetFullPath(safePath);
            var watchFullPath = Path.GetFullPath(_watch.Path);
            
            if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar) &&
                !string.Equals(fullPath, watchFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unsafe file path detected: {relativePath}");
            }
            
            localPath = safePath;
            
            // ディレクトリが存在しない場合は作成
            var localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
                _logger.LogDebug("[{Id}] Created directory: {Directory}", id, localDir);
            }
        }
        else
        {
            // 従来の動作（ファイル名のみ使用）
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

            localPath = safePath;
        }

        _logger.LogInformation("[{Id}] Starting download {Remote} to {Local}", id, item.Path, localPath);

        // 事前にリモートファイルのハッシュを計算
        var remoteHash = await client.GetRemoteHashAsync(item.Path, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
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

    /// <summary>
    /// 指定されたファイルがENDファイルかどうかを判定
    /// </summary>
    private bool IsEndFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || _watch.EndFileExtensions == null)
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return _watch.EndFileExtensions.Any(endExt =>
            {
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    return false;
                }
                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                return string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error checking if file is END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// ENDファイルから対応するデータファイル名を取得
    /// </summary>
    private string GetDataFileForEndFile(string endFilePath)
    {
        if (string.IsNullOrEmpty(endFilePath) || _watch.EndFileExtensions == null)
        {
            return string.Empty;
        }

        try
        {
            var fileName = Path.GetFileName(endFilePath);
            var extension = Path.GetExtension(endFilePath);

            // ENDファイル拡張子を除去してデータファイル名を取得
            foreach (var endExt in _watch.EndFileExtensions)
            {
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                if (string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase))
                {
                    // ENDファイル拡張子を除去してデータファイル名を返す
                    return fileName.Substring(0, fileName.Length - normalizedEndExt.Length);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error getting data file name for END file {EndFile}: {Error}", endFilePath, ex.Message);
        }

        return string.Empty;
    }

    /// <summary>
    /// 指定されたファイルに対応するENDファイルが存在するかどうかを確認
    /// </summary>
    private bool HasEndFile(string filePath)
    {
        // 入力値の検証
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        // ENDファイル拡張子が設定されていない場合
        if (_watch.EndFileExtensions == null || _watch.EndFileExtensions.Length == 0)
        {
            return false;
        }

        try
        {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            var directory = Path.GetDirectoryName(filePath);

            // ディレクトリまたはファイル名が取得できない場合
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileNameWithoutExtension))
            {
                return false;
            }

            foreach (var endExt in _watch.EndFileExtensions)
            {
                // null または空の拡張子をスキップ
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var endFileExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                var endFilePath = Path.Combine(directory, fileNameWithoutExtension + endFileExt);

                if (File.Exists(endFilePath))
                {
                    return true;
                }
            }
        }
        catch (ArgumentException ex)
        {
            // 不正なパス文字が含まれる場合
            _logger.LogWarning("Invalid file path for END file check: {FilePath}. Error: {Error}", filePath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // その他の予期しないエラー
            _logger.LogWarning("Error checking END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }

        return false;
    }

    /// <summary>
    /// パフォーマンス監視タスクを開始
    /// </summary>
    private async Task StartPerformanceMonitoringAsync(TransferQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

                var stats = queue.GetStatistics();
                if (stats.TotalEnqueued > 0)
                {
                    _logger.LogInformation("Transfer Progress - Total: {Total}, Completed: {Completed}, Failed: {Failed}, Active: {Active}, Memory: {Memory}MB, Success Rate: {Rate:F1}%",
                        stats.TotalEnqueued, stats.TotalCompleted, stats.TotalFailed, stats.ActiveItems, stats.MemoryUsageMB, stats.SuccessRate);
                }

                // 長時間実行中のアイテムを警告
                var longRunningItems = queue.GetLongRunningItems(TimeSpan.FromMinutes(5));
                foreach (var (itemKey, duration) in longRunningItems)
                {
                    _logger.LogWarning("Long running transfer detected: {ItemKey} running for {Duration}", itemKey, duration);
                }

                // メモリ使用量が高い場合は警告
                if (stats.MemoryUsageMB > 500)
                {
                    _logger.LogWarning("High memory usage detected: {Memory}MB", stats.MemoryUsageMB);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常な終了処理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance monitoring task failed");
        }
    }

    public override void Dispose()
    {
        // 後始末
        base.Dispose();
    }
}
