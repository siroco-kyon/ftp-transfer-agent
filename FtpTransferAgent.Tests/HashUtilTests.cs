using System.IO;
using System.Security.Cryptography;
using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="HashUtil"/> の機能を検証するテストクラス
/// </summary>
public class HashUtilTests
{
    /// <summary>
    /// MD5 と SHA256 のハッシュ値が .NET の計算結果と一致することを確認する
    /// </summary>
    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA256")]
    public async Task ComputeHashAsync_ReturnsExpectedHash(string algorithm)
    {
        // 一時ファイルを作成し既知のデータを書き込む
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "test data");

        // .NET 標準ライブラリを用いたハッシュ値
        string expected;
        using (HashAlgorithm hasher = algorithm == "SHA256" ? SHA256.Create() : MD5.Create())
        using (var stream = File.OpenRead(tempFile))
        {
            var hash = hasher.ComputeHash(stream);
            expected = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        // HashUtil の計算結果と比較
        var actual = await HashUtil.ComputeHashAsync(tempFile, algorithm, CancellationToken.None);
        File.Delete(tempFile);
        Assert.Equal(expected, actual);
    }
}
