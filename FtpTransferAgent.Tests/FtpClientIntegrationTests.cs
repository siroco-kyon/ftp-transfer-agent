using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 実際にローカル FTP サーバーを起動して転送を検証する統合テスト
/// </summary>
public class FtpClientIntegrationTests
{
    private async Task<Process> StartFtpServerAsync(string root, int port)
    {
        var python = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        var psi = new ProcessStartInfo(python, $"-m pyftpdlib -p {port} -w -d {root} -u user -P pass")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;

        // サーバー起動を確実に待機する
        var maxWaitTime = TimeSpan.FromSeconds(10);
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
                await Task.Delay(200);
            }
        }

        if (!connected)
        {
            proc.Kill();
            throw new InvalidOperationException($"FTP server failed to start on port {port}");
        }

        return proc;
    }

    [Fact]
    public async Task UploadAndDownload_WorksAgainstLocalFtpServer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var server = await StartFtpServerAsync(tempDir, 2121);
        try
        {
            var opts = new TransferOptions
            {
                Mode = "ftp",
                Direction = "both",
                Host = "localhost",
                Port = 2121,
                Username = "user",
                Password = "pass",
                RemotePath = "/"
            };
            var wrapper = new AsyncFtpClientWrapper(opts, NullLogger<AsyncFtpClientWrapper>.Instance);

            var localPath = Path.Combine(tempDir, "test.txt");
            await File.WriteAllTextAsync(localPath, "hello");
            await wrapper.UploadAsync(localPath, "/upload.txt", CancellationToken.None);

            var files = await wrapper.ListFilesAsync("/", CancellationToken.None);
            Assert.Contains("/upload.txt", files);

            var downloadPath = Path.Combine(tempDir, "download.txt");
            await wrapper.DownloadAsync("/upload.txt", downloadPath, CancellationToken.None);
            Assert.True(File.Exists(downloadPath));
        }
        finally
        {
            try
            {
                if (!server.HasExited)
                {
                    server.Kill();
                    server.WaitForExit(5000); // 5秒待機
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to kill FTP server process: {ex.Message}");
            }
            finally
            {
                server.Dispose();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete temp directory: {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task Upload_OverwritesExistingRemoteFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var server = await StartFtpServerAsync(tempDir, 2122);
        try
        {
            var opts = new TransferOptions
            {
                Mode = "ftp",
                Direction = "put",
                Host = "localhost",
                Port = 2122,
                Username = "user",
                Password = "pass",
                RemotePath = "/"
            };
            var wrapper = new AsyncFtpClientWrapper(opts, NullLogger<AsyncFtpClientWrapper>.Instance);

            var existingPath = Path.Combine(tempDir, "upload.txt");
            await File.WriteAllTextAsync(existingPath, "old");

            var localPath = Path.Combine(tempDir, "local.txt");
            await File.WriteAllTextAsync(localPath, "newcontent");
            await wrapper.UploadAsync(localPath, "/upload.txt", CancellationToken.None);

            var downloadPath = Path.Combine(tempDir, "verify.txt");
            await wrapper.DownloadAsync("/upload.txt", downloadPath, CancellationToken.None);
            var content = await File.ReadAllTextAsync(downloadPath);
            Assert.Equal("newcontent", content);
        }
        finally
        {
            try
            {
                if (!server.HasExited)
                {
                    server.Kill();
                    server.WaitForExit(5000); // 5秒待機
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to kill FTP server process: {ex.Message}");
            }
            finally
            {
                server.Dispose();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete temp directory: {ex.Message}");
            }
        }
    }
}
