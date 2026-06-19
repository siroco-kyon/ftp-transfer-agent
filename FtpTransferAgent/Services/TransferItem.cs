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
    string? GroupId = null,
    IReadOnlyList<string>? RelatedEndFilePaths = null,
    string? OriginalRelativePath = null,
    IReadOnlyList<string>? RelatedEndFileOriginalRelativePaths = null,
    string? LocalPathOverride = null,
    IReadOnlyList<string>? RelatedEndFileLocalPathOverrides = null)
{
    /// <summary>
    /// ログ追跡用の転送 ID。リトライ時もアイテム単位で同じ ID を保ち、
    /// 1 アイテムのログを通しで追えるようにする。
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

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

    // Id はログ追跡用の付随情報であり、転送対象としての同一性には含めない
    // (record 既定の equality だと同じ転送対象でも Id 違いで不等になるため手動実装)
    public virtual bool Equals(TransferItem? other) =>
        other is not null
        && Path == other.Path
        && Action == other.Action
        && Equals(Destination, other.Destination)
        && GroupId == other.GroupId
        && Equals(RelatedEndFilePaths, other.RelatedEndFilePaths)
        && OriginalRelativePath == other.OriginalRelativePath
        && Equals(RelatedEndFileOriginalRelativePaths, other.RelatedEndFileOriginalRelativePaths)
        && LocalPathOverride == other.LocalPathOverride
        && Equals(RelatedEndFileLocalPathOverrides, other.RelatedEndFileLocalPathOverrides);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Path);
        hash.Add(Action);
        hash.Add(Destination);
        hash.Add(GroupId);
        hash.Add(RelatedEndFilePaths);
        hash.Add(OriginalRelativePath);
        hash.Add(RelatedEndFileOriginalRelativePaths);
        hash.Add(LocalPathOverride);
        hash.Add(RelatedEndFileLocalPathOverrides);
        return hash.ToHashCode();
    }
}
