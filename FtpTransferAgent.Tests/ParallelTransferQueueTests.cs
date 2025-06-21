using System.IO;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 改修された並列転送キューのテスト
/// </summary>
public class ParallelTransferQueueTests
{
    [Fact]
    public async Task TransferQueue_PreventsDuplicateProcessing()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var channel = Channel.CreateUnbounded<TransferItem>();
        var mockLogger = new Mock<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, options, mockLogger.Object, 2);

        var processedItems = new ConcurrentBag<string>();

        // 同じアイテムを複数回追加
        var item = new TransferItem("test.txt", TransferAction.Upload);
        channel.Writer.TryWrite(item);
        channel.Writer.TryWrite(item);
        channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        // Act
        await queue.StartAsync(async (transferItem, token) =>
        {
            processedItems.Add(transferItem.Path);
            await Task.Delay(50, token);
        }, CancellationToken.None);

        // Assert
        Assert.Single(processedItems); // 重複処理が防がれること
    }

    [Fact]
    public async Task TransferQueue_RetriesOnFailure()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 2, DelaySeconds = 1 };
        var channel = Channel.CreateUnbounded<TransferItem>();
        var mockLogger = new Mock<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, options, mockLogger.Object, 1);

        var item = new TransferItem("test.txt", TransferAction.Upload);
        channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        var attemptCount = 0;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.StartAsync(async (transferItem, token) =>
            {
                attemptCount++;
                throw new InvalidOperationException("Test error");
            }, CancellationToken.None);
        });

        Assert.Equal(3, attemptCount); // 初回 + 2回リトライ
    }

    [Fact]
    public async Task TransferQueue_HandlesConcurrentProcessing()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var channel = Channel.CreateUnbounded<TransferItem>();
        var mockLogger = new Mock<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, options, mockLogger.Object, 4);

        var processedFiles = new ConcurrentBag<string>();

        // 複数のファイルをキューに追加
        for (int i = 0; i < 10; i++)
        {
            channel.Writer.TryWrite(new TransferItem($"file{i}.txt", TransferAction.Upload));
        }
        channel.Writer.Complete();

        // Act
        await queue.StartAsync(async (item, token) =>
        {
            await Task.Delay(Random.Shared.Next(10, 30), token);
            processedFiles.Add(item.Path);
        }, CancellationToken.None);

        // Assert
        Assert.Equal(10, processedFiles.Count);
        Assert.Equal(10, processedFiles.Distinct().Count()); // 重複なし
    }
}