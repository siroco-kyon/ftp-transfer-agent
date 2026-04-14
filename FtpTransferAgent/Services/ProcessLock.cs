using System;
using System.Diagnostics;
using System.IO;

namespace FtpTransferAgent.Services;

/// <summary>
/// 二重起動を防止するためのロックファイル。
/// PID をロックファイルに書き込み、既存ロックの PID が生存している場合は取得を失敗させる。
/// </summary>
public sealed class ProcessLock : IDisposable
{
    private readonly string _lockFilePath;
    private FileStream? _stream;
    private bool _disposed;

    public string LockFilePath => _lockFilePath;

    private ProcessLock(string lockFilePath, FileStream stream)
    {
        _lockFilePath = lockFilePath;
        _stream = stream;
    }

    /// <summary>
    /// ロックを取得する。既存ロックがあり、該当 PID が生存している場合は
    /// <see cref="InvalidOperationException"/> をスローする。
    /// </summary>
    public static ProcessLock Acquire(string? lockFilePath)
    {
        var path = ResolveLockFilePath(lockFilePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 既存ロックがあれば PID を読み、生存確認
        if (File.Exists(path))
        {
            if (TryReadPid(path, out var existingPid) && IsProcessAlive(existingPid))
            {
                throw new InvalidOperationException(
                    $"Another instance is running (PID={existingPid}, lock file={path}).");
            }
            // 死に PID なら安全に上書きするため削除
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to remove stale lock file {path}: {ex.Message}", ex);
            }
        }

        // 排他書き込みでロックファイル作成
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to acquire lock file {path}: {ex.Message}", ex);
        }

        try
        {
            var pidBytes = System.Text.Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fs.Write(pidBytes, 0, pidBytes.Length);
            fs.Flush();
        }
        catch
        {
            fs.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
            throw;
        }

        return new ProcessLock(path, fs);
    }

    private static string ResolveLockFilePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        return Path.Combine(AppContext.BaseDirectory, "ftp-transfer-agent.lock");
    }

    private static bool TryReadPid(string path, out int pid)
    {
        pid = 0;
        try
        {
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out pid);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            // 該当 PID のプロセスが存在しない
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignore
        }
        _stream = null;

        try
        {
            if (File.Exists(_lockFilePath))
            {
                File.Delete(_lockFilePath);
            }
        }
        catch
        {
            // ignore
        }
    }
}
