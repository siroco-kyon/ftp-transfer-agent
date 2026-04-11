using System.IO;
using System.Collections.Concurrent;
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
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

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
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = true });

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
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
        var hash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

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
        mock.Setup(c => c.GetRemoteHashAsync(expected, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, Options.Create(new HashOptions { Algorithm = "SHA256" }), cleanup,
            provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelUploads_VerifiesHashPerFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            const int fileCount = 24;
            for (int i = 0; i < fileCount; i++)
            {
                var filePath = Path.Combine(dir, $"file{i:D2}.txt");
                await File.WriteAllTextAsync(filePath, $"payload-{i}-{Guid.NewGuid():N}");
            }

            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
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
                Concurrency = 4,
                PreserveFolderStructure = false
            });

            var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var inMemoryClient = new InMemoryFileTransferClient(async (_, _, _, token) =>
            {
                await Task.Delay(5, token);
            });

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, inMemoryClient);
            await worker.RunAsync(CancellationToken.None);

            Assert.Equal(fileCount, inMemoryClient.UploadCount);
            Assert.Equal(fileCount, inMemoryClient.StoredFileCount);

            foreach (var localFile in Directory.EnumerateFiles(dir, "*.txt"))
            {
                var fileName = Path.GetFileName(localFile);
                var remotePath = $"/remote/{fileName}";
                var localHash = await HashUtil.ComputeHashAsync(localFile, "SHA256", CancellationToken.None);
                var remoteHash = await inMemoryClient.GetRemoteHashAsync(remotePath, "SHA256", CancellationToken.None);
                Assert.Equal(localHash, remoteHash);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ParallelUploads_WithFlattenedDuplicateNames_CanCauseHashMismatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var firstSub = Path.Combine(dir, "a");
        var secondSub = Path.Combine(dir, "b");
        Directory.CreateDirectory(firstSub);
        Directory.CreateDirectory(secondSub);

        try
        {
            var firstFile = Path.Combine(firstSub, "duplicate.txt");
            var secondFile = Path.Combine(secondSub, "duplicate.txt");
            await File.WriteAllTextAsync(firstFile, "first-content");
            await File.WriteAllTextAsync(secondFile, "second-content");

            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                IncludeSubfolders = true,
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
                Concurrency = 2,
                PreserveFolderStructure = false
            });

            var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var secondUploadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inMemoryClient = new InMemoryFileTransferClient(async (sequence, _, _, token) =>
            {
                if (sequence == 1)
                {
                    await secondUploadDone.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                else if (sequence == 2)
                {
                    secondUploadDone.TrySetResult();
                }
            });

            var services = new ServiceCollection();
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var logger = new Mock<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger.Object, lifetime, inMemoryClient);
            await worker.RunAsync(CancellationToken.None);

            logger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Hash mismatch", StringComparison.OrdinalIgnoreCase)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);

            Assert.Equal(2, inMemoryClient.UploadCount);
            Assert.Equal(1, inMemoryClient.StoredFileCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HashDisabled_UploadsWithoutCallingGetRemoteHash()
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
        // GetRemoteHashAsync が呼ばれていないことを確認
        mock.Verify(c => c.GetRemoteHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        Assert.True(File.Exists(file)); // DeleteAfterVerify=false なのでファイルは残る
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_HashDisabled_DeleteAfterVerify_StillDeletesFile()
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = true });

        var remotePath = "/remote/sample.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        Assert.False(File.Exists(file));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_HashDisabled_DownloadsWithoutCallingGetRemoteHash()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

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
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remoteFile = "/remote/data.txt";
        var localPath = Path.Combine(dir, "data.txt");

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { remoteFile });
        mock.Setup(c => c.DownloadAsync(remoteFile, localPath, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) => File.WriteAllText(lp, "content"))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify();
        // GetRemoteHashAsync が呼ばれていないことを確認
        mock.Verify(c => c.GetRemoteHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
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

    private sealed class InMemoryFileTransferClient : IFileTransferClient
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<int, string, string, CancellationToken, Task>? _onUpload;
        private int _uploadSequence;

        public InMemoryFileTransferClient(Func<int, string, string, CancellationToken, Task>? onUpload = null)
        {
            _onUpload = onUpload;
        }

        public int UploadCount => _uploadSequence;
        public int StoredFileCount => _files.Count;

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var sequence = Interlocked.Increment(ref _uploadSequence);
            var normalized = Normalize(remotePath);
            var data = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
            _files[normalized] = data;

            if (_onUpload != null)
            {
                await _onUpload(sequence, localPath, normalized, ct).ConfigureAwait(false);
            }
        }

        public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(remotePath);
            if (!_files.TryGetValue(normalized, out var data))
            {
                throw new FileNotFoundException($"Remote file not found: {normalized}");
            }

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(localPath, data, ct).ConfigureAwait(false);
        }

        public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(remotePath);
            if (!_files.TryGetValue(normalized, out var data))
            {
                throw new FileNotFoundException($"Remote file not found: {normalized}");
            }

            using var stream = new MemoryStream(data, writable: false);
            return await HashUtil.ComputeHashAsync(stream, algorithm, ct).ConfigureAwait(false);
        }

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(remotePath).TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
            {
                normalized = "/";
            }

            var files = _files.Keys
                .Where(path => path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Task.FromResult<IEnumerable<string>>(files);
        }

        public Task DeleteAsync(string remotePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _files.TryRemove(Normalize(remotePath), out _);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        private static string Normalize(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return "/";
            }

            var normalized = remotePath.Replace('\\', '/');
            return normalized.StartsWith("/") ? normalized : "/" + normalized;
        }
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
