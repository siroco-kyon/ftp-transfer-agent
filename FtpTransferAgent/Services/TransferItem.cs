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
/// キューに格納する転送対象
/// </summary>
public record TransferItem(string Path, TransferAction Action);
