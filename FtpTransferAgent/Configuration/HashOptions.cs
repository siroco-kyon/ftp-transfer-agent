using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// ハッシュ計算に関する設定
/// </summary>
public class HashOptions
{
    [Required]
    [RegularExpression("MD5|SHA256")]
    public string Algorithm { get; set; } = "MD5";
}
