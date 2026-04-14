using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Services;

/// <summary>
/// 転送の種別
/// </summary>
public enum TransferAction
{
    Upload,
    Download
}

/// <summary>
/// キューに格納する転送対象。
/// Upload の場合は <see cref="Destination"/> が必須 (primary は TransferOptions 自身を、
/// 追加宛先は AdditionalDestinations[i] を渡す)。
/// Download の場合は Destination = null (primary のみ)。
/// <see cref="GroupId"/> は 1 ファイル × N 宛先のファンアウト結果を集約するためのキー。
/// </summary>
public record TransferItem(
    string Path,
    TransferAction Action,
    DestinationOptions? Destination = null,
    string? GroupId = null)
{
    /// <summary>
    /// キュー上での重複抑止キー。Upload ファンアウトでは宛先が異なる兄弟アイテムを
    /// 別物として扱う必要があるため、宛先情報と GroupId を含める。
    /// </summary>
    public string DedupKey
    {
        get
        {
            if (Action == TransferAction.Upload && Destination is not null)
            {
                var destPart = $"{Destination.Mode}://{Destination.Host}:{Destination.Port}{Destination.RemotePath}";
                return $"Upload:{Path}|{destPart}|{GroupId ?? string.Empty}";
            }
            return $"{Action}:{Path}";
        }
    }
}
