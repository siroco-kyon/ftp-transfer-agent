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
/// paramiko 製の実 SFTP サーバに対する統合テスト。
///
/// これまで SFTP の実サーバ検証は Docker (atmoz/sftp) にしか無く、Docker が無い環境では
/// 該当テストが静かにスキップされて「緑だが SFTP は未検証」という状態になっていた。
/// 本クラスは Python + paramiko だけで動くため、FTP 側 (pyftpdlib) と同じ前提で常に実行できる。
///
/// paramiko は posix-rename@openssh.com 拡張を告知しないため、
/// <see cref="SftpClientWrapper"/> の「削除 + リネーム」フォールバック経路も検証される
/// (OpenSSH ベースの Docker サーバでは通らない経路)。
/// </summary>
public class SftpParamikoIntegrationTests
{
    [Fact]
    public async Task Worker_PutThenGet_RoundTripsThroughParamikoSftpServer()
    {
        var ctx = await SftpTestServer.StartAsync();
        try
        {
            var remoteDir = Path.Combine(ctx.Root, "remote");
            Directory.CreateDirectory(remoteDir);

            // 通常ファイルに加え、これまで実転送での検証が無かったケースを併せて確認する
            var payloads = new Dictionary<string, string>
            {
                ["alpha.txt"] = "alpha-content",
                ["empty.txt"] = "",
                ["日本語 スペース.txt"] = "にほんご ないよう",
                ["large.txt"] = new string('x', 2 * 1024 * 1024),
            };
            foreach (var (name, content) in payloads)
            {
                await File.WriteAllTextAsync(Path.Combine(ctx.WatchDir, name), content);
            }

            await RunWorkerAsync(ctx, "put", ctx.WatchDir);

            // すべてサーバへ到達し、内容が一致する
            foreach (var (name, content) in payloads)
            {
                var uploaded = Path.Combine(remoteDir, name);
                Assert.True(File.Exists(uploaded), $"{name} was not uploaded.");
                Assert.Equal(content, await File.ReadAllTextAsync(uploaded));
            }
            // 一時ファイルが残っていない (temp 名 -> リネームの後始末)
            Assert.Empty(Directory.GetFiles(remoteDir, "*.tmp.*"));

            // 続けて get で取り出し、バイト内容が一致することを確認する
            var downloadDir = Path.Combine(ctx.Root, "download");
            Directory.CreateDirectory(downloadDir);

            await RunWorkerAsync(ctx, "get", downloadDir);

            foreach (var (name, content) in payloads)
            {
                var downloaded = Path.Combine(downloadDir, name);
                Assert.True(File.Exists(downloaded), $"{name} was not downloaded.");
                Assert.Equal(content, await File.ReadAllTextAsync(downloaded));
            }
            // ダウンロード用の一時ディレクトリに残骸が無い
            var tempDir = Path.Combine(downloadDir, ".ftptransferagent-tmp");
            Assert.True(!Directory.Exists(tempDir) || Directory.GetFiles(tempDir).Length == 0);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    /// <summary>
    /// posix-rename 非対応サーバでの上書き put。既存の宛先ファイルが新しい内容で
    /// 置き換わり、一時ファイルが残らないことを確認する。
    /// </summary>
    [Fact]
    public async Task Worker_Put_OverwritesExistingRemoteFile_ViaDeleteRenameFallback()
    {
        var ctx = await SftpTestServer.StartAsync();
        try
        {
            var remoteDir = Path.Combine(ctx.Root, "remote");
            Directory.CreateDirectory(remoteDir);
            await File.WriteAllTextAsync(Path.Combine(remoteDir, "report.txt"), "old-content");

            await File.WriteAllTextAsync(Path.Combine(ctx.WatchDir, "report.txt"), "new-content");

            await RunWorkerAsync(ctx, "put", ctx.WatchDir);

            Assert.Equal("new-content", await File.ReadAllTextAsync(Path.Combine(remoteDir, "report.txt")));
            Assert.Empty(Directory.GetFiles(remoteDir, "*.tmp.*"));
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static async Task RunWorkerAsync(SftpTestServer ctx, string direction, string localDir)
    {
        var watch = Options.Create(new WatchOptions
        {
            Path = localDir,
            AllowedExtensions = new[] { ".txt" }
        });

        var transfer = Options.Create(new TransferOptions
        {
            Mode = "sftp",
            Direction = direction,
            Host = "127.0.0.1",
            Port = ctx.Port,
            Username = SftpTestServer.Username,
            Password = SftpTestServer.Password,
            RemotePath = "/remote",
            Concurrency = 2
        });

        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        using var lifetime = new DummyLifetime();

        var worker = new RealWorker(
            watch,
            transfer,
            Options.Create(new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }),
            Options.Create(new HashOptions { Enabled = true, Algorithm = "SHA256" }),
            Options.Create(new CleanupOptions { DeleteAfterVerify = false }),
            provider,
            provider.GetRequiredService<ILogger<Worker>>(),
            lifetime);

        await worker.RunAsync();
    }

    /// <summary>CreateClient をオーバーライドしない = 実 SftpClientWrapper を使う Worker。</summary>
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await ExecuteAsync(cts.Token);
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

/// <summary>
/// テスト用の paramiko SFTP サーバを起動・停止するヘルパ。
/// </summary>
internal sealed class SftpTestServer : IDisposable
{
    internal const string Username = "testuser";
    internal const string Password = "testpass";

    public required string Root { get; init; }
    public required string WatchDir { get; init; }
    public required int Port { get; init; }
    public required Process Process { get; init; }

    private static string PythonExecutable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";

    public static async Task<SftpTestServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SftpParamiko_" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(root, "watch");
        Directory.CreateDirectory(watchDir);

        // ホスト鍵は paramiko 自身で生成する (ssh-keygen の有無に依存しない)
        var hostKeyPath = Path.Combine(root, "hostkey");
        RunPython($"-c \"import paramiko; paramiko.RSAKey.generate(2048).write_private_key_file(r'{hostKeyPath}')\"");

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "TestServers", "paramiko_sftpd.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Test SFTP server script not found: {scriptPath}");
        }

        var port = GetAvailablePort();
        var psi = new ProcessStartInfo(PythonExecutable, $"\"{scriptPath}\" \"{root}\" {port} \"{hostKeyPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var process = Process.Start(psi)!;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var connected = false;
        while (DateTime.UtcNow < deadline && !connected)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Test SFTP server exited during startup: {stderr}");
            }

            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync("127.0.0.1", port);
                connected = true;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        if (!connected)
        {
            try { process.Kill(); } catch { }
            throw new InvalidOperationException($"Test SFTP server failed to start on port {port}");
        }

        // 起動確認が済んでからストリームを読み捨てる。読まないとパイプバッファが埋まった時点で
        // サーバプロセスがログ出力でブロックし、転送が進まなくなる
        _ = Task.Run(() => process.StandardOutput.ReadToEndAsync());
        _ = Task.Run(() => process.StandardError.ReadToEndAsync());

        return new SftpTestServer { Root = root, WatchDir = watchDir, Port = port, Process = process };
    }

    private static void RunPython(string arguments)
    {
        var psi = new ProcessStartInfo(PythonExecutable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"python {arguments} failed: {stderr}");
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            Process.Dispose();
        }

        try { Directory.Delete(Root, true); } catch { }
    }
}
