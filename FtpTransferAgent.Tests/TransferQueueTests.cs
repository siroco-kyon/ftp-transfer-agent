using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="TransferQueue"/> の再試行ロジックをテストする
/// </summary>
public class TransferQueueTests
{
    /// <summary>
    /// 失敗した処理がリトライされ、最終的に 1 回だけハンドラーが完了することを確認
    /// </summary>
    [Fact]
    public async Task StartAsync_ProcessesAllItemsWithRetries()
    {
        // キューと再試行オプションを準備
        var channel = Channel.CreateUnbounded<TransferItem>();
        var options = new RetryOptions { MaxAttempts = 3, DelaySeconds = 0 };
        var queue = new TransferQueue(channel, options, NullLogger<TransferQueue>.Instance, 1);
        int attempt = 0;
        int processed = 0;

        // ハンドラー内で 1 度失敗させてリトライさせる
        var queueTask = queue.StartAsync(async (item, ct) =>
        {
            attempt++;
            if (attempt < 2)
            {
                throw new Exception("fail");
            }
            await Task.Delay(10);
            processed++;
        }, CancellationToken.None);

        // 1 件のアイテムを投入して処理させる
        channel.Writer.TryWrite(new TransferItem("a", TransferAction.Upload));
        channel.Writer.Complete();

        await queueTask;
        Assert.Equal(1, processed);
        Assert.True(attempt >= 2);
    }
}
