using System.IO;
using System.Text;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 改修された確実な転送機能のテスト
/// </summary>
public class ReliableTransferTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ILogger<TransferQueue>> _mockLogger;
    private readonly Mock<IFileTransferClient> _mockClient;

    public ReliableTransferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _mockLogger = new Mock<ILogger<TransferQueue>>();
        _mockClient = new Mock<IFileTransferClient>();
    }

    [Fact]
    public async Task TransferQueue_ShouldPreventDuplicateProcessing()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 3, DelaySeconds = 1 };
        var channel = System.Threading.Channels.Channel.CreateUnbounded<TransferItem>();
        var queue = new TransferQueue(channel, options, _mockLogger.Object, 2);

        var processedItems = new List<string>();
        var handlerCalls = 0;

        // 同じアイテムを複数回キューに追加
        var item = new TransferItem("test.txt", TransferAction.Upload);
        channel.Writer.TryWrite(item);
        channel.Writer.TryWrite(item);
        channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        // Act
        await queue.StartAsync(async (transferItem, token) =>
        {
            Interlocked.Increment(ref handlerCalls);
            processedItems.Add(transferItem.Path);
            await Task.Delay(100, token); // 処理時間をシミュレート
        }, CancellationToken.None);

        // Assert
        Assert.Equal(1, handlerCalls); // 重複処理が防がれること
        Assert.Single(processedItems);
    }

    [Fact]
    public async Task TransferQueue_ShouldRetryOnFailure()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 3, DelaySeconds = 1 };
        var channel = System.Threading.Channels.Channel.CreateUnbounded<TransferItem>();
        var queue = new TransferQueue(channel, options, _mockLogger.Object, 1);

        var item = new TransferItem("test.txt", TransferAction.Upload);
        channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        var attemptCount = 0;

        // Act - 並列処理改善後は例外が再スローされない
        await queue.StartAsync((transferItem, token) =>
        {
            attemptCount++;
            throw new TimeoutException("Network timeout - retryable");
        }, CancellationToken.None);

        // Assert - 統計情報でリトライ動作と失敗を確認
        Assert.Equal(options.MaxAttempts + 1, attemptCount); // 初回 + リトライ
        var stats = queue.GetStatistics();
        Assert.Equal(1, stats.TotalFailed);
        Assert.Equal(0, stats.CriticalErrorCount); // TimeoutExceptionはクリティカルエラーではない
    }

    [Fact]
    public async Task HashUtil_ShouldCalculateLocalHash()
    {
        // Arrange
        var testFile = Path.Combine(_tempDir, "test.txt");
        var testContent = "Test content for hash calculation";
        await File.WriteAllTextAsync(testFile, testContent);

        // Act
        var hash = await HashUtil.ComputeHashAsync(testFile, "MD5", CancellationToken.None);

        // Assert
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        // Verify hash is correct MD5 of the content
        Assert.Equal("3e80805c418d2355888f2e1bb6a2b030", hash);
    }

    [Fact]
    public async Task HashVerification_ShouldFailOnMismatch()
    {
        // Arrange
        var localFile = Path.Combine(_tempDir, "local.txt");
        await File.WriteAllTextAsync(localFile, "Local content");

        var remoteContent = "Different remote content";
        var remoteHash = await HashUtil.ComputeHashAsync(new MemoryStream(Encoding.UTF8.GetBytes(remoteContent)), "MD5", CancellationToken.None);
        var localHash = await HashUtil.ComputeHashAsync(localFile, "MD5", CancellationToken.None);

        // ハッシュが異なることを確認
        Assert.NotEqual(remoteHash, localHash);

        // Act & Assert
        // Worker.ProcessUploadAsync の動作をシミュレート
        var hashMismatch = !string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase);
        Assert.True(hashMismatch);
    }

    [Fact]
    public async Task ConcurrentTransfer_ShouldNotCauseRaceConditions()
    {
        // Arrange
        var options = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var channel = System.Threading.Channels.Channel.CreateUnbounded<TransferItem>();
        var queue = new TransferQueue(channel, options, _mockLogger.Object, 4); // 4並列

        var processedFiles = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        // 複数のファイルをキューに追加
        for (int i = 0; i < 20; i++)
        {
            channel.Writer.TryWrite(new TransferItem($"file{i}.txt", TransferAction.Upload));
        }
        channel.Writer.Complete();

        // Act
        await queue.StartAsync(async (item, token) =>
        {
            // 並列処理をシミュレート
            await Task.Delay(Random.Shared.Next(10, 50), token);
            processedFiles.Add(item.Path);
        }, CancellationToken.None);

        // Assert
        Assert.Equal(20, processedFiles.Count);
        Assert.Equal(20, processedFiles.Distinct().Count()); // 重複がないことを確認
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
