using System.IO;
using System.Reflection;

namespace FtpTransferAgent.Tests;

/// <summary>
/// RollingFileLogger.CleanupOldLogs の保持期間とフォルダ削除を検証する
/// (RollingFileLogger は internal なのでリフレクション経由で呼ぶ)
/// </summary>
public class RollingFileLoggerRetentionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _rollingPath;

    public RollingFileLoggerRetentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "loglifetime-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _rollingPath = Path.Combine(_dir, "ftp-transfer-.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private static int CleanupOldLogs(string rollingFilePath, int retentionDays)
    {
        var type = typeof(Worker).Assembly.GetType("FtpTransferAgent.Logging.RollingFileLogger", true)!;
        var mi = type.GetMethod("CleanupOldLogs",
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(string), typeof(int) })!;
        return (int)mi.Invoke(null, new object[] { rollingFilePath, retentionDays })!;
    }

    private string WriteLogAt(DateTime date, string? suffix = null)
    {
        var sub = Path.Combine(_dir, date.ToString("yyyy"), date.ToString("MM"));
        Directory.CreateDirectory(sub);
        var name = $"ftp-transfer-{date:yyyyMMdd}{suffix}.log";
        var path = Path.Combine(sub, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void DeletesFilesOlderThanRetention()
    {
        var old = WriteLogAt(DateTime.UtcNow.Date.AddDays(-40));
        var recent = WriteLogAt(DateTime.UtcNow.Date.AddDays(-5));

        var deleted = CleanupOldLogs(_rollingPath, 30);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void EnabledFalse_BehaviorIsEquivalentToZeroDays()
    {
        var f = WriteLogAt(DateTime.UtcNow.Date.AddDays(-100));
        var deleted = CleanupOldLogs(_rollingPath, 0);
        Assert.Equal(0, deleted);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void UnparseableFilenames_AreSkipped()
    {
        var date = DateTime.UtcNow.Date.AddDays(-100);
        var sub = Path.Combine(_dir, date.ToString("yyyy"), date.ToString("MM"));
        Directory.CreateDirectory(sub);

        var badName = Path.Combine(sub, "ftp-transfer-notadate.log");
        File.WriteAllText(badName, "x");
        var wrongPrefix = Path.Combine(sub, "other-20200101.log");
        File.WriteAllText(wrongPrefix, "x");

        var deleted = CleanupOldLogs(_rollingPath, 30);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(badName));
        Assert.True(File.Exists(wrongPrefix));
    }

    [Fact]
    public void RemovesEmptyYearAndMonthFolders()
    {
        var date = DateTime.UtcNow.Date.AddDays(-60);
        var file = WriteLogAt(date);
        var monthDir = Path.GetDirectoryName(file)!;
        var yearDir = Path.GetDirectoryName(monthDir)!;

        CleanupOldLogs(_rollingPath, 30);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(monthDir));
        Assert.False(Directory.Exists(yearDir));
    }

    [Fact]
    public void KeepsFolders_WhenRecentFilesRemain()
    {
        var oldDate = DateTime.UtcNow.Date.AddDays(-60);
        var recentDate = DateTime.UtcNow.Date.AddDays(-2);
        WriteLogAt(oldDate);
        var recent = WriteLogAt(recentDate);

        CleanupOldLogs(_rollingPath, 30);

        Assert.True(File.Exists(recent));
        Assert.True(Directory.Exists(Path.GetDirectoryName(recent)!));
    }

    [Fact]
    public void HandlesRotatedSuffixedFilenames()
    {
        var old = WriteLogAt(DateTime.UtcNow.Date.AddDays(-40), "_1");
        CleanupOldLogs(_rollingPath, 30);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void DoesNotRemoveUnrelatedEmptyDirectories()
    {
        WriteLogAt(DateTime.UtcNow.Date.AddDays(-40));
        var unrelated = Path.Combine(_dir, "scratch");
        Directory.CreateDirectory(unrelated);

        CleanupOldLogs(_rollingPath, 30);

        Assert.True(Directory.Exists(unrelated));
    }
}
