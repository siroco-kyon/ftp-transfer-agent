using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>末尾文字列による END の関連付けを、送信・受信とクリーンアップまで検証する。</summary>
public class EndFileSuffixTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "end-suffix-" + Path.GetRandomFileName());
    private readonly string _watchDirectory;
    private readonly string _destinationDirectory;

    public EndFileSuffixTransferTests()
    {
        _watchDirectory = Path.Combine(_root, "watch");
        _destinationDirectory = Path.Combine(_root, "destination");
        Directory.CreateDirectory(_watchDirectory);
    }

    [Fact]
    public async Task Put_NoEndNamingSetting_TransfersDefaultDotEndAfterData()
    {
        await WriteLocalAsync("batch.csv", "payload");
        await WriteLocalAsync("batch.csv.END", "ready");
        var uploads = new List<string>();
        var watch = new WatchOptions
        {
            Path = _watchDirectory,
            RequireEndFile = true,
            TransferEndFiles = true
        };

        var exitCode = await RunAsync(watch, LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "batch.csv", "batch.csv.END" }, uploads);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, "batch.csv")));
        Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, "batch.csv.END")));
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Theory]
    [InlineData("sales_tokyo_001.csv")]
    [InlineData("sales_end_tokyo_001.csv")]
    [InlineData("batch_001")]
    public async Task Put_LiteralSuffix_TransfersDataBeforeEndAndDeletesBothSources(string dataName)
    {
        await WriteLocalAsync(dataName, "payload");
        await WriteLocalAsync(dataName + "_end", "ready");
        var uploads = new List<string>();
        var watch = SuffixWatch(requireEnd: true, transferEnd: true);

        var exitCode = await RunAsync(watch, LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, dataName)));
        Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, dataName + "_end")));
        Assert.False(File.Exists(Path.Combine(_destinationDirectory, dataName + "._end")));
        Assert.Equal(new[] { dataName, dataName + "_end" }, uploads);
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Fact]
    public async Task Put_SuffixCaseDiffersFromSetting_RecognizesAndPreservesActualEndFileName()
    {
        await WriteLocalAsync("sales_001.csv", "payload");
        await WriteLocalAsync("sales_001.csv_END", "ready");
        var uploads = new List<string>();

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "sales_001.csv", "sales_001.csv_END" }, uploads);
        Assert.Contains("sales_001.csv_END", Directory.GetFiles(_destinationDirectory).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Fact]
    public async Task Put_OnCaseSensitiveFileSystem_OnlyMatchingDataNameUsesAndDeletesEnd()
    {
        Assert.SkipUnless(IsCaseSensitiveFileSystem(_watchDirectory), "Requires a case-sensitive file system.");
        await WriteLocalAsync("sample.csv", "matching data");
        await WriteLocalAsync("Sample.csv", "other data");
        await WriteLocalAsync("sample.csv_end", "ready");
        var uploads = new List<string>();

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "sample.csv", "sample.csv_end" }, uploads);
        Assert.Equal("matching data", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, "sample.csv")));
        Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(_destinationDirectory, "sample.csv_end")));
        Assert.False(File.Exists(Path.Combine(_destinationDirectory, "Sample.csv")));
        Assert.False(File.Exists(Path.Combine(_watchDirectory, "sample.csv")));
        Assert.False(File.Exists(Path.Combine(_watchDirectory, "sample.csv_end")));
        Assert.Equal("other data", await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "Sample.csv")));
    }

    [Fact]
    public async Task Put_EndUploadFails_RetainsBothLocalSources()
    {
        await WriteLocalAsync("batch.csv", "payload");
        await WriteLocalAsync("batch.csv_end", "ready");
        var client = new Mock<IFileTransferClient>();
        client.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        client.Setup(c => c.UploadAsync(It.IsAny<string>(),
                It.Is<string>(path => path.EndsWith("_end", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("END upload failed"));

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, client.Object);

        Assert.NotEqual(0, exitCode.Code);
        client.Verify(c => c.UploadAsync(It.IsAny<string>(),
            It.Is<string>(path => path.EndsWith("_end", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv")));
        Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv_end")));
    }

    [Fact]
    public async Task Put_RequireEnd_SkipsDataWithoutMatchingSuffixAndOrphanEnd()
    {
        await WriteLocalAsync("missing.csv", "pending");
        await WriteLocalAsync("orphan.csv_end", "ready");
        var uploads = new List<string>();

        await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Empty(uploads);
        Assert.True(File.Exists(Path.Combine(_watchDirectory, "missing.csv")));
        Assert.True(File.Exists(Path.Combine(_watchDirectory, "orphan.csv_end")));
    }

    [Fact]
    public async Task Put_ExplicitSuffix_DoesNotImplicitlyAcceptDefaultEndExtension()
    {
        await WriteLocalAsync("batch.csv", "pending");
        await WriteLocalAsync("batch.csv.END", "extension marker");
        var uploads = new List<string>();

        await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = false }, uploads: uploads);

        Assert.Empty(uploads);
    }

    [Fact]
    public async Task Put_MixedSuffixes_TransfersBothMarkersDespiteDataExtensionFilter()
    {
        await WriteLocalAsync("batch.csv", "payload");
        await WriteLocalAsync("batch.csv.END", "dot marker");
        await WriteLocalAsync("batch.csv_end", "underscore marker");
        var watch = SuffixWatch(requireEnd: true, transferEnd: true);
        watch.EndFileSuffixes = new[] { ".END", "_end" };
        watch.AllowedExtensions = new[] { ".csv" };
        var uploads = new List<string>();

        var exitCode = await RunAsync(watch, LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(3, uploads.Count);
        Assert.Equal("batch.csv", uploads[0]);
        Assert.Contains("batch.csv.END", uploads);
        Assert.Contains("batch.csv_end", uploads);
        Assert.Equal(3, Directory.GetFiles(_destinationDirectory).Length);
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Fact]
    public async Task Put_UntransferredSuffixMarker_IsDeletedWhenSkippedEndCleanupIsEnabled()
    {
        await WriteLocalAsync("batch.csv", "payload");
        await WriteLocalAsync("batch.csv_end", "ready");
        var uploads = new List<string>();

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: false), LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = true, DeleteLocalSkippedEndFiles = true }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "batch.csv" }, uploads);
        Assert.Empty(Directory.GetFiles(_watchDirectory));
        Assert.False(File.Exists(Path.Combine(_destinationDirectory, "batch.csv_end")));
    }

    [Fact]
    public async Task Put_Fanout_LiteralSuffixReachesBothDestinationsBeforeLocalCleanup()
    {
        await WriteLocalAsync("sales_end_001.csv", "payload");
        await WriteLocalAsync("sales_end_001.csv_end", "ready");
        var transfer = LocalTransfer();
        var backupDirectory = Path.Combine(_root, "backup");
        transfer.AdditionalDestinations = new List<DestinationOptions>
        {
            new() { Name = "backup", Mode = "local", RemotePath = backupDirectory }
        };

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), transfer,
            new CleanupOptions { DeleteAfterVerify = true });

        Assert.Equal(0, exitCode.Code);
        foreach (var directory in new[] { _destinationDirectory, backupDirectory })
        {
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(directory, "sales_end_001.csv")));
            Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(directory, "sales_end_001.csv_end")));
        }
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Fact]
    public async Task Put_ExtensionWithoutDot_AddsDot()
    {
        await WriteLocalAsync("batch.csv", "payload");
        await WriteLocalAsync("batch.csv.END", "ready");
        var watch = new WatchOptions
        {
            Path = _watchDirectory,
            RequireEndFile = true,
            TransferEndFiles = true,
            EndFileExtensions = new[] { "END" }
        };
        var uploads = new List<string>();

        var exitCode = await RunAsync(watch, LocalTransfer(),
            new CleanupOptions { DeleteAfterVerify = false }, uploads: uploads);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "batch.csv", "batch.csv.END" }, uploads);
        Assert.True(File.Exists(Path.Combine(_destinationDirectory, "batch.csv.END")));
    }

    [Fact]
    public async Task Get_NoEndNamingSetting_DownloadsDefaultDotEndAfterData()
    {
        const string remoteData = "/remote/batch.csv";
        const string remoteEnd = "/remote/batch.csv.END";
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteEnd, remoteData }, downloaded, deleted);
        var watch = new WatchOptions
        {
            Path = _watchDirectory,
            RequireEndFile = true,
            TransferEndFiles = true
        };

        var exitCode = await RunAsync(watch, RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { remoteData, remoteEnd }, downloaded);
        Assert.Equal(new[] { remoteEnd, remoteData }, deleted);
        Assert.Equal(remoteData, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv")));
        Assert.Equal(remoteEnd, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv.END")));
    }

    [Fact]
    public async Task Get_ExplicitExtensionWithoutDot_UsesTrgWithoutImplicitDefaultSuffix()
    {
        const string remoteData = "/remote/batch.csv";
        const string remoteTrg = "/remote/batch.csv.TRG";
        const string remoteDefaultEnd = "/remote/batch.csv.END";
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteDefaultEnd, remoteData, remoteTrg }, downloaded, deleted);
        var watch = new WatchOptions
        {
            Path = _watchDirectory,
            RequireEndFile = true,
            TransferEndFiles = true,
            AllowedExtensions = new[] { ".csv" },
            EndFileExtensions = new[] { "TRG" }
        };

        var exitCode = await RunAsync(watch, RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { remoteData, remoteTrg }, downloaded);
        Assert.Equal(new[] { remoteTrg, remoteData }, deleted);
        Assert.False(File.Exists(Path.Combine(_watchDirectory, "batch.csv.END")));
        Assert.Equal(remoteTrg, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv.TRG")));
    }

    [Theory]
    [InlineData("sales_tokyo_001.csv", "_end")]
    [InlineData("sales_end_tokyo_001.csv", "_END")]
    [InlineData("batch_001", "_end")]
    public async Task Get_LiteralSuffix_DownloadsDataBeforeEndAndDeletesTheirRemoteSources(string dataName, string actualSuffix)
    {
        var remoteData = "/remote/" + dataName;
        var remoteEnd = remoteData + actualSuffix;
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteEnd, remoteData }, downloaded, deleted);

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { remoteData, remoteEnd }, downloaded);
        Assert.Equal(new[] { remoteEnd, remoteData }, deleted);
        Assert.Equal(remoteData, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, dataName)));
        Assert.Equal(remoteEnd, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, dataName + actualSuffix)));
    }

    [Fact]
    public async Task Get_MixedSuffixes_DownloadsDataAndBothMarkersBeforeDeletingRemoteSources()
    {
        const string remoteData = "/remote/batch.csv";
        const string remoteDotEnd = "/remote/batch.csv.END";
        const string remoteUnderscoreEnd = "/remote/batch.csv_end";
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteUnderscoreEnd, remoteData, remoteDotEnd }, downloaded, deleted);
        int? downloadsAtFirstDeletion = null;
        client.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((remote, _) =>
            {
                downloadsAtFirstDeletion ??= downloaded.Count;
                deleted.Add(remote);
            })
            .Returns(Task.CompletedTask);
        var watch = SuffixWatch(requireEnd: true, transferEnd: true);
        watch.EndFileSuffixes = new[] { ".END", "_end" };
        watch.AllowedExtensions = new[] { ".csv" };

        var exitCode = await RunAsync(watch, RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(3, downloaded.Count);
        Assert.Equal(remoteData, downloaded[0]);
        Assert.Contains(remoteDotEnd, downloaded);
        Assert.Contains(remoteUnderscoreEnd, downloaded);
        Assert.Equal(3, downloadsAtFirstDeletion);
        Assert.Equal(3, deleted.Count);
        Assert.Equal(remoteData, deleted[^1]);
        Assert.Contains(remoteDotEnd, deleted);
        Assert.Contains(remoteUnderscoreEnd, deleted);
        foreach (var remote in new[] { remoteData, remoteDotEnd, remoteUnderscoreEnd })
        {
            Assert.Equal(remote, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, Path.GetFileName(remote))));
        }
    }

    [Fact]
    public async Task Get_EndDownloadFails_DoesNotDeleteRemoteDataOrEnd()
    {
        const string remoteData = "/remote/batch.csv";
        const string remoteEnd = "/remote/batch.csv_end";
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteData, remoteEnd }, downloaded, deleted);
        client.Setup(c => c.DownloadAsync(remoteEnd, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("END download failed"));

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.NotEqual(0, exitCode.Code);
        Assert.Contains(remoteData, downloaded);
        client.Verify(c => c.DownloadAsync(remoteEnd, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Empty(deleted);
        Assert.Equal(remoteData, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "batch.csv")));
        Assert.False(File.Exists(Path.Combine(_watchDirectory, "batch.csv_end")));
    }

    [Fact]
    public async Task Get_RequireEnd_SkipsDataWithoutMatchingSuffixAndOrphanEnd()
    {
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { "/remote/missing.csv", "/remote/orphan.csv_end" }, downloaded, deleted);

        await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Empty(downloaded);
        Assert.Empty(deleted);
        Assert.Empty(Directory.GetFiles(_watchDirectory));
    }

    [Fact]
    public async Task Get_UntransferredSuffixMarker_IsDeletedWithRelatedData()
    {
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { "/remote/batch.csv", "/remote/batch.csv_end" }, downloaded, deleted);

        var exitCode = await RunAsync(SuffixWatch(requireEnd: true, transferEnd: false), RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { "/remote/batch.csv" }, downloaded);
        Assert.Equal(new[] { "/remote/batch.csv_end", "/remote/batch.csv" }, deleted);
        Assert.False(File.Exists(Path.Combine(_watchDirectory, "batch.csv_end")));
    }

    [Fact]
    public async Task Get_EndTextInsideName_IsOrdinaryDataWhenNotAtEnd()
    {
        const string remoteData = "/remote/sales_end_001.csv";
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { remoteData }, downloaded, deleted);

        var exitCode = await RunAsync(SuffixWatch(requireEnd: false, transferEnd: false), RemoteTransfer(),
            new CleanupOptions(), client.Object);

        Assert.Equal(0, exitCode.Code);
        Assert.Equal(new[] { remoteData }, downloaded);
        Assert.Equal(remoteData, await File.ReadAllTextAsync(Path.Combine(_watchDirectory, "sales_end_001.csv")));
    }

    [Fact]
    public async Task Get_ExplicitSuffix_DoesNotImplicitlyAcceptDefaultEndExtension()
    {
        var downloaded = new List<string>();
        var deleted = new List<string>();
        var client = RemoteClient(new[] { "/remote/batch.csv", "/remote/batch.csv.END" }, downloaded, deleted);

        await RunAsync(SuffixWatch(requireEnd: true, transferEnd: true), RemoteTransfer(),
            new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true }, client.Object);

        Assert.Empty(downloaded);
        Assert.Empty(deleted);
    }

    private WatchOptions SuffixWatch(bool requireEnd, bool transferEnd) => new()
    {
        Path = _watchDirectory,
        RequireEndFile = requireEnd,
        TransferEndFiles = transferEnd,
        EndFileSuffixes = new[] { "_end" }
    };

    private TransferOptions LocalTransfer() => new()
    {
        Mode = "local",
        Direction = "put",
        RemotePath = _destinationDirectory,
        Concurrency = 1,
        StateDirectory = Path.Combine(_root, "state"),
        RetryDirectory = Path.Combine(_root, "retry")
    };

    private static TransferOptions RemoteTransfer() => new()
    {
        Mode = "ftp",
        Direction = "get",
        Host = "host",
        Username = "user",
        Password = "pass",
        RemotePath = "/remote",
        Concurrency = 1
    };

    private Task WriteLocalAsync(string name, string content) => File.WriteAllTextAsync(Path.Combine(_watchDirectory, name), content);

    private static bool IsCaseSensitiveFileSystem(string directory)
    {
        var fileName = ".case-probe-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        try
        {
            return !File.Exists(Path.Combine(directory, fileName.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Mock<IFileTransferClient> RemoteClient(string[] remoteFiles, List<string> downloaded, List<string> deleted)
    {
        var client = new Mock<IFileTransferClient>();
        client.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false)).ReturnsAsync(remoteFiles);
        client.Setup(c => c.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => remoteFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
        client.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((remote, local, _) =>
            {
                downloaded.Add(remote);
                File.WriteAllText(local, remote);
            })
            .Returns(Task.CompletedTask);
        client.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((remote, _) => deleted.Add(remote))
            .Returns(Task.CompletedTask);
        return client;
    }

    private static async Task<ApplicationExitCode> RunAsync(WatchOptions watch, TransferOptions transfer,
        CleanupOptions cleanup, IFileTransferClient? client = null, List<string>? uploads = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        using var lifetime = new DummyLifetime();
        var exitCode = new ApplicationExitCode();
        var worker = new WorkerHarness(watch, transfer, cleanup, provider, lifetime, exitCode, client, uploads);
        await worker.RunAsync();
        return exitCode;
    }

    private sealed class WorkerHarness : Worker
    {
        private readonly IFileTransferClient? _client;
        private readonly List<string>? _uploads;

        public WorkerHarness(WatchOptions watch, TransferOptions transfer, CleanupOptions cleanup,
            IServiceProvider services, IHostApplicationLifetime lifetime, ApplicationExitCode exitCode,
            IFileTransferClient? client, List<string>? uploads)
            : base(Options.Create(watch), Options.Create(transfer),
                Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }),
                Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" }),
                Options.Create(cleanup), services, services.GetRequiredService<ILogger<Worker>>(), lifetime, exitCode)
        {
            _client = client;
            _uploads = uploads;
        }

        protected override IFileTransferClient CreateClient() => _client ?? base.CreateClient();

        protected override IFileTransferClient CreateClientFor(DestinationOptions destination)
        {
            var client = base.CreateClientFor(destination);
            return _uploads is null ? client : new RecordingUploadClient(client, _uploads);
        }

        public async Task RunAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await base.ExecuteAsync(timeout.Token);
        }
    }

    private sealed class RecordingUploadClient(IFileTransferClient inner, List<string> uploads) : IFileTransferClient
    {
        public async Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
        {
            await inner.UploadAsync(localPath, remotePath, ct);
            uploads.Add(Path.GetFileName(remotePath));
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) => inner.GetRemoteHashAsync(remotePath, algorithm, ct, useServerCommand);
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) => inner.ListFilesAsync(remotePath, ct, includeSubdirectories);
        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => inner.ExistsAsync(remotePath, ct);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => inner.DeleteAsync(remotePath, ct);
        public void Dispose() => inner.Dispose();
    }

    private sealed class DummyLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopping.Token;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
