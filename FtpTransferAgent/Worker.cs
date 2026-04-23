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
    private const int QueueCapacity = 1000;
    private readonly ILogger<Worker> _logger;
    private readonly WatchOptions _watch;
    private readonly TransferOptions _transfer;
    private readonly RetryOptions _retry;
    private readonly HashOptions _hash;
    private readonly CleanupOptions _cleanup;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly FanoutCoordinator _fanout = new();

    // 転送処理用のチャンネル（容量制限でメモリリーク防止）

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

    private sealed class QueueContext
    {
        public QueueContext(DestinationOptions destination, string name, Channel<TransferItem> channel, TransferQueue queue)
        {
            Destination = destination;
            Name = name;
            Channel = channel;
            Queue = queue;
        }

        public DestinationOptions Destination { get; }
        public string Name { get; }
        public Channel<TransferItem> Channel { get; }
        public TransferQueue Queue { get; }
    }

    // 実クライアント生成の共通実装。virtual メソッドから分離して相互再帰を避ける。
    private IFileTransferClient CreateClientCore(DestinationOptions dest)
    {
        return dest.Mode.ToLowerInvariant() switch
        {
            "sftp" => ActivatorUtilities.CreateInstance<SftpClientWrapper>(_services, dest),
            "ftp" => ActivatorUtilities.CreateInstance<AsyncFtpClientWrapper>(_services, dest),
            _ => throw new ArgumentException($"Unsupported transfer mode: {dest.Mode}")
        };
    }

    // DI を活用してクライアントファクトリパターンを実装（primary 用）
    protected virtual IFileTransferClient CreateClient()
    {
        return CreateClientCore(_transfer);
    }

    // ファンアウト対応: 宛先ごとにクライアントを生成する
    protected virtual IFileTransferClient CreateClientFor(DestinationOptions dest)
    {
        // dest が primary と同一参照なら CreateClient() を使う (テストのモックを尊重するため)
        if (ReferenceEquals(dest, _transfer))
        {
            return CreateClient();
        }
        return CreateClientCore(dest);
    }

    // put 方向で使用する全宛先リスト (primary + 追加宛先)
    private IReadOnlyList<DestinationOptions> GetUploadDestinations()
    {
        var list = new List<DestinationOptions> { _transfer };
        if (_transfer.AdditionalDestinations is { Count: > 0 })
        {
            list.AddRange(_transfer.AdditionalDestinations);
        }
        return list;
    }

    private static string DescribeDestination(DestinationOptions d) =>
        $"{d.Mode}://{d.Host}:{d.Port}{d.RemotePath}";

    private static Channel<TransferItem> CreateTransferChannel()
    {
        return Channel.CreateBounded<TransferItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    private QueueContext CreateQueueContext(DestinationOptions destination, string name, int concurrency)
    {
        var queueLogger = _services.GetRequiredService<ILogger<TransferQueue>>();
        var channel = CreateTransferChannel();
        var queue = new TransferQueue(channel, _retry, queueLogger, concurrency);
        return new QueueContext(destination, name, channel, queue);
    }

    private List<QueueContext> CreateQueueContexts()
    {
        var contexts = new List<QueueContext>
        {
            CreateQueueContext(_transfer, $"primary {DescribeDestination(_transfer)}", _transfer.Concurrency)
        };

        if (_transfer.Direction is not "put" and not "both")
        {
            return contexts;
        }

        if (_transfer.AdditionalDestinations is not { Count: > 0 })
        {
            return contexts;
        }

        for (int i = 0; i < _transfer.AdditionalDestinations.Count; i++)
        {
            var destination = _transfer.AdditionalDestinations[i];
            contexts.Add(CreateQueueContext(destination, $"destination#{i + 1} {DescribeDestination(destination)}", destination.Concurrency));
        }

        return contexts;
    }

    private QueueContext GetPrimaryQueueContext(IReadOnlyList<QueueContext> queueContexts)
    {
        return queueContexts[0];
    }

    private QueueContext GetQueueContextForDestination(IReadOnlyList<QueueContext> queueContexts, DestinationOptions destination)
    {
        foreach (var context in queueContexts)
        {
            if (ReferenceEquals(context.Destination, destination))
            {
                return context;
            }
        }

        throw new InvalidOperationException($"Queue context not found for destination {DescribeDestination(destination)}");
    }

    // 1 ファイルを全宛先に対してファンアウトしてキューへ投入する。
    // 全宛先完了時に FanoutCoordinator からコールバックされ、
    // 全成功ならローカル削除・部分失敗ならローカル保持のログを出力する。
    private async Task EnqueueFanoutAsync(
        string file,
        IReadOnlyList<DestinationOptions> destinations,
        IReadOnlyList<QueueContext> queueContexts,
        bool isEndFile,
        CancellationToken token)
    {
        var groupId = Guid.NewGuid().ToString("N");
        _fanout.Register(groupId, file, destinations.Count, (source, results) =>
        {
            HandleFanoutCompletion(source, results, isEndFile);
        });

        foreach (var dest in destinations)
        {
            var queueContext = GetQueueContextForDestination(queueContexts, dest);
            await queueContext.Channel.Writer.WriteAsync(
                new TransferItem(file, TransferAction.Upload, dest, groupId),
                token).ConfigureAwait(false);
        }
    }

    private void HandleFanoutCompletion(string sourcePath, IReadOnlyList<FanoutCoordinator.DestinationResult> results, bool isEndFile)
    {
        var total = results.Count;
        var succeeded = results.Count(r => r.Success);
        var allSuccess = succeeded == total;

        if (!allSuccess)
        {
            var failed = results.Where(r => !r.Success).Select(r => r.DestinationLabel);
            _logger.LogError("Partial failure for {File}: {Ok}/{Total} destinations succeeded. Failed: {Failed}. Local file retained for next run. Retry policy is all-or-nothing, so already-successful destinations will also be retried.",
                sourcePath, succeeded, total, string.Join(", ", failed));
            return;
        }

        _logger.LogInformation("All {Total} destinations succeeded for {File}", total, sourcePath);

        // 全成功時のみローカル削除 (END ファイルは常に、通常ファイルは DeleteAfterVerify 有効時)
        var shouldDeleteLocal = isEndFile || _cleanup.DeleteAfterVerify;
        if (shouldDeleteLocal)
        {
            try
            {
                File.Delete(sourcePath);
                var fileType = isEndFile ? "END file" : "local file";
                _logger.LogInformation("Deleted {FileType} {File} after all destinations succeeded", fileType, sourcePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Failed to delete local file {File}: {Error}", sourcePath, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied deleting local file {File}: {Error}", sourcePath, ex.Message);
            }
        }
    }

    // バックグラウンド処理の本体
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // クライアントは各転送タスク毎に作成するため、ここでは共通インスタンスを生成しない

        // 再試行付きの転送キューを開始
        var queueContexts = CreateQueueContexts();
        var primaryQueue = GetPrimaryQueueContext(queueContexts);

        // パフォーマンス監視用のCancellationTokenSourceを作成
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitorTasks = queueContexts
            .Select(context => StartPerformanceMonitoringAsync(context.Name, context.Queue, monitorCts.Token))
            .ToArray();

        // 各キュー処理では専用のクライアントを生成して処理する
        var queueTasks = queueContexts
            .Select(context => context.Queue.StartAsync(
            async (item, token) =>
            {
                // 各転送処理の識別子
                var id = Guid.NewGuid();
                long bytesTransferred;
                if (item.Action == TransferAction.Upload)
                {
                    // Upload は宛先ごとに専用クライアントを生成
                    var dest = item.Destination ?? context.Destination;
                    using var perItemClient = CreateClientFor(dest);
                    bytesTransferred = await ProcessUploadAsync(perItemClient, item, id, token).ConfigureAwait(false);
                }
                else
                {
                    // Download は primary のみ (Destination 未使用)
                    using var perItemClient = CreateClient();
                    bytesTransferred = await ProcessDownloadAsync(perItemClient, item, id, token).ConfigureAwait(false);
                }
                // 成功時のみバイト数を集計（例外時はここまで到達しない）
                context.Queue.RecordBytesTransferred(bytesTransferred);
            },
            onFinalOutcome: (item, ex) =>
            {
                // ファンアウト集約: Upload かつ GroupId 付きのみ報告
                if (item.Action != TransferAction.Upload || string.IsNullOrEmpty(item.GroupId))
                {
                    return;
                }
                var dest = item.Destination ?? context.Destination;
                var label = DescribeDestination(dest);
                _fanout.ReportResult(item.GroupId,
                    new FanoutCoordinator.DestinationResult(label, ex == null, ex));
            },
            stoppingToken))
            .ToArray();

        // ファイル列挙フェーズ：例外発生時も必ず Channel を完了してワーカーを解放する
        try
        {

        // アップロード処理が有効な場合は指定フォルダ内のファイルを列挙
        if (_transfer.Direction is "put" or "both")
        {
            var patterns = _watch.AllowedExtensions ?? System.Array.Empty<string>();
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
                    // ENDファイルかどうかを先にチェック
                    if (IsEndFile(file))
                    {
                        // ENDファイルは常に別リストに保存（転送するかどうかは後で判断）
                        endFiles.Add(file);
                        continue;
                    }

                    // 通常ファイルに対してファイル名パターン (拡張子 or ワイルドカード) を適用
                    if (!FileNameMatcher.IsMatch(Path.GetFileName(file), patterns))
                    {
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

                var destinations = GetUploadDestinations();
                _logger.LogInformation("Upload fanout: {FileCount} file(s) x {DestCount} destination(s) = {Total} transfer item(s)",
                    dataFiles.Count + (_watch.TransferEndFiles ? endFiles.Count : 0),
                    destinations.Count,
                    (dataFiles.Count + (_watch.TransferEndFiles ? endFiles.Count : 0)) * destinations.Count);

                // 1. まずデータファイルを全宛先に対してファンアウトしてキューへ投入
                foreach (var file in dataFiles)
                {
                    await EnqueueFanoutAsync(file, destinations, queueContexts, isEndFile: false, stoppingToken).ConfigureAwait(false);
                }

                // 2. ENDファイルを投入（TransferEndFiles が true の場合）
                if (_watch.TransferEndFiles)
                {
                    foreach (var endFile in endFiles)
                    {
                        var dataFileName = GetDataFileForEndFile(endFile);
                        if (dataFiles.Any(f => string.Equals(Path.GetFileName(f), dataFileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.LogDebug("Queueing END file {File} for transfer after data file", endFile);
                            await EnqueueFanoutAsync(endFile, destinations, queueContexts, isEndFile: true, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogDebug("Skipping END file {File} - corresponding data file not being transferred", endFile);
                        }
                    }
                }
                else if (_cleanup.DeleteLocalSkippedEndFiles && endFiles.Count > 0)
                {
                    // TransferEndFiles=false のとき、転送しなかった END ファイルをローカルから削除する
                    foreach (var endFile in endFiles)
                    {
                        try
                        {
                            File.Delete(endFile);
                            _logger.LogInformation("Deleted skipped END file {File} (TransferEndFiles=false, DeleteLocalSkippedEndFiles=true)", endFile);
                        }
                        catch (IOException ex)
                        {
                            _logger.LogWarning("Failed to delete skipped END file {File}: {Error}", endFile, ex.Message);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            _logger.LogWarning("Access denied deleting skipped END file {File}: {Error}", endFile, ex.Message);
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

        // ダウンロード方向で追加宛先が設定されていれば警告（未使用になる）
        if (_transfer.Direction is "get" or "both" && _transfer.AdditionalDestinations is { Count: > 0 })
        {
            _logger.LogWarning("AdditionalDestinations is set but Direction is '{Direction}'. Additional destinations are used only for uploads and will be ignored for downloads.",
                _transfer.Direction);
        }

        // ダウンロード処理が有効な場合はリモート一覧を取得
        if (_transfer.Direction is "get" or "both")
        {
            try
            {
                // リモート一覧取得用に専用のクライアントを生成する
                using var listClient = CreateClient();
                // リモートファイル一覧を取得
                var files = await listClient.ListFilesAsync(_transfer.RemotePath, stoppingToken, _watch.IncludeSubfolders).ConfigureAwait(false);

                // ファイルごとに正規化パス(先頭に "/" を付与)と元のパスを保持する辞書を作成する
                var normalizedMap = new Dictionary<string, string>();
                foreach (var f in files)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    var norm = f.StartsWith("/") ? f : "/" + f;
                    // 末尾の無駄なスラッシュは除去
                    norm = norm.TrimEnd('/');
                    normalizedMap[norm] = f;
                }

                // AllowedExtensions (ファイル名パターン) フィルタ用
                var patterns = _watch.AllowedExtensions ?? System.Array.Empty<string>();

                // 正規化パスでソートしてENDファイルとデータファイルを分ける
                var sortedNormPaths = normalizedMap.Keys
                    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var dataFiles = new List<string>();    // キューに追加するオリジナルパス
                var endFiles = new List<string>();     // キューに追加するオリジナルパス

                foreach (var normPath in sortedNormPaths)
                {
                    var originalPath = normalizedMap[normPath];

                    // ENDファイルかどうかを正規化パスで判定
                    if (IsEndFileRemote(normPath))
                    {
                        if (_watch.TransferEndFiles)
                        {
                            endFiles.Add(originalPath);
                        }
                        continue;
                    }

                    // ファイル名パターン (拡張子 or ワイルドカード) フィルタをオリジナルパスに対して適用
                    if (!FileNameMatcher.IsMatch(Path.GetFileName(originalPath), patterns))
                    {
                        continue;
                    }

                    // ENDファイル必須の場合は対応ENDファイルの存在を確認
                    if (_watch.RequireEndFile)
                    {
                        if (!HasEndFileRemote(normPath, sortedNormPaths))
                        {
                            _logger.LogDebug("Skipping remote file {File} - no corresponding END file found", originalPath);
                            continue;
                        }
                    }

                    dataFiles.Add(originalPath);
                }

                // 1. まずデータファイルを転送キューに追加
                foreach (var file in dataFiles)
                {
                    await primaryQueue.Channel.Writer.WriteAsync(new TransferItem(file, TransferAction.Download), stoppingToken);
                }

                // 2. その後でENDファイルを転送キューに追加
                if (_watch.TransferEndFiles)
                {
                    foreach (var endOrigPath in endFiles)
                    {
                        // ENDファイルの正規化パスを生成
                        var endNorm = endOrigPath.StartsWith("/") ? endOrigPath : "/" + endOrigPath;
                        endNorm = endNorm.TrimEnd('/');
                        // 対応するデータファイルが存在する場合のみENDファイルを転送
                        var dataNormFull = GetDataFileForEndFileRemote(endNorm);
                        if (!string.IsNullOrEmpty(dataNormFull))
                        {
                            // dataNormFull も正規化して先頭に '/' を付与し、末尾の不要なスラッシュを削除
                            var dataFullNormalized = dataNormFull.StartsWith("/") ? dataNormFull : "/" + dataNormFull;
                            dataFullNormalized = dataFullNormalized.TrimEnd('/', '\\');

                            // 対応するデータファイルが存在する場合のみENDファイルを転送
                            var exists = dataFiles.Any(f =>
                            {
                                var fNorm = f.StartsWith("/") ? f : "/" + f;
                                fNorm = fNorm.TrimEnd('/', '\\');
                                return string.Equals(fNorm, dataFullNormalized, StringComparison.OrdinalIgnoreCase);
                            });

                            if (exists)
                            {
                                _logger.LogDebug("Queueing remote END file {File} for transfer after data file", endOrigPath);
                                await primaryQueue.Channel.Writer.WriteAsync(new TransferItem(endOrigPath, TransferAction.Download), stoppingToken);
                            }
                            else
                            {
                                _logger.LogDebug("Skipping remote END file {File} - corresponding data file not being transferred", endOrigPath);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Skipping remote END file {File} - corresponding data file not found or invalid name", endOrigPath);
                        }
                    }
                }
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogError("Remote directory not found: {RemotePath}. Error: {Error}", _transfer.RemotePath, ex.Message);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError("Access denied to remote directory: {RemotePath}. Error: {Error}", _transfer.RemotePath, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error listing remote files from {RemotePath}: {Error}", _transfer.RemotePath, ex.Message);
                throw;
            }
        }

        } // try（ファイル列挙フェーズ）
        finally
        {
            // 例外発生時もワーカーが WaitToReadAsync でブロックし続けないよう Channel を必ず完了する
            foreach (var context in queueContexts)
            {
                context.Channel.Writer.TryComplete();
            }
        }

        // 書き込みを完了してキュー処理の完了を待機（正常パスでは TryComplete が Complete の役割を担う）
        
        try
        {
            // まずキュー処理の完了を待機
            await Task.WhenAll(queueTasks).ConfigureAwait(false);
            
            // キュー処理が完了したら監視タスクをキャンセル
            monitorCts.Cancel();
            
            // 監視タスクの完了を少し待機（強制終了を避けるため）
            try
            {
                await Task.WhenAll(monitorTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Performance monitoring task did not complete within timeout");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during transfer: {Error}", ex.Message);
            throw;
        }

        // 最終統計情報をログ出力
        var finalStats = AggregateStatistics(queueContexts.Select(context => context.Queue.GetStatistics()));
        _logger.LogInformation("Transfer completed. Total: {Total}, Success: {Success}, Failed: {Failed}, Critical Errors: {Critical}, Success Rate: {Rate:F1}%, Volume: {Volume}",
            finalStats.TotalEnqueued, finalStats.TotalCompleted, finalStats.TotalFailed, finalStats.CriticalErrorCount, finalStats.SuccessRate, FormatBytes(finalStats.TotalBytesTransferred));

        // クリティカルエラーがあれば詳細をログ出力
        var criticalExceptions = queueContexts.SelectMany(context => context.Queue.GetCriticalExceptions());
        foreach (var ex in criticalExceptions)
        {
            _logger.LogError(ex, "Critical error occurred during transfer: {Message}", ex.Message);
        }

        // すべての処理が完了したらアプリケーションを停止
        _lifetime.StopApplication();
    }

    private static TransferStatistics AggregateStatistics(IEnumerable<TransferStatistics> allStats)
    {
        var aggregated = new TransferStatistics();
        foreach (var stats in allStats)
        {
            aggregated.TotalEnqueued += stats.TotalEnqueued;
            aggregated.TotalCompleted += stats.TotalCompleted;
            aggregated.TotalFailed += stats.TotalFailed;
            aggregated.ActiveItems += stats.ActiveItems;
            aggregated.ProcessedItems += stats.ProcessedItems;
            aggregated.MemoryUsageMB = Math.Max(aggregated.MemoryUsageMB, stats.MemoryUsageMB);
            aggregated.ActiveWorkers += stats.ActiveWorkers;
            aggregated.CriticalErrorCount += stats.CriticalErrorCount;
            aggregated.TotalBytesTransferred += stats.TotalBytesTransferred;
        }

        return aggregated;
    }

    /// <summary>
    /// バイト数を人が読みやすい単位にフォーマットする
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F2} MB";
        if (bytes >= KB) return $"{bytes / KB:F2} KB";
        return $"{bytes:N0} B";
    }

    /// <summary>
    /// アップロード処理（確実なハッシュ検証付き）。
    /// ファンアウト対応: item.Destination が示す 1 宛先へ送信し、
    /// FanoutCoordinator に成功/失敗を報告する。ローカル削除は coordinator 側で集約実施。
    /// 戻り値は成功時に転送したバイト数（統計集計用）。失敗時は例外をスロー。
    /// </summary>
    private async Task<long> ProcessUploadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        var dest = item.Destination ?? _transfer;
        var destLabel = DescribeDestination(dest);
        var name = dest.PreserveFolderStructure
            ? Path.GetRelativePath(_watch.Path, item.Path)
            : Path.GetFileName(item.Path);
        // リモートパスを組み立てる際、Path.Combineでは余計なセパレーターが入る場合があるため手動で結合する。
        // 宛先ごとの RemotePath の末尾のスラッシュを除去し、ファイル名または相対パスを '/' で連結する。
        // 元のRemotePathがルート("/")の場合はそのまま保持する。
        var rawBase = dest.RemotePath ?? string.Empty;
        string remoteBase;
        if (rawBase == "/")
        {
            remoteBase = "/";
        }
        else
        {
            // 末尾の区切り文字を削除（Windowsの '\' も対象）
            remoteBase = rawBase.TrimEnd('/', '\\');
        }
        var remoteName = name.Replace('\\', '/');
        string remotePath;
        if (string.IsNullOrEmpty(remoteBase))
        {
            // ベースが空ならそのまま名前を返す
            remotePath = remoteName;
        }
        else if (remoteBase == "/")
        {
            // ルートの場合はルートと名前を連結
            remotePath = $"/{remoteName}";
        }
        else
        {
            remotePath = $"{remoteBase}/{remoteName}";
        }

        // 転送バイト数を取得（リトライ時も毎回読み直す: ファイルが途中で差し替わっている可能性に備えて）
        var fileSize = new FileInfo(item.Path).Length;

        _logger.LogInformation("[{Id}] Starting upload {File} ({Size}) to {Dest} ({Remote})", id, item.Path, FormatBytes(fileSize), destLabel, remotePath);

        var isEndFile = IsEndFile(item.Path);
        if (_hash.Enabled)
        {
            var localHash = await HashUtil.ComputeHashAsync(item.Path, _hash.Algorithm, token).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Local hash calculated: {Hash}", id, localHash);

            await client.UploadAsync(item.Path, remotePath, token).ConfigureAwait(false);
            _logger.LogInformation("[{Id}] Upload completed for {File} -> {Dest} ({Size})", id, item.Path, destLabel, FormatBytes(fileSize));

            var remoteHash = await client.GetRemoteHashAsync(remotePath, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Remote hash calculated: {Hash}", id, remoteHash);

            if (!string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                var error = $"Hash mismatch for {item.Path} at {destLabel}: Local={localHash}, Remote={remoteHash}";
                _logger.LogError("[{Id}] {Error}", id, error);
                throw new InvalidOperationException(error);
            }

            _logger.LogInformation("[{Id}] Hash verification successful for {File} at {Dest}", id, item.Path, destLabel);
        }
        else
        {
            await client.UploadAsync(item.Path, remotePath, token).ConfigureAwait(false);
            _logger.LogInformation("[{Id}] Upload completed for {File} -> {Dest} ({Size}, hash verification disabled)", id, item.Path, destLabel, FormatBytes(fileSize));
        }

        // 転送先のENDファイル削除判定（宛先ごとの操作）
        if (isEndFile && _cleanup.DeleteRemoteEndFiles)
        {
            try
            {
                await client.DeleteAsync(remotePath, token).ConfigureAwait(false);
                _logger.LogInformation("[{Id}] Deleted remote END file {Remote} at {Dest}", id, remotePath, destLabel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[{Id}] Failed to delete remote END file {Remote} at {Dest}: {Error}", id, remotePath, destLabel, ex.Message);
            }
        }

        // ローカルファイル削除はファンアウト集約で実施するためここでは行わない
        return fileSize;
    }

    /// <summary>
    /// ダウンロード処理（確実なハッシュ検証付き）。
    /// 戻り値は成功時に転送したバイト数（統計集計用）。失敗時は例外をスロー。
    /// </summary>
    private async Task<long> ProcessDownloadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        string localPath;
        
        if (_transfer.PreserveFolderStructure && _watch.IncludeSubfolders)
        {
            // サブディレクトリ構造を保持する場合
            // ListFilesAsync で生成した item.Path は先頭に '/' を付加している場合があるが、
            // Transfer.RemotePath は必ず '/' 始まりとは限らないため、RelativePath の計算時にベースパスも正規化する。
            var remoteBase = _transfer.RemotePath ?? string.Empty;
            var normalizedRemoteBase = remoteBase.StartsWith("/") ? remoteBase : "/" + remoteBase;
            // item.Path が先頭に '/' を持たない場合は付与して正規化する
            var normalizedItemPath = item.Path.StartsWith("/") ? item.Path : "/" + item.Path;
            var relativePath = Path.GetRelativePath(normalizedRemoteBase, normalizedItemPath);
            
            // パストラバーサル攻撃対策（包括的検証）
            if (relativePath.Contains("..") || Path.IsPathRooted(relativePath) ||
                relativePath.Contains("\\..\\") || relativePath.Contains("/../") ||
                relativePath.StartsWith("..\\\\") ||
                relativePath.StartsWith("../") || relativePath.EndsWith("\\..") || relativePath.EndsWith("/.."))
            {
                throw new ArgumentException($"Unsafe relative path detected: {relativePath}");
            }
            
            var safePath = Path.Combine(_watch.Path, relativePath);
            var fullPath = Path.GetFullPath(safePath);
            var watchFullPath = Path.GetFullPath(_watch.Path);
            
            if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
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
            // 危険な文字とパターンをチェック（プラットフォーム依存文字を考慮）
            var invalidChars = Path.GetInvalidFileNameChars();
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\') ||
                fileName.Any(c => invalidChars.Contains(c)) || fileName.Any(c => c < 32) ||
                fileName.Length > 255) // ファイル名長制限
            {
                throw new ArgumentException($"Unsafe or invalid characters in file name: {fileName}");
            }

            var safePath = Path.Combine(_watch.Path, fileName);
            var fullPath = Path.GetFullPath(safePath);
            var watchFullPath = Path.GetFullPath(_watch.Path);

            // より厳密なパス検証
            if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, watchFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Path traversal attempt detected: {fileName}");
            }

            // 最終的な安全性確認
            var relativePath = Path.GetRelativePath(watchFullPath, fullPath);
            if (relativePath.StartsWith("..") || Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException($"Invalid relative path detected: {fileName}");
            }

            localPath = safePath;
        }

        _logger.LogInformation("[{Id}] Starting download {Remote} to {Local}", id, item.Path, localPath);

        long fileSize;
        if (_hash.Enabled)
        {
            // 事前にリモートファイルのハッシュを計算
            var remoteHash = await client.GetRemoteHashAsync(item.Path, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Remote hash calculated: {Hash}", id, remoteHash);

            // ダウンロード実行
            await client.DownloadAsync(item.Path, localPath, token).ConfigureAwait(false);
            fileSize = new FileInfo(localPath).Length;
            _logger.LogInformation("[{Id}] Download completed for {Remote} ({Size})", id, item.Path, FormatBytes(fileSize));

            // ローカルファイルのハッシュを計算して検証
            var localHash = await HashUtil.ComputeHashAsync(localPath, _hash.Algorithm, token).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Local hash calculated: {Hash}", id, localHash);

            if (!string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                var error = $"Hash mismatch for {item.Path}: Remote={remoteHash}, Local={localHash}";
                _logger.LogError("[{Id}] {Error}", id, error);
                throw new InvalidOperationException(error);
            }

            _logger.LogInformation("[{Id}] Hash verification successful for {Remote}", id, item.Path);
        }
        else
        {
            // ハッシュ検証なしでダウンロード
            await client.DownloadAsync(item.Path, localPath, token).ConfigureAwait(false);
            fileSize = new FileInfo(localPath).Length;
            _logger.LogInformation("[{Id}] Download completed for {Remote} ({Size}, hash verification disabled)", id, item.Path, FormatBytes(fileSize));
        }

        // ENDファイルまたは通常ファイルの削除判定
        var isEndFileRemote = IsEndFileRemote(item.Path);
        var shouldDeleteRemote = isEndFileRemote ? _cleanup.DeleteRemoteEndFiles : _cleanup.DeleteRemoteAfterDownload;

        if (shouldDeleteRemote)
        {
            // ダウンロード後のリモートファイル削除は失敗しても転送全体を失敗させないようにtry/catchで処理します。
            try
            {
                await client.DeleteAsync(item.Path, token).ConfigureAwait(false);
                var fileType = isEndFileRemote ? "remote END file" : "remote file";
                _logger.LogInformation("[{Id}] Deleted {FileType} {Remote}", id, fileType, item.Path);
            }
            catch (Exception ex)
            {
                // 削除に失敗した場合は警告ログのみ出力し、以後の処理を継続します。
                _logger.LogWarning("[{Id}] Failed to delete remote file {Remote}: {Error}", id, item.Path, ex.Message);
            }
        }

        return fileSize;
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
            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath);

            // ディレクトリまたはファイル名が取得できない場合
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
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
                var endFilePath = Path.Combine(directory, fileName + endFileExt);

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
    /// 指定されたリモートファイルがENDファイルかどうかを判定
    /// </summary>
    private bool IsEndFileRemote(string filePath)
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
            _logger.LogWarning("Error checking if remote file is END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// リモートENDファイルから対応するデータファイル名を取得
    /// </summary>
    private string GetDataFileForEndFileRemote(string endFilePath)
    {
        // ENDファイルから対応するデータファイルの完全パスを取得します。
        // ディレクトリとファイル名を分離し、拡張子を除去した上で再結合します。
        if (string.IsNullOrEmpty(endFilePath) || _watch.EndFileExtensions == null)
        {
            return string.Empty;
        }

        try
        {
            // ディレクトリとファイル名を取得
            var directory = Path.GetDirectoryName(endFilePath) ?? string.Empty;
            var fileName = Path.GetFileName(endFilePath);
            var extension = Path.GetExtension(endFilePath);

            foreach (var endExt in _watch.EndFileExtensions)
            {
                // 空白や空の拡張子はスキップ
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                if (string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase))
                {
                    // ファイル名からEND拡張子を除去
                    var baseName = fileName.Substring(0, fileName.Length - normalizedEndExt.Length);
                    // ディレクトリと結合して完全パスを生成。セパレーターは '/' に統一
                    string fullPath;
                    if (string.IsNullOrEmpty(directory))
                    {
                        // ルートディレクトリにファイルがある場合、必ず先頭に'/'を付与
                        fullPath = $"/{baseName}";
                    }
                    else
                    {
                        var trimmedDir = directory.TrimEnd(Path.DirectorySeparatorChar, '/', '\\');
                        // ディレクトリがルート("/")の場合はそのまま、そうでなければスラッシュを付与
                        if (trimmedDir.Length == 0 || trimmedDir == "/")
                        {
                            fullPath = $"/{baseName}";
                        }
                        else
                        {
                            fullPath = $"/{trimmedDir.TrimStart('/', '\\')}/{baseName}";
                        }
                    }
                    // パス区切り文字を統一し返す
                    return fullPath.Replace('\\', '/');
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error getting data file name for remote END file {EndFile}: {Error}", endFilePath, ex.Message);
        }

        return string.Empty;
    }

    /// <summary>
    /// 指定されたリモートファイルに対応するENDファイルが存在するかどうかを確認
    /// </summary>
    private bool HasEndFileRemote(string filePath, List<string> remoteFiles)
    {
        // 入力値の検証
        if (string.IsNullOrEmpty(filePath) || remoteFiles == null)
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
            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;

            // ファイル名が取得できない場合
            if (string.IsNullOrEmpty(fileName))
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
                var endFileName = fileName + endFileExt;
                
                // リモートパスはUnix形式（/）を使用（パス正規化）
                var normalizedDirectory = directory?.Replace('\\', '/');
                // ディレクトリが空でない場合、先頭の/を保持
                var endFilePath = string.IsNullOrEmpty(normalizedDirectory) ? endFileName : 
                    normalizedDirectory.StartsWith("/") ? $"{normalizedDirectory}/{endFileName}" : $"/{normalizedDirectory}/{endFileName}";

                // リモートファイル一覧から対応するENDファイルを検索
                // リストの表記はサーバ実装により '/' の有無が異なる場合があるため、先頭のセパレータを除去して比較する。
                if (remoteFiles.Any(f => string.Equals(
                        f?.TrimStart('/', '\\'),
                        endFilePath.TrimStart('/', '\\'),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (ArgumentException ex)
        {
            // 不正なパス文字が含まれる場合
            _logger.LogWarning("Invalid remote file path for END file check: {FilePath}. Error: {Error}", filePath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // その他の予期しないエラー
            _logger.LogWarning("Error checking remote END file for {FilePath}: {Error}", filePath, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// パフォーマンス監視タスクを開始
    /// </summary>
    private async Task StartPerformanceMonitoringAsync(string queueName, TransferQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

                var stats = queue.GetStatistics();
                if (stats.TotalEnqueued > 0)
                {
                    _logger.LogInformation("Transfer Progress [{Queue}] - Total: {Total}, Completed: {Completed}, Failed: {Failed}, Active: {Active}, Memory: {Memory}MB, Success Rate: {Rate:F1}%, Volume: {Volume}",
                        queueName, stats.TotalEnqueued, stats.TotalCompleted, stats.TotalFailed, stats.ActiveItems, stats.MemoryUsageMB, stats.SuccessRate, FormatBytes(stats.TotalBytesTransferred));
                }

                // 長時間実行中のアイテムを警告
                var longRunningItems = queue.GetLongRunningItems(TimeSpan.FromMinutes(5));
                foreach (var (itemKey, duration) in longRunningItems)
                {
                    _logger.LogWarning("Long running transfer detected [{Queue}]: {ItemKey} running for {Duration}", queueName, itemKey, duration);
                }

                // メモリ使用量が高い場合は警告
                if (stats.MemoryUsageMB > 500)
                {
                    _logger.LogWarning("High memory usage detected [{Queue}]: {Memory}MB", queueName, stats.MemoryUsageMB);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常な終了処理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance monitoring task failed for queue {Queue}", queueName);
        }
    }

    public override void Dispose()
    {
        // 後始末
        base.Dispose();
    }
}
