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

    public TransferQueue(Channel<TransferItem> channel, RetryOptions options, ILogger<TransferQueue> logger, int concurrency = 1)
    {
        _reader = channel.Reader;
        _logger = logger;
        _concurrency = Math.Max(1, Math.Min(concurrency, 16)); // 最大16に制限
        // 例外発生時にリトライするポリシー
        _policy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: options.MaxAttempts,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(options.DelaySeconds * Math.Pow(2, attempt - 1)), // 指数バックオフ
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
            tasks[i] = Task.Run(async () => await Worker(workerId, ct), ct);
        }
        return Task.WhenAll(tasks);

        // キューからアイテムを読み取りハンドラーを実行
        async Task Worker(int workerId, CancellationToken token)
        {
            _logger.LogDebug("Worker {WorkerId} started", workerId);
            try
            {
                while (await _reader.WaitToReadAsync(token))
                {
                    while (_reader.TryRead(out var item))
                    {
                        // 重複処理を防ぐ
                        var itemKey = $"{item.Action}:{item.Path}";
                        if (!_processedItems.TryAdd(itemKey, true))
                        {
                            _logger.LogDebug("Item {ItemKey} already processed, skipping", itemKey);
                            continue;
                        }

                        var context = new Context(itemKey) { ["ItemPath"] = item.Path };
                        try
                        {
                            await _policy.ExecuteAsync(async (ctx, t) =>
                            {
                                _logger.LogDebug("Worker {WorkerId} processing {ItemKey}", workerId, itemKey);
                                await handler(item, t);
                            }, context, token);
                            _logger.LogDebug("Worker {WorkerId} completed {ItemKey}", workerId, itemKey);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Worker {WorkerId} failed to process {ItemKey} after all retries", workerId, itemKey);
                            // 失敗したアイテムを処理済みから除去（再処理できるように）
                            _processedItems.TryRemove(itemKey, out _);
                            throw;
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
    public (int ProcessedCount, int TotalItems) GetStatistics()
    {
        return (_processedItems.Count, _processedItems.Count);
    }
}