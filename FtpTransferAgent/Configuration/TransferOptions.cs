using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

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
}
