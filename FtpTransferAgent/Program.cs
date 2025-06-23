using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// アプリケーションのエントリーポイント

// ホストビルダーを生成
var builder = Host.CreateApplicationBuilder(args);

// 設定クラスを DI コンテナに登録し、起動時に検証を行う
builder.Services.AddOptions<WatchOptions>().BindConfiguration("Watch").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TransferOptions>().BindConfiguration("Transfer").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetryOptions>().BindConfiguration("Retry").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HashOptions>().BindConfiguration("Hash").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CleanupOptions>().BindConfiguration("Cleanup").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LoggingOptions>().BindConfiguration("Logging").ValidateDataAnnotations().ValidateOnStart();

// ログ出力の設定を読み込み
var logging = builder.Configuration.GetSection("Logging").Get<LoggingOptions>() ?? new LoggingOptions();
var smtp = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
builder.Logging.ClearProviders();
// 設定値のパースに例外処理を追加
var logLevel = LogLevel.Information; // デフォルト値
if (!string.IsNullOrEmpty(logging.Level) && !Enum.TryParse<LogLevel>(logging.Level, true, out logLevel))
{
    Console.WriteLine($"Warning: Invalid log level '{logging.Level}'. Using default 'Information'.");
    logLevel = LogLevel.Information;
}
builder.Logging.SetMinimumLevel(logLevel);
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
if (!string.IsNullOrEmpty(logging.RollingFilePath))
{
    // ログをファイルにも出力する
    builder.Logging.AddProvider(new RollingFileLoggerProvider(logging));
}
if (smtp.Enabled)
{
    builder.Logging.AddProvider(new ErrorEmailLoggerProvider(smtp));
}

// 設定バリデーターを登録
builder.Services.AddSingleton<ConfigurationValidator>();

// バックグラウンド処理を行う Worker を登録
builder.Services.AddHostedService<Worker>();

// ホストを構築して実行
try
{
    var host = builder.Build();
    
    // 設定の包括的バリデーションを実行
    var validator = host.Services.GetRequiredService<ConfigurationValidator>();
    var watchOptions = host.Services.GetRequiredService<IOptions<WatchOptions>>().Value;
    var transferOptions = host.Services.GetRequiredService<IOptions<TransferOptions>>().Value;
    var retryOptions = host.Services.GetRequiredService<IOptions<RetryOptions>>().Value;
    var hashOptions = host.Services.GetRequiredService<IOptions<HashOptions>>().Value;
    var cleanupOptions = host.Services.GetRequiredService<IOptions<CleanupOptions>>().Value;
    
    ConfigurationValidationResult validationResult = validator.ValidateConfiguration(
        watchOptions, transferOptions, retryOptions, hashOptions, cleanupOptions);
    
    if (!validationResult.IsValid)
    {
        Console.WriteLine("Configuration validation failed:");
        foreach (var error in validationResult.Errors)
        {
            Console.WriteLine($"ERROR: {error}");
        }
        Environment.Exit(1);
    }
    
    if (validationResult.HasWarnings)
    {
        Console.WriteLine("Configuration warnings:");
        foreach (var warning in validationResult.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
    }
    
    host.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Application terminated unexpectedly: {ex.Message}");
    Environment.Exit(1);
}
