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

    public TransferQueue(Channel<TransferItem> channel, RetryOptions options, ILogger<TransferQueue> logger)
    {
        _reader = channel.Reader;
        _logger = logger;
        _policy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(options.MaxAttempts,
                _ => TimeSpan.FromSeconds(options.DelaySeconds),
                (ex, ts, attempt, ctx) => _logger.LogWarning(ex, "Retry {Attempt}", attempt));
    }

    public async Task StartAsync(Func<TransferItem, CancellationToken, Task> handler, CancellationToken ct)
    {
        await foreach (var item in _reader.ReadAllAsync(ct))
        {
            await _policy.ExecuteAsync(token => handler(item, token), ct);
        }
    }
}
