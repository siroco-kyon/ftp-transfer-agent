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
    /// 指定された EventId が「複数宛先での宛先失敗」を表すか (詳細・サマリ両方)。
    /// </summary>
    public static bool IsMultiDestinationFailure(EventId eventId) =>
        eventId.Id == MultiDestinationTransferFailure.Id || eventId.Id == MultiDestinationPartialFailure.Id;

    /// <summary>
    /// 指定された EventId が「メール抑制してよい per-destination の詳細失敗ログ」か。
    /// <see cref="Configuration.SmtpOptions.SuppressPerDestinationFailureDetailEmails"/> によるメール抑制判定に使う。
    /// 抑制対象は個々の宛先失敗の詳細 (<see cref="MultiDestinationTransferFailure"/>) のみで、
    /// ファイル単位の部分失敗サマリ (<see cref="MultiDestinationPartialFailure"/>) は抑制しない
    /// (宛先が継続的に落ちていることを運用者が気付けるよう、サマリ通知は残す)。
    /// </summary>
    public static bool IsSuppressiblePerDestinationDetail(EventId eventId) =>
        eventId.Id == MultiDestinationTransferFailure.Id;
}
