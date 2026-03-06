using System.Reflection;
using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Tests;

public class ErrorEmailLoggerProductionTests
{
    private static readonly Assembly AgentAssembly = typeof(Worker).Assembly;
    private static readonly Type ProviderType = AgentAssembly.GetType("FtpTransferAgent.Logging.ErrorEmailLoggerProvider", throwOnError: true)!;
    private static readonly Type LoggerType = AgentAssembly.GetType("FtpTransferAgent.Logging.ErrorEmailLogger", throwOnError: true)!;

    [Fact]
    public void Provider_ShouldCreateProductionErrorLogger()
    {
        var provider = CreateProvider(CreateOptions());
        var disposableProvider = Assert.IsAssignableFrom<IDisposable>(provider);
        using (disposableProvider)
        {
            var createLoggerMethod = ProviderType.GetMethod("CreateLogger")!;
            var logger = createLoggerMethod.Invoke(provider, new object[] { "Category" });

            Assert.NotNull(logger);
            Assert.Equal(LoggerType, logger!.GetType());
        }
    }

    [Fact]
    public void Logger_IsEnabled_ShouldMatchErrorThreshold()
    {
        var logger = CreateLogger(CreateOptions());

        Assert.True(InvokeIsEnabled(logger, LogLevel.Error));
        Assert.True(InvokeIsEnabled(logger, LogLevel.Critical));
        Assert.False(InvokeIsEnabled(logger, LogLevel.Warning));
        Assert.False(InvokeIsEnabled(logger, LogLevel.Information));
    }

    [Fact]
    public void Logger_BeginScope_ShouldReturnNull()
    {
        var logger = CreateLogger(CreateOptions());
        var scope = InvokeBeginScope(logger, "scope");
        Assert.Null(scope);
    }

    [Fact]
    public void Logger_Log_ShouldIgnoreNonErrorLevels()
    {
        var logger = CreateLogger(CreateOptions());

        var exception = Record.Exception(() =>
            InvokeLog(logger, LogLevel.Information, "info message", null));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Logger_Log_ShouldNotThrow_WhenSmtpOperationFails()
    {
        var options = CreateOptions();
        options.From = "invalid-address";
        options.RelayHost = "127.0.0.1";
        options.RelayPort = 1;

        var logger = CreateLogger(options);

        var exception = Record.Exception(() =>
            InvokeLog(logger, LogLevel.Error, "error message", new InvalidOperationException("boom")));

        Assert.Null(exception);

        // Give the background SMTP task time to run.
        await Task.Delay(200);
    }

    private static object CreateProvider(SmtpOptions options)
    {
        return Activator.CreateInstance(ProviderType, options)!;
    }

    private static object CreateLogger(SmtpOptions options)
    {
        return Activator.CreateInstance(LoggerType, "TestCategory", options)!;
    }

    private static bool InvokeIsEnabled(object logger, LogLevel level)
    {
        var method = LoggerType.GetMethod("IsEnabled")!;
        return (bool)method.Invoke(logger, new object[] { level })!;
    }

    private static IDisposable? InvokeBeginScope(object logger, string state)
    {
        var method = LoggerType.GetMethod("BeginScope")!.MakeGenericMethod(typeof(string));
        return (IDisposable?)method.Invoke(logger, new object[] { state });
    }

    private static void InvokeLog(object logger, LogLevel level, string state, Exception? exception)
    {
        var method = LoggerType.GetMethod("Log")!.MakeGenericMethod(typeof(string));
        Func<string, Exception?, string> formatter = (s, e) => s;
        method.Invoke(logger, new object?[] { level, new EventId(1, "test"), state, exception, formatter });
    }

    private static SmtpOptions CreateOptions()
    {
        return new SmtpOptions
        {
            Enabled = true,
            RelayHost = "localhost",
            RelayPort = 25,
            UseTls = false,
            From = "noreply@example.com",
            To = new[] { "admin@example.com" }
        };
    }
}
