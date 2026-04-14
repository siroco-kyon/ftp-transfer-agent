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

    /// <summary>
    /// 古いログファイルを削除する。ファイル名に含まれる YYYYMMDD をパースし、
    /// 指定日数より古いものを削除する。パースできないファイルは無視する。
    /// 空になった月・年フォルダも削除する。
    /// </summary>
    /// <returns>削除したファイル数</returns>
    public static int CleanupOldLogs(string rollingFilePath, int retentionDays)
    {
        if (string.IsNullOrWhiteSpace(rollingFilePath) || retentionDays <= 0)
        {
            return 0;
        }

        var baseDir = Path.GetDirectoryName(rollingFilePath);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            return 0;
        }

        var prefix = Path.GetFileNameWithoutExtension(rollingFilePath);
        var ext = Path.GetExtension(rollingFilePath);
        var cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
        var cleanupDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(baseDir, "*" + ext, SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var remainder = name.Substring(prefix.Length);
            // 期待形式: YYYYMMDD または YYYYMMDD_n
            if (remainder.Length < 8)
            {
                continue;
            }
            var datePart = remainder.Substring(0, 8);
            if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var fileDate))
            {
                continue;
            }
            if (fileDate.Date >= cutoff)
            {
                continue;
            }
            try
            {
                File.Delete(file);
                deleted++;
                AddCleanupCandidates(cleanupDirs, baseDir, file);
            }
            catch (IOException) { /* ファイルがロックされている場合は次回 */ }
            catch (UnauthorizedAccessException) { /* 権限がない場合はスキップ */ }
        }

        // 削除したログが置かれていた月・年フォルダだけを後片付けする
        foreach (var dir in cleanupDirs.OrderByDescending(GetPathDepth))
        {
            TryRemoveIfEmpty(dir);
        }

        return deleted;
    }

    private static void AddCleanupCandidates(HashSet<string> cleanupDirs, string baseDir, string filePath)
    {
        var monthDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(monthDir))
        {
            return;
        }

        cleanupDirs.Add(monthDir);

        var yearDir = Path.GetDirectoryName(monthDir);
        if (!string.IsNullOrEmpty(yearDir)
            && string.Equals(Path.GetDirectoryName(yearDir), baseDir, StringComparison.OrdinalIgnoreCase))
        {
            cleanupDirs.Add(yearDir);
        }
    }

    private static int GetPathDepth(string path)
    {
        return path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
    }

    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch { /* ignore */ }
    }
}
