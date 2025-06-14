using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

public class RetryOptions
{
    [Range(0, int.MaxValue)]
    public int MaxAttempts { get; set; } = 3;

    [Range(0, int.MaxValue)]
    public int DelaySeconds { get; set; } = 5;
}
