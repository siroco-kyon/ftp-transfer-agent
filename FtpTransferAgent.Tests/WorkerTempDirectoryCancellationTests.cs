using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

public class WorkerTempDirectoryCancellationTests
{
    [Theory]
    [InlineData("put")]
    [InlineData("get")]
    public async Task CancelledTransfer_RemovesEmptyTempDirectoryAndKeepsSource(string direction)
    {
        var root = Path.Combine(Path.GetTempPath(), "temp-cancellation-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var dataPath = Path.Combine(root, "data.txt");
            var tempDirectory = Path.Combine(root, ".ftptransferagent-tmp");
            if (direction == "put")
            {
                await File.WriteAllTextAsync(dataPath, "source data");
            }

            using var stopping = new CancellationTokenSource();
            var started = false;
            var client = new Mock<IFileTransferClient>();
            client.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(new[] { "/remote/data.txt" });
            client.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string _, CancellationToken token) =>
                {
                    started = true;
                    Assert.True(Directory.Exists(tempDirectory));
                    stopping.Cancel();
                    return Task.FromCanceled(token);
                });
            client.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string localPath, CancellationToken token) =>
                {
                    started = true;
                    Assert.True(Directory.Exists(tempDirectory));
                    File.WriteAllText(localPath, "partial download");
                    stopping.Cancel();
                    return Task.FromCanceled(token);
                });

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var worker = new WorkerHarness(root, direction, provider, Mock.Of<IHostApplicationLifetime>(), client.Object);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(stopping.Token));

            Assert.True(started);
            Assert.False(Directory.Exists(tempDirectory), "Cancellation must not leave an empty agent temp directory.");
            if (direction == "put")
            {
                Assert.Equal("source data", await File.ReadAllTextAsync(dataPath));
            }
            else
            {
                Assert.False(File.Exists(dataPath));
                client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class WorkerHarness : Worker
    {
        private readonly IFileTransferClient _client;

        public WorkerHarness(string watchPath, string direction, IServiceProvider services,
            IHostApplicationLifetime lifetime, IFileTransferClient client)
            : base(Options.Create(new WatchOptions { Path = watchPath }),
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
                Options.Create(new HashOptions { Enabled = false }),
                Options.Create(new CleanupOptions { DeleteAfterVerify = true, DeleteRemoteAfterDownload = true }),
                services, services.GetRequiredService<ILogger<Worker>>(), lifetime)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public async Task RunAsync(CancellationToken token)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            await base.ExecuteAsync(combined.Token);
        }
    }
}
