namespace FtpTransferAgent.Configuration;

/// <summary>
/// 転送後のクリーンアップに関する設定
/// </summary>
public class CleanupOptions
{
    public bool DeleteAfterVerify { get; set; }
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
