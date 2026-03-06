using System.Diagnostics;
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
/// Performance tests for END file behavior.
/// </summary>
public class EndFilePerformanceTests
{
    [Fact]
    public async Task EndFileCheck_WithManyFiles_ShouldCompleteReasonablyFast()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            // Create 100 data files and END files for half of them.
            const int fileCount = 100;
            var tasks = new List<Task>();

            for (int i = 0; i < fileCount; i++)
            {
                var fileName = $"test{i:D4}.txt";
                var filePath = Path.Combine(dir, fileName);
                tasks.Add(File.WriteAllTextAsync(filePath, $"data{i}"));

                if (i % 2 == 0)
                {
                    var endFilePath = Path.Combine(dir, $"test{i:D4}.txt.END");
                    tasks.Add(File.WriteAllTextAsync(endFilePath, string.Empty));
                }
            }

            await Task.WhenAll(tasks);

            // Build expected hashes by remote filename for mock GetRemoteHashAsync.
            var expectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var localFile in Directory.EnumerateFiles(dir, "*.txt"))
            {
                var hashValue = await HashUtil.ComputeHashAsync(localFile, "SHA256", CancellationToken.None);
                expectedHashes[Path.GetFileName(localFile)] = hashValue;
            }

            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                RequireEndFile = true,
                EndFileExtensions = new[] { ".END", ".end" },
                AllowedExtensions = new[] { ".txt" }
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
            mock.Setup(c => c.GetRemoteHashAsync(It.IsAny<string>(), "SHA256", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync((string remotePath, string _, CancellationToken __, bool ___) =>
                {
                    var remoteName = Path.GetFileName(remotePath);
                    return expectedHashes.TryGetValue(remoteName, out var hashValue) ? hashValue : string.Empty;
                });
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            var stopwatch = Stopwatch.StartNew();
            await worker.RunAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 30000,
                $"Processing took too long: {stopwatch.ElapsedMilliseconds}ms");
            mock.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Exactly(50));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task EndFileCheck_WithManyExtensions_ShouldNotDegrade()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            var file = Path.Combine(dir, "test.txt");
            var endFile = Path.Combine(dir, "test.txt.END");
            await File.WriteAllTextAsync(file, "data");
            await File.WriteAllTextAsync(endFile, string.Empty);

            var expectedHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

            // Configure many END extensions and keep .END as the matching one.
            var extensions = new List<string>();
            for (int i = 0; i < 19; i++)
            {
                extensions.Add($".EXT{i:D2}");
            }
            extensions.Add(".END");

            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                RequireEndFile = true,
                EndFileExtensions = extensions.ToArray(),
                AllowedExtensions = new[] { ".txt" }
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
            mock.Setup(c => c.GetRemoteHashAsync(It.IsAny<string>(), "SHA256", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(expectedHash);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            var stopwatch = Stopwatch.StartNew();
            await worker.RunAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 30000,
                $"Processing with many extensions took too long: {stopwatch.ElapsedMilliseconds}ms");
            mock.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
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
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
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
            _stoppingTokenSource.Dispose();
            _stoppedTokenSource.Dispose();
        }
    }
}
