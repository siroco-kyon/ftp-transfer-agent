using System.Threading;

namespace FtpTransferAgent.Services;

/// <summary>
/// バッチ実行の最終終了コードを Worker から Program へ伝えるための状態。
/// </summary>
public sealed class ApplicationExitCode
{
    private int _code;

    public int Code => Volatile.Read(ref _code);

    public void MarkFailure()
    {
        Interlocked.CompareExchange(ref _code, 1, 0);
    }
}
