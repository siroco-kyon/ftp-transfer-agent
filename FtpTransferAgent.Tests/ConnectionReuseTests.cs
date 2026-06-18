using System.Collections.Concurrent;
using System.Net.Sockets;
using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 接続プールにより、ワーカーが接続を使い回す（接続確立コストを削減する）こと、
/// および壊れた接続が破棄されて張り直されることを検証する
/// </summary>
public class ConnectionReuseTests
{
    [Fact]
    public async Task ExecuteAsync_ReusesSingleConnectionAcrossFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            const int fileCount = 6;
            for (int i = 0; i < fileCount; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, $"file{i:D2}.txt"), $"payload-{i}");
            }

            var client = new CountingClient();
            var worker = CreateWorker(dir, concurrency: 1, retryAttempts: 1, () => client, out var createCount);

            await worker.RunAsync(CancellationToken.None);

            // Concurrency=1 なので、6 ファイルでも接続生成は 1 回だけ（以降はプールから再利用）
            Assert.Equal(1, createCount());
            Assert.Equal(fileCount, client.UploadCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_BrokenConnection_IsDiscardedAndRecreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "only.txt"), "payload");

            var successfulUploads = 0;
            var createdCount = 0;
            Func<IFileTransferClient> factory = () =>
            {
                var n = Interlocked.Increment(ref createdCount);
                // 1 本目の接続は最初の操作で接続断を起こす。2 本目以降は正常。
                return new FlakyClient(failOnUpload: n == 1, onSuccess: () => Interlocked.Increment(ref successfulUploads));
            };

            // MaxAttempts=2 なので、接続断 → 破棄 → 再生成 → 成功 まで到達する
            var worker = CreateWorker(dir, concurrency: 1, retryAttempts: 2, factory, out var createCount);

            await worker.RunAsync(CancellationToken.None);

            Assert.Equal(2, createCount());     // 壊れた接続を捨てて 2 本目を生成した
            Assert.Equal(1, successfulUploads); // 2 本目で最終的に成功した
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HighConcurrency_TransfersAllFilesWithoutConcurrentClientUse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            const int fileCount = 50;
            for (int i = 0; i < fileCount; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(dir, $"file{i:D2}.txt"), $"payload-{i}");
            }

            // プールが生成する全クライアントを記録し、いずれも並行使用されないことを確認する
            var createdClients = new ConcurrentBag<ConcurrencyTrackingClient>();
            var violations = 0;
            Func<IFileTransferClient> factory = () =>
            {
                var c = new ConcurrencyTrackingClient(() => Interlocked.Increment(ref violations));
                createdClients.Add(c);
                return c;
            };

            const int concurrency = 8;
            var worker = CreateWorker(dir, concurrency, retryAttempts: 1, factory, out _);

            await worker.RunAsync(CancellationToken.None);

            var totalUploads = createdClients.Sum(c => c.UploadCount);
            Assert.Equal(fileCount, totalUploads); // 全ファイルが確実に転送された
            Assert.Equal(0, violations);           // 同一接続が同時に使われていない
            // 接続生成数は並列数を超えない（接続枯渇・過剰生成が起きない）
            Assert.True(createdClients.Count <= concurrency,
                $"created {createdClients.Count} clients for concurrency {concurrency}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static ReuseWorker CreateWorker(
        string dir,
        int concurrency,
        int retryAttempts,
        Func<IFileTransferClient> factory,
        out Func<int> createCount)
    {
        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            AllowedExtensions = new[] { ".txt" }
        });

        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "primary",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = concurrency
        });

        var retry = Options.Create(new RetryOptions { MaxAttempts = retryAttempts, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var worker = new ReuseWorker(watch, transfer, retry, hash, cleanup, provider, logger, new DummyLifetime(), factory);
        createCount = () => worker.CreateCount;
        return worker;
    }

    private sealed class ReuseWorker : Worker
    {
        private readonly Func<IFileTransferClient> _factory;
        private int _createCount;

        public ReuseWorker(
            IOptions<WatchOptions> watch,
            IOptions<TransferOptions> transfer,
            IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash,
            IOptions<CleanupOptions> cleanup,
            IServiceProvider services,
            ILogger<Worker> logger,
            IHostApplicationLifetime lifetime,
            Func<IFileTransferClient> factory)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
        {
            _factory = factory;
        }

        public int CreateCount => _createCount;

        protected override IFileTransferClient CreateClient()
        {
            Interlocked.Increment(ref _createCount);
            return _factory();
        }

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            Interlocked.Increment(ref _createCount);
            return _factory();
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combinedCts.Token);
        }
    }

    /// <summary>同一インスタンスでアップロード回数を数えるだけのクライアント</summary>
    private sealed class CountingClient : IFileTransferClient
    {
        private int _uploadCount;

        public int UploadCount => _uploadCount;

        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            Interlocked.Increment(ref _uploadCount);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(true);

        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>最初の接続だけ Upload で接続断を起こすクライアント</summary>
    private sealed class FlakyClient : IFileTransferClient
    {
        private readonly bool _failOnUpload;
        private readonly Action _onSuccess;

        public FlakyClient(bool failOnUpload, Action onSuccess)
        {
            _failOnUpload = failOnUpload;
            _onSuccess = onSuccess;
        }

        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            if (_failOnUpload)
            {
                // 接続そのものが切れたことを示す例外（IsConnectionBroken=true）
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            _onSuccess();
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(true);

        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>アップロード中に同一インスタンスが並行使用されたら違反として記録するクライアント</summary>
    private sealed class ConcurrencyTrackingClient : IFileTransferClient
    {
        private readonly Action _onViolation;
        private int _active;
        private int _uploadCount;

        public ConcurrencyTrackingClient(Action onViolation) => _onViolation = onViolation;

        public int UploadCount => _uploadCount;

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                _onViolation();
            }

            try
            {
                await Task.Delay(5, ct); // 並行使用が起き得る時間窓を作る
                Interlocked.Increment(ref _uploadCount);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(true);

        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class DummyLifetime : IHostApplicationLifetime
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
    }
}
