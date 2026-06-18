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
    [RegularExpression("^(get|put)$")]
    public string Direction { get; set; } = "put";

    /// <summary>
    /// put (アップロード) 方向のみで利用する追加の送信先。
    /// 1 ファイルをメイン + 各追加宛先に対して同時に送信する。
    /// </summary>
    public List<DestinationOptions> AdditionalDestinations { get; set; } = new();

    /// <summary>
    /// 複数宛先の「宛先別配信トラッキング」を有効にする (put 方向のみ)。
    /// true のとき、部分失敗で保持したファイルを次回バッチで再送する際、
    /// 既に配信済みの宛先には再送せず、未配信の宛先だけに送信する。
    /// 配信状態は <see cref="StateDirectory"/> 配下のマーカーファイルで永続化する。
    /// 既定 false (従来の all-or-nothing 動作)。
    /// </summary>
    public bool PerDestinationDeliveryTracking { get; set; }

    /// <summary>
    /// 配信済みマーカーを保存するディレクトリ。
    /// 空または null の場合、LocalApplicationData 配下の
    /// "FtpTransferAgent/delivery-state/&lt;watch パスのハッシュ&gt;" を使用する。
    /// 列挙対象 (Watch.Path) の外に置くことを推奨する。
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// 配信済み判定に使うファイル指紋の算出方式。
    /// "sizetime" (既定): サイズ + 最終更新時刻。軽量。同サイズかつ mtime 据え置きの上書きは検出できない。
    /// "hash": Hash.Algorithm でファイルハッシュを算出。厳密だが保持ファイルごとに計算コストが掛かる。
    /// </summary>
    public string DeliverySignatureMode { get; set; } = "sizetime";
}
