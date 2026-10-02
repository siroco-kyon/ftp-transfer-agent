using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent.Tests;

public class SftpConnectionLimiterTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task SameEndpoint_WaitsAtLimit_AndResumesWhenLeaseIsReleased(int limit)
    {
        var limiter = new SftpConnectionLimiter(limit);
        var leases = new List<IDisposable>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < limit; i++)
            {
                leases.Add(await limiter.AcquireAsync("SERVER", 22, timeout.Token));
            }
            var waiting = limiter.AcquireAsync("server", 22, timeout.Token);
            Assert.False(waiting.IsCompleted);

            // 別ホスト・別ポートは、同じホストの接続待ちに巻き込まれない。
            using var otherHost = await limiter.AcquireAsync("other", 22, timeout.Token);
            using var otherPort = await limiter.AcquireAsync("server", 2222, timeout.Token);
            leases[0].Dispose();
            leases[0].Dispose(); // 二重破棄で枠を増やさない。
            using var resumed = await waiting;
            var stillWaiting = limiter.AcquireAsync("server", 22, timeout.Token);
            Assert.False(stillWaiting.IsCompleted);
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stillWaiting);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public async Task CanceledWaitAndFailedConnection_DoNotConsumeSlots()
    {
        var limiter = new SftpConnectionLimiter(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var lease = await limiter.AcquireAsync("server", 22, timeout.Token);
            using var canceled = new CancellationTokenSource();
            var waiting = limiter.AcquireAsync("server", 22, canceled.Token);
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            throw new IOException("Simulated connection failure");
        });
        using var next = await limiter.AcquireAsync("server", 22, timeout.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void OutOfRangeConfiguration_IsRejectedAtStartup(int value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Transfer:SftpMaxConcurrentHandshakes"] = value.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<TransferOptions>().Bind(config.GetSection("Transfer")).ValidateDataAnnotations();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TransferOptions>>().Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SftpConnectionLimiter(value));
    }

    [Fact]
    public void CommandLine_CanOverrideJsonValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:SftpMaxConcurrentHandshakes"] = "4" })
            .AddCommandLine(["--Transfer:SftpMaxConcurrentHandshakes=8"])
            .Build();
        var options = config.GetSection("Transfer").Get<TransferOptions>()!;
        Assert.Equal(8, options.SftpMaxConcurrentHandshakes);
        Assert.Equal(4, new TransferOptions().SftpMaxConcurrentHandshakes);
    }
}
