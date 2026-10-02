using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FtpTransferAgent.Services;

/// <summary>監視フォルダーの表記差を吸収し、ルートの区切り文字を保持する。</summary>
public static class LocalPathIdentity
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        while (true)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            if (trimmed == full) return full;
            full = trimmed;
        }
    }

    public static string Prefix(string path)
    {
        var full = Normalize(path);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    public static bool Contains(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Normalize(path);
        return string.Equals(Normalize(root), full, comparison) || full.StartsWith(Prefix(root), comparison);
    }

    public static string WatchHash(string watchPath) => LegacyHash(Normalize(watchPath));

    private static string LegacyHash(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    // 旧版が末尾の区切り文字を含めて作った保存先・ロックも引き継ぐ。
    public static IReadOnlyList<string> CompatibleHashes(string watchPath) =>
        new[] { WatchHash(watchPath), LegacyHash(Prefix(watchPath)), LegacyHash(watchPath) }
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
