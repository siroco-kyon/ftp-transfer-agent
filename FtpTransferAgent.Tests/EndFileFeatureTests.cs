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
/// ENDファイル機能を検証するテスト
/// </summary>
public class EndFileFeatureTests
{
    [Fact]
    public async Task ExecuteAsync_SkipsFileWithoutEndFile_WhenRequireEndFileEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        await File.WriteAllTextAsync(file, "data");

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end" }
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
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // UploadAsyncが呼ばれないことを確認（ENDファイルがないため）
        mock.Verify(c => c.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesFileWithEndFile_WhenRequireEndFileEnabled()
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
            EndFileExtensions = new[] { ".END", ".end" }
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

        var remotePath = "/remote/test.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // UploadAsyncが呼ばれることを確認（ENDファイルがあるため）
        mock.Verify();

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesFileWithLowercaseEndFile_WhenRequireEndFileEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.end");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end" }
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

        var remotePath = "/remote/test.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // UploadAsyncが呼ばれることを確認（.endファイルがあるため）
        mock.Verify();

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesFileWithCustomEndExtension_WhenRequireEndFileEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.TRG");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".TRG", ".trg" }
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

        var remotePath = "/remote/test.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // UploadAsyncが呼ばれることを確認（.TRGファイルがあるため）
        mock.Verify();

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesAllFiles_WhenRequireEndFileDisabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        await File.WriteAllTextAsync(file, "data");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = false // 無効にする
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

        var remotePath = "/remote/test.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // UploadAsyncが呼ばれることを確認（ENDファイル機能が無効のため、すべてのファイルが処理される）
        mock.Verify();

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesEndFiles_FromTransfer()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.txt");
        var endFile = Path.Combine(dir, "test.txt.END");
        var anotherFile = Path.Combine(dir, "another.txt");
        var anotherEndFile = Path.Combine(dir, "another.txt.TRG");
        await File.WriteAllTextAsync(file, "data");
        await File.WriteAllTextAsync(endFile, "");
        await File.WriteAllTextAsync(anotherFile, "another data");
        await File.WriteAllTextAsync(anotherEndFile, "");
        var localHash = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end", ".TRG", ".trg" },
            AllowedExtensions = new string[0] // 空にして全拡張子を許可
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

        var remotePath = "/remote/test.txt";
        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remotePath, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(localHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // test.txtのみがアップロードされ、ENDファイルはアップロードされないことを確認
        mock.Verify(c => c.UploadAsync(file, remotePath, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(endFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(c => c.UploadAsync(anotherEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesFilesInStableOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        
        // アルファベット順でEND ファイルが先に来るようなファイル名を作成
        var files = new[]
        {
            Path.Combine(dir, "b_data.txt"),
            Path.Combine(dir, "a_data.txt.END"), // ENDファイル（先頭に来る）
            Path.Combine(dir, "c_data.txt"),
            Path.Combine(dir, "b_data.txt.END"), // ENDファイル
            Path.Combine(dir, "a_data.txt")
        };
        
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "data");
        }

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END" },
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
        mock.Setup(c => c.GetRemoteHashAsync(It.IsAny<string>(), "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync("d41d8cd98f00b204e9800998ecf8427e");
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // ENDファイルを持つ.txtファイルのみが転送される（a_data.txt, b_data.txt）
        // ENDファイル自体は転送されない
        mock.Verify(c => c.UploadAsync(It.Is<string>(s => s.EndsWith("a_data.txt")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(It.Is<string>(s => s.EndsWith("b_data.txt")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.UploadAsync(It.Is<string>(s => s.EndsWith("c_data.txt")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // ENDファイルなし
        mock.Verify(c => c.UploadAsync(It.Is<string>(s => s.EndsWith(".END")), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // ENDファイル自体

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_Get_SkipsFileWithoutEndFile_WhenRequireEndFileEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            EndFileExtensions = new[] { ".END", ".end" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var localTestFile = Path.Combine(dir, "test.txt");

        var mock = new Mock<IFileTransferClient>();
        // リモートファイル一覧（ENDファイルなし）
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { "/remote/test.txt" });
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // DownloadAsyncが呼ばれないことを確認（ENDファイルがないため）
        mock.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_Get_ProcessesFileWithEndFile_WhenRequireEndFileEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            AllowedExtensions = new[] { ".txt" },
            EndFileExtensions = new[] { ".END", ".end" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var localTestFile = Path.Combine(dir, "test.txt");
        var testData = "test data";
        
        // メモリストリームを使ってハッシュを事前計算
        string expectedHash;
        {
            await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(testData));
            expectedHash = await HashUtil.ComputeHashAsync(ms, "SHA256", CancellationToken.None);
        }
        
        var mock = new Mock<IFileTransferClient>();
        // リモートファイル一覧（対応するENDファイルあり）
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { "/remote/test.txt", "/remote/test.txt.END" });
        mock.Setup(c => c.DownloadAsync("/remote/test.txt", localTestFile, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) => File.WriteAllText(lp, testData))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(expectedHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // ダウンロードが実行されることを確認
        mock.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_Get_TransfersEndFiles_WhenTransferEndFilesEnabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            TransferEndFiles = true,
            AllowedExtensions = new[] { ".txt" },
            EndFileExtensions = new[] { ".END", ".end" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hash = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var localTestFile = Path.Combine(dir, "test.txt");
        var localEndFile = Path.Combine(dir, "test.txt.END");
        var testData = "test data";
        var endData = "";

        // メモリストリームを使ってハッシュを事前計算
        string testDataHash, endDataHash;
        {
            await using var ms1 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(testData));
            testDataHash = await HashUtil.ComputeHashAsync(ms1, "SHA256", CancellationToken.None);
        }
        {
            await using var ms2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(endData));
            endDataHash = await HashUtil.ComputeHashAsync(ms2, "SHA256", CancellationToken.None);
        }

        var mock = new Mock<IFileTransferClient>();
        // リモートファイル一覧（データファイルとENDファイル両方）
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { "/remote/test.txt", "/remote/test.txt.END" });
        mock.Setup(c => c.DownloadAsync("/remote/test.txt", localTestFile, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) => File.WriteAllText(lp, testData))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.DownloadAsync("/remote/test.txt.END", localEndFile, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) => File.WriteAllText(lp, endData))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(testDataHash);
        mock.Setup(c => c.GetRemoteHashAsync("/remote/test.txt.END", "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(endDataHash);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // 両方のファイルがダウンロードされることを確認
        mock.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_Get_SkipsOrphanEndFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = false,
            TransferEndFiles = true,
            EndFileExtensions = new[] { ".END", ".end" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
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
        // リモートファイル一覧（対応するデータファイルがないENDファイルのみ）
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { "/remote/orphan.txt.END" });
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hash, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // 孤立したENDファイルはダウンロードされないことを確認
        mock.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

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
}