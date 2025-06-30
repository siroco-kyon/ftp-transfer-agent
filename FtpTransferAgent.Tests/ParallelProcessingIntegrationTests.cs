using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 並列処理とエラーハンドリングの統合テストクラス
/// </summary>
public class ParallelProcessingIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ServiceProvider _serviceProvider;

    public ParallelProcessingIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ParallelTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.Configure<WatchOptions>(options => options.Path = _testDirectory);
        services.Configure<TransferOptions>(options =>
        {
            options.Mode = "ftp";
            options.Direction = "put";
            options.Host = "localhost";
            options.Username = "test";
            options.Password = "test";
            options.Concurrency = 4;
        });
        services.Configure<RetryOptions>(options =>
        {
            options.MaxAttempts = 2;
            options.DelaySeconds = 1;
        });
        services.Configure<HashOptions>(options => options.Algorithm = "MD5");
        services.Configure<CleanupOptions>(options => options.DeleteAfterVerify = false);
        services.Configure<SmtpOptions>(options => options.Enabled = false);
        services.Configure<LoggingOptions>(options => options.Level = "Debug");
        services.AddSingleton<ConfigurationValidator>();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ConcurrentTransferQueue_WithMixedSuccessFailure_ShouldHandleProperly()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(10);
        var retryOptions = new RetryOptions { MaxAttempts = 2, DelaySeconds = 1 };
        var logger = _serviceProvider.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, retryOptions, logger, 3);

        var successCount = 0;
        var failureCount = 0;
        var callCounts = new Dictionary<string, int>();
        var lockObject = new object();

        async Task MixedHandler(TransferItem item, CancellationToken ct)
        {
            lock (lockObject)
            {
                callCounts.TryGetValue(item.Path, out var count);
                callCounts[item.Path] = count + 1;
            }

            var currentCount = callCounts[item.Path];

            switch (item.Path)
            {
                case "success1.txt":
                case "success2.txt":
                case "success3.txt":
                    await Task.Delay(100, ct); // 短い処理時間をシミュレート
                    Interlocked.Increment(ref successCount);
                    break;
                case "retry_then_success.txt":
                    if (currentCount <= 1)
                    {
                        throw new TimeoutException("Temporary network issue");
                    }
                    await Task.Delay(50, ct);
                    Interlocked.Increment(ref successCount);
                    break;
                case "permanent_failure.txt":
                    Interlocked.Increment(ref failureCount);
                    throw new ArgumentException("Configuration error - non-retryable");
                default:
                    throw new InvalidOperationException($"Unexpected file: {item.Path}");
            }
        }

        // Act
        var files = new[] { "success1.txt", "success2.txt", "success3.txt", "retry_then_success.txt", "permanent_failure.txt" };
        foreach (var file in files)
        {
            channel.Writer.TryWrite(new TransferItem(file, TransferAction.Upload));
        }
        channel.Writer.Complete();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await queue.StartAsync(MixedHandler, cts.Token);

        // Assert
        var stats = queue.GetStatistics();
        Assert.Equal(5, stats.TotalEnqueued);
        Assert.Equal(4, stats.TotalCompleted); // success1-3 + retry_then_success
        Assert.Equal(1, stats.TotalFailed);    // permanent_failure
        Assert.Equal(1, stats.CriticalErrorCount); // 非リトライ可能エラー
        Assert.Equal(0, stats.ActiveItems);

        var criticalExceptions = queue.GetCriticalExceptions().ToList();
        Assert.Single(criticalExceptions);
        Assert.IsType<ArgumentException>(criticalExceptions.First());

        Assert.Equal(4, successCount);
        Assert.Equal(1, failureCount);
    }

    [Fact]
    public async Task TransferQueue_WithLongRunningItems_ShouldDetectCorrectly()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(5);
        var retryOptions = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var logger = _serviceProvider.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, retryOptions, logger, 2);

        async Task DelayedHandler(TransferItem item, CancellationToken ct)
        {
            var delay = item.Path switch
            {
                "quick.txt" => 100,
                "medium.txt" => 2000,
                "slow.txt" => 5000,
                _ => 50
            };
            await Task.Delay(delay, ct);
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("quick.txt", TransferAction.Upload));
        channel.Writer.TryWrite(new TransferItem("medium.txt", TransferAction.Upload));
        channel.Writer.TryWrite(new TransferItem("slow.txt", TransferAction.Upload));
        channel.Writer.Complete();

        var processingTask = queue.StartAsync(DelayedHandler, CancellationToken.None);

        // 少し待ってから長時間実行中のアイテムをチェック
        await Task.Delay(1500);
        var longRunningItems = queue.GetLongRunningItems(TimeSpan.FromSeconds(1)).ToList();

        // Assert
        Assert.NotEmpty(longRunningItems);
        Assert.Contains(longRunningItems, item => item.ItemKey.Contains("medium.txt") || item.ItemKey.Contains("slow.txt"));

        await processingTask;

        var finalStats = queue.GetStatistics();
        Assert.Equal(3, finalStats.TotalCompleted);
        Assert.Equal(0, finalStats.TotalFailed);
    }

    [Fact]
    public async Task TransferQueue_MemoryUsageTracking_ShouldReportCorrectly()
    {
        // Arrange
        var channel = Channel.CreateBounded<TransferItem>(1);
        var retryOptions = new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 };
        var logger = _serviceProvider.GetRequiredService<ILogger<TransferQueue>>();
        var queue = new TransferQueue(channel, retryOptions, logger, 1);

        var initialMemory = GC.GetTotalMemory(false);

        async Task MemoryHandler(TransferItem item, CancellationToken ct)
        {
            // 意図的にメモリを使用
            var largeArray = new byte[1024 * 1024]; // 1MB
            await Task.Delay(100, ct);
            GC.KeepAlive(largeArray);
        }

        // Act
        channel.Writer.TryWrite(new TransferItem("memory_test.txt", TransferAction.Upload));
        channel.Writer.Complete();

        await queue.StartAsync(MemoryHandler, CancellationToken.None);

        // Assert
        var stats = queue.GetStatistics();
        Assert.True(stats.MemoryUsageMB > 0);

        var memoryIncrease = GC.GetTotalMemory(false) - initialMemory;
        Assert.True(memoryIncrease > 0);
    }

    [Fact]
    public void ConfigurationValidator_WithComplexScenario_ShouldValidateCorrectly()
    {
        // Arrange
        var validator = _serviceProvider.GetRequiredService<ConfigurationValidator>();

        // 問題のある設定の組み合わせ
        var watch = new WatchOptions
        {
            Path = _testDirectory,
            IncludeSubfolders = true,
            AllowedExtensions = new[] { "txt", "csv", "" } // 空の拡張子
        };
        var transfer = new TransferOptions
        {
            Mode = "ftp", // セキュリティ警告対象
            Direction = "both", // 双方向
            Host = "example.com",
            Username = "user",
            Password = "plaintext",
            Concurrency = 12, // 高い並列度
            Port = 21
        };
        var retry = new RetryOptions { MaxAttempts = 8, DelaySeconds = 2 }; // 高いリトライ回数
        var hash = new HashOptions { Algorithm = "SHA256" };
        var cleanup = new CleanupOptions
        {
            DeleteAfterVerify = true,
            DeleteRemoteAfterDownload = true // 両方向削除
        };

        // Act
        ConfigurationValidationResult result = validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid); // 無効な拡張子によりエラー
        Assert.True(result.HasWarnings); // 複数の警告

        Assert.Contains(result.Errors, e => e.Contains("Invalid file extensions"));
        Assert.Contains(result.Warnings, w => w.Contains("FTP transmits passwords"));
        Assert.Contains(result.Warnings, w => w.Contains("High concurrency"));
        Assert.Contains(result.Warnings, w => w.Contains("Both local and remote file deletion"));
    }

    [Fact]
    public void ConfigurationValidator_ChangeImpactAssessment_ShouldEvaluateCorrectly()
    {
        // Arrange
        var validator = _serviceProvider.GetRequiredService<ConfigurationValidator>();

        var oldConfig = new TransferOptions
        {
            Host = "old.example.com",
            Port = 21,
            Username = "olduser",
            Concurrency = 2,
            Direction = "put"
        };

        var newConfig = new TransferOptions
        {
            Host = "new.example.com", // 変更
            Port = 22, // 変更
            Username = "newuser", // 変更
            Concurrency = 8, // 4倍増加
            Direction = "both" // 変更
        };

        // Act
        var assessment = validator.AssessConfigurationChange(oldConfig, newConfig);

        // Assert
        Assert.True(assessment.RequiresRestart);
        Assert.Contains(assessment.Impacts, i => i.Contains("Connection settings changed"));
        Assert.Contains(assessment.Impacts, i => i.Contains("Authentication settings changed"));
        Assert.Contains(assessment.Impacts, i => i.Contains("Concurrency changed"));
        Assert.Contains(assessment.Impacts, i => i.Contains("Transfer direction changed"));
        Assert.Contains(assessment.Warnings, w => w.Contains("Significant increase in concurrency"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch (Exception)
        {
            // テスト後のクリーンアップエラーは無視
        }

        _serviceProvider?.Dispose();
    }
}