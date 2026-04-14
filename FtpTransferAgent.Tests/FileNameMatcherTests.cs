using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="FileNameMatcher"/> の拡張子/ワイルドカードマッチを検証する
/// </summary>
public class FileNameMatcherTests
{
    [Theory]
    [InlineData("foo.txt", "txt", true)]
    [InlineData("foo.txt", ".txt", true)]
    [InlineData("foo.TXT", "txt", true)]
    [InlineData("foo.log", "txt", false)]
    public void ExtensionOnly_Matches(string fileName, string pattern, bool expected)
    {
        Assert.Equal(expected, FileNameMatcher.IsMatch(fileName, new[] { pattern }));
    }

    [Theory]
    [InlineData("data.csv", "*.csv", true)]
    [InlineData("data.csv.bak", "*.csv", false)]
    [InlineData("data_20260414.csv", "data_*.csv", true)]
    [InlineData("data_.csv", "data_*.csv", true)]
    [InlineData("info_20260414.csv", "data_*.csv", false)]
    [InlineData("a.log", "?.log", true)]
    [InlineData("ab.log", "?.log", false)]
    public void Wildcard_Matches(string fileName, string pattern, bool expected)
    {
        Assert.Equal(expected, FileNameMatcher.IsMatch(fileName, new[] { pattern }));
    }

    [Fact]
    public void EmptyOrNullPatterns_AllowAll()
    {
        Assert.True(FileNameMatcher.IsMatch("any.bin", null));
        Assert.True(FileNameMatcher.IsMatch("any.bin", Array.Empty<string>()));
    }

    [Fact]
    public void WhitespacePatterns_AreSkipped()
    {
        // 空白のみのパターンは無視され、他のパターンで判定される
        Assert.False(FileNameMatcher.IsMatch("x.txt", new[] { "", "   " }));
        Assert.True(FileNameMatcher.IsMatch("x.txt", new[] { "", "txt" }));
    }

    [Fact]
    public void MixedPatterns_AnyMatchWins()
    {
        var patterns = new[] { "txt", "*.csv", "data_*.log" };
        Assert.True(FileNameMatcher.IsMatch("a.txt", patterns));
        Assert.True(FileNameMatcher.IsMatch("a.csv", patterns));
        Assert.True(FileNameMatcher.IsMatch("data_1.log", patterns));
        Assert.False(FileNameMatcher.IsMatch("a.bin", patterns));
    }
}
