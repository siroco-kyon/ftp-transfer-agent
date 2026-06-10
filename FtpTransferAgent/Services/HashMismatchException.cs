namespace FtpTransferAgent.Services;

/// <summary>
/// 転送後のハッシュ検証が一致しなかったことを表す例外。
/// 転送中のビット化けなど一過性の破損で発生し得るため、
/// <see cref="RetryableExceptionClassifier"/> ではリトライ可能に分類される
/// (再転送で回復する可能性が高い)。
/// </summary>
public sealed class HashMismatchException : Exception
{
    public HashMismatchException(string message)
        : base(message)
    {
    }
}
