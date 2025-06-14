using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 再試行に関する設定
/// </summary>
public class RetryOptions
{
    [Range(0, int.MaxValue)]
    public int MaxAttempts { get; set; } = 3;

    [Range(0, int.MaxValue)]
    public int DelaySeconds { get; set; } = 5;
}
