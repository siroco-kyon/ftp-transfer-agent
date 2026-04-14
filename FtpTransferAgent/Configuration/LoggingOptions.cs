using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// ログ出力に関する設定
/// </summary>
public class LoggingOptions
{
    [Required]
    [RegularExpression("^(Trace|Debug|Information|Warning|Error|Critical|None)$")]
    public string Level { get; set; } = "Information";

    [Required]
    public string RollingFilePath { get; set; } = string.Empty;

    /// <summary>
    /// ログファイルをローテーションする最大サイズ（バイト）。
    /// 既定値は 10MB。
    /// </summary>
    [Range(1024, long.MaxValue, ErrorMessage = "MaxBytes must be at least 1024 bytes")]
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// ログの自動削除設定。起動時に一度だけ古いログを掃除する。
    /// </summary>
    public LogRetentionOptions Retention { get; set; } = new();
}

/// <summary>
/// 古いログファイルの保持ポリシー。
/// </summary>
public class LogRetentionOptions
{
    /// <summary>
    /// true の場合のみクリーンアップを実行。既定は false (削除しない)。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 保持日数。この日数より古いログファイルは起動時に削除される。
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;
}
