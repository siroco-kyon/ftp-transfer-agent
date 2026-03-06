using System.IO;
using System.Security.Cryptography;
using System.Text;
using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="HashUtil"/> の機能を検証するテストクラス
/// </summary>
public class HashUtilTests
{
    /// <summary>
    /// SHA256 と SHA512 のハッシュ値が .NET の計算結果と一致することを確認する
    /// </summary>
    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA512")]
    public async Task ComputeHashAsync_ReturnsExpectedHash(string algorithm)
    {
        // 一時ファイルを作成し既知のデータを書き込む
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "test data");

        // .NET 標準ライブラリを用いたハッシュ値
        string expected;
        using (HashAlgorithm hasher = algorithm == "SHA256" ? SHA256.Create() : SHA512.Create())
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

    [Fact]
    public async Task ComputeHashAsync_ReturnsExpectedMd5Hash()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "md5 test");

        string expected;
        using (var hasher = MD5.Create())
        using (var stream = File.OpenRead(tempFile))
        {
            var hash = hasher.ComputeHash(stream);
            expected = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        var actual = await HashUtil.ComputeHashAsync(tempFile, "MD5", CancellationToken.None);
        File.Delete(tempFile);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeHashSync_ReturnsSameValueAsAsync()
    {
        var bytes = Encoding.UTF8.GetBytes("sync hash test data");
        using var syncStream = new MemoryStream(bytes);
        using var asyncStream = new MemoryStream(bytes);

        var syncHash = HashUtil.ComputeHashSync(syncStream, "SHA256");
        var asyncHash = HashUtil.ComputeHashAsync(asyncStream, "SHA256", CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(asyncHash, syncHash);
    }

    [Fact]
    public async Task ComputeHashAsync_WorksWithNonSeekableStream()
    {
        var bytes = Encoding.UTF8.GetBytes("non seek stream data");
        using var baseStream = new MemoryStream(bytes);
        using var nonSeekable = new NonSeekableReadOnlyStream(baseStream);

        var actual = await HashUtil.ComputeHashAsync(nonSeekable, "SHA512", CancellationToken.None);

        using var verifyStream = new MemoryStream(bytes);
        var expected = HashUtil.ComputeHashSync(verifyStream, "SHA512");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeHashSync_ThrowsForUnsupportedAlgorithm()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("unsupported"));
        Assert.Throws<ArgumentException>(() => HashUtil.ComputeHashSync(stream, "SHA1"));
    }

    private sealed class NonSeekableReadOnlyStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableReadOnlyStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
    }
}
