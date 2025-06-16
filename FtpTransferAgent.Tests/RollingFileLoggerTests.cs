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

            file = Path.Combine(dir, $"log{DateTime.UtcNow:yyyyMMdd}.txt");
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

            baseName = Path.Combine(dir, $"log{DateTime.UtcNow:yyyyMMdd}");
        }

        Assert.True(File.Exists(baseName + ".txt"));
        Assert.True(File.Exists(baseName + "_1.txt"));

        Directory.Delete(dir, true);
    }
}

