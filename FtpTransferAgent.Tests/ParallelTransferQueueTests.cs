using System.Collections.Concurrent;
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
    public async Task TransferQueue_DuplicateItem_ReportsFinalOutcomeAsFailure()
    {
        var options = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var channel = Channel.CreateUnbounded<TransferItem>();
        var mockLogger = new Mock<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, options, mockLogger.Object, 2);

        var item = new TransferItem("test.txt", TransferAction.Upload);
        channel.Writer.TryWrite(item);
        channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        var handlerCalls = 0;
        var outcomes = new ConcurrentBag<Exception?>();

        await queue.StartAsync(
            async (_, token) =>
            {
                Interlocked.Increment(ref handlerCalls);
                await Task.Delay(50, token);
            },
            (_, ex) => outcomes.Add(ex),
            CancellationToken.None);

        Assert.Equal(1, handlerCalls);
        Assert.Equal(2, outcomes.Count);
        Assert.Single(outcomes, ex => ex is null);
        Assert.Single(outcomes, ex => ex is InvalidOperationException);

        var stats = queue.GetStatistics();
        Assert.Equal(2, stats.TotalEnqueued);
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(1, stats.TotalFailed);
    }

    [Fact]
    public async Task TransferQueue_FinalOutcomeCallbackFailure_IsCountedAsCritical()
    {
        var options = new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 };
        var channel = Channel.CreateUnbounded<TransferItem>();
        var mockLogger = new Mock<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, options, mockLogger.Object, 1);

        channel.Writer.TryWrite(new TransferItem("test.txt", TransferAction.Upload));
        channel.Writer.Complete();

        await queue.StartAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => throw new IOException("marker update failed"),
            CancellationToken.None);

        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(1, stats.CriticalErrorCount);
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

        // Act
        await queue.StartAsync((transferItem, token) =>
        {
            attemptCount++;
            throw new TimeoutException("Network timeout"); // リトライ可能な例外を使用
        }, CancellationToken.None);

        // Assert - 並列処理改善後は例外が再スローされずに統計情報に記録される
        Assert.Equal(3, attemptCount); // 初回 + 2回リトライ
        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.TotalFailed);
        Assert.Equal(0, stats.CriticalErrorCount); // TimeoutExceptionはクリティカルエラーではない
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
