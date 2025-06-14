namespace FtpTransferAgent.Configuration;

/// <summary>
/// ログ出力に関する設定
/// </summary>
public class LoggingOptions
{
    public string Level { get; set; } = "Information";
    public string RollingFilePath { get; set; } = string.Empty;
    /// <summary>
    /// ログファイルをローテーションする最大サイズ（バイト）。
    /// 既定値は 10MB。
    /// </summary>
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;
}
