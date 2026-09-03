using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="Worker"/> のダウンロード処理を検証するテスト
/// </summary>
public class WorkerDownloadTests
{
    [Fact]
    public async Task ExecuteAsync_DownloadsFileAndDeletesRemote()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var remoteContent = "data";
        var watch = Options.Create(new WatchOptions { Path = dir });
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
        var hashOpt = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true });

        var remoteFile = "/remote/sample.txt";
        var localPath = Path.Combine(dir, "sample.txt");

        // メモリストリームを早期Disposeしないように修正
        string remoteHash;
        {
            await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(remoteContent));
            remoteHash = await HashUtil.ComputeHashAsync(ms, "SHA256", CancellationToken.None);
        }

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new[] { remoteFile });
        mock.Setup(c => c.DownloadAsync(remoteFile, It.Is<string>(p => p.EndsWith(".verify", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) =>
            {
                File.WriteAllText(lp, remoteContent);
            })
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.GetRemoteHashAsync(remoteFile, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(remoteHash);
        mock.Setup(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        // タイムアウトを設定して無限待機を防止
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.RunAsync(cts.Token);

        mock.Verify();
        try
        {
            Assert.True(File.Exists(localPath));
        }
        finally
        {
            // リソースクリーンアップを確実に実行
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenRemoteDeletionFails_MarksFailureAndKeepsDownloadedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var remoteFile = "/remote/sample.txt";
            var watch = Options.Create(new WatchOptions { Path = dir });
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true });

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(new[] { remoteFile });
            mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, path, _) => File.WriteAllText(path, "downloaded"))
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("remote delete failed"));
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(
                watch, transfer, retry, hash, cleanup, provider,
                provider.GetRequiredService<ILogger<Worker>>(), lifetime,
                new NoDisposeClient(mock.Object), exitCode);

            await worker.RunAsync(CancellationToken.None);

            Assert.Equal(1, exitCode.Code);
            Assert.Equal("downloaded", await File.ReadAllTextAsync(Path.Combine(dir, "sample.txt")));
            mock.Verify(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DownloadIntoDirectorySymlink_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var watchDirectory = Path.Combine(root, "watch");
        var outsideDirectory = Path.Combine(root, "outside");
        Directory.CreateDirectory(watchDirectory);
        Directory.CreateDirectory(outsideDirectory);
        var link = Path.Combine(watchDirectory, "linked");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outsideDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // シンボリックリンクを作成できない環境 (Windows で開発者モード/管理者権限が無い等)。
                // 黙って成功扱いにせず、スキップとして可視化する
                Assert.Skip($"Cannot create a directory symbolic link in this environment: {ex.Message}");
                return;
            }

            var remoteFile = "/remote/linked/outside.txt";
            var watch = Options.Create(new WatchOptions { Path = watchDirectory, IncludeSubfolders = true });
            var transfer = Options.Create(new TransferOptions
            {
                Mode = "ftp",
                Direction = "get",
                Host = "host",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                PreserveFolderStructure = true,
                Concurrency = 1
            });
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), true))
                .ReturnsAsync(new[] { remoteFile });
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(
                watch, transfer, retry, hash, cleanup, provider,
                provider.GetRequiredService<ILogger<Worker>>(), lifetime,
                new NoDisposeClient(mock.Object), exitCode);

            await worker.RunAsync(CancellationToken.None);

            Assert.Equal(1, exitCode.Code);
            Assert.False(File.Exists(Path.Combine(outsideDirectory, "outside.txt")));
            mock.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DownloadHashMismatch_DoesNotReplaceExistingLocalFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var remoteFile = "/remote/sample.txt";
        var localPath = Path.Combine(dir, "sample.txt");
        await File.WriteAllTextAsync(localPath, "existing good data");

        var expectedRemoteContent = "expected remote data";
        var corruptedDownloadContent = "corrupted download data";
        string remoteHash;
        {
            await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(expectedRemoteContent));
            remoteHash = await HashUtil.ComputeHashAsync(ms, "SHA256", CancellationToken.None);
        }

        var watch = Options.Create(new WatchOptions { Path = dir });
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true });

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { remoteFile });
        mock.Setup(c => c.GetRemoteHashAsync(remoteFile, "SHA256", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(remoteHash);
        mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) => File.WriteAllText(lp, corruptedDownloadContent))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();
        var exitCode = new ApplicationExitCode();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object), exitCode);
        await worker.RunAsync(CancellationToken.None);

        mock.Verify(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, exitCode.Code);
        Assert.Equal("existing good data", await File.ReadAllTextAsync(localPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir), p => Path.GetFileName(p).Contains(".verify.", StringComparison.Ordinal));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_TransferEndFiles_WithParallelDownloads_WaitsForDataBeforeEndFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            TransferEndFiles = true,
            EndFileExtensions = new[] { ".END" }
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 2
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remoteFile = "/remote/sample.txt";
        var remoteEndFile = "/remote/sample.txt.END";
        var localPath = Path.Combine(dir, "sample.txt");
        var localEndPath = Path.Combine(dir, "sample.txt.END");

        var dataStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseData = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataCompleted = 0;
        var endStartedBeforeDataCompleted = 0;

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { remoteFile, remoteEndFile });
        // 非ハッシュのダウンロードは一時ディレクトリ配下のパスに書き、Worker が最終パスへ移動する。
        // 宛先はリモートパスで識別する (第2引数は一時パスになるため一致条件には使わない)。
        mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string lp, CancellationToken _) =>
            {
                dataStarted.TrySetResult(true);
                await releaseData.Task;
                File.WriteAllText(lp, "data");
                Interlocked.Exchange(ref dataCompleted, 1);
            });
        mock.Setup(c => c.DownloadAsync(remoteEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string lp, CancellationToken _) =>
            {
                if (Volatile.Read(ref dataCompleted) == 0)
                {
                    Interlocked.Exchange(ref endStartedBeforeDataCompleted, 1);
                }
                File.WriteAllText(lp, "end");
                return Task.CompletedTask;
            });
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        var workerTask = worker.RunAsync(CancellationToken.None);

        await dataStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref endStartedBeforeDataCompleted));

        releaseData.TrySetResult(true);
        await workerTask;

        mock.Verify(c => c.DownloadAsync(remoteEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, Volatile.Read(ref endStartedBeforeDataCompleted));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotDeleteRemoteData_WhenRelatedEndFileDownloadFails()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            RequireEndFile = true,
            TransferEndFiles = true,
            EndFileExtensions = new[] { ".END" }
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions
        {
            DeleteRemoteAfterDownload = true,
            DeleteRemoteEndFiles = true
        });

        var remoteFile = "/remote/sample.txt";
        var remoteEndFile = "/remote/sample.txt.END";
        var localPath = Path.Combine(dir, "sample.txt");
        var localEndPath = Path.Combine(dir, "sample.txt.END");

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new[] { remoteFile, remoteEndFile });
        // DeleteRemoteEndFiles 有効時、Worker は END ダウンロード前に存在確認を行う
        mock.Setup(c => c.ExistsAsync(remoteEndFile, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // 非ハッシュのダウンロードは一時パスに書かれ、Worker が最終パスへ移動する。
        mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, lp, _) =>
            {
                File.WriteAllText(lp, "data");
            })
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.DownloadAsync(remoteEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("end download failed"));
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        mock.Verify(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.DownloadAsync(remoteEndFile, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(c => c.DeleteAsync(remoteFile, It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(c => c.DeleteAsync(remoteEndFile, It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(File.Exists(localPath));
        Assert.False(File.Exists(localEndPath));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_DownloadsSubdirectoryFilesWithPreserveFolderStructure()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            IncludeSubfolders = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1,
            PreserveFolderStructure = true
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remoteFiles = new[]
        {
            "/remote/file1.txt",
            "/remote/subdir/file2.txt",
            "/remote/subdir/nested/file3.txt"
        };

        var remoteContent = "test data";
        string remoteHash;
        {
            await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(remoteContent));
            remoteHash = await HashUtil.ComputeHashAsync(ms, "SHA256", CancellationToken.None);
        }

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(remoteFiles);

        foreach (var remoteFile in remoteFiles)
        {
            mock.Setup(c => c.GetRemoteHashAsync(remoteFile, "SHA256", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(remoteHash);
            mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((remote, local, ct) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    File.WriteAllText(local, remoteContent);
                });
        }

        var services = new ServiceCollection()
            .AddSingleton(watch)
            .AddSingleton(transfer)
            .AddSingleton(retry)
            .AddSingleton(hashOpt)
            .AddSingleton(cleanup)
            .AddSingleton<ILogger<Worker>>(new Mock<ILogger<Worker>>().Object)
            .AddSingleton<ILogger<TransferQueue>>(new Mock<ILogger<TransferQueue>>().Object)
            .AddSingleton<IHostApplicationLifetime>(new DummyLifetime())
            .BuildServiceProvider();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, services,
            new Mock<ILogger<Worker>>().Object, lifetime, new NoDisposeClient(mock.Object));

        await worker.RunAsync(CancellationToken.None);

        // 期待されるローカルファイルパスを確認
        var expectedFiles = new[]
        {
            Path.Combine(dir, "file1.txt"),
            Path.Combine(dir, "subdir", "file2.txt"),
            Path.Combine(dir, "subdir", "nested", "file3.txt")
        };

        foreach (var expectedFile in expectedFiles)
        {
            Assert.True(File.Exists(expectedFile), $"Expected file not found: {expectedFile}");
            Assert.Equal(remoteContent, File.ReadAllText(expectedFile));
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_DownloadsSubdirectoryFilesWithoutPreserveFolderStructure()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions
        {
            Path = dir,
            IncludeSubfolders = true
        });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1,
            PreserveFolderStructure = false
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remoteFiles = new[]
        {
            "/remote/file1.txt",
            "/remote/subdir/file2.txt",
            "/remote/subdir/nested/file3.txt"
        };

        var remoteContent = "test data";
        string remoteHash;
        {
            await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(remoteContent));
            remoteHash = await HashUtil.ComputeHashAsync(ms, "SHA256", CancellationToken.None);
        }

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(remoteFiles);

        foreach (var remoteFile in remoteFiles)
        {
            mock.Setup(c => c.GetRemoteHashAsync(remoteFile, "SHA256", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(remoteHash);
            mock.Setup(c => c.DownloadAsync(remoteFile, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((remote, local, ct) =>
                {
                    File.WriteAllText(local, remoteContent);
                });
        }

        var services = new ServiceCollection()
            .AddSingleton(watch)
            .AddSingleton(transfer)
            .AddSingleton(retry)
            .AddSingleton(hashOpt)
            .AddSingleton(cleanup)
            .AddSingleton<ILogger<Worker>>(new Mock<ILogger<Worker>>().Object)
            .AddSingleton<ILogger<TransferQueue>>(new Mock<ILogger<TransferQueue>>().Object)
            .AddSingleton<IHostApplicationLifetime>(new DummyLifetime())
            .BuildServiceProvider();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, services,
            new Mock<ILogger<Worker>>().Object, lifetime, new NoDisposeClient(mock.Object));

        await worker.RunAsync(CancellationToken.None);

        // すべてのファイルがルートディレクトリに保存されることを確認
        var expectedFiles = new[]
        {
            Path.Combine(dir, "file1.txt"),
            Path.Combine(dir, "file2.txt"),
            Path.Combine(dir, "file3.txt")
        };

        foreach (var expectedFile in expectedFiles)
        {
            Assert.True(File.Exists(expectedFile), $"Expected file not found: {expectedFile}");
            Assert.Equal(remoteContent, File.ReadAllText(expectedFile));
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_RemoteFilesDifferingOnlyByCase_DoNotCrashEnumeration()
    {
        // 大文字小文字を区別するリモートサーバは "Sample.txt" と "sample.txt" を同時に返し得る。
        // 大小無視のキー比較だと重複キー例外で列挙全体が失敗する回帰を防ぐ
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions { Path = dir });
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
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        var remoteFiles = new[] { "/remote/Sample.txt", "/remote/sample.txt" };
        var downloaded = new List<string>();

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(remoteFiles);
        mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((remote, local, _) =>
            {
                lock (downloaded) { downloaded.Add(remote); }
                File.WriteAllText(local, "data");
            })
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // 列挙は重複キー例外を起こさない (この回帰防止が本テストの元の目的)
        if (CaseInsensitiveFileSystem(dir))
        {
            // 大小を区別しないファイルシステムでは両者が同一のローカルパスへ着地する。
            // 片方だけ通すと、次回実行では衝突相手が消えていて上書きが成立してしまうため、
            // グループの全員を転送対象から外す
            Assert.Empty(downloaded);
            Assert.Empty(Directory.GetFiles(dir));
        }
        else
        {
            // 大小を区別するファイルシステムでは衝突しないので両方処理される
            Assert.Equal(2, downloaded.Count);
            Assert.Contains("/remote/Sample.txt", downloaded);
            Assert.Contains("/remote/sample.txt", downloaded);
        }

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// PreserveFolderStructure=false では、異なるサブディレクトリの同名ファイルが
    /// すべて Watch.Path 直下の同じ名前へ着地する。黙って上書きすると内容が失われ、
    /// DeleteRemoteAfterDownload と併用するとリモート側も消えてデータ消失になるため、
    /// 衝突を検出して片方を失敗させることを保証する。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RemoteFilesCollidingOnLocalPath_DoNotSilentlyOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        var watch = Options.Create(new WatchOptions { Path = dir, IncludeSubfolders = true });
        var transfer = Options.Create(new TransferOptions
        {
            Mode = "ftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote",
            Concurrency = 1,
            PreserveFolderStructure = false
        });
        var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
        var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
        var cleanup = Options.Create(new CleanupOptions());

        // 別ディレクトリの同名ファイル。どちらも "<Watch.Path>/result.csv" へ着地する
        var remoteFiles = new[] { "/remote/a/result.csv", "/remote/b/result.csv" };
        var downloaded = new List<string>();

        var mock = new Mock<IFileTransferClient>();
        mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(remoteFiles);
        mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((remote, local, _) =>
            {
                lock (downloaded) { downloaded.Add(remote); }
                File.WriteAllText(local, remote);
            })
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.Dispose());

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<Worker>>();

        using var lifetime = new DummyLifetime();
        var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));
        await worker.RunAsync(CancellationToken.None);

        // どちらも転送されない。片方だけ通すと、次回実行では衝突相手が消えていて
        // 上書きが成立し、DeleteRemoteAfterDownload と併用するとデータ消失になる
        Assert.Empty(downloaded);
        Assert.Empty(Directory.GetFiles(dir));

        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExecuteAsync_Get_CleansUpOrphanedDownloadTempDirectoryButKeepsUserFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // 前回の異常終了で残ったエージェント専用の一時ディレクトリと残骸 (削除されるべき)
            var tempDir = Path.Combine(dir, ".ftptransferagent-tmp");
            Directory.CreateDirectory(tempDir);
            var verifyLeftover = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".verify");
            var dlLeftover = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".dl");
            var innerTmpLeftover = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".verify.tmp." + Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(verifyLeftover, "partial");
            await File.WriteAllTextAsync(dlLeftover, "partial");
            await File.WriteAllTextAsync(innerTmpLeftover, "partial");

            // 利用者ファイル: エージェントの一時命名規則に「似た」名前でも消してはいけない
            // (Codex レビュー指摘の回帰: 名前パターンで利用者データを消さない)
            var realFile = Path.Combine(dir, "data.txt");
            var lookAlike = Path.Combine(dir, "report.tmp." + new string('a', 32)); // *.tmp.<32hex> に酷似
            var verifyLookAlike = Path.Combine(dir, "notes.verify." + new string('b', 32));
            await File.WriteAllTextAsync(realFile, "keep me");
            await File.WriteAllTextAsync(lookAlike, "keep me too");
            await File.WriteAllTextAsync(verifyLookAlike, "keep me three");

            var watch = Options.Create(new WatchOptions { Path = dir });
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(Array.Empty<string>());
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();
            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            await worker.RunAsync(CancellationToken.None);

            // エージェント専用ディレクトリの残骸だけが消える
            Assert.False(File.Exists(verifyLeftover), "orphaned .verify temp must be cleaned up");
            Assert.False(File.Exists(dlLeftover), "orphaned .dl temp must be cleaned up");
            Assert.False(File.Exists(innerTmpLeftover), "orphaned inner client temp must be cleaned up");
            // 利用者ファイルは名前が似ていても保持される
            Assert.True(File.Exists(realFile), "real data file must be kept");
            Assert.True(File.Exists(lookAlike), "user file resembling the temp pattern must be kept");
            Assert.True(File.Exists(verifyLookAlike), "user file resembling the verify pattern must be kept");
            // 掃除後の空ディレクトリは Watch.Path に残さない
            Assert.False(Directory.Exists(tempDir), "the emptied temp directory must not be left behind");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Get_RemovesEmptyDownloadTempDirectoryAfterRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var tempDir = Path.Combine(dir, ".ftptransferagent-tmp");

            var watch = Options.Create(new WatchOptions { Path = dir });
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(new[] { "/remote/data.txt" });
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, local, _) =>
                {
                    // 転送中は一時ディレクトリが存在する (削除は全転送完了後)
                    Assert.True(Directory.Exists(tempDir), "temp directory must exist while downloading");
                    File.WriteAllText(local, "data");
                })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();
            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            await worker.RunAsync(CancellationToken.None);

            // ダウンロード結果は残り、空になった一時ディレクトリは Watch.Path から消える
            Assert.True(File.Exists(Path.Combine(dir, "data.txt")), "downloaded file must be kept");
            Assert.False(Directory.Exists(tempDir), "the empty temp directory must be removed after the run");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Get_RemovesEmptyDownloadTempDirectoryLeftByPreviousRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // 前回実行が残した「空の」一時ディレクトリ (転送対象は無い)
            var tempDir = Path.Combine(dir, ".ftptransferagent-tmp");
            Directory.CreateDirectory(tempDir);

            var userFile = Path.Combine(dir, "data.txt");
            await File.WriteAllTextAsync(userFile, "keep me");

            var watch = Options.Create(new WatchOptions { Path = dir });
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(Array.Empty<string>());
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();
            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            await worker.RunAsync(CancellationToken.None);

            Assert.False(Directory.Exists(tempDir), "the empty temp directory from a previous run must be removed");
            Assert.True(File.Exists(userFile), "user files must be kept");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Get_KeepsDownloadTempDirectoryWhenFilesRemainInside()
    {
        // 空フォルダ削除がファイルを巻き込まないことの回帰テスト。
        // 実行終了時点で一時ディレクトリに中身が残っていれば、ディレクトリごと消してはいけない
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var tempDir = Path.Combine(dir, ".ftptransferagent-tmp");
            string? survivor = null;

            var watch = Options.Create(new WatchOptions { Path = dir });
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(new[] { "/remote/data.txt" });
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, local, _) =>
                {
                    File.WriteAllText(local, "data");
                    // 転送中に一時ディレクトリへ別ファイルが置かれた状況を作る
                    survivor = Path.Combine(Path.GetDirectoryName(local)!, "in-flight.dat");
                    File.WriteAllText(survivor, "must survive");
                })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();
            using var lifetime = new DummyLifetime();
            var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object));

            await worker.RunAsync(CancellationToken.None);

            Assert.NotNull(survivor);
            Assert.True(File.Exists(survivor), "files inside the temp directory must never be deleted by the empty-directory cleanup");
            Assert.True(Directory.Exists(tempDir), "a non-empty temp directory must be kept");
            Assert.Equal("must survive", await File.ReadAllTextAsync(survivor!));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>
    /// 相手が応答を止めた場合にワンショットのバッチが終わらなくなるのを防ぐため、
    /// Transfer.TransferTimeoutSeconds で 1 ファイルの処理を打ち切れることを保証する。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_StalledTransfer_IsAbortedByTransferTimeout()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var watch = Options.Create(new WatchOptions { Path = dir });
            var transfer = Options.Create(new TransferOptions
            {
                Mode = "ftp",
                Direction = "get",
                Host = "host",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                Concurrency = 1,
                TransferTimeoutSeconds = 1
            });
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(new[] { "/remote/stalled.txt" });
            // 相手が応答しない転送を模擬する。打ち切られなければ 60 秒待ち続ける
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, _, ct) => Task.Delay(TimeSpan.FromSeconds(60), ct));
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<Worker>>();

            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(watch, transfer, retry, hashOpt, cleanup, provider, logger, lifetime, new NoDisposeClient(mock.Object), exitCode);

            var started = DateTime.UtcNow;
            await worker.RunAsync(CancellationToken.None);
            var elapsed = DateTime.UtcNow - started;

            // TransferTimeoutSeconds=1 で打ち切られる。テストハーネス側の全体キャンセル (5 秒) より
            // 明確に早いことを確認し、「全体キャンセルで終わっただけ」と区別する
            Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Transfer was not aborted by the per-transfer timeout (elapsed: {elapsed}).");
            // 打ち切られた転送は失敗として扱われ、ローカルにファイルは残らない
            Assert.NotEqual(0, exitCode.Code);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 悪意のあるリモートパスが Watch.Path の外へ書き出されないことを、
    /// 実際に Worker のダウンロード経路を通して検証する。
    /// (以前はこの領域に Assert.True(true) だけの、製品コードを一切呼ばないテストが
    ///  2 箇所に重複して存在していた)
    /// </summary>
    [Theory]
    [InlineData("/remote/../../../etc/passwd")]
    [InlineData("/remote/../../outside.txt")]
    [InlineData("/remote/sub/../../../escape.txt")]
    public async Task ExecuteAsync_MaliciousRemotePath_DoesNotWriteOutsideWatchDirectory(string maliciousRemotePath)
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var watchDirectory = Path.Combine(root, "watch");
        Directory.CreateDirectory(watchDirectory);
        try
        {
            var watch = Options.Create(new WatchOptions { Path = watchDirectory, IncludeSubfolders = true });
            var transfer = Options.Create(new TransferOptions
            {
                Mode = "ftp",
                Direction = "get",
                Host = "host",
                Username = "user",
                Password = "pass",
                RemotePath = "/remote",
                PreserveFolderStructure = true,
                Concurrency = 1
            });
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hash = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions());

            var writtenPaths = new List<string>();
            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), true))
                .ReturnsAsync(new[] { maliciousRemotePath });
            // ガードが効かなければ、渡されたローカルパスへ実際に書き込まれてしまう
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((_, local, _) =>
                {
                    lock (writtenPaths) { writtenPaths.Add(local); }
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    File.WriteAllText(local, "escaped");
                })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(
                watch, transfer, retry, hash, cleanup, provider,
                provider.GetRequiredService<ILogger<Worker>>(), lifetime,
                new NoDisposeClient(mock.Object), exitCode);

            await worker.RunAsync(CancellationToken.None);

            // ダウンロードは開始すらされない
            Assert.Empty(writtenPaths);
            // 拒否は失敗として扱われる
            Assert.NotEqual(0, exitCode.Code);
            // Watch.Path の外に何も作られていない
            var watchFull = Path.GetFullPath(watchDirectory);
            Assert.All(
                Directory.GetFiles(root, "*", SearchOption.AllDirectories),
                f => Assert.StartsWith(watchFull, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// END ファイルを扱う構成で、大小のみ異なるデータファイルが同居する場合は
    /// どの END がどちらのものか判別できない。取り違えて DeleteRemoteEndFiles を適用すると
    /// 一方が他方の END を削除してしまうため、どちらも転送しないことを保証する。
    ///
    /// 大小を区別するファイルシステム (CI の Linux) ではローカルパスが衝突しないため、
    /// この除外は END 関連付けの曖昧さ判定によってのみ成立する。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RemoteFilesDifferingOnlyByCase_AreNotTransferredWhenEndFilesAreUsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                TransferEndFiles = true,
                EndFileExtensions = new[] { ".END" }
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true });

            // 大小のみ異なるデータファイルと、片方にしか無い END ファイル
            var remoteFiles = new[] { "/remote/Sample.txt", "/remote/sample.txt", "/remote/sample.txt.END" };
            var downloaded = new List<string>();
            var deleted = new List<string>();

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(remoteFiles);
            mock.Setup(c => c.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((remote, local, _) =>
                {
                    lock (downloaded) { downloaded.Add(remote); }
                    File.WriteAllText(local, remote);
                })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((remote, _) => { lock (deleted) { deleted.Add(remote); } })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(
                watch, transfer, retry, hashOpt, cleanup, provider,
                provider.GetRequiredService<ILogger<Worker>>(), lifetime,
                new NoDisposeClient(mock.Object), exitCode);

            await worker.RunAsync(CancellationToken.None);

            // どちらのデータも転送されず、END も削除されない
            Assert.Empty(downloaded);
            Assert.Empty(deleted);
            Assert.NotEqual(0, exitCode.Code);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// データファイルが 1 つでも、大小のみ異なる END が複数ある場合 (/foo, /foo.END, /FOO.END) は
    /// どちらが本来のマーカーか判別できない。両方を関連付けるとローカルで同一パスに着地して
    /// 転送が失敗し続け、DeleteRemoteEndFiles 有効時は先に片方を削除してしまう。
    /// そのため、このデータファイルは転送しない。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_EndFilesDifferingOnlyByCase_AreNotTransferred()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var watch = Options.Create(new WatchOptions
            {
                Path = dir,
                AllowedExtensions = new[] { ".txt" },
                TransferEndFiles = true,
                EndFileExtensions = new[] { ".END" }
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
            var retry = Options.Create(new RetryOptions { MaxAttempts = 0, DelaySeconds = 0 });
            var hashOpt = Options.Create(new HashOptions { Enabled = false, Algorithm = "SHA256" });
            var cleanup = Options.Create(new CleanupOptions { DeleteRemoteAfterDownload = true, DeleteRemoteEndFiles = true });

            // データは 1 つ。END は大小のみ異なるものが 2 つ
            var remoteFiles = new[] { "/remote/foo.txt", "/remote/foo.txt.END", "/remote/FOO.TXT.END" };
            var downloaded = new List<string>();
            var deleted = new List<string>();

            var mock = new Mock<IFileTransferClient>();
            mock.Setup(c => c.ListFilesAsync("/remote", It.IsAny<CancellationToken>(), false))
                .ReturnsAsync(remoteFiles);
            mock.Setup(c => c.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            mock.Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((remote, local, _) =>
                {
                    lock (downloaded) { downloaded.Add(remote); }
                    File.WriteAllText(local, remote);
                })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((remote, _) => { lock (deleted) { deleted.Add(remote); } })
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.Dispose());

            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            using var lifetime = new DummyLifetime();
            var exitCode = new ApplicationExitCode();
            var worker = new TestWorker(
                watch, transfer, retry, hashOpt, cleanup, provider,
                provider.GetRequiredService<ILogger<Worker>>(), lifetime,
                new NoDisposeClient(mock.Object), exitCode);

            await worker.RunAsync(CancellationToken.None);

            // データも END も転送されず、リモートからも消されない
            Assert.Empty(downloaded);
            Assert.Empty(deleted);
            Assert.NotEqual(0, exitCode.Code);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>製品コードと同じく、対象ボリュームの大小区別を実測する。</summary>
    private static bool CaseInsensitiveFileSystem(string directory)
    {
        var probe = Path.Combine(directory, $".case-probe-{Guid.NewGuid():N}");
        File.WriteAllBytes(probe, Array.Empty<byte>());
        try
        {
            return File.Exists(probe.ToUpperInvariant());
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private class TestWorker : Worker
    {
        private readonly IFileTransferClient _client;
        public TestWorker(IOptions<WatchOptions> w, IOptions<TransferOptions> t, IOptions<RetryOptions> r, IOptions<HashOptions> h, IOptions<CleanupOptions> c, IServiceProvider sp, ILogger<Worker> l, IHostApplicationLifetime lifetime, IFileTransferClient client, ApplicationExitCode? exitCode = null)
            : base(w, t, r, h, c, sp, l, lifetime, exitCode)
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
}
