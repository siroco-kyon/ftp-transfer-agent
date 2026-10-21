using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FtpTransferAgent.Tests;

/// <summary>本番 SmtpClient の MIME を受信・復号するローカルテストサーバー。</summary>
internal sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(60));
    private readonly ConcurrentBag<Task> _handlers = new();
    private readonly Task _accept;
    public ConcurrentQueue<(string Subject, string Body)> Messages { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public TestSmtpServer()
    {
        _listener.Start(64);
        // xUnit の同時実行数が少ない環境でも、送信完了待機と受信処理を相互待機させない。
        _accept = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _handlers.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient connection)
    {
        using (connection)
        using (var reader = new StreamReader(connection.GetStream(), Encoding.ASCII, leaveOpen: true))
        using (var writer = new StreamWriter(connection.GetStream(), Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
        {
            await writer.WriteLineAsync("220 localhost test SMTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var command = line.Split(' ')[0].ToUpperInvariant();
                if (command is "EHLO" or "HELO")
                    await writer.WriteLineAsync("250 localhost");
                else if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 Send message");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".")
                        data.Append(body.StartsWith("..", StringComparison.Ordinal) ? body[1..] : body).Append("\r\n");
                    var parts = data.ToString().Split("\r\n\r\n", 2);
                    var headers = Regex.Replace(parts[0], @"\r\n[ \t]+", " ");
                    var subject = Regex.Match(headers, @"^Subject: (.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                    subject = Regex.Replace(subject, @"\?=\s+(?==\?)", "?=");
                    subject = Regex.Replace(subject, @"=\?utf-8\?B\?([^?]+)\?=", match => Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)), RegexOptions.IgnoreCase);
                    var text = headers.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : parts[1];
                    Messages.Enqueue((subject, text));
                    await writer.WriteLineAsync("250 Accepted");
                }
                else if (command == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                    await writer.WriteLineAsync("250 OK");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        try { await Task.WhenAll(_handlers); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        _stop.Dispose();
    }
}
