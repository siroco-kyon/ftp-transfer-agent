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
/// <see cref="Worker"/> の基本的なアップロード処理を検証するテスト
/// </summary>
public class WorkerTests
{
    [Fact]
    public async Task ExecuteAsync_UploadsFileAndDeletesAfterVerification()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sample.txt");
        await File.WriteAllTextAsync(file, "data");
        var localHash = await HashUtil.ComputeHashAsync(file, "MD5", CancellationToken.None);

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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "MD5" });
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = true });

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "MD5", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
        Assert.False(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesFolderStructure()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "sample.txt");
        await File.WriteAllTextAsync(file, "data");
        var hash = await HashUtil.ComputeHashAsync(file, "MD5", CancellationToken.None);

        var watch = Options.Create(new WatchOptions { Path = dir, IncludeSubfolders = true });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1,
            PreserveFolderStructure = true
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var cleanup = Options.Create(new CleanupOptions());

        var expected = "/remote/sub/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, expected, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(expected, "MD5", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, Options.Create(new HashOptions { Algorithm = "MD5" }), cleanup,
            provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
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
