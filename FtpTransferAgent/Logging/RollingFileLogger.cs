using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Logging;

/// <summary>
/// 日付とサイズでローテーションするファイルロガーのプロバイダー
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly LoggingOptions _options;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();

    public RollingFileLoggerProvider(LoggingOptions options)
    {
        _options = options;
        var dir = Path.GetDirectoryName(_options.RollingFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, _options));
    }

    public void Dispose()
    {
        foreach (var logger in _loggers.Values)
        {
            logger.Dispose();
        }
        _loggers.Clear();
    }
}

/// <summary>
/// 1 日ごと、かつ指定サイズでファイルをローテーションするロガー
/// </summary>
internal sealed class RollingFileLogger : ILogger, IDisposable
{
    private readonly string _category;
    private readonly LoggingOptions _options;
    private readonly object _lock = new();
    private DateTime _currentDate = DateTime.UtcNow.Date;
    private int _index;
    private StreamWriter? _writer;
    private bool _disposed;

    public RollingFileLogger(string category, LoggingOptions options)
    {
        _category = category;
        _options = options;
    }

    // 現在のログファイルパスを取得
    private string GetPath()
    {
        var basePath = _options.RollingFilePath;
        var dir = Path.GetDirectoryName(basePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        var suffix = _index > 0 ? $"_{_index}" : string.Empty;
        return Path.Combine(dir, $"{name}{_currentDate:yyyyMMdd}{suffix}{ext}");
    }

    // ログファイルのローテーションを管理
    private void EnsureWriter()
    {
        var now = DateTime.UtcNow.Date;
        if (_writer == null)
        {
            var fs = new FileStream(GetPath(), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs) { AutoFlush = true };
            _currentDate = now;
            return;
        }
        if (now != _currentDate)
        {
            _writer.Dispose();
            _index = 0;
            _currentDate = now;
            var fs1 = new FileStream(GetPath(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs1) { AutoFlush = true };
            return;
        }
        
        // ファイルサイズチェックを安全に行う
        try
        {
            if (new FileInfo(GetPath()).Length >= _options.MaxBytes)
            {
                _writer.Dispose();
                _index++;
                var fs2 = new FileStream(GetPath(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs2) { AutoFlush = true };
            }
        }
        catch (IOException)
        {
            // ファイルアクセス中の場合は次回チェック
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (_lock)
        {
            EnsureWriter();
            _writer!.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {_category} {message}");
            if (exception != null)
            {
                _writer.WriteLine(exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _writer?.Dispose();
            _writer = null;
            _disposed = true;
        }
    }
}
