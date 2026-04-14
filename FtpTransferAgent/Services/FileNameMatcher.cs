using System.Collections.Generic;
using System.IO.Enumeration;

namespace FtpTransferAgent.Services;

/// <summary>
/// ファイル名のフィルタリングマッチャー。
/// 設定パターンは以下の 2 形式を受け入れる:
///   1) 拡張子のみ ("txt" または ".txt") : 従来と同じ拡張子完全一致
///   2) グロブ ("*.txt", "data_*.csv", "?.log" 等) : Windows 互換のシンプルマッチ
/// </summary>
public static class FileNameMatcher
{
    public static bool IsMatch(string fileName, IReadOnlyList<string>? patterns)
    {
        // パターン未指定/空は全許可 (従来仕様)
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var pattern = raw.Trim();
            if (ContainsWildcard(pattern))
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
                {
                    return true;
                }
            }
            else
            {
                var normalizedExt = pattern.StartsWith('.') ? pattern : "." + pattern;
                var ext = System.IO.Path.GetExtension(fileName);
                if (string.Equals(ext, normalizedExt, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsWildcard(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' || c == '?') return true;
        }
        return false;
    }
}
