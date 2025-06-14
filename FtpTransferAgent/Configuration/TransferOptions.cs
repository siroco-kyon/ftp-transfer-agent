using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 転送処理に関する設定
/// </summary>
public class TransferOptions
{
    [Required]
    [RegularExpression("ftp|sftp")]
    public string Mode { get; set; } = "ftp";

    [Required]
    [RegularExpression("get|put|both")]
    public string Direction { get; set; } = "put";

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>
    /// 並列転送数。1 以上を指定します。
    /// 既定値は 1 (逐次転送)。
    /// </summary>
    [Range(1, 16)]
    public int Concurrency { get; set; } = 1;
}
