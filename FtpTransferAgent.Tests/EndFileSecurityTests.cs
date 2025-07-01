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
/// ENDファイル機能のセキュリティとエッジケースを検証するテスト
/// </summary>
public class EndFileSecurityTests
{
    [Fact]
    public async Task HasEndFile_WithNullFilePath_ShouldReturnFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" }
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        // nullファイルパスではエラーにならずfalseを返すことを確認
        var hasEndResult = worker.TestHasEndFile(null!);
        Assert.False(hasEndResult);
        
        var isEndResult = worker.TestIsEndFile(null!);
        Assert.False(isEndResult);
        
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HasEndFile_WithEmptyExtensions_ShouldReturnFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        await File.WriteAllTextAsync(file, "data");

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = Array.Empty<string>() // 空の配列
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        var result = worker.TestHasEndFile(file);
        Assert.False(result);
        
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HasEndFile_WithNullExtensions_ShouldReturnFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        await File.WriteAllTextAsync(file, "data");

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = null! // null配列
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        var result = worker.TestHasEndFile(file);
        Assert.False(result);
        
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HasEndFile_WithInvalidPath_ShouldReturnFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" }
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        // 不正なパス文字を含むファイルパス
        var invalidPaths = new[] 
        {
            "file<invalid>.txt",
            "file|invalid.txt",
            "file\"invalid.txt",
            ""
        };

        foreach (var invalidPath in invalidPaths)
        {
            var result = worker.TestHasEndFile(invalidPath);
            Assert.False(result);
        }
        
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HasEndFile_WithNullInExtensions_ShouldSkipNullValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { null!, ".END", "", "   " } // null, 空文字、空白を含む
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        // null値をスキップして有効な拡張子(.END)で検出される
        var result = worker.TestHasEndFile(file);
        Assert.True(result);
        
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task IsEndFile_ShouldIdentifyEndFilesCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions 
        { 
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end", ".TRG", ".trg" }
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        
        // ENDファイルとして認識されるべきファイル
        Assert.True(worker.TestIsEndFile("test.END"));
        Assert.True(worker.TestIsEndFile("test.end"));
        Assert.True(worker.TestIsEndFile("test.TRG"));
        Assert.True(worker.TestIsEndFile("test.trg"));
        Assert.True(worker.TestIsEndFile("/path/to/file.END"));
        
        // ENDファイルとして認識されないファイル
        Assert.False(worker.TestIsEndFile("test.txt"));
        Assert.False(worker.TestIsEndFile("test.csv"));
        Assert.False(worker.TestIsEndFile("test.ENDING")); // 部分マッチではない
        Assert.False(worker.TestIsEndFile("test")); // 拡張子なし
        Assert.False(worker.TestIsEndFile(""));
        Assert.False(worker.TestIsEndFile(null!));
        
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

        // HasEndFileメソッドをテスト用に公開
        public bool TestHasEndFile(string filePath)
        {
            var method = typeof(Worker).GetMethod("HasEndFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)method!.Invoke(this, new object[] { filePath })!;
        }

        // IsEndFileメソッドをテスト用に公開
        public bool TestIsEndFile(string filePath)
        {
            var method = typeof(Worker).GetMethod("IsEndFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)method!.Invoke(this, new object[] { filePath })!;
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
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct) => _inner.ListFilesAsync(remotePath, ct);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => _inner.DeleteAsync(remotePath, ct);
    }

    private class DummyLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}