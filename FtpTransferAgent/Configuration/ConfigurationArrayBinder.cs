using Microsoft.Extensions.Configuration;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 配列設定を「設定ファイルに書かれていれば C# 側の初期値を置き換える」形で読み出す。
///
/// 既定の配列バインドはプロパティの初期値に設定値を追記するため、初期値が空でない配列
/// (<see cref="WatchOptions.EndFileExtensions"/> 等) では、設定に書いていない既定値が残る。
/// 素朴に置き換えようとすると、さらに 2 つの落とし穴がある。
///   1. 空配列 ("EndFileExtensions": []) は値も子も持たないため
///      <see cref="IConfigurationSection.Exists"/> が false になり、「書いていない」と区別できない。
///   2. マージ済みの設定を見ると、優先度の高いファイルが [] を宣言していても、優先度の低い
///      ファイルの要素が GetChildren() に残るため、上書きが効かない。
/// そのためプロバイダを優先度の高い順に走査し、最初にキーを宣言しているものを採用する。
/// </summary>
public static class ConfigurationArrayBinder
{
    /// <summary>
    /// <paramref name="key"/> の配列が設定に宣言されていれば <paramref name="apply"/> を呼ぶ。
    /// 宣言が無ければ何もしない (C# 側の初期値を維持する)。
    /// </summary>
    public static void ReplaceIfDeclared(IConfiguration configuration, string key, Action<string[]> apply)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            var indices = provider.GetChildKeys(Enumerable.Empty<string>(), key).ToList();
            if (indices.Count > 0)
            {
                // このプロバイダが要素を持つ配列として宣言している
                apply(indices
                    .Select(index => provider.TryGet($"{key}:{index}", out var value) ? value ?? string.Empty : string.Empty)
                    .ToArray());
                return;
            }

            if (provider.TryGet(key, out _))
            {
                // 要素を持たずにキーだけを宣言している = 空配列
                apply(Array.Empty<string>());
                return;
            }
        }
    }
}
