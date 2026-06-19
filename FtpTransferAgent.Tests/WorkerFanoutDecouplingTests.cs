using System.Collections.Concurrent;
using System.Diagnostics;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ファンアウト投入が宛先ごとに独立していること (1 宛先の詰まりが他宛先の投入を止めないこと) を検証する。
/// 旧実装 (列挙ループが宛先チャネルへ逐次 await write) では、1 宛先のチャネルが満杯になると
/// 列挙全体がブロックし、健全な宛先への投入まで止まっていた。
/// [[parallel-transfer-robustness-priority]] に従い、並行処理変更の競合・同時使用テストとして追加。
/// </summary>
public class WorkerFanoutDecouplingTests : IDisposable
{
    private readonly string _watchDir;
    private readonly string _stateDir;
    private readonly string _retryDir;

    public WorkerFanoutDecouplingTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "decouple-watch-" + Path.GetRandomFileName());
        _stateDir = Path.Combine(Path.GetTempPath(), "decouple-state-" + Path.GetRandomFileName());
        _retryDir = Path.Combine(Path.GetTempPath(), "decouple-retry-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_watchDir);
    }

    [Fact]
    public async Task SlowDestination_DoesNotBlock_OtherDestinationEnqueue()
    {
        // チャネル容量 (1000) を超える数のファイルを用意し、片方の宛先 (backup) を意図的にブロックする。
        // 宛先別プロデューサが独立していれば、backup が詰まっていても primary は全件完了できる。
        const int fileCount = 1100;
        for (int i = 0; i < fileCount; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_watchDir, $"f{i:D5}.txt"), "x");
        }

        var additional = new DestinationOptions
        {
            Name = "backup",
            Mode = "ftp",
            Host = "backup-host",
            Username = "u",
            Password = "p",
            RemotePath = "/backup",
            Concurrency = 1
        };
        var transfer = new TransferOptions
        {
            Name = "primary",
            Mode = "ftp",
            Direction = "put",
            Host = "primary-host",
            Username = "u",
            Password = "p",
            RemotePath = "/primary",
            Concurrency = 4,
            StateDirectory = _stateDir,
            RetryDirectory = _retryDir,
            AdditionalDestinations = new List<DestinationOptions> { additional }
        };

        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();
        using var backupGate = new ManualResetEventSlim(initialState: false);

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var worker = new RoutingWorker(
            Options.Create(new WatchOptions { Path = _watchDir, AllowedExtensions = new[] { ".txt" } }),
            Options.Create(transfer),
            Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 }),
            Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions { DeleteAfterVerify = true }),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            new DummyLifetime(),
            transfer,
            additional,
            primaryStore,
            backupStore,
            backupGate);

        using var overallTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = Task.Run(() => worker.RunAsync(overallTimeout.Token));

        // backup がブロックされている間に primary が全件完了することを待つ。
        // 宛先が結合していると primary は ~1000 件で頭打ちになり 1100 に到達しない。
        var sw = Stopwatch.StartNew();
        while (primaryStore.Count < fileCount && sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (runTask.IsFaulted)
            {
                await runTask; // 例外を表面化させる
            }
            await Task.Delay(25);
        }

        Assert.True(primaryStore.Count == fileCount,
            $"primary should complete all {fileCount} uploads while backup is blocked (decoupled producers); actual={primaryStore.Count}");
        // backup はまだブロック中なので 1 件も完了していない
        Assert.Equal(0, backupStore.Count);

        // backup を解放 → 残りが流れて全宛先完了する
        backupGate.Set();
        await runTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(fileCount, backupStore.Count);
        // 全宛先完了 → ローカル削除
        Assert.Empty(Directory.GetFiles(_watchDir, "*.txt"));
    }

    private sealed class RoutingWorker : Worker
    {
        private readonly TransferOptions _transferOptions;
        private readonly DestinationOptions _additional;
        private readonly DestinationStore _primaryStore;
        private readonly DestinationStore _backupStore;
        private readonly ManualResetEventSlim _backupGate;

        public RoutingWorker(
            IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services,
            ILogger<Worker> logger, IHostApplicationLifetime lifetime,
            TransferOptions transferOptions, DestinationOptions additional,
            DestinationStore primaryStore, DestinationStore backupStore, ManualResetEventSlim backupGate)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
        {
            _transferOptions = transferOptions;
            _additional = additional;
            _primaryStore = primaryStore;
            _backupStore = backupStore;
            _backupGate = backupGate;
        }

        protected override IFileTransferClient CreateClient() => new RecordingClient(_primaryStore, null);

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            if (ReferenceEquals(dest, _transferOptions)) return new RecordingClient(_primaryStore, null);
            if (ReferenceEquals(dest, _additional)) return new RecordingClient(_backupStore, _backupGate);
            throw new InvalidOperationException($"Unexpected destination: {dest.Host}");
        }

        public async Task RunAsync(CancellationToken token) => await base.ExecuteAsync(token);
    }

    private sealed class DestinationStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public int Count => _files.Count;
        public void Upload(string remotePath, byte[] bytes) => _files[remotePath.Replace('\\', '/')] = bytes;
        public bool Contains(string remotePath) => _files.ContainsKey(remotePath.Replace('\\', '/'));
    }

    private sealed class RecordingClient : IFileTransferClient
    {
        private readonly DestinationStore _store;
        private readonly ManualResetEventSlim? _gate;

        public RecordingClient(DestinationStore store, ManualResetEventSlim? gate)
        {
            _store = store;
            _gate = gate;
        }

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            // ゲートが設定されている宛先は、解放されるまでアップロードを保留する (詰まりの模擬)
            _gate?.Wait(ct);
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            _store.Upload(remotePath, bytes);
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) => throw new NotSupportedException();
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) =>
            Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(_store.Contains(remotePath));
        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class DummyLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() { _stopping.Cancel(); _stopped.Cancel(); }
        public void Dispose() { _stopping.Dispose(); _stopped.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _watchDir, _stateDir, _retryDir })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
