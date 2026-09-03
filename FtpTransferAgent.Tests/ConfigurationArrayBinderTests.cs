using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Configuration;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 配列設定が「設定ファイルに書かれていれば C# 側の初期値を置き換える」ことを検証する。
/// </summary>
public class ConfigurationArrayBinderTests
{
    private static IConfigurationRoot Build(params string[] jsonDocuments)
    {
        var builder = new ConfigurationBuilder();
        foreach (var json in jsonDocuments)
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            File.WriteAllText(path, json);
            builder.AddJsonFile(path, optional: false);
        }
        return builder.Build();
    }

    [Fact]
    public void ReplaceIfDeclared_DoesNothing_WhenTheKeyIsAbsent()
    {
        var config = Build("{ \"Watch\": { \"AllowedExtensions\": [ \".txt\" ] } }");

        string[]? applied = null;
        ConfigurationArrayBinder.ReplaceIfDeclared(config, "Watch:EndFileExtensions", v => applied = v);

        // 宣言が無ければ呼ばれない = C# 側の初期値が維持される
        Assert.Null(applied);
    }

    [Fact]
    public void ReplaceIfDeclared_ReplacesWithTheDeclaredValues()
    {
        var config = Build("{ \"Watch\": { \"EndFileExtensions\": [ \".TRG\", \".trg\" ] } }");

        string[]? applied = null;
        ConfigurationArrayBinder.ReplaceIfDeclared(config, "Watch:EndFileExtensions", v => applied = v);

        Assert.Equal(new[] { ".TRG", ".trg" }, applied);
    }

    [Fact]
    public void ReplaceIfDeclared_TreatsAnExplicitlyEmptyArrayAsDeclared()
    {
        // 空配列は値も子も持たないため IConfigurationSection.Exists() は false になる。
        // それでも「書かれている」と判定し、既定値を無効化できなければならない
        var config = Build("{ \"Watch\": { \"EndFileExtensions\": [] } }");

        string[]? applied = null;
        ConfigurationArrayBinder.ReplaceIfDeclared(config, "Watch:EndFileExtensions", v => applied = v);

        Assert.NotNull(applied);
        Assert.Empty(applied!);
    }

    [Fact]
    public void ReplaceIfDeclared_HonorsAnEmptyArrayFromTheHighestPriorityProvider()
    {
        // マージ済みの設定を見ると、優先度の低いファイルの要素が GetChildren() に残るため、
        // マージ結果から組み直すと上書きが効かない
        var config = Build(
            "{ \"Watch\": { \"EndFileExtensions\": [ \".END\", \".end\" ] } }",
            "{ \"Watch\": { \"EndFileExtensions\": [] } }");

        Assert.Equal(2, config.GetSection("Watch:EndFileExtensions").GetChildren().Count());

        string[]? applied = null;
        ConfigurationArrayBinder.ReplaceIfDeclared(config, "Watch:EndFileExtensions", v => applied = v);

        Assert.NotNull(applied);
        Assert.Empty(applied!);
    }

    [Fact]
    public void ReplaceIfDeclared_HonorsTheHighestPriorityProvidersValues()
    {
        var config = Build(
            "{ \"Watch\": { \"EndFileExtensions\": [ \".END\", \".end\" ] } }",
            "{ \"Watch\": { \"EndFileExtensions\": [ \".TRG\" ] } }");

        string[]? applied = null;
        ConfigurationArrayBinder.ReplaceIfDeclared(config, "Watch:EndFileExtensions", v => applied = v);

        // 低優先ファイルの 2 要素目が残ってはならない
        Assert.Equal(new[] { ".TRG" }, applied);
    }
}

/// <summary>
/// 残した一時ファイルの記録が、宛先単位で共有され一度だけ取り出せることを検証する。
/// </summary>
public class RetainedTempFileRegistryTests
{
    [Fact]
    public void TakeRetained_ReturnsEmpty_WhenNothingWasRetained()
    {
        var registry = new RetainedTempFileRegistry();

        Assert.Empty(registry.TakeRetained("/remote/a.txt"));
    }

    [Fact]
    public void TakeRetained_ReturnsRetainedPathsOnlyOnce()
    {
        var registry = new RetainedTempFileRegistry();
        registry.Retain("/remote/a.txt", "/remote/a.txt.tmp.1");
        registry.Retain("/remote/a.txt", "/remote/a.txt.tmp.2");
        registry.Retain("/remote/b.txt", "/remote/b.txt.tmp.1");

        var taken = registry.TakeRetained("/remote/a.txt");

        Assert.Equal(2, taken.Count);
        Assert.Contains("/remote/a.txt.tmp.1", taken);
        Assert.Contains("/remote/a.txt.tmp.2", taken);
        // 取り出しは一度だけ成功する (二重削除を避ける)
        Assert.Empty(registry.TakeRetained("/remote/a.txt"));
        // 別の宛先パスの記録は影響を受けない
        Assert.Single(registry.TakeRetained("/remote/b.txt"));
    }
}
