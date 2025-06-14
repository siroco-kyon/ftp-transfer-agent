using System;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace FtpTransferAgent.Services;

public class TransferQueue
{
    private readonly ChannelReader<TransferItem> _reader;
    private readonly AsyncRetryPolicy _policy;
    private readonly ILogger<TransferQueue> _logger;
    private readonly int _concurrency;

    public TransferQueue(Channel<TransferItem> channel, RetryOptions options, ILogger<TransferQueue> logger, int concurrency = 1)
    {
        _reader = channel.Reader;
        _logger = logger;
        _concurrency = Math.Max(1, concurrency);
        _policy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(options.MaxAttempts,
                _ => TimeSpan.FromSeconds(options.DelaySeconds),
                (ex, ts, attempt, ctx) => _logger.LogWarning(ex, "Retry {Attempt}", attempt));
    }

    public Task StartAsync(Func<TransferItem, CancellationToken, Task> handler, CancellationToken ct)
    {
        var tasks = new Task[_concurrency];
        for (int i = 0; i < _concurrency; i++)
        {
            tasks[i] = Task.Run(() => Worker(ct), ct);
        }
        return Task.WhenAll(tasks);

        async Task Worker(CancellationToken token)
        {
            while (await _reader.WaitToReadAsync(token))
            {
                while (_reader.TryRead(out var item))
                {
                    await _policy.ExecuteAsync(t => handler(item, t), token);
                }
            }
        }
    }
}
