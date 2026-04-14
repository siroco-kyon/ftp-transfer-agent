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

    // 現在のログファイルパスを取得（年月サブフォルダ付き）
    private string GetPath()
    {
        var basePath = _options.RollingFilePath;
        var baseDir = Path.GetDirectoryName(basePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        var suffix = _index > 0 ? $"_{_index}" : string.Empty;
        var subDir = Path.Combine(baseDir, _currentDate.ToString("yyyy"), _currentDate.ToString("MM"));
        return Path.Combine(subDir, $"{name}{_currentDate:yyyyMMdd}{suffix}{ext}");
    }

    // ログファイルのローテーションを管理
    private void EnsureWriter()
    {
        var now = DateTime.UtcNow.Date;
        if (_writer == null)
        {
            _writer = OpenWriter(FileMode.Append);
            _currentDate = now;
            return;
        }
        if (now != _currentDate)
        {
            _writer.Dispose();
            _writer = null;
            _index = 0;
            _currentDate = now;
            _writer = OpenWriter(FileMode.Create);
            return;
        }

        // ファイルサイズチェックを安全に行う
        try
        {
            if (new FileInfo(GetPath()).Length >= _options.MaxBytes)
            {
                _writer.Dispose();
                _writer = null;
                _index++;
                _writer = OpenWriter(FileMode.Create);
            }
        }
        catch (IOException)
        {
            // ファイルアクセス中の場合は次回チェック
        }
    }

    // StreamWriter 生成失敗時に FileStream がリークしないよう安全に生成する
    private StreamWriter OpenWriter(FileMode mode)
    {
        var path = GetPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var fs = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite);
        try
        {
            return new StreamWriter(fs) { AutoFlush = true };
        }
        catch
        {
            fs.Dispose();
            throw;
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
