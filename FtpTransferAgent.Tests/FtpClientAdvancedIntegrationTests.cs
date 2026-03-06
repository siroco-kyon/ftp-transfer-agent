using System.Diagnostics;
using System.Runtime.InteropServices;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

public class FtpClientAdvancedIntegrationTests
{
    [Fact]
    public async Task ListFilesAsync_WithIncludeSubdirectories_ShouldReturnNestedFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(tempDir, "nested", "child.txt"), "child");

        var port = GetAvailablePort();
        var server = await StartFtpServerAsync(tempDir, port);
        try
        {
            var wrapper = new AsyncFtpClientWrapper(CreateOptions(port), NullLogger<AsyncFtpClientWrapper>.Instance);

            var files = (await wrapper.ListFilesAsync("/", CancellationToken.None, includeSubdirectories: true)).ToArray();

            Assert.Contains(files, f => f.EndsWith("/root.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => f.EndsWith("/nested/child.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(server, tempDir);
        }
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRemoteFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "delete-me.txt"), "to delete");

        var port = GetAvailablePort();
        var server = await StartFtpServerAsync(tempDir, port);
        try
        {
            var wrapper = new AsyncFtpClientWrapper(CreateOptions(port), NullLogger<AsyncFtpClientWrapper>.Instance);

            await wrapper.DeleteAsync("/delete-me.txt", CancellationToken.None);
            var files = (await wrapper.ListFilesAsync("/", CancellationToken.None)).ToArray();

            Assert.DoesNotContain(files, f => f.EndsWith("/delete-me.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(server, tempDir);
        }
    }

    [Fact]
    public async Task GetRemoteHashAsync_ShouldMatchLocalHash_WithAndWithoutServerCommand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "hash-source.txt");
        await File.WriteAllTextAsync(sourcePath, "hash content");
        var expectedHash = await HashUtil.ComputeHashAsync(sourcePath, "SHA256", CancellationToken.None);

        var port = GetAvailablePort();
        var server = await StartFtpServerAsync(tempDir, port);
        try
        {
            var wrapper = new AsyncFtpClientWrapper(CreateOptions(port), NullLogger<AsyncFtpClientWrapper>.Instance);

            var hashByDownload = await wrapper.GetRemoteHashAsync("/hash-source.txt", "SHA256", CancellationToken.None, useServerCommand: false);
            var hashByServerCommand = await wrapper.GetRemoteHashAsync("/hash-source.txt", "SHA256", CancellationToken.None, useServerCommand: true);

            Assert.Equal(expectedHash, hashByDownload);
            Assert.Equal(expectedHash, hashByServerCommand.ToLowerInvariant());
        }
        finally
        {
            Cleanup(server, tempDir);
        }
    }

    private static TransferOptions CreateOptions(int port)
    {
        return new TransferOptions
        {
            Mode = "ftp",
            Direction = "both",
            Host = "localhost",
            Port = port,
            Username = "user",
            Password = "pass",
            RemotePath = "/"
        };
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

    private static void Cleanup(Process server, string tempDir)
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

        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
        catch
        {
        }
    }
}
