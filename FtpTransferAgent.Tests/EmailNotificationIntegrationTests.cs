using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Tests;

[Collection("SMTP integration")]
public class EmailNotificationIntegrationTests
{
    private static SmtpOptions Options(TestSmtpServer server) => new()
    {
        Enabled = true,
        RelayHost = "127.0.0.1",
        RelayPort = server.Port,
        From = "sender@example.test",
        To = new[] { "receiver@example.test" },
        JobName = "売上データ連携",
        SourceServerName = "APP01"
    };

    [Fact]
    public async Task LegacySettings_UseOriginalSubjectAndBodyEvenWithDisplayScope()
    {
        await using var server = new TestSmtpServer();
        var options = Options(server);
        options.JobName = null;
        options.SourceServerName = null;
        using (var provider = new ErrorEmailLoggerProvider(options))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("LegacyCategory");
            using var scope = logger.BeginScope(EmailTransferContext.For(new DestinationOptions { DisplayName = "大阪FTP" }, TransferAction.Upload));
            logger.LogError("従来の詳細");
            await Task.Run(provider.Dispose);
        }
        var mail = Assert.Single(server.Messages);
        Assert.Equal("[LegacyCategory] Error", mail.Subject);
        Assert.Equal("従来の詳細", mail.Body.TrimEnd());
    }

    [Fact]
    public async Task ParallelQueueAndNestedClientLogs_SendCorrectJapanesePeerAndRestoreScopes()
    {
        await using var server = new TestSmtpServer();
        using (var provider = new ErrorEmailLoggerProvider(Options(server)))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var channel = Channel.CreateUnbounded<TransferItem>();
            for (var index = 0; index < 12; index++)
                channel.Writer.TryWrite(new TransferItem($"file-{index}.txt", TransferAction.Upload,
                    new DestinationOptions { Name = $"dest-{index}", DisplayName = $"拠点{index}", Host = $"server-{index}" }));
            channel.Writer.Complete();
            var queue = new TransferQueue(channel, new RetryOptions { MaxAttempts = 0 }, factory.CreateLogger<TransferQueue>(), concurrency: 4);
            await queue.StartAsync(async (item, _) =>
            {
                await Task.Yield();
                factory.CreateLogger("TransferClient").LogError("client failed for {File}", item.Path);
                throw new InvalidOperationException("final failure");
            }, CancellationToken.None);
            Assert.Equal(12, queue.GetStatistics().TotalFailed);
            factory.CreateLogger("OutsideQueue").LogError("outside");
            await Task.Run(provider.Dispose);
        }

        Assert.Equal(25, server.Messages.Count);
        foreach (var index in Enumerable.Range(0, 12))
        {
            var mails = server.Messages.Where(mail => mail.Subject == $"[売上データ連携][実行元: APP01] 拠点{index}へ転送失敗").ToArray();
            Assert.Equal(2, mails.Length);
            Assert.All(mails, mail => Assert.Contains($"接続先: ftp://server-{index}:21", mail.Body));
        }
        Assert.Single(server.Messages, mail => mail.Subject == "[売上データ連携][実行元: APP01] 転送処理でエラー");
    }

    [Fact]
    public async Task DownloadQueue_UsesPrimaryDestinationEvenWhenItemDestinationIsNull()
    {
        await using var server = new TestSmtpServer();
        var options = Options(server);
        options.SubjectPrefix = "検証";
        using (var provider = new ErrorEmailLoggerProvider(options))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var channel = Channel.CreateUnbounded<TransferItem>();
            channel.Writer.TryWrite(new TransferItem("report.txt", TransferAction.Download));
            channel.Writer.Complete();
            var queue = new TransferQueue(channel, new RetryOptions { MaxAttempts = 0 }, factory.CreateLogger<TransferQueue>(),
                destination: new DestinationOptions { DisplayName = "東京SFTP", Mode = "sftp", Host = "tokyo", Port = 22 });
            await queue.StartAsync((_, _) => throw new TimeoutException("connection timed out"), CancellationToken.None);
            await Task.Run(provider.Dispose);
        }
        var mail = Assert.Single(server.Messages);
        Assert.Equal("[検証][売上データ連携][実行元: APP01] 東京SFTPからの取得失敗", mail.Subject);
        Assert.Contains("connection timed out", mail.Body);
    }

    [Fact]
    public async Task SuppressionAndLimit_KeepSummaryAndStopAtConfiguredCount()
    {
        await using var server = new TestSmtpServer();
        var options = Options(server);
        options.MaxEmailsPerRun = 2;
        options.SuppressPerDestinationFailureDetailEmails = true;
        using (var provider = new ErrorEmailLoggerProvider(options))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var logger = factory.CreateLogger("Test");
            logger.LogWarning("not an email");
            logger.LogError(LogEvents.MultiDestinationTransferFailure, "suppressed detail");
            logger.LogError(LogEvents.MultiDestinationPartialFailure, "partial summary");
            logger.LogCritical("critical");
            logger.LogError("over limit");
            await Task.Run(provider.Dispose);
        }
        Assert.Equal(2, server.Messages.Count);
        Assert.Contains(server.Messages, mail => mail.Body.Contains("partial summary", StringComparison.Ordinal));
        Assert.Contains(server.Messages, mail => mail.Body.Contains("critical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryThenSuccess_OnlyWarningIsLoggedAndNoMailIsSent()
    {
        await using var server = new TestSmtpServer();
        using (var provider = new ErrorEmailLoggerProvider(Options(server)))
        using (var factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
        {
            var channel = Channel.CreateUnbounded<TransferItem>();
            channel.Writer.TryWrite(new TransferItem("report.txt", TransferAction.Upload));
            channel.Writer.Complete();
            var queue = new TransferQueue(channel, new RetryOptions { MaxAttempts = 1, DelaySeconds = 0 }, factory.CreateLogger<TransferQueue>());
            var attempts = 0;
            await queue.StartAsync((_, _) => ++attempts == 1 ? Task.FromException(new TimeoutException()) : Task.CompletedTask, CancellationToken.None);
            Assert.Equal(2, attempts);
            Assert.Equal(1, queue.GetStatistics().TotalCompleted);
            await Task.Run(provider.Dispose);
        }
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task WorkerPartialFailure_EmailsFailedPeerAndDisplayRenameDoesNotResendDeliveredFile()
    {
        await using var server = new TestSmtpServer();
        var root = Path.Combine(Path.GetTempPath(), "email-worker-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "watch");
        var primary = Path.Combine(root, "primary");
        var backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(watch);
        await File.WriteAllTextAsync(Path.Combine(watch, "report.txt"), "payload");
        await File.WriteAllTextAsync(backup, "block directory creation");
        var transfer = new TransferOptions
        {
            Name = "primary",
            DisplayName = "東京共有",
            Mode = "local",
            RemotePath = primary,
            StateDirectory = Path.Combine(root, "state"),
            RetryDirectory = Path.Combine(root, "retry"),
            AdditionalDestinations = new() { new DestinationOptions { Name = "backup", DisplayName = "大阪共有", Mode = "local", RemotePath = backup } }
        };
        try
        {
            Assert.Equal(1, await RunProcessAsync());
            Assert.Contains(server.Messages, mail => mail.Subject == "[売上データ連携][実行元: APP01] 大阪共有へ未配信");
            Assert.DoesNotContain(server.Messages, mail => mail.Subject.Contains("東京共有へ転送失敗", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(root, "retry", "report.txt")));
            var markers = Directory.GetFiles(transfer.StateDirectory, "*.marker");
            Assert.Single(markers);

            // 成功済み宛先を変更しておく。誤って再送した場合、この内容が上書きされる。
            await File.WriteAllTextAsync(Path.Combine(primary, "report.txt"), "must-not-overwrite");
            transfer.DisplayName = "東京共有・新表示";
            transfer.AdditionalDestinations[0].DisplayName = "大阪共有・新表示";
            File.Delete(backup);
            Directory.CreateDirectory(backup);
            var beforeRecovery = server.Messages.Count;
            Assert.Equal(0, await RunProcessAsync());
            Assert.Equal(beforeRecovery, server.Messages.Count);
            Assert.Equal("must-not-overwrite", await File.ReadAllTextAsync(Path.Combine(primary, "report.txt")));
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(backup, "report.txt")));
            Assert.True(File.Exists(Path.Combine(watch, "report.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        async Task<int> RunProcessAsync()
        {
            var config = new
            {
                Watch = new { Path = watch, AllowedExtensions = new[] { ".txt" } },
                Transfer = transfer,
                Retry = new { MaxAttempts = 0 },
                Hash = new { Enabled = false },
                Cleanup = new { DeleteAfterVerify = false },
                Smtp = Options(server),
                Logging = new { RollingFilePath = "", Level = "Warning" },
                App = new { LockFilePath = Path.Combine(root, "agent.lock") }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(config));
            var assemblyDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = assemblyDirectory.Parent!.Name;
            var repository = assemblyDirectory;
            while (!Directory.Exists(Path.Combine(repository.FullName, "FtpTransferAgent")))
                repository = repository.Parent ?? throw new DirectoryNotFoundException();
            var dll = Path.Combine(repository.FullName, "FtpTransferAgent", "bin", configuration, assemblyDirectory.Name, "FtpTransferAgent.dll");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.ArgumentList.Add(dll);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await output;
                await error;
                return process.ExitCode;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }
}
