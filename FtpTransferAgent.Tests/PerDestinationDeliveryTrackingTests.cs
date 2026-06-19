using System.Collections.Concurrent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 宛先別配信トラッキング (PerDestinationDeliveryTracking) のバッチ跨ぎ挙動を検証する。
/// 1 宛先がメンテ等で落ちた場合、次回バッチでは配信済み宛先へ再送せず未配信先だけに送ることを確認する。
/// </summary>
public class PerDestinationDeliveryTrackingTests : IDisposable
{
    private readonly string _watchDir;
    private readonly string _stateDir;
    private readonly string _retryDir;

    public PerDestinationDeliveryTrackingTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "track-watch-" + Path.GetRandomFileName());
        _stateDir = Path.Combine(Path.GetTempPath(), "track-state-" + Path.GetRandomFileName());
        _retryDir = Path.Combine(_watchDir, "retry");
        Directory.CreateDirectory(_watchDir);
    }

    [Fact]
    public async Task PartialFailureThenRecovery_OnlyResendsToPendingDestination()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        // --- Run 1: backup がメンテで全失敗 ---
        backupStore.FailUploads = true;
        var exit1 = new ApplicationExitCode();
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, exit1);

        var retryPath = Path.Combine(_retryDir, "report.txt");
        Assert.False(File.Exists(dataPath));
        Assert.True(File.Exists(retryPath), "部分失敗時は未完了ファイルを retry ディレクトリへ移動する");
        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        Assert.False(backupStore.Contains("/backup/report.txt"));
        Assert.Equal(1, exit1.Code); // 失敗ありで終了コード 1
        Assert.Single(Directory.GetFiles(_stateDir, "*.marker")); // primary の配信マーカー 1 件

        // --- Run 2: backup 復活 ---
        backupStore.FailUploads = false;
        var exit2 = new ApplicationExitCode();
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, exit2);

        // primary には再送されない (カウントは 1 のまま)
        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        // backup には今回送られる
        Assert.Equal(1, backupStore.UploadCount("/backup/report.txt"));
        Assert.Equal("the-payload", backupStore.GetText("/backup/report.txt"));
        // 全宛先完了 → ローカル削除・マーカー掃除
        Assert.False(File.Exists(dataPath));
        Assert.False(File.Exists(retryPath));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
        Assert.Equal(0, exit2.Code);
    }

    [Fact]
    public async Task Overwrite_AfterPartialFailure_ResendsToAllDestinations()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "v1");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        // Run 1: backup 失敗 → primary に v1 配信、マーカー記録
        backupStore.FailUploads = true;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode());
        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        var retryPath = Path.Combine(_retryDir, "report.txt");
        Assert.True(File.Exists(retryPath));

        // 同名で内容を差し替え (上書き)
        await File.WriteAllTextAsync(retryPath, "v2-completely-different-content");

        // Run 2: backup 復活。指紋が変わったので primary にも再送される
        backupStore.FailUploads = false;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode());

        Assert.Equal(2, primaryStore.UploadCount("/primary/report.txt")); // 再送された
        Assert.Equal("v2-completely-different-content", primaryStore.GetText("/primary/report.txt"));
        Assert.Equal("v2-completely-different-content", backupStore.GetText("/backup/report.txt"));
        Assert.False(File.Exists(dataPath));
        Assert.False(File.Exists(retryPath));
    }

    [Fact]
    public async Task AllSuccessFirstRun_LeavesNoMarkers_AndDeletesLocal()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "payload");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode());

        // 全宛先成功 → マーカーは 1 件も作られず、ローカルは削除される
        Assert.False(File.Exists(dataPath));
        Assert.True(primaryStore.Contains("/primary/report.txt"));
        Assert.True(backupStore.Contains("/backup/report.txt"));
        Assert.False(Directory.Exists(_retryDir) && Directory.GetFiles(_retryDir, "*", SearchOption.AllDirectories).Length > 0);
        Assert.False(Directory.Exists(_stateDir) && Directory.GetFiles(_stateDir, "*.marker").Length > 0);
    }

    [Fact]
    public async Task DeletingRetryFile_RemovesOrphanMarkerAndStopsPendingRetry()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        backupStore.FailUploads = true;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode());

        var retryPath = Path.Combine(_retryDir, "report.txt");
        Assert.True(File.Exists(retryPath));
        Assert.Single(Directory.GetFiles(_stateDir, "*.marker"));

        File.Delete(retryPath);
        backupStore.FailUploads = false;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode());

        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        Assert.False(backupStore.Contains("/backup/report.txt"));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task DefaultRetryDirectory_IsOutsideWatchAndStillRetriesSubfolderFiles()
    {
        var subDir = Path.Combine(_watchDir, "sub");
        Directory.CreateDirectory(subDir);
        var dataPath = Path.Combine(subDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, additional) = BuildOptions();
        transfer.RetryDirectory = null;
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        backupStore.FailUploads = true;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode(), includeSubfolders: true);

        var defaultRetryDir = DeliveryStateStore.ResolveRetryDirectory(null, _watchDir)!;
        var retryPath = Path.Combine(defaultRetryDir, "sub", "report.txt");
        Assert.False(File.Exists(dataPath));
        Assert.True(File.Exists(retryPath));
        Assert.False(Directory.Exists(Path.Combine(_watchDir, "retry")));

        backupStore.FailUploads = false;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode(), includeSubfolders: true);

        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        Assert.Equal(1, backupStore.UploadCount("/backup/report.txt"));
        Assert.False(File.Exists(retryPath));
    }

    [Fact]
    public async Task PartialFailure_WhenRetryEndTargetExists_RetainsWatchFiles()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        var endPath = Path.Combine(_watchDir, "report.txt.END");
        await File.WriteAllTextAsync(dataPath, "the-payload");
        await File.WriteAllTextAsync(endPath, "end");

        Directory.CreateDirectory(_retryDir);
        var retryEndPath = Path.Combine(_retryDir, "report.txt.END");
        await File.WriteAllTextAsync(retryEndPath, "existing");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        backupStore.FailUploads = true;
        var exitCode = new ApplicationExitCode();
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, exitCode);

        Assert.Equal(1, exitCode.Code);
        Assert.True(File.Exists(dataPath));
        Assert.True(File.Exists(endPath));
        Assert.False(File.Exists(Path.Combine(_retryDir, "report.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(retryEndPath));
        Assert.Single(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task PartialFailure_WhenRelatedEndFileDisappears_RetainsWatchDataFile()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        var endPath = Path.Combine(_watchDir, "report.txt.END");
        await File.WriteAllTextAsync(dataPath, "the-payload");
        await File.WriteAllTextAsync(endPath, "end");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore
        {
            AfterUpload = remotePath =>
            {
                if (remotePath.EndsWith("/report.txt", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(endPath))
                {
                    File.Delete(endPath);
                }
            }
        };
        var backupStore = new DestinationStore { FailUploads = true };

        var exitCode = new ApplicationExitCode();
        await RunWorkerAsync(
            transfer,
            additional,
            primaryStore,
            backupStore,
            exitCode,
            requireEndFile: true);

        Assert.Equal(1, exitCode.Code);
        Assert.True(File.Exists(dataPath));
        Assert.False(File.Exists(Path.Combine(_retryDir, "report.txt")));
        Assert.False(File.Exists(Path.Combine(_retryDir, "report.txt.END")));
        Assert.Single(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task HashSignature_UnreadableFile_DoesNotBlockOtherFiles()
    {
        var lockedPath = Path.Combine(_watchDir, "a-locked.txt");
        var readyPath = Path.Combine(_watchDir, "b-ready.txt");
        await File.WriteAllTextAsync(lockedPath, "locked");
        await File.WriteAllTextAsync(readyPath, "ready-payload");

        var (transfer, additional) = BuildOptions();
        transfer.DeliverySignatureMode = "hash";
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();
        await using var lockedStream = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exitCode = new ApplicationExitCode();
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, exitCode);

        Assert.Equal(1, exitCode.Code);
        Assert.False(primaryStore.Contains("/primary/a-locked.txt"));
        Assert.False(backupStore.Contains("/backup/a-locked.txt"));
        Assert.Equal(1, primaryStore.UploadCount("/primary/b-ready.txt"));
        Assert.Equal(1, backupStore.UploadCount("/backup/b-ready.txt"));
        Assert.False(File.Exists(readyPath));
        Assert.True(File.Exists(lockedPath));
    }

    [Fact]
    public async Task DeleteAfterVerifyFalse_RecoveryRestoresFileFromRetryDirectoryToWatch()
    {
        var dataPath = Path.Combine(_watchDir, "report.txt");
        await File.WriteAllTextAsync(dataPath, "the-payload");

        var (transfer, additional) = BuildOptions();
        var primaryStore = new DestinationStore();
        var backupStore = new DestinationStore();

        // --- Run 1: backup 全失敗 → 部分失敗で retry ディレクトリへ退避 (DeleteAfterVerify=false でも退避する) ---
        backupStore.FailUploads = true;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode(), deleteAfterVerify: false);

        var retryPath = Path.Combine(_retryDir, "report.txt");
        Assert.False(File.Exists(dataPath));
        Assert.True(File.Exists(retryPath), "部分失敗時は retry ディレクトリへ退避する");

        // --- Run 2: backup 復活 → 全宛先完了。DeleteAfterVerify=false なので削除せず watch.Path へ復元する ---
        backupStore.FailUploads = false;
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode(), deleteAfterVerify: false);

        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt")); // primary へは再送しない
        Assert.Equal(1, backupStore.UploadCount("/backup/report.txt"));   // backup へ今回送信
        // ファイルは隠しリトライディレクトリに取り残されず、watch の元の場所へ戻る
        Assert.True(File.Exists(dataPath), "全宛先完了かつ DeleteAfterVerify=false なら watch へ復元される");
        Assert.False(File.Exists(retryPath));
        // 全宛先分のマーカーが揃い、次回は再送されない
        Assert.Equal(2, Directory.GetFiles(_stateDir, "*.marker").Length);

        // --- Run 3: 変更なし → 全宛先配信済みとしてスキップ、再送されない ---
        await RunWorkerAsync(transfer, additional, primaryStore, backupStore, new ApplicationExitCode(), deleteAfterVerify: false);
        Assert.Equal(1, primaryStore.UploadCount("/primary/report.txt"));
        Assert.Equal(1, backupStore.UploadCount("/backup/report.txt"));
        Assert.True(File.Exists(dataPath));
    }

    private (TransferOptions, DestinationOptions) BuildOptions()
    {
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
            Concurrency = 1,
            PerDestinationDeliveryTracking = true,
            StateDirectory = _stateDir,
            RetryDirectory = _retryDir,
            DeliverySignatureMode = "sizetime",
            AdditionalDestinations = new List<DestinationOptions> { additional }
        };

        return (transfer, additional);
    }

    private async Task RunWorkerAsync(
        TransferOptions transfer,
        DestinationOptions additional,
        DestinationStore primaryStore,
        DestinationStore backupStore,
        ApplicationExitCode exitCode,
        bool includeSubfolders = false,
        bool requireEndFile = false,
        bool transferEndFiles = false,
        bool deleteAfterVerify = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var worker = new RoutingWorker(
            Options.Create(new WatchOptions
            {
                Path = _watchDir,
                IncludeSubfolders = includeSubfolders,
                AllowedExtensions = new[] { ".txt" },
                RequireEndFile = requireEndFile,
                TransferEndFiles = transferEndFiles,
                EndFileExtensions = new[] { ".END" }
            }),
            Options.Create(transfer),
            Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 }),
            Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions { DeleteAfterVerify = deleteAfterVerify }),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            new DummyLifetime(),
            transfer,
            additional,
            primaryStore,
            backupStore,
            exitCode);

        await worker.RunAsync(CancellationToken.None);
    }

    private sealed class RoutingWorker : Worker
    {
        private readonly TransferOptions _transferOptions;
        private readonly DestinationOptions _additional;
        private readonly DestinationStore _primaryStore;
        private readonly DestinationStore _backupStore;

        public RoutingWorker(
            IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services,
            ILogger<Worker> logger, IHostApplicationLifetime lifetime,
            TransferOptions transferOptions, DestinationOptions additional,
            DestinationStore primaryStore, DestinationStore backupStore, ApplicationExitCode exitCode)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime, exitCode)
        {
            _transferOptions = transferOptions;
            _additional = additional;
            _primaryStore = primaryStore;
            _backupStore = backupStore;
        }

        protected override IFileTransferClient CreateClient() => new RecordingClient(_primaryStore);

        protected override IFileTransferClient CreateClientFor(DestinationOptions dest)
        {
            if (ReferenceEquals(dest, _transferOptions)) return new RecordingClient(_primaryStore);
            if (ReferenceEquals(dest, _additional)) return new RecordingClient(_backupStore);
            throw new InvalidOperationException($"Unexpected destination: {dest.Host}");
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combined.Token);
        }
    }

    private sealed class DestinationStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _uploadCounts = new(StringComparer.OrdinalIgnoreCase);

        public bool FailUploads { get; set; }
        public Action<string>? AfterUpload { get; set; }

        public void Upload(string remotePath, byte[] bytes)
        {
            if (FailUploads)
            {
                throw new IOException("simulated destination outage");
            }
            var key = Normalize(remotePath);
            _files[key] = bytes;
            _uploadCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
            AfterUpload?.Invoke(key);
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
        var defaultRetryDir = DeliveryStateStore.ResolveRetryDirectory(null, _watchDir);
        try { if (defaultRetryDir is not null && Directory.Exists(defaultRetryDir)) Directory.Delete(defaultRetryDir, true); } catch { /* ignore */ }
        try { if (Directory.Exists(_watchDir)) Directory.Delete(_watchDir, true); } catch { /* ignore */ }
        try { if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, true); } catch { /* ignore */ }
    }
}
