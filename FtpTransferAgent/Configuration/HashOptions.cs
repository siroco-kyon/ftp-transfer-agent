using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// ハッシュ計算に関する設定
/// </summary>
public class HashOptions
{
    // ハッシュ検証を有効にするか（無効時はネットワーク転送が約半分になる）
    // SFTP は転送レイヤーで HMAC による整合性保証があるため、無効化しても実用上問題ない
    public bool Enabled { get; set; } = true;

    [Required]
    [RegularExpression("^(SHA256|SHA512)$")]
    public string Algorithm { get; set; } = "SHA256";

    // FTP サーバーのハッシュ計算コマンドを利用するか
    public bool UseServerCommand { get; set; } = false; // サーバーコマンドは使用せず確実なローカル計算を行う
}
