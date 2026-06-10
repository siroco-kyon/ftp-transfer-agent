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
            if (TryReadLockInfo(path, out var existingPid, out var existingName) && IsProcessAlive(existingPid, existingName))
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
            // 1 行目: PID、2 行目: プロセス名。
            // プロセス名も照合することで、PID が無関係なプロセスに再利用された場合の
            // 「実行中」誤判定 (起動拒否) を防ぐ
            var content = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\n" + GetCurrentProcessName();
            var pidBytes = System.Text.Encoding.UTF8.GetBytes(content);
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

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        return Path.Combine(baseDir, "FtpTransferAgent", "ftp-transfer-agent.lock");
    }

    private static string GetCurrentProcessName()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryReadLockInfo(string path, out int pid, out string? processName)
    {
        pid = 0;
        processName = null;
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return false;
            }
            // 2 行目が無い旧形式のロックファイルも PID のみで判定できるよう許容する
            if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
            {
                processName = lines[1].Trim();
            }
            return int.TryParse(lines[0].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out pid);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid, string? expectedProcessName)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                return false;
            }
            // プロセス名が記録されている場合は照合し、PID 再利用による誤判定を防ぐ
            if (!string.IsNullOrEmpty(expectedProcessName)
                && !string.Equals(proc.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
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
