using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = true });

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "MD5", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("hash error"));
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider,
            logger, new NoDisposeClient(mock.Object));

        await Assert.ThrowsAsync<Exception>(() => worker.RunAsync(CancellationToken.None));

        mock.Verify(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.True(File.Exists(file));

        Directory.Delete(dir, true);
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r,
            IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp,
            ILogger<Worker> l, IFileTransferClient client)
            : base(w, t, r, h, c, sp, l)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public Task RunAsync(CancellationToken token) => base.ExecuteAsync(token);
    }

    private class NoDisposeClient : IFileTransferClient
    {
        private readonly IFileTransferClient _inner;
        public NoDisposeClient(IFileTransferClient inner) => _inner = inner;
        public void Dispose() { }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => _inner.UploadAsync(localPath, remotePath, ct);
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => _inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct) => _inner.GetRemoteHashAsync(remotePath, algorithm, ct);
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct) => _inner.ListFilesAsync(remotePath, ct);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => _inner.DeleteAsync(remotePath, ct);
    }
}
