using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ENDファイル転送機能を検証するテスト
/// </summary>
public class EndFileTransferTests
{
    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_Disabled_ShouldNotTransferEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false // 無効
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルのみ転送、ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_Enabled_ShouldTransferBothDataAndEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "end marker");
        var dataHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true // 有効
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.UploadAsync(endFile, "/remote/test.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルとENDファイルの両方が転送される
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, "/remote/test.txt.END", It.IsAny<CancellationToken>()), Times.Once);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_OnlyTransfersEndFilesWithDataFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "test1.txt");
        var endFile1 = Path.Combine(dir, "test1.txt.END");
        var endFileOrphan = Path.Combine(dir, "orphan.txt.END"); // 対応するデータファイルなし
        await File.WriteAllTextAsync(file1, "data1");
        await File.WriteAllTextAsync(endFile1, "end1");
        await File.WriteAllTextAsync(endFileOrphan, "orphan");
        var dataHash = await HashUtil.ComputeHashAsync(file1, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile1, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true,
            AllowedExtensions = new string[0] // 全拡張子許可
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // test1.txtとtest1.txt.ENDは転送される
        mock.Verify(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()), Times.Once);

        // orphan.ENDは対応するデータファイルがないため転送されない
        mock.Verify(c => c.UploadAsync(endFileOrphan, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_WithMultipleExtensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "test1.txt");
        var file2 = Path.Combine(dir, "test2.txt");
        var endFile1 = Path.Combine(dir, "test1.txt.END");
        var endFile2 = Path.Combine(dir, "test2.txt.TRG");
        await File.WriteAllTextAsync(file1, "data1");
        await File.WriteAllTextAsync(file2, "data2");
        await File.WriteAllTextAsync(endFile1, "end1");
        await File.WriteAllTextAsync(endFile2, "end2");

        var hash1 = await HashUtil.ComputeHashAsync(file1, "SHA256", CancellationToken.None);
        var hash2 = await HashUtil.ComputeHashAsync(file2, "SHA256", CancellationToken.None);
        var endHash1 = await HashUtil.ComputeHashAsync(endFile1, "SHA256", CancellationToken.None);
        var endHash2 = await HashUtil.ComputeHashAsync(endFile2, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".TRG" },
            TransferEndFiles = true,
            AllowedExtensions = new string[0] // 全拡張子許可
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash1);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test2.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash2);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash1);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test2.txt.TRG", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash2);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // 全ファイルが転送される（データファイル2つ + ENDファイル2つ）
        mock.Verify(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(file2, "/remote/test2.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile1, "/remote/test1.txt.END", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile2, "/remote/test2.txt.TRG", It.IsAny<CancellationToken>()), Times.Once);

        Directory.Delete(dir, true);
    }

    // GetDataFileForEndFile (ローカル版) はプロダクションコードで未使用となり削除されたため、
    // 対応するリフレクションベースのテストも削除した。
    // リモート版 (GetDataFileForEndFileRemote) の動作は get 方向の END 連携テストで検証される。

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_ShouldPreserveOnDiskEndExtensionCasing()
    {
        // ディスク上の END ファイルは小文字 ".end"。設定は大文字 ".END" を先頭に持つが、
        // 転送先は設定値の大小ではなく実ファイルの大小 ".end" を維持しなければならない。
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var dataFile = Path.Combine(dir, "data.txt");
        var endFile = Path.Combine(dir, "data.txt.end"); // 小文字で作成
        await File.WriteAllTextAsync(dataFile, "payload");
        await File.WriteAllTextAsync(endFile, "end marker");
        var dataHash = await HashUtil.ComputeHashAsync(dataFile, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end" }, // 大文字が先頭（既定と同様）
            TransferEndFiles = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        // 転送先は小文字 ".end" のまま（設定の ".END" ではなく実ファイルの大小）
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data.txt.end", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルは通常どおり、END ファイルは小文字 ".end" のまま転送される
        mock.Verify(c => c.UploadAsync(dataFile, "/remote/data.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, "/remote/data.txt.end", It.IsAny<CancellationToken>()), Times.Once);
        // 大文字 ".END" では転送されない（設定値の大小を引きずらない）
        mock.Verify(c => c.UploadAsync(It.IsAny<string>(), "/remote/data.txt.END", It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_ConfigCaseDiffersFromDisk_UploadsThenDeletesConsistently()
    {
        // レビュー指摘の回帰: 設定は大文字 ".END" のみ、ディスク上は小文字 ".end"、RequireEndFile=false。
        // 転送する END と「成功時に削除する END」が一致しないと、未転送のまま END マーカーを失う。
        // 大小を区別する FS (CI の Linux 等) では、修正前は END が転送されず削除だけされていた。
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var dataFile = Path.Combine(dir, "data.txt");
        var endFile = Path.Combine(dir, "data.txt.end"); // 小文字で作成
        await File.WriteAllTextAsync(dataFile, "payload");
        await File.WriteAllTextAsync(endFile, "end marker");
        var dataHash = await HashUtil.ComputeHashAsync(dataFile, "SHA256", CancellationToken.None);
        var endHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = false,
            AllowedExtensions = new[] { ".txt" },
            EndFileExtensions = new[] { ".END" }, // 大文字のみ
            TransferEndFiles = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        // データファイルの保持を検証するため、既定 true の DeleteAfterVerify を明示的に false にする。
        var cleanup = Options.Create(new CleanupOptions { DeleteAfterVerify = false });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(dataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data.txt.end", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // END は実ファイルの大小 ".end" のまま転送される（未転送のまま削除されない）
        mock.Verify(c => c.UploadAsync(endFile, "/remote/data.txt.end", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(It.IsAny<string>(), "/remote/data.txt.END", It.IsAny<CancellationToken>()), Times.Never);
        // 転送成功後はローカル END が削除され、データファイルは残る（転送と削除が一致）
        Assert.False(File.Exists(endFile), "END marker should be deleted only after it was transferred");
        Assert.True(File.Exists(dataFile));

        Directory.Delete(dir, true);
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r, IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp, ILogger<Worker> l, IHostApplicationLifetime lifetime, IFileTransferClient client)
            : base(w, t, r, h, c, sp, l, lifetime)
        {
            _client = client;
        }

        protected override IFileTransferClient CreateClient() => _client;

        public async Task RunAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            await base.ExecuteAsync(combinedCts.Token);
        }
    }

    private class NoDisposeClient : IFileTransferClient
    {
        private readonly IFileTransferClient _inner;
        public NoDisposeClient(IFileTransferClient inner) => _inner = inner;
        public void Dispose() { }
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => _inner.UploadAsync(localPath, remotePath, ct);
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => _inner.DownloadAsync(remotePath, localPath, ct);
        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false) => _inner.GetRemoteHashAsync(remotePath, algorithm, ct, useServerCommand);
        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false) => _inner.ListFilesAsync(remotePath, ct, includeSubdirectories);
        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => _inner.ExistsAsync(remotePath, ct);
        public Task DeleteAsync(string remotePath, CancellationToken ct) => _inner.DeleteAsync(remotePath, ct);
    }

    private class DummyLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stoppingTokenSource = new();
        private readonly CancellationTokenSource _stoppedTokenSource = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stoppingTokenSource.Token;
        public CancellationToken ApplicationStopped => _stoppedTokenSource.Token;

        public void StopApplication()
        {
            _stoppingTokenSource.Cancel();
            _stoppedTokenSource.Cancel();
        }

        public void Dispose()
        {
            _stoppingTokenSource?.Dispose();
            _stoppedTokenSource?.Dispose();
        }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesCorrectEndFileNaming_DataFileDotTxtDotEND()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var dataFile = Path.Combine(dir, "data1.txt");
        var endFile = Path.Combine(dir, "data1.txt.END");
        await File.WriteAllTextAsync(dataFile, "test data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(dataFile, "SHA256", CancellationToken.None);
        var endFileHash = await HashUtil.ComputeHashAsync(endFile, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(dataFile, "/remote/data1.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.UploadAsync(endFile, "/remote/data1.txt.END", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/data1.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endFileHash);

        var services = new ServiceCollection()
            .AddSingleton(watch)
            .AddSingleton(transfer)
            .AddSingleton(retry)
            .AddSingleton(hash)
            .AddSingleton(cleanup)
            .AddSingleton<ILogger<Worker>>(new Mock<ILogger<Worker>>().Object)
            .AddSingleton<ILogger<TransferQueue>>(new Mock<ILogger<TransferQueue>>().Object)
            .BuildServiceProvider();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, services, new Mock<ILogger<Worker>>().Object, lifetime, new NoDisposeClient(mock.Object));

        await worker.RunAsync(CancellationToken.None);

        // data1.txtが転送されることを確認
        mock.Verify(c => c.UploadAsync(dataFile, "/remote/data1.txt", It.IsAny<CancellationToken>()), Times.Once);
        // data1.txt.ENDも転送されることを確認
        mock.Verify(c => c.UploadAsync(endFile, "/remote/data1.txt.END", It.IsAny<CancellationToken>()), Times.Once);

        // ENDファイルが削除されることを確認
        Assert.False(File.Exists(endFile), "END file should be deleted after successful transfer");

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_False_DeleteLocalSkippedEndFiles_True_DeletesEndFile()
    {
        // TransferEndFiles=false かつ DeleteLocalSkippedEndFiles=true のとき、ENDファイルをローカルから削除する
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteLocalSkippedEndFiles = true });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルは転送される
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        // ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(endFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // ENDファイルはローカルから削除される
        Assert.False(File.Exists(endFile), "END file should be deleted when DeleteLocalSkippedEndFiles=true");

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteLocalSkippedEndFiles_True_RetainsEndFileWhenDataTransferFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteLocalSkippedEndFiles = true });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("upload failed"));
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(File.Exists(file), "Data file should remain when upload fails");
        Assert.True(File.Exists(endFile), "END file should remain when the matching data transfer fails");

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_False_DeleteLocalSkippedEndFiles_False_RetainsEndFile()
    {
        // DeleteLocalSkippedEndFiles=false（デフォルト）のとき、ENDファイルはローカルに残る
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteLocalSkippedEndFiles = false });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルは転送される
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        // ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(endFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // ENDファイルはローカルに残る
        Assert.True(File.Exists(endFile), "END file should remain when DeleteLocalSkippedEndFiles=false");

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_False_DeleteLocalSkippedEndFiles_True_DeletesMultipleEndFiles()
    {
        // 複数のENDファイルが全て削除される
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file1 = Path.Combine(dir, "test1.txt");
        var file2 = Path.Combine(dir, "test2.txt");
        var endFile1 = Path.Combine(dir, "test1.txt.END");
        var endFile2 = Path.Combine(dir, "test2.txt.END");
        await File.WriteAllTextAsync(file1, "data1");
        await File.WriteAllTextAsync(file2, "data2");
        await File.WriteAllTextAsync(endFile1, "");
        await File.WriteAllTextAsync(endFile2, "");
        var hash1 = await HashUtil.ComputeHashAsync(file1, "SHA256", CancellationToken.None);
        var hash2 = await HashUtil.ComputeHashAsync(file2, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteLocalSkippedEndFiles = true });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test1.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash1);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test2.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(hash2);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルは転送される
        mock.Verify(c => c.UploadAsync(file1, "/remote/test1.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(file2, "/remote/test2.txt", It.IsAny<CancellationToken>()), Times.Once);
        // ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(endFile1, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(c => c.UploadAsync(endFile2, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // 両方のENDファイルが削除される
        Assert.False(File.Exists(endFile1), "END file 1 should be deleted");
        Assert.False(File.Exists(endFile2), "END file 2 should be deleted");

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_False_DeleteLocalSkippedEndFiles_True_RetainsOrphanEndFiles()
    {
        // RequireEndFile=false でも、対応するデータファイルがない孤立ENDファイルは削除されない
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        var orphanEndFile = Path.Combine(dir, "orphan.txt.END"); // 対応するデータファイルなし
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        await File.WriteAllTextAsync(orphanEndFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = false,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = false,
            AllowedExtensions = new[] { ".txt" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteLocalSkippedEndFiles = true });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // データファイルは転送される
        mock.Verify(c => c.UploadAsync(file, "/remote/test.txt", It.IsAny<CancellationToken>()), Times.Once);
        // ENDファイルは転送されない
        mock.Verify(c => c.UploadAsync(It.Is<string>(s => s.EndsWith(".END")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // 対応ENDファイルだけが削除され、孤立ENDファイルは残る
        Assert.False(File.Exists(endFile), "END file should be deleted");
        Assert.True(File.Exists(orphanEndFile), "Orphan END file should be retained because no data file was transferred for it");

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_IncludeSubfolders_OnlyTransfersEndFileFromSameDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var dataDir = Path.Combine(dir, "a");
        var otherDir = Path.Combine(dir, "b");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(otherDir);

        var dataFile = Path.Combine(dataDir, "result.txt");
        var matchingEndFile = Path.Combine(dataDir, "result.txt.END");
        var unrelatedEndFile = Path.Combine(otherDir, "result.txt.END");
        await File.WriteAllTextAsync(dataFile, "data");
        await File.WriteAllTextAsync(matchingEndFile, "");
        await File.WriteAllTextAsync(unrelatedEndFile, "");

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            IncludeSubfolders = true,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
            TransferEndFiles = true,
            AllowedExtensions = new[] { ".txt" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            PreserveFolderStructure = true,
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify(c => c.UploadAsync(dataFile, "/remote/a/result.txt", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(matchingEndFile, "/remote/a/result.txt.END", It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(unrelatedEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.False(File.Exists(matchingEndFile), "Matching END file should be deleted after successful transfer");
        Assert.True(File.Exists(unrelatedEndFile), "Unrelated END file in another directory should remain");

        try { Directory.Delete(dir, true); } catch { }
    }
}
