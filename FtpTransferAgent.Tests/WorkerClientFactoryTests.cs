using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="Worker"/> のクライアントファクトリが primary 宛先で再帰しないことを検証する
/// </summary>
public class WorkerClientFactoryTests
{
    [Fact]
    public void CreateClient_ShouldCreatePrimaryClientWithoutRecursion()
    {
        using var services = BuildServices();
        var worker = CreateWorkerHarness(services);

        using var client = worker.CreatePrimaryClient();

        Assert.IsType<AsyncFtpClientWrapper>(client);
    }

    [Fact]
    public void CreateClientFor_PrimaryDestination_UsesOverriddenCreateClient()
    {
        using var services = BuildServices();
        var sentinel = new NoOpClient();
        var worker = CreateDelegatingWorker(services, sentinel);

        var client = worker.CreatePrimaryViaDestination();

        Assert.Same(sentinel, client);
    }

    private static ServiceProvider BuildServices()
    {
        return new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
    }

    private static WorkerHarness CreateWorkerHarness(ServiceProvider services)
    {
        var transferOptions = CreateTransferOptions();
        return new WorkerHarness(
            Options.Create(new WatchOptions { Path = Path.GetTempPath() }),
            Options.Create(transferOptions),
            Options.Create(new RetryOptions()),
            Options.Create(new HashOptions { Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions()),
            services,
            services.GetRequiredService<ILogger<Worker>>(),
            Mock.Of<IHostApplicationLifetime>());
    }

    private static DelegatingWorker CreateDelegatingWorker(ServiceProvider services, IFileTransferClient client)
    {
        var transferOptions = CreateTransferOptions();
        return new DelegatingWorker(
            Options.Create(new WatchOptions { Path = Path.GetTempPath() }),
            Options.Create(transferOptions),
            Options.Create(new RetryOptions()),
            Options.Create(new HashOptions { Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions()),
            services,
            services.GetRequiredService<ILogger<Worker>>(),
            Mock.Of<IHostApplicationLifetime>(),
            client);
    }

    private static TransferOptions CreateTransferOptions()
    {
        return new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "localhost",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote"
        };
    }

    private sealed class WorkerHarness : Worker
    {
        public WorkerHarness(
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

        public IFileTransferClient CreatePrimaryClient() => base.CreateClient();
    }

    private sealed class DelegatingWorker : Worker
    {
        private readonly IFileTransferClient _client;
        private readonly TransferOptions _transferOptions;

        public DelegatingWorker(
            IOptions<WatchOptions> watch,
            IOptions<TransferOptions> transfer,
            IOptions<RetryOptions> retry,
            IOptions<HashOptions> hash,
            IOptions<CleanupOptions> cleanup,
            IServiceProvider services,
            ILogger<Worker> logger,
            IHostApplicationLifetime lifetime,
            IFileTransferClient client)
            : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
        {
            _client = client;
            _transferOptions = transfer.Value;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public IFileTransferClient CreatePrimaryViaDestination() => base.CreateClientFor(_transferOptions);
    }

    private sealed class NoOpClient : IFileTransferClient
    {
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => Task.CompletedTask;

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
