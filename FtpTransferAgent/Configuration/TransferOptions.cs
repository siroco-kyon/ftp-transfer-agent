using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 転送処理に関する設定。メインの転送先を保持しつつ、
/// <see cref="AdditionalDestinations"/> により複数宛先ファンアウト (put 方向) に対応する。
/// </summary>
[TransferOptionsValidation]
public class TransferOptions : DestinationOptions
{
    [Required]
    [RegularExpression("^(get|put|both)$")]
    public string Direction { get; set; } = "put";

    /// <summary>
    /// put (アップロード) 方向のみで利用する追加の送信先。
    /// 1 ファイルをメイン + 各追加宛先に対して同時に送信する。
    /// </summary>
    public List<DestinationOptions> AdditionalDestinations { get; set; } = new();
}
