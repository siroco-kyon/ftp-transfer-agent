using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// Mode=local (ローカル / UNC 宛先) で Worker の put 経路をエンドツーエンドに検証する。
/// 既存ツールが CIFS で行っていた「ローカル → 共有フォルダ書き込み」を、本ツールの
/// アトミック転送・ハッシュ検証・END・複数宛先配信付きで置き換えられることを担保する。
///
/// CreateClient を差し替えず、実際の <see cref="LocalFileTransferClient"/> を生成させて
/// テンポラリディレクトリへ書き込む (ネットワーク不要)。
/// </summary>
public class WorkerLocalDestinationTests : IDisposable
{
    private readonly string _root;
    private readonly string _watchDir;
    private readonly string _destDir;

    public WorkerLocalDestinationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "worker-local-" + Path.GetRandomFileName());
        _watchDir = Path.Combine(_root, "watch");
        _destDir = Path.Combine(_root, "dest");
        Directory.CreateDirectory(_watchDir);
    }

    [Fact]
    public async Task Put_SingleLocalDestination_VerifiesHashAndDeletesSource()
    {
        var src = Path.Combine(_watchDir, "report.csv");
        await File.WriteAllTextAsync(src, "the-payload");

        var transfer = new TransferOptions
        {
            Mode = "local",
            Direction = "put",
            RemotePath = _destDir,   // ローカル / UNC の宛先ディレクトリ
        };

        var exit = new ApplicationExitCode();
        await RunAsync(transfer, hashEnabled: true, deleteAfterVerify: true, exitCode: exit);

        var dest = Path.Combine(_destDir, "report.csv");
        Assert.Equal(0, exit.Code);
        Assert.True(File.Exists(dest));
        Assert.Equal("the-payload", await File.ReadAllTextAsync(dest));
        Assert.False(File.Exists(src));                       // DeleteAfterVerify で元ファイル削除
        Assert.Empty(Directory.GetFiles(_destDir, "*.tmp.*")); // 一時ファイルが残らない
    }

    [Fact]
    public async Task Put_PreservesFolderStructure_UnderDestination()
    {
        var sub = Path.Combine(_watchDir, "2026", "06");
        Directory.CreateDirectory(sub);
        var src = Path.Combine(sub, "data.csv");
        await File.WriteAllTextAsync(src, "nested");

        var transfer = new TransferOptions
        {
            Mode = "local",
            Direction = "put",
            RemotePath = _destDir,
            PreserveFolderStructure = true,
        };

        await RunAsync(transfer, hashEnabled: true, deleteAfterVerify: false, includeSubfolders: true);

        var dest = Path.Combine(_destDir, "2026", "06", "data.csv");
        Assert.True(File.Exists(dest));
        Assert.Equal("nested", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Put_TransfersEndFile_WhenConfigured()
    {
        var data = Path.Combine(_watchDir, "batch.csv");
        var end = Path.Combine(_watchDir, "batch.csv.END");
        await File.WriteAllTextAsync(data, "rows");
        await File.WriteAllTextAsync(end, string.Empty);

        var transfer = new TransferOptions
        {
            Mode = "local",
            Direction = "put",
            RemotePath = _destDir,
        };

        await RunAsync(transfer, hashEnabled: true, deleteAfterVerify: false,
            configureWatch: w =>
            {
                w.RequireEndFile = true;
                w.TransferEndFiles = true;
                w.EndFileExtensions = new[] { ".END" };
            });

        Assert.True(File.Exists(Path.Combine(_destDir, "batch.csv")));
        Assert.True(File.Exists(Path.Combine(_destDir, "batch.csv.END")));
    }

    [Fact]
    public async Task Put_Fanout_TwoLocalDestinations_BothReceiveFile()
    {
        var src = Path.Combine(_watchDir, "report.csv");
        await File.WriteAllTextAsync(src, "fanout-payload");

        var destA = Path.Combine(_root, "destA");
        var destB = Path.Combine(_root, "destB");
        var stateDir = Path.Combine(_root, "state");

        var transfer = new TransferOptions
        {
            Name = "primary",
            Mode = "local",
            Direction = "put",
            RemotePath = destA,
            StateDirectory = stateDir,
            RetryDirectory = Path.Combine(_root, "retry"),
            AdditionalDestinations = new List<DestinationOptions>
            {
                new() { Name = "backup", Mode = "local", RemotePath = destB }
            }
        };

        var exit = new ApplicationExitCode();
        await RunAsync(transfer, hashEnabled: true, deleteAfterVerify: true, exitCode: exit);

        Assert.Equal(0, exit.Code);
        Assert.Equal("fanout-payload", await File.ReadAllTextAsync(Path.Combine(destA, "report.csv")));
        Assert.Equal("fanout-payload", await File.ReadAllTextAsync(Path.Combine(destB, "report.csv")));
        Assert.False(File.Exists(src));   // 全宛先成功 → ローカル削除
        // 全宛先成功なのでマーカーは残らない
        Assert.False(Directory.Exists(stateDir) && Directory.GetFiles(stateDir, "*.marker").Length > 0);
    }

    private async Task RunAsync(
        TransferOptions transfer,
        bool hashEnabled,
        bool deleteAfterVerify,
        bool includeSubfolders = false,
        Action<WatchOptions>? configureWatch = null,
        ApplicationExitCode? exitCode = null)
    {
        var watch = new WatchOptions { Path = _watchDir, IncludeSubfolders = includeSubfolders };
        configureWatch?.Invoke(watch);

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var worker = new LocalWorkerHarness(
            Options.Create(watch),
            Options.Create(transfer),
            Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }),
            Options.Create(new HashOptions { Enabled = hashEnabled, Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions { DeleteAfterVerify = deleteAfterVerify }),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            new DummyLifetime(),
            exitCode ?? new ApplicationExitCode());

        await worker.RunAsync(CancellationToken.None);
    }

    // CreateClient を差し替えないため、Mode=local では実際の LocalFileTransferClient が生成される
    private sealed class LocalWorkerHarness : Worker
    {
        public LocalWorkerHarness(
            IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services,
            ILogger<Worker> logger, IHostApplicationLifetime lifetime, ApplicationExitCode exitCode)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime, exitCode)
        {
        }

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combined.Token);
        }
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
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
