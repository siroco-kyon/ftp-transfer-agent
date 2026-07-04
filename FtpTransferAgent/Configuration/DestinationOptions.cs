using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 1 つの転送先サーバーへの接続・送信設定
/// </summary>
public class DestinationOptions
{
    /// <summary>
    /// 宛先の安定した識別子。複数宛先の配信トラッキング
    /// (<see cref="TransferOptions.PerDestinationDeliveryTracking"/>) を有効にした場合、
    /// 配信済みマーカーの宛先キーとして使用する。ホスト名やパスを後から変更しても
    /// 同じ宛先と認識し続けられるよう、接続情報とは独立した名前を付ける。
    /// トラッキング有効時は全宛先 (primary 含む) で必須かつ一意であること。
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 転送方式。<c>ftp</c> / <c>sftp</c> は通信プロトコル、<c>local</c> はローカル / UNC
    /// (SMB 共有) のファイルシステムへの転送 (put 専用)。
    /// </summary>
    [Required]
    [RegularExpression("^(ftp|sftp|local)$")]
    public string Mode { get; set; } = "ftp";

    /// <summary>
    /// 接続先ホスト。<c>ftp</c> / <c>sftp</c> では必須 (ConfigurationValidator で検証)。
    /// <c>local</c> では使用しない。
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    /// <summary>
    /// 接続ユーザー名。<c>ftp</c> / <c>sftp</c> では必須 (ConfigurationValidator で検証)。
    /// <c>local</c> では使用しない (OS の実行ユーザー権限でアクセスする)。
    /// </summary>
    public string Username { get; set; } = string.Empty;

    public string? Password { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? PrivateKeyPassphrase { get; set; }

    public string? HostKeyFingerprint { get; set; }

    [Required]
    public string RemotePath { get; set; } = string.Empty;

    [Range(1, 16)]
    public int Concurrency { get; set; } = 1;

    public bool PreserveFolderStructure { get; set; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 接続を再利用する際にアイドルタイムアウトで切断されないよう送る KeepAlive の間隔（秒）。
    /// 0 で無効。&gt;0 のとき SFTP は KeepAliveInterval、FTP は SocketKeepAlive を有効化する。
    /// </summary>
    [Range(0, 3600)]
    public int KeepAliveSeconds { get; set; }

    /// <summary>
    /// SFTP アップロードのリネーム完了後に宛先パスの存在確認 (2 往復) を追加で行うか。
    /// リネームの成功応答 (SSH_FX_OK) 自体がサーバによる完了確認のため、false にしても
    /// 転送の完了保証はプロトコルレベルで担保される。高レイテンシ回線で小さいファイルを
    /// 多数送る場合は false にすると往復数を削減できる。既定 true（従来動作）。SFTP のみ使用。
    /// </summary>
    public bool VerifyUploadedFileExists { get; set; } = true;

    /// <summary>
    /// SFTP の 1 書き込み要求 (SSH_FXP_WRITE) あたりのバッファサイズ (KB)。
    /// 実際のチャンクサイズはサーバが告知するチャネルパケット上限との min になるため、
    /// OpenSSH 系サーバ (上限 32KB) では既定値から上げても効果はない。32KB 超を受け付ける
    /// サーバでは 64 に上げるとスループットが向上する。65 以上は SSH.NET のトランスポート
    /// パケット上限 (約 68KB) を超え送信エラーになるため設定不可。SFTP のみ使用。
    /// </summary>
    [Range(1, 64)]
    public int BufferSizeKB { get; set; } = 32;
}
