using FtpTransferAgent;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

// アプリケーションのエントリーポイント

// ホストビルダーを生成
var builder = Host.CreateApplicationBuilder(args);

// 設定クラスを DI コンテナに登録し、起動時に検証を行う
// 配列は PostConfigure で明示的に置き換える。既定の配列バインドは C# 側の初期値に
// 設定値を「追記」するため、EndFileExtensions: [".TRG"] と書いても既定の
// ".END"/".end" が残ってしまう (指定した拡張子だけが有効にならない)。
builder.Services.AddOptions<WatchOptions>()
    .BindConfiguration("Watch")
    .PostConfigure(o =>
    {
        ReplaceArrayFromConfiguration(builder.Configuration, "Watch:AllowedExtensions", v => o.AllowedExtensions = v);
        ReplaceArrayFromConfiguration(builder.Configuration, "Watch:EndFileExtensions", v => o.EndFileExtensions = v);
    })
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<TransferOptions>().BindConfiguration("Transfer").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RetryOptions>().BindConfiguration("Retry").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HashOptions>().BindConfiguration("Hash").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<CleanupOptions>().BindConfiguration("Cleanup").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LoggingOptions>().BindConfiguration("Logging").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AppOptions>().BindConfiguration("App");

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

    // 起動時に古いログを掃除（Retention.Enabled=true 時のみ）
    if (logging.Retention?.Enabled == true)
    {
        try
        {
            var removed = RollingFileLogger.CleanupOldLogs(logging.RollingFilePath, logging.Retention.RetentionDays);
            Console.WriteLine($"INFO: Log retention cleanup removed {removed} file(s) older than {logging.Retention.RetentionDays} day(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Log retention cleanup failed: {ex.Message}");
        }
    }
}
if (smtp.Enabled)
{
    builder.Logging.AddProvider(new ErrorEmailLoggerProvider(smtp));
}

// 設定バリデーターを登録
builder.Services.AddSingleton<ConfigurationValidator>();
builder.Services.AddSingleton<ApplicationExitCode>();

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

    if (validationResult.HasInfos)
    {
        foreach (var info in validationResult.Infos)
        {
            Console.WriteLine($"INFO: {info}");
        }
    }

    // 二重起動を防止するロックを取得してから実行
    var appOptions = host.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    ProcessLock? procLock;
    try
    {
        procLock = ProcessLock.Acquire(appOptions.LockFilePath, watchOptions.Path);
        Console.WriteLine($"INFO: Acquired process lock at {procLock.LockFilePath}");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Environment.Exit(2);
        return; // 到達しないが静的解析のため
    }

    // host.Run() は終了時にホスト (IServiceProvider) を Dispose するため、
    // 終了コードを保持するシングルトンの参照は Run の前に取得しておく。
    // Run 後に host.Services を参照すると ObjectDisposedException となる。
    var exitCodeTracker = host.Services.GetRequiredService<ApplicationExitCode>();

    try
    {
        host.Run();
        if (exitCodeTracker.Code != 0)
        {
            Environment.ExitCode = exitCodeTracker.Code;
        }
    }
    finally
    {
        procLock.Dispose();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Application terminated unexpectedly: {ex.Message}");
    Environment.Exit(1);
}

// 設定セクションに配列が明示されている場合、C# 側の初期値を「置き換える」。
// Microsoft.Extensions.Configuration の既定の配列バインドはプロパティの初期値に
// 設定値を追記するため、初期値が空でない配列 (WatchOptions.EndFileExtensions 等) では
// 設定に書いていない既定値が残り続けてしまう。設定に書いた内容だけを有効にする。
static void ReplaceArrayFromConfiguration(IConfiguration configuration, string key, Action<string[]> apply)
{
    var section = configuration.GetSection(key);
    if (!section.Exists())
    {
        return;
    }

    apply(section.GetChildren()
        .Select(c => c.Value ?? string.Empty)
        .ToArray());
}
