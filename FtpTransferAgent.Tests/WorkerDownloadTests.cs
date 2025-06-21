using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="Worker"/> のダウンロード処理を検証するテスト
/// </summary>
public class WorkerDownloadTests
{
    [Fact]
    public async Task ExecuteAsync_DownloadsFileAndDeletesRemote()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var remoteContent = "data";
        var watch = Options.Create(new WatchOptions { Path = dir });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true });

        var remoteFile = "/remote/sample.txt";
        var localPath = Path.Combine(dir, "sample.txt");
        await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(remoteContent));
        var remoteHash = await HashUtil.ComputeHashAsync(ms, "MD5", CancellationToken.None);

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { remoteFile });
        mock.Setup(c => c.DownloadAsync(remoteFile, localPath, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>(async (_, lp, _) =>
            {
                await File.WriteAllTextAsync(lp, remoteContent);
            })
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remoteFile, "MD5", It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(remoteHash);
        mock.Setup(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
        Assert.True(File.Exists(localPath));
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
        public Task RunAsync(CancellationToken token) => base.ExecuteAsync(token);
    }

    private class NoDisposeClient : IFileTransferClient
    {
        private readonly IFileTransferClient _inner;
        public NoDisposeClient(IFileTransferClient inner) => _inner = inner;
        public void Dispose() { }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => _inner.UploadAsync(localPath, remotePath, ct);
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => _inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = true) => _inner.GetRemoteHashAsync(remotePath, algorithm, ct, useServerCommand);
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
