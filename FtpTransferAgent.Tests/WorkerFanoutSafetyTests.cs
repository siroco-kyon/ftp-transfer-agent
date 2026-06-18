using System.Collections.Concurrent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

public class WorkerFanoutSafetyTests
{
    [Fact]
    public async Task ExecuteAsync_FanoutParallelUpload_TransfersEndAfterDataPerDestinationAndDeletesLocalAfterAllSuccess()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            var expectedContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 2; i++)
            {
                var dataName = $"file{i:D2}.txt";
                var dataPath = Path.Combine(dir, dataName);
                var endPath = Path.Combine(dir, dataName + ".END");
                expectedContent[dataName] = $"payload-{i}-{Guid.NewGuid():N}";
                expectedContent[dataName + ".END"] = $"end-{i}";
                await File.WriteAllTextAsync(dataPath, expectedContent[dataName]);
                await File.WriteAllTextAsync(endPath, expectedContent[dataName + ".END"]);
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

            var transferOptions = new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "primary",
                Username = "user",
                Password = "pass",
                RemotePath = "/primary",
                Concurrency = 2,
                AdditionalDestinations = new List<DestinationOptions> { additionalDestination }
            };

            var primaryStore = new DestinationStore();
            var backupStore = new DestinationStore();
            var worker = CreateWorker(
                dir,
                transferOptions,
                additionalDestination,
                primaryStore,
                backupStore,
                new CleanupOptions { DeleteAfterVerify = true },
                new HashOptions { Enabled = true, Algorithm = "SHA256" });

            await worker.RunAsync(CancellationToken.None);

            AssertFanoutStore(primaryStore, "/primary", expectedContent);
            AssertFanoutStore(backupStore, "/backup", expectedContent);

            foreach (var localPath in Directory.EnumerateFiles(dir))
            {
                Assert.False(File.Exists(localPath), $"Local file should have been deleted after every destination succeeded: {localPath}");
            }
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
    public async Task ExecuteAsync_FanoutEndUploadFailure_RetainsLocalDataAndEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        try
        {
            var dataPath = Path.Combine(dir, "sample.txt");
            var endPath = Path.Combine(dir, "sample.txt.END");
            await File.WriteAllTextAsync(dataPath, "payload");
            await File.WriteAllTextAsync(endPath, "end marker");

            var additionalDestination = new DestinationOptions
            {
                Mode = "ftp",
                Host = "backup",
                Username = "backup-user",
                Password = "backup-pass",
                RemotePath = "/backup",
                Concurrency = 1
            };

            var transferOptions = new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "primary",
                Username = "user",
                Password = "pass",
                RemotePath = "/primary",
                Concurrency = 1,
                AdditionalDestinations = new List<DestinationOptions> { additionalDestination }
            };

            var exitCode = new ApplicationExitCode();
            var primaryStore = new DestinationStore();
            var backupStore = new DestinationStore(failEndUploads: true);
            var worker = CreateWorker(
                dir,
                transferOptions,
                additionalDestination,
                primaryStore,
                backupStore,
                new CleanupOptions { DeleteAfterVerify = true },
                new HashOptions { Enabled = false, Algorithm = "SHA256" },
                exitCode);

            await worker.RunAsync(CancellationToken.None);

            Assert.True(File.Exists(dataPath), "Data file must remain when any destination fails.");
            Assert.True(File.Exists(endPath), "END file must remain when any destination fails.");
            Assert.Equal(1, exitCode.Code);

            Assert.True(primaryStore.Contains("/primary/sample.txt"));
            Assert.True(primaryStore.Contains("/primary/sample.txt.END"));
            Assert.True(backupStore.Contains("/backup/sample.txt"));
            Assert.False(backupStore.Contains("/backup/sample.txt.END"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private static RoutingWorker CreateWorker(
        string dir,
        TransferOptions transferOptions,
        DestinationOptions additionalDestination,
        DestinationStore primaryStore,
        DestinationStore backupStore,
        CleanupOptions cleanupOptions,
        HashOptions hashOptions,
        ApplicationExitCode? exitCode = null)
    {
        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            AllowedExtensions = new[] { ".txt" },
            RequireEndFile = true,
            TransferEndFiles = true,
            EndFileExtensions = new[] { ".END" }
        });
        var transfer = Options.Create(transferOptions);
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hash = Options.Create(hashOptions);
        var cleanup = Options.Create(cleanupOptions);

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        return new RoutingWorker(
            watch,
            transfer,
            retry,
            hash,
            cleanup,
            provider,
            logger,
            new DummyLifetime(),
            additionalDestination,
            primaryStore,
            backupStore,
            exitCode);
    }

    private static void AssertFanoutStore(DestinationStore store, string remoteBase, Dictionary<string, string> expectedContent)
    {
        foreach (var (fileName, content) in expectedContent)
        {
            var remotePath = $"{remoteBase}/{fileName}";
            Assert.True(store.TryGet(remotePath, out var bytes), $"Missing remote file: {remotePath}");
            Assert.Equal(content, System.Text.Encoding.UTF8.GetString(bytes!));
        }

        foreach (var dataName in expectedContent.Keys.Where(name => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            var dataPath = $"{remoteBase}/{dataName}";
            var endPath = $"{remoteBase}/{dataName}.END";
            Assert.True(store.UploadIndex(dataPath) < store.UploadIndex(endPath),
                $"END file must be uploaded after its data file for {remoteBase}: {dataName}");
        }
    }

    private sealed class RoutingWorker : Worker
    {
        private readonly TransferOptions _transferOptions;
        private readonly DestinationOptions _additionalDestination;
        private readonly DestinationStore _primaryStore;
        private readonly DestinationStore _backupStore;

        public RoutingWorker(
            IOptions<WatchOptions> watch,
            IOptions<TransferOptions> transfer,
            IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash,
            IOptions<CleanupOptions> cleanup,
            IServiceProvider services,
            ILogger<Worker> logger,
            IHostApplicationLifetime lifetime,
            DestinationOptions additionalDestination,
            DestinationStore primaryStore,
            DestinationStore backupStore,
            ApplicationExitCode? exitCode)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime, exitCode)
        {
            _transferOptions = transfer.Value;
            _additionalDestination = additionalDestination;
            _primaryStore = primaryStore;
            _backupStore = backupStore;
        }

        protected override IFileTransferClient CreateClient() => new RecordingClient(_primaryStore);

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            if (ReferenceEquals(dest, _transferOptions))
            {
                return new RecordingClient(_primaryStore);
            }

            if (ReferenceEquals(dest, _additionalDestination))
            {
                return new RecordingClient(_backupStore);
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

    private sealed class DestinationStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _uploadIndexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _failEndUploads;
        private int _sequence;

        public DestinationStore(bool failEndUploads = false)
        {
            _failEndUploads = failEndUploads;
        }

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            if (_failEndUploads && remotePath.EndsWith(".END", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated END upload failure");
            }

            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            var normalized = Normalize(remotePath);
            _files[normalized] = bytes;
            _uploadIndexes[normalized] = Interlocked.Increment(ref _sequence);
        }

        public bool Contains(string remotePath) => _files.ContainsKey(Normalize(remotePath));

        public bool TryGet(string remotePath, out byte[]? bytes) => _files.TryGetValue(Normalize(remotePath), out bytes);

        public int UploadIndex(string remotePath) => _uploadIndexes[Normalize(remotePath)];

        public void Delete(string remotePath) => _files.TryRemove(Normalize(remotePath), out _);

        private static string Normalize(string path) => path.Replace('\\', '/');
    }

    private sealed class RecordingClient : IFileTransferClient
    {
        private readonly DestinationStore _store;

        public RecordingClient(DestinationStore store)
        {
            _store = store;
        }

        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) =>
            _store.UploadAsync(localPath, remotePath, ct);

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
        {
            if (!_store.TryGet(remotePath, out var bytes) || bytes is null)
            {
                throw new FileNotFoundException(remotePath);
            }

            using var stream = new MemoryStream(bytes, writable: false);
            return await HashUtil.ComputeHashAsync(stream, algorithm, ct);
        }

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) =>
            Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) =>
            Task.FromResult(_store.Contains(remotePath));

        public Task DeleteAsync(string remotePath, CancellationToken ct)
        {
            _store.Delete(remotePath);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
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
