using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 1 つの転送先サーバーへの接続・送信設定
/// </summary>
public class DestinationOptions
{
    [Required]
    [RegularExpression("^(ftp|sftp)$")]
    public string Mode { get; set; } = "ftp";

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    [Required]
    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? PrivateKeyPassphrase { get; set; }

    public string? HostKeyFingerprint { get; set; }

    [Required]
    public string RemotePath { get; set; } = string.Empty;

    [Range(1, 16)]
    public int Concurrency { get; set; } = 1;

    public bool PreserveFolderStructure { get; set; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;
}
