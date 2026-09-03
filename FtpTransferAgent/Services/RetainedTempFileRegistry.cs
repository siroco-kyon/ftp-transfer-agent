using System.Collections.Concurrent;

namespace FtpTransferAgent.Services;

/// <summary>
/// 宛先削除後のリネーム失敗で、意図的にリモートへ残した一時ファイルを記録する。
///
/// 一時ファイルは「宛先が消えたのに新ファイルもまだ無い」状態での唯一のデータなので、
/// その場では消せない。再試行が成功して宛先が正しく作られた時点で初めて不要になる。
///
/// 再試行のたびに接続はプールから借り直され、接続が壊れていれば破棄されて別インスタンスに
/// なるため、記録をクライアント個体に持たせると再試行時に失われる。宛先単位で共有する。
/// </summary>
public sealed class RetainedTempFileRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _byRemotePath = new(StringComparer.Ordinal);

    /// <summary>宛先パスに対して残した一時ファイルを記録する。</summary>
    public void Retain(string remotePath, string tempPath)
        => _byRemotePath.GetOrAdd(remotePath, _ => new ConcurrentBag<string>()).Add(tempPath);

    /// <summary>
    /// 宛先パスに対して記録済みの一時ファイルを取り出して記録から外す。
    /// 掃除は取り出した側の責務になる (取り出しは一度だけ成功する)。
    /// </summary>
    public IReadOnlyList<string> TakeRetained(string remotePath)
    {
        if (!_byRemotePath.TryRemove(remotePath, out var paths))
        {
            return Array.Empty<string>();
        }

        return paths.ToArray();
    }
}
