using FtpTransferAgent;
using FtpTransferAgent.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<WatchOptions>().BindConfiguration("Watch").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TransferOptions>().BindConfiguration("Transfer").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetryOptions>().BindConfiguration("Retry").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HashOptions>().BindConfiguration("Hash").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CleanupOptions>().BindConfiguration("Cleanup").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LoggingOptions>().BindConfiguration("Logging").ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
