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

    [Required]
    [RegularExpression("^(ftp|sftp)$")]
    public string Mode { get; set; } = "ftp";

    [Required]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 21;

    [Required]
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
}
