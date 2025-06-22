using System.IO;
using System.Text;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 改修された転送機能の統合テスト
/// </summary>
public class ReliableIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchDir;
    private readonly string _remoteDir;

    public ReliableIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _watchDir = Path.Combine(_tempDir, "watch");
        _remoteDir = Path.Combine(_tempDir, "remote");

        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_remoteDir);
    }

    [Fact]
    public async Task Worker_ShouldTransferFilesReliably()
    {
        // Arrange
        var testFiles = new[]
        {
            "file1.txt",
            "file2.txt",
            "file3.txt"
        };

        // テストファイルを作成
        foreach (var fileName in testFiles)
        {
            var filePath = Path.Combine(_watchDir, fileName);
            await File.WriteAllTextAsync(filePath, $"Content of {fileName}");
        }

        var watchOptions = Options.Create(new WatchOptions
        {
            Path = _watchDir,
            IncludeSubfolders = false,
            AllowedExtensions = new[] { ".txt" }
        });

        var transferOptions = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "localhost",
            Username = "test",
            Password = "test",
            RemotePath = _remoteDir,
            Concurrency = 2,
            PreserveFolderStructure = false
        });

        var retryOptions = Options.Create(new RetryOptions
        {
            MaxAttempts = 3,
            DelaySeconds = 1
        });

        var hashOptions = Options.Create(new HashOptions
        {
            Algorithm = "MD5",
            UseServerCommand = false
        });

        var cleanupOptions = Options.Create(new CleanupOptions
        {
            DeleteAfterVerify = false,
            DeleteRemoteAfterDownload = false
        });

        var mockLogger = new Mock<ILogger<FtpTransferAgent.Worker>>();
        var mockLifetime = new Mock<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        // モッククライアントを設定
        var mockClient = new Mock<IFileTransferClient>();

        // アップロード処理をシミュレート
        mockClient.Setup(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns((string local, string remote, CancellationToken ct) =>
                  {
                      // ローカルファイルをリモートディレクトリにコピー
                      var remotePath = Path.Combine(_remoteDir, Path.GetFileName(remote));
                      File.Copy(local, remotePath, true);
                      return Task.CompletedTask;
                  });

        // ハッシュ取得処理をシミュレート
        mockClient.Setup(x => x.GetRemoteHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), false))
                  .Returns(async (string path, string algorithm, CancellationToken ct, bool useServer) =>
                  {
                      var remotePath = Path.Combine(_remoteDir, Path.GetFileName(path));
                      return await HashUtil.ComputeHashAsync(remotePath, algorithm, ct);
                  });

        // サービスプロバイダーのモック設定
        mockServiceProvider.Setup(x => x.GetService(typeof(ILogger<TransferQueue>)))
                          .Returns(new Mock<ILogger<TransferQueue>>().Object);

        // Workerをテスト用に拡張したクラスを作成
        var worker = new TestableWorker(
            watchOptions,
            transferOptions,
            retryOptions,
            hashOptions,
            cleanupOptions,
            mockServiceProvider.Object,
            mockLogger.Object,
            mockLifetime.Object,
            mockClient.Object);

        // Act
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 統合テストに十分な時間を設定
        await worker.TestExecuteAsync(cts.Token);

        // Assert
        // すべてのファイルが転送されたことを確認
        foreach (var fileName in testFiles)
        {
            var remotePath = Path.Combine(_remoteDir, fileName);
            Assert.True(File.Exists(remotePath), $"File {fileName} should be transferred");

            // ハッシュが一致することを確認
            var localHash = await HashUtil.ComputeHashAsync(Path.Combine(_watchDir, fileName), "MD5", CancellationToken.None);
            var remoteHash = await HashUtil.ComputeHashAsync(remotePath, "MD5", CancellationToken.None);
            Assert.Equal(localHash, remoteHash);
        }
    }

    [Fact]
    public async Task Worker_ShouldHandleHashMismatchCorrectly()
    {
        // Arrange
        var testFile = Path.Combine(_watchDir, "corrupted.txt");
        await File.WriteAllTextAsync(testFile, "Original content");

        var watchOptions = Options.Create(new WatchOptions { Path = _watchDir });
        var transferOptions = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "localhost",
            Username = "test",
            Password = "test",
            RemotePath = _remoteDir,
            Concurrency = 1
        });
        var retryOptions = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 1 });
        var hashOptions = Options.Create(new HashOptions { Algorithm = "MD5", UseServerCommand = false });
        var cleanupOptions = Options.Create(new CleanupOptions());

        var mockLogger = new Mock<ILogger<FtpTransferAgent.Worker>>();
        var mockLifetime = new Mock<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        var mockClient = new Mock<IFileTransferClient>();

        // アップロード処理（ファイル破損をシミュレート）
        mockClient.Setup(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns((string local, string remote, CancellationToken ct) =>
                  {
                      var remotePath = Path.Combine(_remoteDir, Path.GetFileName(remote));
                      // 破損したファイルを作成
                      File.WriteAllText(remotePath, "Corrupted content");
                      return Task.CompletedTask;
                  });

        // 破損したファイルのハッシュを返す
        mockClient.Setup(x => x.GetRemoteHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), false))
                  .Returns(async (string path, string algorithm, CancellationToken ct, bool useServer) =>
                  {
                      var remotePath = Path.Combine(_remoteDir, Path.GetFileName(path));
                      return await HashUtil.ComputeHashAsync(remotePath, algorithm, ct);
                  });

        mockServiceProvider.Setup(x => x.GetService(typeof(ILogger<TransferQueue>)))
                          .Returns(new Mock<ILogger<TransferQueue>>().Object);

        var worker = new TestableWorker(
            watchOptions, transferOptions, retryOptions, hashOptions, cleanupOptions,
            mockServiceProvider.Object, mockLogger.Object, mockLifetime.Object, mockClient.Object);

        // Act & Assert
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // 統合テストに十分な時間を設定

        // ハッシュミスマッチによる例外が発生することを確認
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await worker.TestExecuteAsync(cts.Token);
        });

        // エラーログが出力されることを確認
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Hash mismatch")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}

/// <summary>
/// テスト用のWorkerクラス（protected メソッドをテスト可能にする）
/// </summary>
public class TestableWorker : FtpTransferAgent.Worker
{
    private readonly IFileTransferClient _testClient;

    public TestableWorker(
        IOptions<WatchOptions> watch,
        IOptions<TransferOptions> transfer,
        IOptions<RetryOptions> retry,
        IOptions<HashOptions> hash,
        IOptions<CleanupOptions> cleanup,
        IServiceProvider services,
        ILogger<FtpTransferAgent.Worker> logger,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime,
        IFileTransferClient testClient)
        : base(watch, transfer, retry, hash, cleanup, services, logger, lifetime)
    {
        _testClient = testClient;
    }

    protected override IFileTransferClient CreateClient()
    {
        return _testClient;
    }

    public async Task TestExecuteAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync(cancellationToken);
    }
}
