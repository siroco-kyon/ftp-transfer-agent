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
/// ハッシュ検証が失敗した場合の再試行とクリーンアップ挙動を検証するテスト
/// </summary>
public class WorkerFailureTests
{
    [Fact]
    public async Task ExecuteAsync_HashFailure_RetriesAndKeepsFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sample.txt");
        await File.WriteAllTextAsync(file, "data");

        var watch = Options.Create(new WatchOptions { Path = dir });
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 2, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = true });

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ThrowsAsync(new TimeoutException("Network timeout during hash calculation")); // リトライ可能な例外に変更
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider,
            logger, lifetime, new NoDisposeClient(mock.Object));

        // 並列処理改善後は例外が再スローされない
        await worker.RunAsync(CancellationToken.None);

        mock.Verify(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.True(File.Exists(file));

        Directory.Delete(dir, true);
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r,
            IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp,
            ILogger<Worker> l, IHostApplicationLifetime lifetime, IFileTransferClient client)
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
}
