using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// ハッシュ計算に関する設定
/// </summary>
public class HashOptions
{
    [Required]
    [RegularExpression("^(MD5|SHA256|SHA512)$")]
    public string Algorithm { get; set; } = "MD5";

    // FTP サーバーのハッシュ計算コマンドを利用するか
    public bool UseServerCommand { get; set; } = false; // サーバーコマンドは使用せず確実なローカル計算を行う
}
