using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace FtpTransferAgent.Services;

/// <summary>
/// 確実な並列転送処理を行うためのキュー
/// </summary>
public class TransferQueue
{
    private readonly ChannelReader<TransferItem> _reader;
    private readonly AsyncRetryPolicy _policy;
    private readonly ILogger<TransferQueue> _logger;
    private readonly int _concurrency;
    private readonly ConcurrentDictionary<string, bool> _processedItems = new();
    private readonly ConcurrentDictionary<string, DateTime> _activeItems = new();
    private readonly ConcurrentBag<Exception> _criticalExceptions = new();
    private int _totalEnqueued = 0;
    private int _totalCompleted = 0;
    private int _totalFailed = 0;

    public TransferQueue(Channel<TransferItem> channel, RetryOptions options, ILogger<TransferQueue> logger, int concurrency = 1)
    {
        _reader = channel.Reader;
        _logger = logger;
        _concurrency = Math.Max(1, Math.Min(concurrency, 16)); // 最大16に制限
        // リトライ可能な例外のみリトライするポリシー
        _policy = Policy
            .Handle<Exception>(ex => RetryableExceptionClassifier.IsRetryable(ex))
            .WaitAndRetryAsync(
                retryCount: options.MaxAttempts,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(options.DelaySeconds * Math.Pow(2, attempt - 1)), // 指数バックオフ（初回は基本遅延）
                onRetry: (ex, ts, attempt, ctx) =>
                {
                    var itemPath = ctx.ContainsKey("ItemPath") ? ctx["ItemPath"].ToString() : "Unknown";
                    _logger.LogWarning(ex, "Retry {Attempt}/{MaxAttempts} for {ItemPath}: {Error}",
                        attempt, options.MaxAttempts + 1, itemPath, ex.Message);
                });
    }

    // キューの読み取りを開始する
    public Task StartAsync(Func<TransferItem, CancellationToken, Task> handler, CancellationToken ct)
    {
        // 指定された並列数だけワーカーを起動
        var tasks = new Task[_concurrency];
        for (int i = 0; i < _concurrency; i++)
        {
            int workerId = i;
            tasks[i] = Task.Run(async () => await Worker(workerId, ct).ConfigureAwait(false), ct);
        }
        return Task.WhenAll(tasks);

        // キューからアイテムを読み取りハンドラーを実行
        async Task Worker(int workerId, CancellationToken token)
        {
            _logger.LogDebug("Worker {WorkerId} started", workerId);
            try
            {
                while (await _reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    // 一度に一つのアイテムのみ処理して真の並列処理を実現
                    if (_reader.TryRead(out var item))
                    {
                        // 重複処理を防ぐ（アトミックな処理保証）
                        var itemKey = $"{item.Action}:{item.Path}";
                        if (!_processedItems.TryAdd(itemKey, true))
                        {
                            _logger.LogDebug("Item {ItemKey} already processed, skipping", itemKey);
                            continue;
                        }

                        // アクティブアイテムとして追跡開始（処理済み登録後に確実に実行）
                        var startTime = DateTime.UtcNow;
                        _activeItems.TryAdd(itemKey, startTime);
                        var enqueuedCount = Interlocked.Increment(ref _totalEnqueued);

                        var context = new Context(itemKey) { ["ItemPath"] = item.Path };
                        try
                        {
                            await _policy.ExecuteAsync(async (ctx, t) =>
                            {
                                _logger.LogDebug("Worker {WorkerId} processing {ItemKey}", workerId, itemKey);
                                await handler(item, t).ConfigureAwait(false);
                            }, context, token).ConfigureAwait(false);

                            _logger.LogDebug("Worker {WorkerId} completed {ItemKey}", workerId, itemKey);
                            _activeItems.TryRemove(itemKey, out _);
                            Interlocked.Increment(ref _totalCompleted);
                        }
                        catch (Exception ex)
                        {
                            _activeItems.TryRemove(itemKey, out _);
                            Interlocked.Increment(ref _totalFailed);

                            if (RetryableExceptionClassifier.IsRetryable(ex))
                            {
                                _logger.LogError(ex, "Worker {WorkerId} failed to process {ItemKey} after all retries (Retryable)", workerId, itemKey);
                            }
                            else
                            {
                                _logger.LogError(ex, "Worker {WorkerId} failed to process {ItemKey} - Non-retryable error", workerId, itemKey);
                                // クリティカルエラーは記録するが他のワーカーの処理は継続
                                _criticalExceptions.Add(ex);
                            }

                            // 例外を再スローせず、他のワーカーの処理を継続させる
                            // 失敗したアイテムは処理済みとして保持（無限リトライ防止）
                        }
                    }
                }
            }
            finally
            {
                _logger.LogDebug("Worker {WorkerId} stopped", workerId);
            }
        }
    }

    /// <summary>
    /// 処理結果の統計情報を取得
    /// </summary>
    public TransferStatistics GetStatistics()
    {
        return new TransferStatistics
        {
            TotalEnqueued = _totalEnqueued,
            TotalCompleted = _totalCompleted,
            TotalFailed = _totalFailed,
            ActiveItems = _activeItems.Count,
            ProcessedItems = _processedItems.Count,
            MemoryUsageMB = GC.GetTotalMemory(false) / (1024 * 1024),
            ActiveWorkers = _concurrency,
            CriticalErrorCount = _criticalExceptions.Count
        };
    }

    /// <summary>
    /// クリティカルエラーの一覧を取得
    /// </summary>
    public IEnumerable<Exception> GetCriticalExceptions()
    {
        return _criticalExceptions;
    }

    /// <summary>
    /// 長時間実行中のアイテムを検出
    /// </summary>
    public IEnumerable<(string ItemKey, TimeSpan Duration)> GetLongRunningItems(TimeSpan threshold)
    {
        var now = DateTime.UtcNow;
        return _activeItems
            .Where(kvp => now - kvp.Value > threshold)
            .Select(kvp => (kvp.Key, now - kvp.Value));
    }
}

/// <summary>
/// 転送統計情報
/// </summary>
public class TransferStatistics
{
    public int TotalEnqueued { get; set; }
    public int TotalCompleted { get; set; }
    public int TotalFailed { get; set; }
    public int ActiveItems { get; set; }
    public int ProcessedItems { get; set; }
    public long MemoryUsageMB { get; set; }
    public int ActiveWorkers { get; set; }
    public int CriticalErrorCount { get; set; }

    public double SuccessRate => TotalEnqueued > 0 ? (double)TotalCompleted / TotalEnqueued * 100 : 0;
    public int RemainingItems => TotalEnqueued - TotalCompleted - TotalFailed;
}
