using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Logging;

/// <summary>
/// ログの種類を識別する EventId 定義。
/// 特定種別のログだけをメール通知から抑制する等、ログの選択的な扱いに使用する。
/// </summary>
public static class LogEvents
{
    /// <summary>
    /// 複数宛先 (ファンアウト) で、ある宛先への個々の転送がリトライ後に最終失敗した。
    /// <see cref="Services.TransferQueue"/> が出力する。
    /// </summary>
    public static readonly EventId MultiDestinationTransferFailure = new(1001, nameof(MultiDestinationTransferFailure));

    /// <summary>
    /// 複数宛先 (ファンアウト) の 1 ファイルについて、一部宛先が未配信のまま完了した
    /// (部分失敗サマリ)。Worker が出力する。
    /// </summary>
    public static readonly EventId MultiDestinationPartialFailure = new(1002, nameof(MultiDestinationPartialFailure));

    /// <summary>
    /// 指定された EventId が「複数宛先での宛先失敗」を表すか。
    /// <see cref="SmtpOptions.SuppressPerDestinationFailureDetailEmails"/> によるメール抑制判定に使う。
    /// </summary>
    public static bool IsMultiDestinationFailure(EventId eventId) =>
        eventId.Id == MultiDestinationTransferFailure.Id || eventId.Id == MultiDestinationPartialFailure.Id;
}
