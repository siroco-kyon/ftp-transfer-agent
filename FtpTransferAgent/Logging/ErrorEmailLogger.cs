using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Logging;

/// <summary>
/// エラーログをメールで送信するロガーのプロバイダー。
/// 送信タスクを追跡し、Dispose 時に未完了の送信を待機することで、
/// バッチ終了直前のエラーメールが失われないようにする。
/// </summary>
internal sealed class ErrorEmailLoggerProvider : ILoggerProvider
{
    private readonly SmtpOptions _options;
    private readonly ErrorEmailDispatcher _dispatcher;

    public ErrorEmailLoggerProvider(SmtpOptions options)
    {
        _options = options;
        _dispatcher = new ErrorEmailDispatcher(options);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ErrorEmailLogger(categoryName, _options, _dispatcher);
    }

    public void Dispose()
    {
        // ワンショットバッチではプロバイダー破棄直後にプロセスが終了するため、
        // ここで送信中のメールを待たないと最終エラーの通知が失われる
        _dispatcher.WaitForPendingSends(TimeSpan.FromSeconds(15));
    }
}

/// <summary>
/// エラーメールの非同期送信を管理するディスパッチャー。
/// 送信中タスクの追跡と、1 回の実行あたりの送信上限 (スパム防止) を担う。
/// </summary>
internal sealed class ErrorEmailDispatcher
{
    private readonly SmtpOptions _options;
    private readonly ConcurrentDictionary<Task, byte> _pendingSends = new();
    private int _enqueuedCount;

    public ErrorEmailDispatcher(SmtpOptions options)
    {
        _options = options;
    }

    public void Enqueue(string category, string message, Exception? exception)
    {
        // 送信上限 (0 以下は無制限)。大量エラー時のメール洪水を防ぐ
        if (_options.MaxEmailsPerRun > 0)
        {
            var count = Interlocked.Increment(ref _enqueuedCount);
            if (count > _options.MaxEmailsPerRun)
            {
                if (count == _options.MaxEmailsPerRun + 1)
                {
                    Console.Error.WriteLine(
                        $"WARNING: Error email limit ({_options.MaxEmailsPerRun}) reached. Further error emails are suppressed for this run.");
                }
                return;
            }
        }

        var sendTask = Task.Run(async () =>
        {
            try
            {
                using var client = new SmtpClient(_options.RelayHost, _options.RelayPort)
                {
                    EnableSsl = _options.UseTls
                };
                if (!string.IsNullOrEmpty(_options.Username))
                {
                    client.Credentials = new NetworkCredential(_options.Username, _options.Password);
                }
                using var mail = new MailMessage
                {
                    From = new MailAddress(_options.From),
                    Subject = $"[{category}] Error",
                    Body = message + (exception != null ? "\n" + exception : string.Empty)
                };
                foreach (var to in _options.To)
                {
                    mail.To.Add(to);
                }
                await client.SendMailAsync(mail).ConfigureAwait(false);
            }
            catch
            {
                // ignore async mail errors to avoid recursive logging
            }
        });

        _pendingSends.TryAdd(sendTask, 0);
        sendTask.ContinueWith(t => _pendingSends.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// 送信中のメールが完了するまで待機する (タイムアウト付きベストエフォート)。
    /// </summary>
    public void WaitForPendingSends(TimeSpan timeout)
    {
        var pending = _pendingSends.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, timeout);
        }
        catch
        {
            // 送信エラーは送信タスク側で握りつぶしているが、
            // タイムアウト等でここに到達しても終了処理を妨げない
        }
    }
}

internal sealed class ErrorEmailLogger : ILogger
{
    private readonly string _category;
    private readonly SmtpOptions _options;
    private readonly ErrorEmailDispatcher _dispatcher;

    // リフレクション経由の利用 (テスト) との互換のため 2 引数コンストラクタを維持
    public ErrorEmailLogger(string category, SmtpOptions options)
        : this(category, options, new ErrorEmailDispatcher(options))
    {
    }

    public ErrorEmailLogger(string category, SmtpOptions options, ErrorEmailDispatcher dispatcher)
    {
        _category = category;
        _options = options;
        _dispatcher = dispatcher;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        _dispatcher.Enqueue(_category, message, exception);
    }
}
