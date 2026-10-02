using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

var port = int.Parse(args[0]);
var output = Path.GetFullPath(args[1]);
var privateKeyPath = args.Length > 2 ? args[2] : null;
Directory.CreateDirectory(output);
var source = Path.Combine(output, "payload.bin");
await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1024));
var expectedHash = await HashUtil.ComputeHashAsync(source, "SHA256", CancellationToken.None);
string? fingerprint = null;
using (var ready = new SftpClient("127.0.0.1", port, "testuser", "testpass"))
{
    ready.HostKeyReceived += (_, e) => fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
    ready.Connect();
}
var options = new DestinationOptions
{
    Mode = "sftp",
    Host = "127.0.0.1",
    Port = port,
    Username = "testuser",
    Password = "testpass",
    HostKeyFingerprint = fingerprint!,
    TimeoutSeconds = 10,
    VerifyUploadedFileExists = true
};
if (privateKeyPath is not null)
{
    options.PrivateKeyPath = privateKeyPath;
    options.Password = null;
}
var results = new List<Trial>();
var random = new Random(20261002);
try
{
    foreach (var heldCount in privateKeyPath is null ? new[] { 0, 4 } : new[] { 0 })
    {
        var limits = privateKeyPath is not null ? new[] { 4, 8 }
            : heldCount == 0 ? new[] { 1, 2, 4, 6, 8, 12, 16 } : new[] { 4, 6, 8, 12, 16 };
        var repetitions = heldCount == 0 ? 20 : 10;
        var held = new List<TcpClient>();
        try
        {
            // 一度だけ作って保持する。開閉の繰り返しによるPerSourcePenaltiesの影響を避ける。
            for (var i = 0; i < heldCount; i++)
            {
                var socket = new TcpClient();
                held.Add(socket);
                await socket.ConnectAsync("127.0.0.1", port);
                var stream = socket.GetStream();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var banner = new List<byte>();
                var one = new byte[1];
                while (await stream.ReadAsync(one, deadline.Token) == 1)
                {
                    banner.Add(one[0]);
                    if (one[0] == '\n') break;
                }
                if (!Encoding.ASCII.GetString(banner.ToArray()).StartsWith("SSH-"))
                    throw new IOException("Could not establish held SSH connection");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-auth-limit-probe\r\n"), deadline.Token);
            }
            var heldClock = Stopwatch.StartNew();
            for (var round = 1; round <= repetitions; round++)
            {
                foreach (var limit in limits.OrderBy(_ => random.Next()))
                {
                    // サーバーの既定LoginGraceTime=120秒を超えた試行を集計に混ぜない。
                    if (heldCount > 0 && heldClock.Elapsed > TimeSpan.FromSeconds(100))
                        throw new TimeoutException("Held authentication phase exceeded safe grace period");
                    var trial = new Trial { Limit = limit, HeldUnauthenticated = heldCount, Round = round };
                    var limiter = new SftpConnectionLimiter(limit);
                    var clock = new Stopwatch();
                    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var tasks = Enumerable.Range(0, 16).Select(async index =>
                    {
                        var result = new ConnectionResult { Index = index };
                        var logger = new ConnectionLogger(clock, result);
                        using var client = new SftpClientWrapper(options, logger, connectionLimiter: limiter);
                        var stage = "connect";
                        await start.Task;
                        try
                        {
                            if (!await client.ExistsAsync("/upload", timeout.Token)) throw new IOException("Upload directory missing");
                            result.ReadyMilliseconds = clock.Elapsed.TotalMilliseconds;
                            stage = "transfer";
                            var remote = $"/upload/probe-{heldCount}-{round}-{limit}-{index}.bin";
                            await client.UploadAsync(source, remote, timeout.Token);
                            result.HashMatches = expectedHash == await client.GetRemoteHashAsync(remote, "SHA256", timeout.Token);
                            if (!result.HashMatches) throw new IOException("SHA256 mismatch");
                            await client.DeleteAsync(remote, timeout.Token);
                            result.Success = true;
                        }
                        catch (Exception ex)
                        {
                            result.FailureStage = stage;
                            result.ErrorType = ex.GetType().FullName;
                            result.Error = ex.Message;
                        }
                        result.FinishedMilliseconds = clock.Elapsed.TotalMilliseconds;
                        return result;
                    }).ToArray();
                    clock.Start();
                    start.SetResult();
                    trial.Connections = await Task.WhenAll(tasks);
                    trial.TotalMilliseconds = clock.Elapsed.TotalMilliseconds;
                    trial.AllAuthenticatedMilliseconds = trial.Connections.All(c => c.ConnectedMilliseconds.HasValue)
                        ? trial.Connections.Max(c => c.ConnectedMilliseconds!.Value) : null;
                    results.Add(trial);
                    await File.WriteAllTextAsync(Path.Combine(output, "trials.json"), JsonSerializer.Serialize(results, JsonOptions.Value));
                    Console.WriteLine($"held={heldCount} round={round} limit={limit} success={trial.Connections.Count(c => c.Success)}/16 connect_all_ms={trial.AllAuthenticatedMilliseconds:F1}");
                    await Task.Delay(200);
                }
            }
        }
        finally
        {
            foreach (var socket in held) socket.Dispose();
        }
    }
    await File.WriteAllTextAsync(Path.Combine(output, "probe-environment.json"), JsonSerializer.Serialize(new
    {
        measuredAtUtc = DateTimeOffset.UtcNow,
        operatingSystem = Environment.OSVersion.ToString(),
        dotnet = Environment.Version.ToString(),
        sshNet = typeof(SftpClient).Assembly.GetName().Version!.ToString(),
        fingerprint,
        authentication = privateKeyPath is null ? "password" : "ed25519-private-key",
        connectionsPerTrial = 16,
        payloadBytes = 1024,
        retryAttempts = 0,
        randomSeed = 20261002,
        timer = "AllAuthenticatedMilliseconds: simultaneous release to last SFTP session-established log; includes gate wait and SFTP initialization; excludes tiny file transfer/hash/delete",
        reuseCheck = "Exists, upload, hash, delete use the same wrapper/connection"
    }, JsonOptions.Value));
}
finally
{
    File.Delete(source);
}

sealed class ConnectionLogger(Stopwatch clock, ConnectionResult result) : ILogger<SftpClientWrapper>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (formatter(state, exception).StartsWith("SFTP session established:")) result.ConnectedMilliseconds = clock.Elapsed.TotalMilliseconds;
    }
}
sealed class Trial
{
    public int Limit { get; set; }
    public int HeldUnauthenticated { get; set; }
    public int Round { get; set; }
    public double? AllAuthenticatedMilliseconds { get; set; }
    public double TotalMilliseconds { get; set; }
    public ConnectionResult[] Connections { get; set; } = [];
}
sealed class ConnectionResult
{
    public int Index { get; set; }
    public double? ConnectedMilliseconds { get; set; }
    public double? ReadyMilliseconds { get; set; }
    public double FinishedMilliseconds { get; set; }
    public bool HashMatches { get; set; }
    public bool Success { get; set; }
    public string? FailureStage { get; set; }
    public string? ErrorType { get; set; }
    public string? Error { get; set; }
}
static class JsonOptions
{
    public static readonly JsonSerializerOptions Value = new() { WriteIndented = true };
}
