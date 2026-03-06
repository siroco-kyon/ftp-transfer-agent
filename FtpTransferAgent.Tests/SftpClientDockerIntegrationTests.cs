using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FtpTransferAgent.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(DetectSkipReason);

    public DockerFactAttribute()
    {
        var reason = SkipReason.Value;
        if (!string.IsNullOrEmpty(reason))
        {
            Skip = reason;
        }
    }

    private static string? DetectSkipReason()
    {
        var psi = new ProcessStartInfo("docker", "version --format \"{{.Server.Version}}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return "Docker is unavailable (failed to start docker process).";
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return "Docker is unavailable (timeout while checking docker version).";
            }

            return process.ExitCode == 0 ? null : process.StandardError.ReadToEnd().Trim();
        }
        catch (Exception ex)
        {
            return $"Docker is unavailable ({ex.Message})";
        }
    }
}

[CollectionDefinition("docker-sftp", DisableParallelization = true)]
public sealed class DockerSftpCollection : ICollectionFixture<DockerSftpFixture>
{
}

public sealed class DockerSftpFixture : IAsyncLifetime
{
    public string Host => "127.0.0.1";
    public int Port { get; private set; }
    public string Username => "testuser";
    public string Password => "testpass";

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }

    private readonly string _containerName = $"ftp-transfer-agent-sftp-{Guid.NewGuid():N}";
    private bool _containerStarted;

    public async Task InitializeAsync()
    {
        Port = GetAvailablePort();

        var dockerVersion = await RunDockerAsync("version --format \"{{.Server.Version}}\"", TimeSpan.FromSeconds(15));
        if (!dockerVersion.Success)
        {
            UnavailableReason = $"Docker is not available: {dockerVersion.Error}";
            return;
        }

        var run = await RunDockerAsync(
            $"run -d --rm -p {Port}:22 --name {_containerName} atmoz/sftp {Username}:{Password}:1001:1001:upload",
            TimeSpan.FromMinutes(2));
        if (!run.Success)
        {
            UnavailableReason = $"Failed to start SFTP container: {run.Error}";
            return;
        }

        _containerStarted = true;

        var ready = await WaitForPortAsync(Host, Port, TimeSpan.FromSeconds(30));
        if (!ready)
        {
            UnavailableReason = $"SFTP container did not become ready on {Host}:{Port}";
            await DisposeAsync();
            return;
        }

        var sshReady = await WaitForSshReadyAsync(Host, Port, Username, Password, TimeSpan.FromSeconds(45));
        if (!sshReady)
        {
            UnavailableReason = $"SFTP container did not become SSH-ready on {Host}:{Port}";
            await DisposeAsync();
            return;
        }

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        IsAvailable = false;

        if (_containerStarted)
        {
            await RunDockerAsync($"rm -f {_containerName}", TimeSpan.FromSeconds(30));
            _containerStarted = false;
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                return true;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        return false;
    }

    private static async Task<bool> WaitForSshReadyAsync(string host, int port, string username, string password, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                using var client = new SftpClient(host, port, username, password);
                client.HostKeyReceived += (_, e) => e.CanTrust = true;
                client.OperationTimeout = TimeSpan.FromSeconds(5);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                client.Connect();
                client.Disconnect();
                return true;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        return false;
    }

    private static async Task<(bool Success, string Output, string Error)> RunDockerAsync(string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, string.Empty, "Failed to start docker process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                return (false, string.Empty, $"Docker command timed out: docker {args}");
            }

            var output = (await stdoutTask).Trim();
            var error = (await stderrTask).Trim();
            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}

[Collection("docker-sftp")]
public class SftpClientDockerIntegrationTests
{
    private readonly DockerSftpFixture _fixture;
    private readonly Mock<ILogger<SftpClientWrapper>> _logger = new();

    public SftpClientDockerIntegrationTests(DockerSftpFixture fixture)
    {
        _fixture = fixture;
    }

    [DockerFact]
    public async Task UploadDownloadHashAndDelete_ShouldWorkAgainstDockerSftpServer()
    {
        Assert.True(_fixture.IsAvailable, _fixture.UnavailableReason ?? "Docker fixture is not ready.");

        var tempDir = CreateTempDir();
        var remoteBase = $"/upload/it-{Guid.NewGuid():N}";
        var remotePath = $"{remoteBase}/nested/test.txt";

        try
        {
            var localSource = Path.Combine(tempDir, "source.txt");
            var localDownload = Path.Combine(tempDir, "download.txt");
            var content = $"docker-sftp-test-{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(localSource, content);

            using var wrapper = new SftpClientWrapper(CreateOptions(), _logger.Object);
            await wrapper.UploadAsync(localSource, remotePath, CancellationToken.None);

            var nonRecursiveFiles = (await wrapper.ListFilesAsync(remoteBase, CancellationToken.None, includeSubdirectories: false)).ToArray();
            Assert.DoesNotContain(nonRecursiveFiles, p => p.EndsWith("/nested/test.txt", StringComparison.Ordinal));

            var recursiveFiles = (await wrapper.ListFilesAsync(remoteBase, CancellationToken.None, includeSubdirectories: true)).ToArray();
            Assert.Contains(recursiveFiles, p => p.EndsWith("/nested/test.txt", StringComparison.Ordinal));

            await wrapper.DownloadAsync(remotePath, localDownload, CancellationToken.None);
            var downloaded = await File.ReadAllTextAsync(localDownload);
            Assert.Equal(content, downloaded);

            var expectedHash = await HashUtil.ComputeHashAsync(localSource, "SHA256", CancellationToken.None);
            var hashWithoutServerCommand = await wrapper.GetRemoteHashAsync(remotePath, "SHA256", CancellationToken.None, useServerCommand: false);
            var hashWithServerCommand = await wrapper.GetRemoteHashAsync(remotePath, "SHA256", CancellationToken.None, useServerCommand: true);
            Assert.Equal(expectedHash, hashWithoutServerCommand);
            Assert.Equal(expectedHash, hashWithServerCommand);

            await wrapper.DeleteAsync(remotePath, CancellationToken.None);
            var filesAfterDelete = (await wrapper.ListFilesAsync(remoteBase, CancellationToken.None, includeSubdirectories: true)).ToArray();
            Assert.DoesNotContain(filesAfterDelete, p => p.EndsWith("/nested/test.txt", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [DockerFact]
    public async Task Connection_ShouldSucceed_WhenHostKeyFingerprintMatches()
    {
        Assert.True(_fixture.IsAvailable, _fixture.UnavailableReason ?? "Docker fixture is not ready.");

        var fingerprint = GetServerFingerprint();
        using var wrapper = new SftpClientWrapper(CreateOptions(fingerprint), _logger.Object);

        var exception = await Record.ExceptionAsync(() =>
            wrapper.ListFilesAsync("/", CancellationToken.None, includeSubdirectories: false));
        Assert.Null(exception);
    }

    [DockerFact]
    public async Task Connection_ShouldFail_WhenHostKeyFingerprintDoesNotMatch()
    {
        Assert.True(_fixture.IsAvailable, _fixture.UnavailableReason ?? "Docker fixture is not ready.");

        var fingerprint = GetServerFingerprint();
        var wrongFingerprint = FlipFirstHexChar(fingerprint);

        using var wrapper = new SftpClientWrapper(CreateOptions(wrongFingerprint), _logger.Object);

        var exception = await Record.ExceptionAsync(() =>
            wrapper.ListFilesAsync("/", CancellationToken.None, includeSubdirectories: false));
        Assert.NotNull(exception);
        Assert.True(exception is SshException || exception.InnerException is SshException);

        _logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Host key fingerprint mismatch", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private TransferOptions CreateOptions(string? hostKeyFingerprint = null)
    {
        return new TransferOptions
        {
            Mode = "sftp",
            Direction = "both",
            Host = _fixture.Host,
            Port = _fixture.Port,
            Username = _fixture.Username,
            Password = _fixture.Password,
            HostKeyFingerprint = hostKeyFingerprint,
            RemotePath = "/",
            TimeoutSeconds = 30
        };
    }

    private string GetServerFingerprint()
    {
        byte[]? fingerprint = null;
        using var client = new SftpClient(_fixture.Host, _fixture.Port, _fixture.Username, _fixture.Password);
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = e.FingerPrint;
            e.CanTrust = true;
        };

        client.Connect();
        client.Disconnect();

        Assert.NotNull(fingerprint);
        return BitConverter.ToString(fingerprint!).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string FlipFirstHexChar(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "0";
        }

        var chars = value.ToCharArray();
        chars[0] = chars[0] == '0' ? '1' : '0';
        return new string(chars);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ftp-transfer-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
