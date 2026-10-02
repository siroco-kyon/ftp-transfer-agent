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
    /// <summary>get成功後に元データの削除を試す。削除失敗は警告ログに残し、自動再試行しない。</summary>
    public bool DeleteRemoteAfterDownload { get; set; }

    /// <summary>
    /// ENDファイル転送成功後に転送先のENDファイルを削除するか
    /// getでは削除失敗を警告ログに残す。ENDが残る場合は関連データも残す。
    /// </summary>
    public bool DeleteRemoteEndFiles { get; set; } = false;

    /// <summary>
    /// put 方向で TransferEndFiles=false のとき、転送しなかった END ファイルをローカルから削除するか
    /// </summary>
    public bool DeleteLocalSkippedEndFiles { get; set; } = false;
}
