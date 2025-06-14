using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<WatchOptions>().BindConfiguration("Watch").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TransferOptions>().BindConfiguration("Transfer").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetryOptions>().BindConfiguration("Retry").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HashOptions>().BindConfiguration("Hash").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CleanupOptions>().BindConfiguration("Cleanup").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LoggingOptions>().BindConfiguration("Logging").ValidateDataAnnotations().ValidateOnStart();

var logging = builder.Configuration.GetSection("Logging").Get<LoggingOptions>() ?? new LoggingOptions();
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(logging.Level, true));
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
if (!string.IsNullOrEmpty(logging.RollingFilePath))
{
    builder.Logging.AddProvider(new RollingFileLoggerProvider(logging));
}

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
