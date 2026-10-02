using System.Collections.Concurrent;

namespace FtpTransferAgent.Services;

/// <summary>
/// 同じホスト・ポートへのSFTP接続確立を制限する。認証済みの接続での転送は制限しない。
/// バッチ内の全宛先で同じインスタンスを共有する。
/// </summary>
public sealed class SftpConnectionLimiter
{
    private readonly ConcurrentDictionary<(string Host, int Port), SemaphoreSlim> _gates = new();
    private readonly int _limit;

    public SftpConnectionLimiter(int limit)
    {
        if (limit is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "SFTP connection limit must be between 1 and 16.");
        }
        _limit = limit;
    }

    public async Task<IDisposable> AcquireAsync(string host, int port, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd((host.ToLowerInvariant(), port), _ => new SemaphoreSlim(_limit, _limit));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
