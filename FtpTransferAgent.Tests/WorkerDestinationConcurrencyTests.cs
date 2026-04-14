using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

public class WorkerDestinationConcurrencyTests
{
    [Fact]
    public async Task ExecuteAsync_UsesAdditionalDestinationSpecificConcurrency()
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

            var additionalDestination = new DestinationOptions
            {
                Mode = "ftp",
                Host = "backup",
                Username = "backup-user",
                Password = "backup-pass",
                RemotePath = "/backup",
                Concurrency = 2
            };

            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                AllowedExtensions = new[] { ".txt" }
            });

            var transferOptions = new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "primary",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                Concurrency = 1,
                AdditionalDestinations = new List<DestinationOptions> { additionalDestination }
            };

            var transfer = Options.Create(transferOptions);
            var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var primaryClient = new TrackingClient();
            var additionalClient = new TrackingClient();

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var worker = new RoutingWorker(
                watch,
                transfer,
                retry,
                hash,
                cleanup,
                provider,
                logger,
                lifetime,
                primaryClient,
                additionalDestination,
                additionalClient);

            await worker.RunAsync(CancellationToken.None);

            Assert.Equal(fileCount, primaryClient.UploadCount);
            Assert.Equal(fileCount, additionalClient.UploadCount);
            Assert.Equal(1, primaryClient.MaxConcurrentUploads);
            Assert.Equal(2, additionalClient.MaxConcurrentUploads);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class RoutingWorker : Worker
    {
        private readonly TransferOptions _transferOptions;
        private readonly IFileTransferClient _primaryClient;
        private readonly DestinationOptions _additionalDestination;
        private readonly IFileTransferClient _additionalClient;

        public RoutingWorker(
            IOptions<WatchOptions> watch,
            IOptions<TransferOptions> transfer,
            IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash,
            IOptions<CleanupOptions> cleanup,
            IServiceProvider services,
            ILogger<Worker> logger,
            IHostApplicationLifetime lifetime,
            IFileTransferClient primaryClient,
            DestinationOptions additionalDestination,
            IFileTransferClient additionalClient)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
        {
            _transferOptions = transfer.Value;
            _primaryClient = primaryClient;
            _additionalDestination = additionalDestination;
            _additionalClient = additionalClient;
        }

        protected override IFileTransferClient CreateClient() => _primaryClient;

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            if (ReferenceEquals(dest, _transferOptions))
            {
                return _primaryClient;
            }

            if (ReferenceEquals(dest, _additionalDestination))
            {
                return _additionalClient;
            }

            throw new InvalidOperationException($"Unexpected destination: {dest.Host}");
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combinedCts.Token);
        }
    }

    private sealed class TrackingClient : IFileTransferClient
    {
        private int _activeUploads;
        private int _maxConcurrentUploads;
        private int _uploadCount;

        public int UploadCount => _uploadCount;
        public int MaxConcurrentUploads => _maxConcurrentUploads;

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            Interlocked.Increment(ref _uploadCount);
            var active = Interlocked.Increment(ref _activeUploads);
            UpdateMax(active);

            try
            {
                await Task.Delay(150, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _activeUploads);
            }
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task DeleteAsync(string remotePath, CancellationToken ct)
            => Task.CompletedTask;

        public void Dispose()
        {
        }

        private void UpdateMax(int active)
        {
            while (true)
            {
                var snapshot = _maxConcurrentUploads;
                if (active <= snapshot)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentUploads, active, snapshot) == snapshot)
                {
                    return;
                }
            }
        }
    }

    private sealed class DummyLifetime : IHostApplicationLifetime, IDisposable
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
