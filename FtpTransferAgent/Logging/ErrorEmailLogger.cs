using System.Net;
using System.Net.Mail;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Logging;

/// <summary>
/// エラーログをメールで送信するロガーのプロバイダー
/// </summary>
internal sealed class ErrorEmailLoggerProvider : ILoggerProvider
{
    private readonly SmtpOptions _options;

    public ErrorEmailLoggerProvider(SmtpOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ErrorEmailLogger(categoryName, _options);
    }

    public void Dispose()
    {
    }
}

internal sealed class ErrorEmailLogger : ILogger
{
    private readonly string _category;
    private readonly SmtpOptions _options;

    public ErrorEmailLogger(string category, SmtpOptions options)
    {
        _category = category;
        _options = options;
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
                Subject = $"[{_category}] Error",
                Body = message + (exception != null ? "\n" + exception : string.Empty)
            };
            foreach (var to in _options.To)
            {
                mail.To.Add(to);
            }
            client.Send(mail);
        }
        catch
        {
            // ignore mail errors to avoid recursive logging
        }
    }
}
