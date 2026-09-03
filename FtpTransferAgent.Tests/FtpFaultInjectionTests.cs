using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentFTP;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 実 FTP サーバ (pyftpdlib) に障害を注入し、FluentFTP が「例外ではなく戻り値」で
/// 通知する失敗を確実に検出できることを検証する。
///
/// FluentFTP の UploadFile/DownloadFile は転送後のサーバ最終応答が 4xx/5xx の場合や
/// データ接続が切れた場合に例外を投げず <see cref="FtpStatus.Failed"/> を返す。
/// 戻り値を検査しないと「サーバに保存されていないのに転送成功」と判定され、
/// Cleanup.DeleteAfterVerify によってローカル原本が削除される (= 無言のデータ消失)。
/// </summary>
public class FtpFaultInjectionTests
{
    /// <summary>
    /// STOR の最終応答を 552 (容量超過) に差し替え、受信済みファイルも破棄する FTP サーバ。
    /// 実サーバがクォータ超過やディスクフルで拒否した状況を再現する。
    /// </summary>
    private const string RejectingServerScript = """
        import sys
        from pyftpdlib.authorizers import DummyAuthorizer
        from pyftpdlib.handlers import FTPHandler, DTPHandler
        from pyftpdlib.servers import FTPServer
        from pyftpdlib.log import logger

        root, port = sys.argv[1], int(sys.argv[2])

        class RejectingDTP(DTPHandler):
            def handle_close(self):
                if not self._closed and self.receive:
                    self.transfer_finished = True
                    try:
                        # 実サーバの 552 と同じく「保存しなかった」状態を作る
                        try:
                            import os
                            name = getattr(self.file_obj, 'name', None)
                            if name:
                                self.file_obj.close()
                                os.remove(name)
                        except Exception:
                            pass
                        self._resp = ("552 Requested file action aborted: exceeded storage allocation.", logger.debug)
                    finally:
                        self.close()
                    return
                super().handle_close()

        auth = DummyAuthorizer()
        auth.add_user("user", "pass", root, perm="elradfmwMT")
        h = FTPHandler
        h.authorizer = auth
        h.encoding = "utf-8"
        h.dtp_handler = RejectingDTP
        FTPServer(("127.0.0.1", port), h).serve_forever()
        """;

    [Fact]
    public async Task UploadAsync_ShouldThrow_WhenServerRejectsStorWithFinalReply()
    {
        var (root, port, server, scriptPath) = await StartRejectingServerAsync();
        try
        {
            var localFile = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(localFile, "important payload");

            using var client = CreateClient(port);

            var ex = await Assert.ThrowsAsync<TransferFailedException>(
                () => client.UploadAsync(localFile, "/uploaded.txt", CancellationToken.None));

            Assert.Contains("did not complete successfully", ex.Message);

            // 宛先も一時ファイルも残っていないこと (サーバが保存を拒否しているため)
            Assert.False(File.Exists(Path.Combine(root, "uploaded.txt")));
            Assert.Empty(Directory.GetFiles(root, "*.tmp.*"));
        }
        finally
        {
            Cleanup(server, root, scriptPath);
        }
    }

    [Fact]
    public async Task UploadAsync_ShouldBeClassifiedAsRetryable_WhenServerRejectsStor()
    {
        var (root, port, server, scriptPath) = await StartRejectingServerAsync();
        try
        {
            var localFile = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(localFile, "important payload");

            using var client = CreateClient(port);

            var ex = await Assert.ThrowsAsync<TransferFailedException>(
                () => client.UploadAsync(localFile, "/uploaded.txt", CancellationToken.None));

            // 一時的なサーバ拒否として再試行されること。非リトライに分類されると
            // 1 回で最終失敗となりクリティカルエラー扱いになる
            Assert.True(RetryableExceptionClassifier.IsRetryable(ex));
        }
        finally
        {
            Cleanup(server, root, scriptPath);
        }
    }

    // --- ヘルパ ---

    private static AsyncFtpClientWrapper CreateClient(int port)
    {
        return new AsyncFtpClientWrapper(
            new DestinationOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "user",
                Password = "pass",
                VerifyUploadedFileExists = true
            },
            NullLogger<AsyncFtpClientWrapper>.Instance);
    }

    private static async Task<(string Root, int Port, Process Server, string ScriptPath)> StartRejectingServerAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "FtpFaultInjection_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var scriptPath = Path.Combine(Path.GetTempPath(), "rejecting_ftpd_" + Guid.NewGuid().ToString("N") + ".py");
        await File.WriteAllTextAsync(scriptPath, RejectingServerScript);

        var port = GetAvailablePort();
        var python = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        var psi = new ProcessStartInfo(python, $"\"{scriptPath}\" \"{root}\" {port}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var connected = false;
        while (DateTime.UtcNow < deadline && !connected)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync("127.0.0.1", port);
                connected = true;
            }
            catch
            {
                await Task.Delay(100);
            }
        }

        if (!connected)
        {
            try { proc.Kill(); } catch { }
            throw new InvalidOperationException($"Fault-injecting FTP server failed to start on port {port}");
        }

        return (root, port, proc, scriptPath);
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Cleanup(Process server, string root, string scriptPath)
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

        try { Directory.Delete(root, true); } catch { }
        try { File.Delete(scriptPath); } catch { }
    }
}
