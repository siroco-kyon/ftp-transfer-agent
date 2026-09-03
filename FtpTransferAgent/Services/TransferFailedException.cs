namespace FtpTransferAgent.Services;

/// <summary>
/// 転送ライブラリが「例外ではなく戻り値」で失敗を通知したケースを表す例外。
/// FluentFTP の UploadFile/DownloadFile は 4xx/5xx 応答やデータ接続断でも例外を投げず
/// <see cref="FluentFTP.FtpStatus.Failed"/> を返すため、それを検出して本例外に変換する。
/// 転送後の検証 (サイズ不一致・宛先不在) にも使用する。
/// リトライ可能として分類される (<see cref="RetryableExceptionClassifier"/>)。
/// </summary>
public class TransferFailedException : Exception
{
    public TransferFailedException(string message) : base(message)
    {
    }

    public TransferFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
