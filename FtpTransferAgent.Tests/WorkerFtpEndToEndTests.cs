using System.Diagnostics;
using System.Runtime.InteropServices;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

/// <summary>
/// Worker をエンドツーエンドで実 FTP サーバ (pyftpdlib) に対して動かす統合テスト。
/// 既存の統合テストはクライアントラッパー単体か、Worker + モッククライアントのいずれかで、
/// 「Worker のオーケストレーション + 実クライアント」の結合は未検証だった。本クラスはその穴を埋める。
/// </summary>
public class WorkerFtpEndToEndTests
{
    [Fact]
    public async Task Worker_Put_UploadsToRealFtpServer_VerifiesHash_AndDeletesLocal()
    {
        var (watchDir, serverRoot, port, server) = await StartAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(watchDir, "alpha.txt"), "alpha-content");
            await File.WriteAllTextAsync(Path.Combine(watchDir, "beta.txt"), "beta-content");
            // 対象外拡張子は転送されないことも確認
            await File.WriteAllTextAsync(Path.Combine(watchDir, "skip.bin"), "ignored");

            var worker = CreateWorker(
                watchDir,
                new TransferOptions
                {
                    Mode = "ftp",
                    Direction = "put",
                    Host = "localhost",
                    Port = port,
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/",
                    Concurrency = 2
                },
                allowedExtensions: new[] { ".txt" },
                hash: new HashOptions { Enabled = true, Algorithm = "SHA256" },
                cleanup: new CleanupOptions { DeleteAfterVerify = true });

            await worker.RunAsync();

            // 実サーバ (= serverRoot) にハッシュ検証付きで届く
            Assert.Equal("alpha-content", await File.ReadAllTextAsync(Path.Combine(serverRoot, "alpha.txt")));
            Assert.Equal("beta-content", await File.ReadAllTextAsync(Path.Combine(serverRoot, "beta.txt")));
            // 対象外は送られない
            Assert.False(File.Exists(Path.Combine(serverRoot, "skip.bin")));
            // 検証成功後にローカルは削除される (対象外は残る)
            Assert.False(File.Exists(Path.Combine(watchDir, "alpha.txt")));
            Assert.False(File.Exists(Path.Combine(watchDir, "beta.txt")));
            Assert.True(File.Exists(Path.Combine(watchDir, "skip.bin")));
            // 一時ファイルが残らない
            Assert.Empty(Directory.GetFiles(serverRoot, "*.tmp.*"));
        }
        finally
        {
            Cleanup(server, watchDir, serverRoot);
        }
    }

    [Fact]
    public async Task Worker_Get_DownloadsFromRealFtpServer_ViaTempDir_VerifiesHash()
    {
        var (watchDir, serverRoot, port, server) = await StartAsync();
        try
        {
            // リモート (= serverRoot) にファイルを用意
            await File.WriteAllTextAsync(Path.Combine(serverRoot, "remote1.txt"), "remote-one");
            await File.WriteAllTextAsync(Path.Combine(serverRoot, "remote2.txt"), "remote-two");

            var worker = CreateWorker(
                watchDir,
                new TransferOptions
                {
                    Mode = "ftp",
                    Direction = "get",
                    Host = "localhost",
                    Port = port,
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/",
                    Concurrency = 2
                },
                allowedExtensions: new[] { ".txt" },
                hash: new HashOptions { Enabled = true, Algorithm = "SHA256" },
                cleanup: new CleanupOptions());

            await worker.RunAsync();

            // ダウンロードは .ftptransferagent-tmp 経由 → 最終パスへ移動。内容が一致する
            Assert.Equal("remote-one", await File.ReadAllTextAsync(Path.Combine(watchDir, "remote1.txt")));
            Assert.Equal("remote-two", await File.ReadAllTextAsync(Path.Combine(watchDir, "remote2.txt")));
            // 検証用一時ディレクトリに最終ファイルが取り残されていない (移動済み)
            var tempDir = Path.Combine(watchDir, ".ftptransferagent-tmp");
            if (Directory.Exists(tempDir))
            {
                Assert.Empty(Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories));
            }
        }
        finally
        {
            Cleanup(server, watchDir, serverRoot);
        }
    }

    [Fact]
    public async Task Worker_Get_WithoutHash_DownloadsViaTempDir()
    {
        var (watchDir, serverRoot, port, server) = await StartAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(serverRoot, "plain.txt"), "plain-content");

            var worker = CreateWorker(
                watchDir,
                new TransferOptions
                {
                    Mode = "ftp",
                    Direction = "get",
                    Host = "localhost",
                    Port = port,
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/",
                    Concurrency = 1
                },
                allowedExtensions: new[] { ".txt" },
                hash: new HashOptions { Enabled = false, Algorithm = "SHA256" },
                cleanup: new CleanupOptions());

            await worker.RunAsync();

            // ハッシュ無効でも一時ディレクトリ経由でダウンロードし、最終パスへ移動する
            Assert.Equal("plain-content", await File.ReadAllTextAsync(Path.Combine(watchDir, "plain.txt")));
        }
        finally
        {
            Cleanup(server, watchDir, serverRoot);
        }
    }

    [Fact]
    public async Task Worker_Put_TransfersDataAndEndFile_AgainstRealFtpServer()
    {
        var (watchDir, serverRoot, port, server) = await StartAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(watchDir, "data.txt"), "payload");
            await File.WriteAllTextAsync(Path.Combine(watchDir, "data.txt.END"), "done");

            var worker = CreateWorker(
                watchDir,
                new TransferOptions
                {
                    Mode = "ftp",
                    Direction = "put",
                    Host = "localhost",
                    Port = port,
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/",
                    Concurrency = 1
                },
                allowedExtensions: new[] { ".txt" },
                hash: new HashOptions { Enabled = true, Algorithm = "SHA256" },
                cleanup: new CleanupOptions { DeleteAfterVerify = true },
                requireEndFile: true,
                transferEndFiles: true,
                endFileExtensions: new[] { ".END" });

            await worker.RunAsync();

            // データと END の両方が実サーバへ届く
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(serverRoot, "data.txt")));
            Assert.Equal("done", await File.ReadAllTextAsync(Path.Combine(serverRoot, "data.txt.END")));
            // 成功後にローカルのデータ/END とも削除される
            Assert.False(File.Exists(Path.Combine(watchDir, "data.txt")));
            Assert.False(File.Exists(Path.Combine(watchDir, "data.txt.END")));
        }
        finally
        {
            Cleanup(server, watchDir, serverRoot);
        }
    }

    [Fact]
    public async Task Worker_Put_PreservesFolderStructure_AgainstRealFtpServer()
    {
        var (watchDir, serverRoot, port, server) = await StartAsync();
        try
        {
            Directory.CreateDirectory(Path.Combine(watchDir, "sub"));
            await File.WriteAllTextAsync(Path.Combine(watchDir, "sub", "nested.txt"), "nested-content");

            var worker = CreateWorker(
                watchDir,
                new TransferOptions
                {
                    Mode = "ftp",
                    Direction = "put",
                    Host = "localhost",
                    Port = port,
                    Username = "user",
                    Password = "pass",
                    RemotePath = "/",
                    Concurrency = 1,
                    PreserveFolderStructure = true
                },
                allowedExtensions: new[] { ".txt" },
                hash: new HashOptions { Enabled = true, Algorithm = "SHA256" },
                cleanup: new CleanupOptions { DeleteAfterVerify = false },
                includeSubfolders: true);

            await worker.RunAsync();

            // サブフォルダ構造を保ってリモート (= serverRoot) に作成される
            Assert.Equal("nested-content", await File.ReadAllTextAsync(Path.Combine(serverRoot, "sub", "nested.txt")));
        }
        finally
        {
            Cleanup(server, watchDir, serverRoot);
        }
    }

    // --- ヘルパ ---

    private static RealWorker CreateWorker(
        string watchDir,
        TransferOptions transfer,
        string[] allowedExtensions,
        HashOptions hash,
        CleanupOptions cleanup,
        bool includeSubfolders = false,
        bool requireEndFile = false,
        bool transferEndFiles = false,
        string[]? endFileExtensions = null)
    {
        var watch = Options.Create(new WatchOptions
        {
            Path = watchDir,
            AllowedExtensions = allowedExtensions,
            IncludeSubfolders = includeSubfolders,
            RequireEndFile = requireEndFile,
            TransferEndFiles = transferEndFiles,
            EndFileExtensions = endFileExtensions ?? new[] { ".END" }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        return new RealWorker(
            watch,
            Options.Create(transfer),
            Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }),
            Options.Create(hash),
            Options.Create(cleanup),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            new DummyLifetime());
    }

    /// <summary>CreateClient をオーバーライドしない = 実クライアントを使う Worker。</summary>
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ExecuteAsync(cts.Token);
        }
    }

    private static async Task<(string WatchDir, string ServerRoot, int Port, Process Server)> StartAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "WorkerFtpE2E_" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(tempDir, "watch");
        var serverRoot = Path.Combine(tempDir, "server");
        Directory.CreateDirectory(watchDir);
        Directory.CreateDirectory(serverRoot);

        var port = GetAvailablePort();
        var server = await StartFtpServerAsync(serverRoot, port);
        return (watchDir, serverRoot, port, server);
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<Process> StartFtpServerAsync(string root, int port)
    {
        var python = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        var psi = new ProcessStartInfo(python, $"-m pyftpdlib -p {port} -w -d {root} -u user -P pass")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;

        var maxWaitTime = TimeSpan.FromSeconds(5);
        var startTime = DateTime.Now;
        var connected = false;
        while (DateTime.Now - startTime < maxWaitTime && !connected)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                connected = true;
            }
            catch
            {
                await Task.Delay(100);
            }
        }

        if (!connected)
        {
            proc.Kill();
            throw new InvalidOperationException($"FTP server failed to start on port {port}");
        }

        return proc;
    }

    private static void Cleanup(Process server, string watchDir, string serverRoot)
    {
        try
        {
            if (!server.HasExited)
            {
                server.Kill();
                server.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            server.Dispose();
        }

        var tempDir = Path.GetDirectoryName(watchDir);
        try
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch
        {
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
