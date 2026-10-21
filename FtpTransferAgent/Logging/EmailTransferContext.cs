using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;

namespace FtpTransferAgent.Logging;

/// <summary>メール用の付随情報。転送の成否・宛先の識別子から独立して扱う。</summary>
internal sealed record EmailTransferContext(string Peer, string Endpoint, string Summary)
{
    public static string DisplayName(DestinationOptions destination) =>
        !string.IsNullOrWhiteSpace(destination.DisplayName) ? destination.DisplayName.Trim() :
        !string.IsNullOrWhiteSpace(destination.Name) ? destination.Name.Trim() :
        string.Equals(destination.Mode, "local", StringComparison.OrdinalIgnoreCase)
            ? destination.RemotePath : destination.Host;

    public static string DescribeEndpoint(DestinationOptions destination) =>
        string.Equals(destination.Mode, "local", StringComparison.OrdinalIgnoreCase)
            ? destination.RemotePath
            : $"{destination.Mode}://{destination.Host}:{destination.Port}{destination.RemotePath}";

    public static EmailTransferContext For(DestinationOptions? destination, TransferAction action)
    {
        if (destination is null)
            return new("不明", "不明", "転送処理でエラー");

        var peer = DisplayName(destination);
        var summary = action == TransferAction.Download
            ? $"{peer}からの取得失敗" : $"{peer}へ転送失敗";
        return new(peer, DescribeEndpoint(destination), summary);
    }
}
