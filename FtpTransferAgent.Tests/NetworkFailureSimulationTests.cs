using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ネットワーク障害シミュレーションのテストクラス
/// </summary>
public class NetworkFailureSimulationTests
{
    private readonly Mock<ILogger<TransferQueue>> _loggerMock;
    private readonly RetryOptions _retryOptions;

    public NetworkFailureSimulationTests()
    {
        _loggerMock = new Mock<ILogger<TransferQueue>>();
        _retryOptions = new RetryOptions
        {
            MaxAttempts = 3,
            DelaySeconds = 1
        };
    }

    [Fact]
    public async Task NetworkTimeoutException_ShouldRetry_UntilMaxAttempts()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 1);
        var callCount = 0;

        // 常にタイムアウトする処理
        async Task FailingHandler(TransferItem item, CancellationToken ct)
        {
            await Task.Yield(); // 非同期であることを明示
            callCount++;
            throw new TimeoutException("Network timeout");
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("test.txt", TransferAction.Upload));
        channel.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await queue.StartAsync(FailingHandler, cts.Token);

        // Assert - 並列処理改善後は例外が再スローされず統計情報で確認
        Assert.Equal(4, callCount); // リトライ回数が正しいことを確認（初回実行 + 3回リトライ = 4回）
        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.TotalFailed);
        Assert.Equal(0, stats.CriticalErrorCount); // TimeoutExceptionはクリティカルエラーではない
    }

    [Fact]
    public async Task SocketException_ShouldRetry_AndEventuallySucceed()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 1);
        var callCount = 0;

        // 2回失敗して3回目で成功する処理
        async Task RecoveringHandler(TransferItem item, CancellationToken ct)
        {
            await Task.Yield(); // 非同期であることを明示
            callCount++;
            if (callCount <= 2)
            {
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
            // 3回目は成功
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("test.txt", TransferAction.Upload));
        channel.Writer.Complete();

        await queue.StartAsync(RecoveringHandler, CancellationToken.None);

        // Assert
        Assert.Equal(3, callCount);
        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.TotalCompleted);
        Assert.Equal(0, stats.TotalFailed);
    }

    [Fact]
    public async Task NonRetryableException_ShouldNotRetry()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 1);
        var callCount = 0;

        // 設定エラー（リトライ不可）
        async Task NonRetryableHandler(TransferItem item, CancellationToken ct)
        {
            await Task.Yield(); // 非同期であることを明示
            callCount++;
            throw new ArgumentException("Invalid configuration");
        }

        // Act & Assert
        channel.Writer.TryWrite(new TransferItem("test.txt", TransferAction.Upload));
        channel.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await queue.StartAsync(NonRetryableHandler, cts.Token);

        // リトライしないことを確認（1回のみ実行）
        Assert.Equal(1, callCount);

        // クリティカルエラーが記録されていることを確認
        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.CriticalErrorCount);

        var criticalExceptions = queue.GetCriticalExceptions().ToList();
        Assert.Single(criticalExceptions);
        Assert.IsType<ArgumentException>(criticalExceptions.First());
    }

    [Fact]
    public async Task ConcurrentNetworkFailures_ShouldHandleIndependently()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(10);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 3);
        var callCounts = new Dictionary<string, int>();
        var lockObject = new object();

        // 各ファイルで異なる失敗パターン
        async Task VariableFailureHandler(TransferItem item, CancellationToken ct)
        {
            await Task.Yield(); // 非同期であることを明示

            lock (lockObject)
            {
                callCounts.TryGetValue(item.Path, out var count);
                callCounts[item.Path] = count + 1;
            }

            var currentCount = callCounts[item.Path];

            switch (item.Path)
            {
                case "file1.txt":
                    if (currentCount <= 1) throw new SocketException((int)SocketError.TimedOut);
                    break;
                case "file2.txt":
                    if (currentCount <= 2) throw new TimeoutException("Connection timeout");
                    break;
                case "file3.txt":
                    throw new ArgumentException("Invalid file"); // 常に失敗（リトライ不可）
                default:
                    break; // 即座に成功
            }
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("file1.txt", TransferAction.Upload));
        channel.Writer.TryWrite(new TransferItem("file2.txt", TransferAction.Upload));
        channel.Writer.TryWrite(new TransferItem("file3.txt", TransferAction.Upload));
        channel.Writer.TryWrite(new TransferItem("file4.txt", TransferAction.Upload));
        channel.Writer.Complete();

        // file3.txtで非リトライ可能例外が発生するが、他のファイルは処理される
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await queue.StartAsync(VariableFailureHandler, cts.Token);

        // Assert - 並列処理改善後は例外が再スローされず統計情報で確認
        var stats = queue.GetStatistics();
        Assert.Equal(4, stats.TotalEnqueued);
        Assert.True(stats.TotalCompleted >= 2); // file1, file2, file4のうち少なくとも2つは成功
        Assert.True(stats.TotalFailed >= 1);    // file3は失敗
        Assert.True(stats.CriticalErrorCount >= 1); // file3でクリティカルエラー発生
    }

    [Fact]
    public void RetryableExceptionClassifier_ShouldClassifyCorrectly()
    {
        // Arrange & Act & Assert

        // リトライ可能な例外
        Assert.True(RetryableExceptionClassifier.IsRetryable(new SocketException()));
        Assert.True(RetryableExceptionClassifier.IsRetryable(new TimeoutException()));
        Assert.True(RetryableExceptionClassifier.IsRetryable(new HttpRequestException()));
        Assert.True(RetryableExceptionClassifier.IsRetryable(new UnauthorizedAccessException()));

        // リトライ不可能な例外
        Assert.False(RetryableExceptionClassifier.IsRetryable(new ArgumentException()));
        Assert.False(RetryableExceptionClassifier.IsRetryable(new ArgumentNullException()));
        Assert.False(RetryableExceptionClassifier.IsRetryable(new InvalidOperationException()));
        Assert.False(RetryableExceptionClassifier.IsRetryable(new DirectoryNotFoundException()));

        // 内部例外のチェック
        var wrapperException = new Exception("Wrapper", new SocketException());
        Assert.True(RetryableExceptionClassifier.IsRetryable(wrapperException));

        var nonRetryableWrapper = new Exception("Wrapper", new ArgumentException());
        Assert.False(RetryableExceptionClassifier.IsRetryable(nonRetryableWrapper));
    }

    [Fact]
    public async Task LongRunningTransfer_ShouldBeDetected()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 1);

        // 長時間実行される処理
        async Task LongRunningHandler(TransferItem item, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(2000), ct);
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("long-running.txt", TransferAction.Upload));
        channel.Writer.Complete();

        var transferTask = queue.StartAsync(LongRunningHandler, CancellationToken.None);

        // 少し待ってから長時間実行中のアイテムをチェック
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var longRunningItems = queue.GetLongRunningItems(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Single(longRunningItems);
        Assert.Contains("long-running.txt", longRunningItems.First().ItemKey);

        // 処理完了を待つ
        await transferTask;
    }

    [Fact]
    public void TransferStatistics_ShouldCalculateCorrectly()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var queue = new TransferQueue(channel, _retryOptions, _loggerMock.Object, 1);

        // Act
        var initialStats = queue.GetStatistics();

        // Assert
        Assert.Equal(0, initialStats.TotalEnqueued);
        Assert.Equal(0, initialStats.TotalCompleted);
        Assert.Equal(0, initialStats.TotalFailed);
        Assert.Equal(0, initialStats.ActiveItems);
        Assert.Equal(0, initialStats.RemainingItems);
        Assert.Equal(0, initialStats.SuccessRate);
        Assert.True(initialStats.MemoryUsageMB > 0);
        Assert.Equal(1, initialStats.ActiveWorkers);
    }
}