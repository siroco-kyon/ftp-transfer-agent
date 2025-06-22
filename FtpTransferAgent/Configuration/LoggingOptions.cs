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
}
