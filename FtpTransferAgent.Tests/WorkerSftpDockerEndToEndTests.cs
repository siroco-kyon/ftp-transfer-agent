using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// Worker をエンドツーエンドで実 SFTP サーバ (Docker: atmoz/sftp) に対して動かす統合テスト。
/// 「Worker のオーケストレーション + 実 SFTP クライアント」の結合を put→get で検証する。
/// </summary>
[Collection("docker-sftp")]
public class WorkerSftpDockerEndToEndTests
{
    private readonly DockerSftpFixture _fixture;

    public WorkerSftpDockerEndToEndTests(DockerSftpFixture fixture)
    {
        _fixture = fixture;
    }

    [DockerFact]
    public async Task Worker_PutThenGet_RoundTripsThroughRealSftpServer()
    {
        Assert.True(_fixture.IsAvailable, _fixture.UnavailableReason ?? "Docker fixture is not ready.");

        var tempDir = Path.Combine(Path.GetTempPath(), "WorkerSftpE2E_" + Guid.NewGuid().ToString("N"));
        var putWatch = Path.Combine(tempDir, "put");
        var getWatch = Path.Combine(tempDir, "get");
        Directory.CreateDirectory(putWatch);
        Directory.CreateDirectory(getWatch);
        var remoteBase = $"/upload/e2e-{Guid.NewGuid():N}";
        var content = $"sftp-e2e-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(putWatch, "doc.txt"), content);

            // PUT: Worker + 実 SFTP クライアントでアップロード + ハッシュ検証 + 成功でローカル削除
            var putWorker = CreateWorker(
                putWatch,
                SftpOptions(remoteBase, "put"),
                new CleanupOptions { DeleteAfterVerify = true });
            await putWorker.RunAsync();

            Assert.False(File.Exists(Path.Combine(putWatch, "doc.txt")),
                "local file should be deleted after a verified upload");

            // GET: 別フォルダへダウンロードし、内容が一致する (一時ディレクトリ経由 + アトミック移動)
            var getWorker = CreateWorker(
                getWatch,
                SftpOptions(remoteBase, "get"),
                new CleanupOptions());
            await getWorker.RunAsync();

            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(getWatch, "doc.txt")));
        }
        finally
        {
            await CleanupRemoteAsync(remoteBase);
            TryDeleteDirectory(tempDir);
        }
    }

    private TransferOptions SftpOptions(string remoteBase, string direction) => new()
    {
        Mode = "sftp",
        Direction = direction,
        Host = _fixture.Host,
        Port = _fixture.Port,
        Username = _fixture.Username,
        Password = _fixture.Password,
        RemotePath = remoteBase,
        Concurrency = 1,
        TimeoutSeconds = 30
    };

    private static RealWorker CreateWorker(string watchDir, TransferOptions transfer, CleanupOptions cleanup)
    {
        var watch = Options.Create(new WatchOptions
        {
            Path = watchDir,
            AllowedExtensions = new[] { ".txt" }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        return new RealWorker(
            watch,
            Options.Create(transfer),
            Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }),
            Options.Create(new HashOptions { Enabled = true, Algorithm = "SHA256" }),
            Options.Create(cleanup),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            new DummyLifetime());
    }

    private async Task CleanupRemoteAsync(string remoteBase)
    {
        try
        {
            using var wrapper = new SftpClientWrapper(SftpOptions(remoteBase, "get"), NullLoggerFor());
            var files = await wrapper.ListFilesAsync(remoteBase, CancellationToken.None, includeSubdirectories: true);
            foreach (var file in files)
            {
                try { await wrapper.DeleteAsync(file, CancellationToken.None); } catch { }
            }
        }
        catch
        {
            // ベストエフォート (コンテナは --rm で破棄される)
        }
    }

    private static ILogger<SftpClientWrapper> NullLoggerFor() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SftpClientWrapper>.Instance;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    /// <summary>CreateClient をオーバーライドしない = 実 SFTP クライアントを使う Worker。</summary>
    private sealed class RealWorker : Worker
    {
        public RealWorker(
            IOptions<WatchOptions> watch,
            IOptions<TransferOptions> transfer,
            IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash,
            IOptions<CleanupOptions> cleanup,
            IServiceProvider services,
            ILogger<Worker> logger,
            IHostApplicationLifetime lifetime)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
        {
        }

        public async Task RunAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await ExecuteAsync(cts.Token);
        }
    }

    private sealed class DummyLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }

        public void Dispose()
        {
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
