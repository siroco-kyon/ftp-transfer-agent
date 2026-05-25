using System.IO;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="RollingFileLogger"/> の基本動作を検証するテスト
/// </summary>
public class RollingFileLoggerTests
{
    [Fact]
    public void Log_WritesMessage()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var options = new LoggingOptions { RollingFilePath = Path.Combine(dir, "log.txt"), MaxBytes = 1024 };
        var type = typeof(Worker).Assembly.GetType("FtpTransferAgent.Logging.RollingFileLoggerProvider", true)!;

        string file;
        using (var provider = (ILoggerProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { options }, null)!)
        {
            var logger = provider.CreateLogger("Test");

            logger.LogInformation("hello");

            file = Path.Combine(dir, $"{DateTime.UtcNow:yyyy}", $"{DateTime.UtcNow:MM}", $"log{DateTime.UtcNow:yyyyMMdd}.txt");
            Assert.True(File.Exists(file));
        }

        var content = File.ReadAllText(file);
        Assert.Contains("hello", content);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Log_OverSize_RotatesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var options = new LoggingOptions { RollingFilePath = Path.Combine(dir, "log.txt"), MaxBytes = 1 };
        var type = typeof(Worker).Assembly.GetType("FtpTransferAgent.Logging.RollingFileLoggerProvider", true)!;
        string baseName;
        using (var provider = (ILoggerProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { options }, null)!)
        {
            var logger = provider.CreateLogger("Test");

            logger.LogInformation("first");
            logger.LogInformation("second");

            baseName = Path.Combine(dir, $"{DateTime.UtcNow:yyyy}", $"{DateTime.UtcNow:MM}", $"log{DateTime.UtcNow:yyyyMMdd}");
        }

        Assert.True(File.Exists(baseName + ".txt"));
        Assert.True(File.Exists(baseName + "_1.txt"));

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Log_WhenCurrentFileAlreadyOverSize_RotatesBeforeFirstWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var options = new LoggingOptions { RollingFilePath = Path.Combine(dir, "log.txt"), MaxBytes = 1 };
        var today = DateTime.UtcNow;
        var baseName = Path.Combine(dir, $"{today:yyyy}", $"{today:MM}", $"log{today:yyyyMMdd}");
        var currentFile = baseName + ".txt";
        Directory.CreateDirectory(Path.GetDirectoryName(currentFile)!);
        File.WriteAllText(currentFile, "already full");

        var type = typeof(Worker).Assembly.GetType("FtpTransferAgent.Logging.RollingFileLoggerProvider", true)!;
        using (var provider = (ILoggerProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { options }, null)!)
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("fresh");
        }

        Assert.DoesNotContain("fresh", File.ReadAllText(currentFile));
        Assert.Contains("fresh", File.ReadAllText(baseName + "_1.txt"));

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Log_OverSize_WithMultipleCategories_DoesNotOverwriteRotatedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var options = new LoggingOptions { RollingFilePath = Path.Combine(dir, "log.txt"), MaxBytes = 1 };
        var type = typeof(Worker).Assembly.GetType("FtpTransferAgent.Logging.RollingFileLoggerProvider", true)!;

        using (var provider = (ILoggerProvider)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { options }, null)!)
        {
            var loggerA = provider.CreateLogger("CategoryA");
            var loggerB = provider.CreateLogger("CategoryB");

            loggerA.LogInformation("alpha");
            loggerA.LogInformation("bravo");
            loggerB.LogInformation("charlie");
            loggerB.LogInformation("delta");
        }

        var content = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories)
                .OrderBy(path => path)
                .Select(File.ReadAllText));

        Assert.Contains("alpha", content);
        Assert.Contains("bravo", content);
        Assert.Contains("charlie", content);
        Assert.Contains("delta", content);

        Directory.Delete(dir, true);
    }
}
