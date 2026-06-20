using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// Transfer settings for the primary destination.
/// AdditionalDestinations enables put-direction fanout to multiple destinations.
/// </summary>
[TransferOptionsValidation]
public class TransferOptions : DestinationOptions
{
    [Required]
    [RegularExpression("^(get|put)$")]
    public string Direction { get; set; } = "put";

    /// <summary>
    /// Additional destinations used only for put-direction fanout.
    /// </summary>
    public List<DestinationOptions> AdditionalDestinations { get; set; } = new();

    /// <summary>
    /// Enables per-destination delivery tracking for put-direction fanout.
    /// </summary>
    public bool PerDestinationDeliveryTracking { get; set; }

    /// <summary>
    /// Directory for delivery marker files.
    /// Empty or null uses LocalApplicationData/FtpTransferAgent/delivery-state/&lt;watch-path-hash&gt;.
    /// </summary>
    public string? StateDirectory { get; set; }

    /// <summary>
    /// Directory for files that partially failed in per-destination delivery tracking.
    /// Null or omitted uses LocalApplicationData/FtpTransferAgent/delivery-retry/&lt;watch-path-hash&gt;.
    /// Relative paths are resolved under Watch.Path. Empty string disables moving files.
    /// </summary>
    public string? RetryDirectory { get; set; }

    /// <summary>
    /// File signature mode used to detect overwritten files: "sizetime" or "hash".
    /// </summary>
    public string DeliverySignatureMode { get; set; } = "sizetime";

    /// <summary>
    /// When true, each file (and its related END files) is copied to a temporary snapshot before
    /// uploading so that every destination in a fanout receives byte-identical, point-in-time content
    /// even if the source is modified mid-transfer. Only applies when delivery tracking is active
    /// (multi-destination put, or single destination with PerDestinationDeliveryTracking).
    /// Default false: uploads read the live source directly (lower I/O, no temporary copy). A source
    /// change during transfer is still detected after the fact, so the file is retained for the next
    /// run; the only difference is that destinations may briefly diverge within that single run.
    /// </summary>
    public bool EnableUploadSnapshot { get; set; }
}
