using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// SMTP 通知に関する設定
/// </summary>
public class SmtpOptions
{
    /// <summary>
    /// エラーメール送信を有効にするかどうか
    /// </summary>
    public bool Enabled { get; set; }
    public string RelayHost { get; set; } = string.Empty;
    public int RelayPort { get; set; } = 25;
    public bool UseTls { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    [EmailAddress]
    public string From { get; set; } = string.Empty;
    [Required]
    public string[] To { get; set; } = Array.Empty<string>();
}
