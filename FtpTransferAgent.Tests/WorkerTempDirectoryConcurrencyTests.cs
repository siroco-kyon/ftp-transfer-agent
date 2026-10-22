using System.Security.Cryptography;
using System.Text;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

public class WorkerTempDirectoryConcurrencyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PutFinishesBeforeConcurrentGetWritesTempFile_PreservesDownloadDirectory(bool hashEnabled, bool putUsesExtensions)
    {
        var root = Path.Combine(Path.GetTempPath(), "temp-concurrency-" + Path.GetRandomFileName());
        var watchDirectory = Path.Combine(root, "watch");
        Directory.CreateDirectory(watchDirectory);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var downloadEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDownloadWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? getTask = null;
        Task? putTask = null;
        try
        {
            // 明示した別々のロックパスなら、同じ監視フォルダでも両実行を開始できる。
            using var getLock = ProcessLock.Acquire(Path.Combine(root, "get.lock"), watchDirectory);
            using var putLock = ProcessLock.Acquire(Path.Combine(root, "put.lock"), watchDirectory);
            Assert.NotEqual(getLock.LockFilePath, putLock.LockFilePath);

            const string remotePath = "/remote/incoming.csv";
            const string payload = "concurrent download payload";
            var getClient = new Mock<IFileTransferClient>();
            getClient.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(new[] { remotePath });
            getClient.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            getClient.Setup(c => c.DownloadAsync(remotePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string _, string localPath, CancellationToken token) =>
                {
                    // 一時ファイルの親が用意された後、クライアントが書き始める直前で止める。
                    downloadEntered.TrySetResult(localPath);
                    await allowDownloadWrite.Task.WaitAsync(token);
                    await File.WriteAllTextAsync(localPath, payload, token);
                });

            var putClient = new Mock<IFileTransferClient>();
            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var getExitCode = new ApplicationExitCode();
            var putExitCode = new ApplicationExitCode();
            using var getWorker = new WorkerHarness(CreateWatchOptions(watchDirectory, "get", false), "get", hashEnabled, provider, getClient.Object, getExitCode);
            using var putWorker = new WorkerHarness(CreateWatchOptions(watchDirectory, "put", putUsesExtensions), "put", hashEnabled, provider, putClient.Object, putExitCode);

            getTask = getWorker.RunAsync(stopping.Token);
            var downloadTempPath = await downloadEntered.Task.WaitAsync(stopping.Token);
            var downloadDirectory = Path.GetDirectoryName(downloadTempPath)!;
            Assert.True(Directory.Exists(downloadDirectory));

            // Put は転送対象がなくても終了時の掃除を行う。
            putTask = putWorker.RunAsync(stopping.Token);
            await putTask;
            Assert.Equal(0, putExitCode.Code);
            putClient.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            var downloadDirectoryPreserved = Directory.Exists(downloadDirectory);

            allowDownloadWrite.TrySetResult();
            await getTask;

            Assert.True(downloadDirectoryPreserved,
                $"Put removed the parent directory of a pending Get download (Get exit code: {getExitCode.Code}).");
            Assert.Equal(0, getExitCode.Code);
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(watchDirectory, "incoming.csv"), stopping.Token));
            getClient.Verify(c => c.DownloadAsync(remotePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.False(Directory.Exists(downloadDirectory));
        }
        finally
        {
            allowDownloadWrite.TrySetResult();
            stopping.Cancel();
            try
            {
                await Task.WhenAll(new[] { getTask, putTask }.OfType<Task>());
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static WatchOptions CreateWatchOptions(string watchPath, string direction, bool useExtensions)
    {
        var watch = new WatchOptions
        {
            Path = watchPath,
            AllowedExtensions = direction == "put" ? new[] { ".out" } : new[] { ".csv" }
        };
        if (useExtensions)
        {
            watch.EndFileExtensions = new[] { "END" };
        }
        else
        {
            watch.EndFileSuffixes = new[] { "_end" };
        }
        return watch;
    }

    private sealed class WorkerHarness : Worker
    {
        private readonly IFileTransferClient _client;

        public WorkerHarness(WatchOptions watch, string direction, bool hashEnabled,
            IServiceProvider services, IFileTransferClient client, ApplicationExitCode exitCode)
            : base(Options.Create(watch),
                Options.Create(new TransferOptions
                {
                    Mode = "ftp",
                    Direction = direction,
                    Host = "host",
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/remote",
                    Concurrency = 1
                }),
                Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 }),
                Options.Create(new HashOptions { Enabled = hashEnabled }),
                Options.Create(new CleanupOptions()),
                services, services.GetRequiredService<ILogger<Worker>>(), Mock.Of<IHostApplicationLifetime>(), exitCode)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public Task RunAsync(CancellationToken token) => base.ExecuteAsync(token);
    }
}
