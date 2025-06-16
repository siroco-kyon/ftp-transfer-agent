using System.Diagnostics;
using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 実際にローカル FTP サーバーを起動して転送を検証する統合テスト
/// </summary>
public class FtpClientIntegrationTests
{
    private Process StartFtpServer(string root, int port)
    {
        var psi = new ProcessStartInfo("python3", $"-m pyftpdlib -p {port} -w -d {root} -u user -P pass")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        Thread.Sleep(1000); // サーバ起動待ち
        return proc;
    }

    [Fact]
    public async Task UploadAndDownload_WorksAgainstLocalFtpServer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var server = StartFtpServer(tempDir, 2121);
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
            server.Kill();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Upload_OverwritesExistingRemoteFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var server = StartFtpServer(tempDir, 2122);
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
            server.Kill();
            Directory.Delete(tempDir, true);
        }
    }
}
