using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FtpTransferAgent.Tests;

public sealed class ProgramEmailIntegrationTests
{
    [Fact]
    public async Task ProcessExit_WaitsForFiveLimitedErrorEmails_AndPreservesFailedSources()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(repository.FullName, "FtpTransferAgent")))
            repository = repository.Parent ?? throw new DirectoryNotFoundException("Repository root was not found.");
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(repository.FullName, "FtpTransferAgent", "bin", configuration, framework, "FtpTransferAgent.dll");
        Assert.True(File.Exists(dll), $"Build the application before running its process integration test: {dll}");

        var root = Path.Combine(Path.GetTempPath(), "smtp-process-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "watch");
        Directory.CreateDirectory(watch);
        var destination = Path.Combine(root, "destination-as-file");
        await File.WriteAllTextAsync(destination, "block directory creation");
        for (var index = 0; index < 20; index++)
            await File.WriteAllTextAsync(Path.Combine(watch, index + ".txt"), "keep this source");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(64);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var handlers = new ConcurrentBag<Task>();
        var received = 0;
        var accept = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                    handlers.Add(HandleAsync(connection));
                }
            }
            catch (OperationCanceledException) { }
        });

        async Task HandleAsync(TcpClient connection)
        {
            using (connection)
            {
                using var reader = new StreamReader(connection.GetStream(), Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(connection.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost test SMTP");
                while (await reader.ReadLineAsync(timeout.Token) is { } line)
                {
                    var command = line.Split(' ')[0].ToUpperInvariant();
                    if (command is "EHLO" or "HELO")
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 SIZE 10000000");
                    }
                    else if (command == "DATA")
                    {
                        await writer.WriteLineAsync("354 Send message");
                        while (await reader.ReadLineAsync(timeout.Token) is { } body && body != ".") { }
                        await Task.Delay(200, timeout.Token);
                        Interlocked.Increment(ref received);
                        await writer.WriteLineAsync("250 Accepted");
                    }
                    else if (command == "QUIT")
                    {
                        await writer.WriteLineAsync("221 Bye");
                        break;
                    }
                    else await writer.WriteLineAsync("250 OK");
                }
            }
        }

        try
        {
            var config = new
            {
                Watch = new { Path = watch, AllowedExtensions = new[] { ".txt" } },
                Transfer = new { Mode = "local", RemotePath = destination, Concurrency = 4 },
                Retry = new { MaxAttempts = 0 },
                Smtp = new { Enabled = true, RelayHost = "127.0.0.1", RelayPort = port, From = "sender@example.test", To = new[] { "receiver@example.test" }, MaxEmailsPerRun = 5 },
                Logging = new { RollingFilePath = "", Level = "Warning" },
                App = new { LockFilePath = Path.Combine(root, "agent.lock") }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(config));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(dll);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(1, process.ExitCode);
                Assert.Equal(5, Volatile.Read(ref received));
                Assert.Equal(20, Directory.GetFiles(watch, "*.txt").Length);
                Assert.Contains("Error email limit (5)", await error);
                await output;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            timeout.Cancel();
            await accept;
            await Task.WhenAll(handlers);
            Directory.Delete(root, recursive: true);
        }
    }
}
