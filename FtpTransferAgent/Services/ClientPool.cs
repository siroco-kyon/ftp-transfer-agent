using System;
using System.Collections.Concurrent;

namespace FtpTransferAgent.Services;

/// <summary>
/// 宛先ごとの転送クライアント（=接続）をワーカー間で再利用するためのプール。
/// 1 ファイルごとに接続を張り直す代わりに、空き接続があれば再利用することで、
/// 接続確立（SFTP の鍵交換・認証など）のコストを並列数（ワーカー数）程度まで削減する。
///
/// <para>
/// <see cref="IFileTransferClient"/>（SftpClient / AsyncFtpClient ラッパー）はスレッドセーフでは
/// ないため、1 接続は同時に 1 ワーカーだけが借りる（Rent された接続は Return まで他から取得されない）。
/// </para>
/// </summary>
public sealed class ClientPool : IDisposable
{
    private readonly ConcurrentBag<IFileTransferClient> _available = new();
    private int _disposed;

    /// <summary>
    /// 空き接続があれば取り出して再利用し、無ければ <paramref name="factory"/> で新規生成する。
    /// </summary>
    public IFileTransferClient Rent(Func<IFileTransferClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return _available.TryTake(out var client) ? client : factory();
    }

    /// <summary>
    /// 借りた接続を返却する。<paramref name="reusable"/> が true でプールが未破棄なら
    /// 再利用のためプールへ戻す。接続が壊れている（reusable=false）場合やプール破棄後は Dispose する。
    /// </summary>
    public void Return(IFileTransferClient client, bool reusable)
    {
        if (client is null)
        {
            return;
        }

        if (reusable && Volatile.Read(ref _disposed) == 0)
        {
            _available.Add(client);
            return;
        }

        SafeDispose(client);
    }

    /// <summary>
    /// プール内の全接続を切断する。全ワーカー終了後（貸出中の接続が無い状態）に呼ぶこと。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_available.TryTake(out var client))
        {
            SafeDispose(client);
        }
    }

    private static void SafeDispose(IFileTransferClient client)
    {
        try
        {
            client.Dispose();
        }
        catch
        {
            // 切断時の例外は無視する（既に切断済み等）
        }
    }
}
