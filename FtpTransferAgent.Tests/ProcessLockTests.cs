using System.IO;
using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="ProcessLock"/> の取得・解放・死に PID の上書き動作を検証する
/// </summary>
public class ProcessLockTests : IDisposable
{
    private readonly string _dir;

    public ProcessLockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "proclock-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string NewLockPath() => Path.Combine(_dir, Path.GetRandomFileName() + ".lock");

    // ProcessLock は FileShare.Read でファイルを保持しているため、書き込み保護された状態のまま読み出すには
    // FileShare.ReadWrite を指定する必要がある。
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Trim();
    }

    // ロックファイルは「1 行目: PID、2 行目: プロセス名」形式
    private static string ReadLockPid(string path)
    {
        var lines = ReadShared(path).Split('\n');
        return lines[0].Trim();
    }

    [Fact]
    public void Acquire_CreatesFileWithCurrentPidAndProcessName()
    {
        var path = NewLockPath();
        using var l = ProcessLock.Acquire(path);

        Assert.True(File.Exists(path));
        var lines = ReadShared(path).Split('\n');
        Assert.Equal(Environment.ProcessId.ToString(), lines[0].Trim());
        Assert.Equal(2, lines.Length);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(current.ProcessName, lines[1].Trim());
    }

    [Fact]
    public void Dispose_RemovesLockFile()
    {
        var path = NewLockPath();
        var l = ProcessLock.Acquire(path);
        Assert.True(File.Exists(path));
        l.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DoubleAcquire_ThrowsWhenCurrentProcessLockHeld()
    {
        var path = NewLockPath();
        using var first = ProcessLock.Acquire(path);

        // 現在プロセスは生存中なので、2 回目の取得は失敗する
        Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(path));
    }

    [Fact]
    public void StaleLockFile_IsOverwritten()
    {
        var path = NewLockPath();
        // 存在し得ないであろう PID を書き込んで「死に PID」を模擬
        File.WriteAllText(path, "2147483646");

        using var l = ProcessLock.Acquire(path);

        Assert.True(File.Exists(path));
        Assert.Equal(Environment.ProcessId.ToString(), ReadLockPid(path));
    }

    [Fact]
    public void UnparseableLockFile_IsOverwritten()
    {
        var path = NewLockPath();
        File.WriteAllText(path, "not-a-pid");

        using var l = ProcessLock.Acquire(path);
        Assert.Equal(Environment.ProcessId.ToString(), ReadLockPid(path));
    }

    [Fact]
    public void LockHeldByReusedPid_IsOverwritten_WhenProcessNameDiffers()
    {
        var path = NewLockPath();
        // 現在プロセスの PID は生存中だが、プロセス名が異なる -> PID 再利用とみなし取得できる
        File.WriteAllText(path, Environment.ProcessId + "\nsome-other-process-name");

        using var l = ProcessLock.Acquire(path);
        Assert.Equal(Environment.ProcessId.ToString(), ReadLockPid(path));
    }

    [Fact]
    public void LegacyPidOnlyLockFile_StillBlocks_WhenProcessAlive()
    {
        var path = NewLockPath();
        // 旧形式 (PID のみ) のロックファイルでも、PID が生存していれば取得は失敗する
        File.WriteAllText(path, Environment.ProcessId.ToString());

        Assert.Throws<InvalidOperationException>(() => ProcessLock.Acquire(path));
    }

    [Fact]
    public void NullPath_UsesDefaultLocation()
    {
        using var l = ProcessLock.Acquire(null);
        Assert.True(File.Exists(l.LockFilePath));
        Assert.EndsWith("ftp-transfer-agent.lock", l.LockFilePath);
        Assert.NotEqual(Path.Combine(AppContext.BaseDirectory, "ftp-transfer-agent.lock"), l.LockFilePath);
    }
}
