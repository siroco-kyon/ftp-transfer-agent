namespace FtpTransferAgent.Configuration;

/// <summary>
/// アプリケーション全体の動作設定。
/// </summary>
public class AppOptions
{
    /// <summary>
    /// 二重起動を防止するためのロックファイルパス。
    /// null または空の場合、実行ディレクトリ配下の既定ファイル名を使用する。
    /// </summary>
    public string? LockFilePath { get; set; }
}
