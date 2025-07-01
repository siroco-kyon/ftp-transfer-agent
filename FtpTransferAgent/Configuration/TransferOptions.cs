using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 転送処理に関する設定
/// </summary>
[TransferOptionsValidation]
public class TransferOptions
{
    [Required]
    [RegularExpression("^(ftp|sftp)$")]
    public string Mode { get; set; } = "ftp";

    [Required]
    [RegularExpression("^(get|put|both)$")]
    public string Direction { get; set; } = "put";

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    [Required]
    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    /// <summary>
    /// SFTP の鍵認証に使用する秘密鍵ファイルのパス
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// 鍵ファイルがパスフレーズで保護されている場合に指定します
    /// </summary>
    public string? PrivateKeyPassphrase { get; set; }

    [Required]
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>
    /// 並列転送数。1 以上を指定します。
    /// 既定値は 1 (逐次転送)。
    /// </summary>
    [Range(1, 16)]
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// サブフォルダを含めてアップロードする際に
    /// ローカルのフォルダ構成を維持するかどうか
    /// </summary>
    public bool PreserveFolderStructure { get; set; }

    /// <summary>
    /// 接続およびデータ転送のタイムアウト時間（秒）
    /// </summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;

}
