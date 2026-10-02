using System.Collections.Concurrent;
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

public sealed class PersistentRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N"));
    private string Watch => Path.Combine(_root, "watch");
    private string State => Path.Combine(_root, "state");

    public PersistentRecoveryTests() => Directory.CreateDirectory(Watch);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RemoteDeletionFailure_ResumesAcrossWorkersWithoutDownloadingDataAgain(bool transferEnd, bool failEnd, bool verifyHash)
    {
        var remote = new MemoryRemote();
        remote.Files["/remote/report.txt"] = "old payload";
        remote.Files["/remote/report.txt.END"] = "ready";
        remote.FailedDelete = failEnd ? "/remote/report.txt.END" : "/remote/report.txt";
        Assert.Equal(1, await RunAsync(remote, transferEnd, verifyHash));
        Assert.True(remote.Files.ContainsKey(remote.FailedDelete));
        Assert.Single(Directory.GetFiles(State, "*.json", SearchOption.AllDirectories));
        Assert.Equal("old payload", await File.ReadAllTextAsync(Path.Combine(Watch, "report.txt")));

        remote.FailedDelete = null;
        Assert.Equal(0, await RunAsync(remote, transferEnd, verifyHash));
        Assert.Empty(remote.Files);
        Assert.Empty(Directory.GetFiles(State, "*.json", SearchOption.AllDirectories));
        Assert.Equal(1, remote.Downloads["/remote/report.txt"]);
        if (transferEnd)
        {
            Assert.Equal(1, remote.Downloads["/remote/report.txt.END"]);
            Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(Watch, "report.txt.END")));
        }
        Assert.Equal(0, await RunAsync(remote, transferEnd, verifyHash));
        Assert.Equal(1, remote.Downloads["/remote/report.txt"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedRemoteGeneration_IsPreservedAndDownloadedOnFollowingRun(bool transferEnd)
    {
        var remote = new MemoryRemote();
        remote.Files["/remote/report.txt"] = "old payload";
        remote.Files["/remote/report.txt.END"] = "old END";
        remote.FailedDelete = "/remote/report.txt";
        Assert.Equal(1, await RunAsync(remote, transferEnd, verifyHash: true));
        remote.Files["/remote/report.txt"] = "new payload";
        remote.Files["/remote/report.txt.END"] = "new END";
        remote.FailedDelete = null;
        Assert.Equal(0, await RunAsync(remote, transferEnd, verifyHash: true));
        Assert.Equal("new payload", remote.Files["/remote/report.txt"]);
        Assert.Equal("new END", remote.Files["/remote/report.txt.END"]);
        Assert.Equal(0, await RunAsync(remote, transferEnd, verifyHash: true));
        Assert.Empty(remote.Files);
        Assert.Equal("new payload", await File.ReadAllTextAsync(Path.Combine(Watch, "report.txt")));
        if (transferEnd) Assert.Equal("new END", await File.ReadAllTextAsync(Path.Combine(Watch, "report.txt.END")));
    }

    [Fact]
    public void ArchiveInterruptedBeforeMarkerCleanup_RecoversPairAndRemovesOldMarkers()
    {
        var retry = Path.Combine(_root, "retry");
        Directory.CreateDirectory(retry);
        File.WriteAllText(Path.Combine(retry, "report.txt"), "old payload");
        File.WriteAllText(Path.Combine(retry, "report.txt.END"), "old END");
        var archive = new CompletedDeliveryArchive(State, retry);
        Assert.Throws<IOException>(() => archive.Archive("report.txt", Directory.GetFiles(retry),
            _ => throw new IOException("injected stop before clearing markers")));
        Assert.Empty(Directory.GetFiles(retry));
        Assert.Single(Directory.GetFiles(State, "pending.json", SearchOption.AllDirectories));
        // ENDの移動だけが未完了の状態からも、同じ記録で再開できる。
        var archivedEnd = Assert.Single(Directory.GetFiles(State, "report.txt.END", SearchOption.AllDirectories));
        File.Move(archivedEnd, Path.Combine(retry, "report.txt.END"));
        var cleared = new List<string>();
        archive.Recover(cleared.Add);
        Assert.Equal(new[] { "report.txt" }, cleared);
        Assert.Empty(Directory.GetFiles(retry));
        Assert.Empty(Directory.GetFiles(State, "pending.json", SearchOption.AllDirectories));
        var data = Assert.Single(Directory.GetFiles(State, "report.txt", SearchOption.AllDirectories));
        Assert.Equal("old payload", File.ReadAllText(data));
        Assert.Equal("old END", File.ReadAllText(data + ".END"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDefaults_MigrateWithoutOverwritingConflictingFiles(bool conflict)
    {
        var directories = new List<string>();
        try
        {
            foreach (var leaf in new[] { "delivery-state", "delivery-retry" })
            {
                var canonical = leaf == "delivery-state" ? DeliveryStateStore.ResolveStateDirectory(null, Watch)
                    : DeliveryStateStore.ResolveRetryDirectory(null, Watch)!;
                var parent = Path.GetDirectoryName(canonical)!;
                var legacyHash = Assert.Single(LocalPathIdentity.CompatibleHashes(Watch), hash => hash != LocalPathIdentity.WatchHash(Watch));
                var legacy = Path.Combine(parent, legacyHash);
                directories.Add(legacy);
                directories.Add(canonical);
                Directory.CreateDirectory(legacy);
                File.WriteAllText(Path.Combine(legacy, "payload"), "legacy content");
                if (conflict)
                {
                    Directory.CreateDirectory(canonical);
                    File.WriteAllText(Path.Combine(canonical, "payload"), "canonical content");
                }
            }
            if (conflict)
            {
                Assert.Throws<IOException>(() => DeliveryStateStore.MigrateDefaultDirectories(null, null, Watch));
                foreach (var directory in directories)
                    Assert.Equal(directory == directories[0] || directory == directories[2] ? "legacy content" : "canonical content",
                        File.ReadAllText(Path.Combine(directory, "payload")));
            }
            else
            {
                DeliveryStateStore.MigrateDefaultDirectories(null, null, Watch + Path.DirectorySeparatorChar);
                Assert.False(Directory.Exists(directories[0]));
                Assert.False(Directory.Exists(directories[2]));
                Assert.Equal("legacy content", File.ReadAllText(Path.Combine(directories[1], "payload")));
                Assert.Equal("legacy content", File.ReadAllText(Path.Combine(directories[3], "payload")));
            }
        }
        finally
        {
            foreach (var directory in directories) if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyTrailingLock_BlocksCanonicalAndDoesNotLeaveAnExtraLock()
    {
        var hash = LocalPathIdentity.CompatibleHashes(Watch).Single(value => value != LocalPathIdentity.WatchHash(Watch));
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyPath = Path.Combine(baseDir, "FtpTransferAgent", "locks", hash, "ftp-transfer-agent.lock");
        using (ProcessLock.Acquire(legacyPath)) Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(null, Watch));
        using var canonical = ProcessLock.Acquire(null, Watch);
        Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(legacyPath));
    }

    [Fact]
    public void PathIdentity_PreservesRootAndRejectsSiblingDirectory()
    {
        var root = Path.GetPathRoot(Watch)!;
        Assert.Equal(root, LocalPathIdentity.Normalize(root));
        Assert.True(LocalPathIdentity.Contains(root, Watch));
        Assert.False(LocalPathIdentity.Contains(Watch, Watch + "-other"));
        Assert.Equal(LocalPathIdentity.WatchHash(Watch), LocalPathIdentity.WatchHash(Watch + new string(Path.DirectorySeparatorChar, 2)));
    }

    private async Task<int> RunAsync(MemoryRemote remote, bool transferEnd, bool verifyHash)
    {
        var transfer = new TransferOptions { Mode = "ftp", Direction = "get", Host = "test", Username = "test", Password = "test", RemotePath = "/remote", Concurrency = 1, StateDirectory = State };
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var exit = new ApplicationExitCode();
        using var worker = new TestWorker(Options.Create(new WatchOptions { Path = Watch, RequireEndFile = true, TransferEndFiles = transferEnd }),
            Options.Create(transfer), Options.Create(new RetryOptions { MaxAttempts = 0 }), Options.Create(new HashOptions { Enabled = verifyHash }),
            Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }),
            provider, provider.GetRequiredService<ILogger<Worker>>(), Mock.Of<IHostApplicationLifetime>(), exit, remote);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.RunAsync(timeout.Token);
        return exit.Code;
    }

    private sealed class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r, IOptions<HashOptions> h, IOptions<CleanupOptions> c,
            IServiceProvider services, ILogger<Worker> logger, IHostApplicationLifetime lifetime, ApplicationExitCode exit, IFileTransferClient client)
            : base(w, t, r, h, c, services, logger, lifetime, exit) => _client = client;
        protected override IFileTransferClient CreateClient() => _client;
        public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class MemoryRemote : IFileTransferClient
    {
        public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);
        public string? FailedDelete { get; set; }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => throw new NotSupportedException();
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
        {
            Downloads.AddOrUpdate(remotePath, 1, (_, count) => count + 1);
            return File.WriteAllTextAsync(localPath, Files[remotePath], ct);
        }
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) =>
            Task.FromResult(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Files[remotePath]))));
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) => Task.FromResult<IEnumerable<string>>(Files.Keys.ToArray());
        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(Files.ContainsKey(remotePath));
        public Task DeleteAsync(string remotePath, CancellationToken ct)
        {
            if (remotePath == FailedDelete) throw new IOException("injected deletion failure");
            Files.TryRemove(remotePath, out _);
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
