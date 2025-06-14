namespace FtpTransferAgent.Services;

public enum TransferAction
{
    Upload,
    Download
}

public record TransferItem(string Path, TransferAction Action);
