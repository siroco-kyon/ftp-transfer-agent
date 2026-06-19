namespace FtpTransferAgent.Configuration;

/// <summary>
/// 転送後のクリーンアップに関する設定
/// </summary>
public class CleanupOptions
{
    /// <summary>
    /// put 方向で、全宛先への配信＋ハッシュ検証が成功した後にローカルの元ファイルを削除するか。
    /// 既定 true (アウトボックス運用: 送信が確認できたファイルは残さない)。
    /// false にすると元ファイルを残す (複数宛先トラッキング時は配信マーカーで再送をスキップする)。
    /// </summary>
    public bool DeleteAfterVerify { get; set; } = true;
    public bool DeleteRemoteAfterDownload { get; set; }

    /// <summary>
    /// ENDファイル転送成功後に転送先のENDファイルを削除するか
    /// </summary>
    public bool DeleteRemoteEndFiles { get; set; } = false;

    /// <summary>
    /// put 方向で TransferEndFiles=false のとき、転送しなかった END ファイルをローカルから削除するか
    /// </summary>
    public bool DeleteLocalSkippedEndFiles { get; set; } = false;
}
