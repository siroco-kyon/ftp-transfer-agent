using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ENDファイル転送機能を検証するテスト
/// </summary>
public class EndFileTransferTests
{
    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_Disabled_ShouldNotTransferEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false // 無効
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルのみ転送、ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_Enabled_ShouldTransferBothDataAndEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "end marker");
        var dataHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true // 有効
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.UploadAsync(endFile, "/remote/test.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルとENDファイルの両方が転送される
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, "/remote/test.txt.END", It.IsAny<CancellationToken>()), Times.Once);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_OnlyTransfersEndFilesWithDataFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "test1.txt");
        var endFile1 = Path.Combine(dir, "test1.txt.END");
        var endFileOrphan = Path.Combine(dir, "orphan.txt.END"); // 対応するデータファイルなし
        await File.WriteAllTextAsync(file1, "data1");
        await File.WriteAllTextAsync(endFile1, "end1");
        await File.WriteAllTextAsync(endFileOrphan, "orphan");
        var dataHash = await HashUtil.ComputeHashAsync(file1, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile1, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true,
            AllowedExtensions = new string[0] // 全拡張子許可
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // test1.txtとtest1.txt.ENDは転送される
        mock.Verify(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()), Times.Once);
        
        // orphan.ENDは対応するデータファイルがないため転送されない
        mock.Verify(c => c.UploadAsync(endFileOrphan, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_WithMultipleExtensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "test1.txt");
        var file2 = Path.Combine(dir, "test2.txt");
        var endFile1 = Path.Combine(dir, "test1.txt.END");
        var endFile2 = Path.Combine(dir, "test2.txt.TRG");
        await File.WriteAllTextAsync(file1, "data1");
        await File.WriteAllTextAsync(file2, "data2");
        await File.WriteAllTextAsync(endFile1, "end1");
        await File.WriteAllTextAsync(endFile2, "end2");

        var hash1 = await HashUtil.ComputeHashAsync(file1, "SHA256", CancellationToken.None);
        var hash2 = await HashUtil.ComputeHashAsync(file2, "SHA256", CancellationToken.None);
        var endHash1 = await HashUtil.ComputeHashAsync(endFile1, "SHA256", CancellationToken.None);
        var endHash2 = await HashUtil.ComputeHashAsync(endFile2, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".TRG" },
            TransferEndFiles = true,
            AllowedExtensions = new string[0] // 全拡張子許可
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash1);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test2.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash2);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash1);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test2.txt.TRG", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash2);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // 全ファイルが転送される（データファイル2つ + ENDファイル2つ）
        mock.Verify(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(file2, "/remote/test2.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile2, "/remote/test2.txt.TRG", It.IsAny<CancellationToken>()), Times.Once);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void GetDataFileForEndFile_ShouldReturnCorrectDataFileName()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            EndFileExtensions = new[] { ".END", ".TRG" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

        // GetDataFileForEndFile メソッドをテスト
        Assert.Equal("test.txt", worker.TestGetDataFileForEndFile("test.txt.END"));
        Assert.Equal("test.txt", worker.TestGetDataFileForEndFile("test.txt.TRG"));
        Assert.Equal("document.csv", worker.TestGetDataFileForEndFile("document.csv.END"));
        Assert.Equal("", worker.TestGetDataFileForEndFile("invalid.XXX"));
        Assert.Equal("", worker.TestGetDataFileForEndFile(""));
        Assert.Equal("", worker.TestGetDataFileForEndFile(null!));

        Directory.Delete(dir, true);
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r, IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp, ILogger<Worker> l, IHostApplicationLifetime lifetime, IFileTransferClient client)
            : base(w, t, r, h, c, sp, l, lifetime)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combinedCts.Token);
        }

        // GetDataFileForEndFileメソッドをテスト用に公開
        public string TestGetDataFileForEndFile(string endFilePath)
        {
            var method = typeof(Worker).GetMethod("GetDataFileForEndFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)method!.Invoke(this, new object[] { endFilePath })!;
        }
    }

    private class NoDisposeClient : IFileTransferClient
    {
        private readonly IFileTransferClient _inner;
        public NoDisposeClient(IFileTransferClient inner) => _inner = inner;
        public void Dispose() { }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => _inner.UploadAsync(localPath, remotePath, ct);
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => _inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) => _inner.GetRemoteHashAsync(remotePath, algorithm, ct, useServerCommand);
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) => _inner.ListFilesAsync(remotePath, ct, includeSubdirectories);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => _inner.DeleteAsync(remotePath, ct);
    }

    private class DummyLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stoppingTokenSource = new();
        private readonly CancellationTokenSource _stoppedTokenSource = new();
        
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingTokenSource.Token;
        public CancellationToken ApplicationStopped => _stoppedTokenSource.Token;
        
        public void StopApplication() 
        {
            _stoppingTokenSource.Cancel();
            _stoppedTokenSource.Cancel();
        }

        public void Dispose()
        {
            _stoppingTokenSource?.Dispose();
            _stoppedTokenSource?.Dispose();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesCorrectEndFileNaming_DataFileDotTxtDotEND()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var dataFile = Path.Combine(dir, "data1.txt");
        var endFile = Path.Combine(dir, "data1.txt.END");
        await File.WriteAllTextAsync(dataFile, "test data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(dataFile, "SHA256", CancellationToken.None);
        var endFileHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(dataFile, "/remote/data1.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.UploadAsync(endFile, "/remote/data1.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endFileHash);

        var services = new ServiceCollection()
            .AddSingleton(watch)
            .AddSingleton(transfer)
            .AddSingleton(retry)
            .AddSingleton(hash)
            .AddSingleton(cleanup)
            .AddSingleton<ILogger<Worker>>(new Mock<ILogger<Worker>>().Object)
            .AddSingleton<ILogger<TransferQueue>>(new Mock<ILogger<TransferQueue>>().Object)
            .BuildServiceProvider();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, services, new Mock<ILogger<Worker>>().Object, lifetime, new NoDisposeClient(mock.Object));

        await worker.RunAsync(CancellationToken.None);

        // data1.txtが転送されることを確認
        mock.Verify(c => c.UploadAsync(dataFile, "/remote/data1.txt", It.IsAny<CancellationToken>()), Times.Once);
        // data1.txt.ENDも転送されることを確認
        mock.Verify(c => c.UploadAsync(endFile, "/remote/data1.txt.END", It.IsAny<CancellationToken>()), Times.Once);
        
        // ENDファイルが削除されることを確認
        Assert.False(File.Exists(endFile), "END file should be deleted after successful transfer");

        try { Directory.Delete(dir, true); } catch { }
    }
}