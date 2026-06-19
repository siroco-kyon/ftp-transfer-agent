using System.Collections.Concurrent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 3 宛先以上のファンアウトを検証する。既存の Worker レベルのファンアウトテストは
/// primary + 追加 1 宛先（計 2 宛先）が中心のため、以下を補強する:
///   1. 配信済みが「2 宛先」ある状態で、未配信の 1 宛先だけへ選択的に再送する（N&gt;2 の経路）
///   2. primary 自身（=_transfer）が失敗する非対称ケース
///   3. 多数ファイル × 3 宛先 × 宛先別並列で、各宛先が全ファイルを内容を割らずに受け取る
/// [[parallel-transfer-robustness-priority]] に従い、並行処理の競合・同時使用テストとして追加。
/// </summary>
public class WorkerFanoutMultiDestinationTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchDir;
    private readonly string _stateDir;
    private readonly string _retryDir;

    public WorkerFanoutMultiDestinationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fanout-multi-" + Path.GetRandomFileName());
        _watchDir = Path.Combine(_root, "watch");
        _stateDir = Path.Combine(_root, "state");
        _retryDir = Path.Combine(_root, "retry");
        Directory.CreateDirectory(_watchDir);
    }

    [Fact]
    public async Task ThreeDestinations_OneBackupFails_OnlyPendingDestinationIsResent()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, stores) = BuildThreeDestinations();

        // --- Run 1: backup2 だけ失敗（primary・backup1 は成功）---
        stores["backup2"].FailUploads = true;
        var exit1 = new ApplicationExitCode();
        await RunWorkerAsync(transfer, stores, exit1);

        Assert.Equal(1, exit1.Code);
        Assert.Equal(1, stores["primary"].UploadCount("/primary/report.txt"));
        Assert.Equal(1, stores["backup1"].UploadCount("/backup1/report.txt"));
        Assert.False(stores["backup2"].Contains("/backup2/report.txt"));
        // 配信済み 2 宛先（primary・backup1）のマーカーが残り、ファイルは retry へ退避
        Assert.Equal(2, Directory.GetFiles(_stateDir, "*.marker").Length);
        Assert.True(File.Exists(Path.Combine(_retryDir, "report.txt")));
        Assert.False(File.Exists(dataPath));

        // --- Run 2: backup2 復活 → backup2 だけへ再送、配信済み 2 宛先へは再送しない ---
        stores["backup2"].FailUploads = false;
        var exit2 = new ApplicationExitCode();
        await RunWorkerAsync(transfer, stores, exit2);

        Assert.Equal(0, exit2.Code);
        Assert.Equal(1, stores["primary"].UploadCount("/primary/report.txt"));  // 再送なし
        Assert.Equal(1, stores["backup1"].UploadCount("/backup1/report.txt")); // 再送なし
        Assert.Equal(1, stores["backup2"].UploadCount("/backup2/report.txt")); // 今回送信
        Assert.Equal("the-payload", stores["backup2"].GetText("/backup2/report.txt"));
        // 全宛先完了 → ローカル削除・マーカー掃除
        Assert.False(File.Exists(dataPath));
        Assert.False(File.Exists(Path.Combine(_retryDir, "report.txt")));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task ThreeDestinations_PrimaryFails_OnlyPrimaryIsResent()
    {
        // primary（=_transfer 自身）が失敗する非対称ケース。
        // 追加宛先と同じく Name キーのマーカーで管理され、復旧時に primary だけ再送されること。
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, stores) = BuildThreeDestinations();

        stores["primary"].FailUploads = true;
        await RunWorkerAsync(transfer, stores, new ApplicationExitCode());

        Assert.False(stores["primary"].Contains("/primary/report.txt"));
        Assert.Equal(1, stores["backup1"].UploadCount("/backup1/report.txt"));
        Assert.Equal(1, stores["backup2"].UploadCount("/backup2/report.txt"));
        Assert.Equal(2, Directory.GetFiles(_stateDir, "*.marker").Length); // backup1・backup2

        stores["primary"].FailUploads = false;
        await RunWorkerAsync(transfer, stores, new ApplicationExitCode());

        Assert.Equal(1, stores["primary"].UploadCount("/primary/report.txt"));  // 今回送信
        Assert.Equal(1, stores["backup1"].UploadCount("/backup1/report.txt")); // 再送なし
        Assert.Equal(1, stores["backup2"].UploadCount("/backup2/report.txt")); // 再送なし
        Assert.False(File.Exists(dataPath));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task ThreeDestinations_ManyFilesConcurrent_EachDestinationGetsEveryFileIntact()
    {
        const int fileCount = 24;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < fileCount; i++)
        {
            var name = $"file{i:D2}.txt";
            var content = $"payload-{i}-{Guid.NewGuid():N}";
            expected[name] = content;
            await File.WriteAllTextAsync(Path.Combine(_watchDir, name), content);
        }

        // 宛先ごとに異なる並列数で同時転送し、内容の取り違え・重複が起きないことを確認する
        var (transfer, stores) = BuildThreeDestinations(primaryConcurrency: 4, backup1Concurrency: 3, backup2Concurrency: 2);

        var exit = new ApplicationExitCode();
        await RunWorkerAsync(transfer, stores, exit);

        Assert.Equal(0, exit.Code);
        foreach (var (dest, remoteBase) in new[] { ("primary", "/primary"), ("backup1", "/backup1"), ("backup2", "/backup2") })
        {
            foreach (var (name, content) in expected)
            {
                var remote = $"{remoteBase}/{name}";
                Assert.Equal(1, stores[dest].UploadCount(remote)); // 各ファイルちょうど 1 回（重複なし）
                Assert.Equal(content, stores[dest].GetText(remote)); // 並行でも内容が割れない
            }
        }

        // 全宛先成功 → マーカーは作られず、ローカルは削除される
        Assert.Empty(Directory.GetFiles(_watchDir, "*.txt"));
        Assert.False(Directory.Exists(_stateDir) && Directory.GetFiles(_stateDir, "*.marker").Length > 0);
    }

    private (TransferOptions, Dictionary<string, DestinationStore>) BuildThreeDestinations(
        int primaryConcurrency = 1,
        int backup1Concurrency = 1,
        int backup2Concurrency = 1)
    {
        var backup1 = new DestinationOptions
        {
            Name = "backup1",
            Mode = "ftp",
            Host = "backup1-host",
            Username = "u",
            Password = "p",
            RemotePath = "/backup1",
            Concurrency = backup1Concurrency
        };
        var backup2 = new DestinationOptions
        {
            Name = "backup2",
            Mode = "ftp",
            Host = "backup2-host",
            Username = "u",
            Password = "p",
            RemotePath = "/backup2",
            Concurrency = backup2Concurrency
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
            Concurrency = primaryConcurrency,
            StateDirectory = _stateDir,
            RetryDirectory = _retryDir,
            DeliverySignatureMode = "sizetime",
            AdditionalDestinations = new List<DestinationOptions> { backup1, backup2 }
        };

        var stores = new Dictionary<string, DestinationStore>(StringComparer.Ordinal)
        {
            ["primary"] = new DestinationStore(),
            ["backup1"] = new DestinationStore(),
            ["backup2"] = new DestinationStore()
        };

        return (transfer, stores);
    }

    private async Task RunWorkerAsync(
        TransferOptions transfer,
        Dictionary<string, DestinationStore> stores,
        ApplicationExitCode exitCode)
    {
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
            stores,
            exitCode);

        await worker.RunAsync(CancellationToken.None);
    }

    private sealed class RoutingWorker : Worker
    {
        private readonly TransferOptions _transferOptions;
        private readonly Dictionary<string, DestinationStore> _stores;

        public RoutingWorker(
            IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services,
            ILogger<Worker> logger, IHostApplicationLifetime lifetime,
            TransferOptions transferOptions, Dictionary<string, DestinationStore> stores, ApplicationExitCode exitCode)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime, exitCode)
        {
            _transferOptions = transferOptions;
            _stores = stores;
        }

        protected override IFileTransferClient CreateClient() => new RecordingClient(_stores["primary"]);

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            // 全宛先（primary 含む）に一意な Name を付けているため Name で振り分ける
            if (dest.Name is { } name && _stores.TryGetValue(name, out var store))
            {
                return new RecordingClient(store);
            }
            throw new InvalidOperationException($"Unexpected destination: {dest.Name}/{dest.Host}");
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combined.Token);
        }
    }

    private sealed class DestinationStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _uploadCounts = new(StringComparer.OrdinalIgnoreCase);

        public bool FailUploads { get; set; }

        public void Upload(string remotePath, byte[] bytes)
        {
            if (FailUploads)
            {
                throw new IOException("simulated destination outage");
            }
            var key = Normalize(remotePath);
            _files[key] = bytes;
            _uploadCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        }

        public bool Contains(string remotePath) => _files.ContainsKey(Normalize(remotePath));
        public bool TryGet(string remotePath, out byte[]? bytes) => _files.TryGetValue(Normalize(remotePath), out bytes);
        public string GetText(string remotePath) => System.Text.Encoding.UTF8.GetString(_files[Normalize(remotePath)]);
        public int UploadCount(string remotePath) => _uploadCounts.TryGetValue(Normalize(remotePath), out var c) ? c : 0;
        public void Delete(string remotePath) => _files.TryRemove(Normalize(remotePath), out _);
        private static string Normalize(string path) => path.Replace('\\', '/');
    }

    private sealed class RecordingClient : IFileTransferClient
    {
        private readonly DestinationStore _store;
        public RecordingClient(DestinationStore store) => _store = store;

        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            _store.Upload(remotePath, bytes);
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => throw new NotSupportedException();

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

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(_store.Contains(remotePath));
        public Task DeleteAsync(string remotePath, CancellationToken ct) { _store.Delete(remotePath); return Task.CompletedTask; }
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
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* ignore */ }
    }
}
