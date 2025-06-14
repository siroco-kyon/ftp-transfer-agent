using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using Microsoft.Extensions.Logging;

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
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(Enum.Parse<LogLevel>(logging.Level, true));
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
if (!string.IsNullOrEmpty(logging.RollingFilePath))
{
    // ログをファイルにも出力する
    builder.Logging.AddProvider(new RollingFileLoggerProvider(logging));
}

// バックグラウンド処理を行う Worker を登録
builder.Services.AddHostedService<Worker>();

// ホストを構築して実行
var host = builder.Build();
host.Run();
