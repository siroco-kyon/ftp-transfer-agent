namespace FtpTransferAgent.Configuration;

/// <summary>
/// アプリケーション全体の動作設定。
/// </summary>
public class AppOptions
{
    /// <summary>
    /// 二重起動を防止するためのロックファイルパス。
    /// null または空の場合、LocalApplicationData 配下の
    /// "FtpTransferAgent/ftp-transfer-agent.lock" を使用する。
    /// </summary>
    public string? LockFilePath { get; set; }
}
