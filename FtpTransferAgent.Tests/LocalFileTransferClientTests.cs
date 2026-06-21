using System.IO;
using System.Linq;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="LocalFileTransferClient"/> の単体テスト。ローカル / UNC 宛先 (Mode=local) で
/// 既存ツールの CIFS 用途 (ローカル → 共有フォルダ書き込み) を置き換えるためのクライアント。
/// ネットワーク不要で、テンポラリディレクトリに対して検証する。
/// </summary>
public class LocalFileTransferClientTests : IDisposable
{
    private readonly string _root;

    public LocalFileTransferClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "local-client-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    private static LocalFileTransferClient NewClient() =>
        new(new DestinationOptions { Mode = "local", RemotePath = "ignored" }, NullLogger<LocalFileTransferClient>.Instance);

    [Fact]
    public async Task UploadAsync_CopiesFile_AndCreatesMissingDirectories()
    {
        var src = Path.Combine(_root, "src.txt");
        await File.WriteAllTextAsync(src, "payload");
        // 宛先の中間ディレクトリは存在しない状態
        var dest = Path.Combine(_root, "out", "sub", "dest.txt");

        var client = NewClient();
        await client.UploadAsync(src, dest, CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Equal("payload", await File.ReadAllTextAsync(dest));
        // 一時ファイル (.tmp.*) が残っていないこと
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!, "*.tmp.*"));
    }

    [Fact]
    public async Task UploadAsync_OverwritesExistingDestination()
    {
        var src = Path.Combine(_root, "src.txt");
        await File.WriteAllTextAsync(src, "new-content");
        var dest = Path.Combine(_root, "dest.txt");
        await File.WriteAllTextAsync(dest, "old-content");

        await NewClient().UploadAsync(src, dest, CancellationToken.None);

        Assert.Equal("new-content", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task UploadAsync_AcceptsForwardSlashPaths()
    {
        // BuildRemotePath は OS ネイティブで組むが、念のため '/' 混在入力でも書けること
        var src = Path.Combine(_root, "src.txt");
        await File.WriteAllTextAsync(src, "data");
        var destNative = Path.Combine(_root, "fwd", "dest.txt");
        var destForward = destNative.Replace('\\', '/');

        await NewClient().UploadAsync(src, destForward, CancellationToken.None);

        Assert.True(File.Exists(destNative));
    }

    [Fact]
    public async Task DownloadAsync_CopiesFile()
    {
        var remote = Path.Combine(_root, "remote.txt");
        await File.WriteAllTextAsync(remote, "down");
        var local = Path.Combine(_root, "local", "local.txt");

        await NewClient().DownloadAsync(remote, local, CancellationToken.None);

        Assert.True(File.Exists(local));
        Assert.Equal("down", await File.ReadAllTextAsync(local));
    }

    [Fact]
    public async Task GetRemoteHashAsync_MatchesHashUtil()
    {
        var file = Path.Combine(_root, "h.txt");
        await File.WriteAllTextAsync(file, "hash me");
        var expected = await HashUtil.ComputeHashAsync(file, "SHA256", CancellationToken.None);

        var actual = await NewClient().GetRemoteHashAsync(file, "SHA256", CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ListFilesAsync_ReturnsFiles_RespectingRecursion()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "b.txt"), "b");

        var client = NewClient();

        var topOnly = (await client.ListFilesAsync(_root, CancellationToken.None, includeSubdirectories: false)).ToList();
        Assert.Single(topOnly);
        Assert.Contains(topOnly, p => p.EndsWith("a.txt"));

        var recursive = (await client.ListFilesAsync(_root, CancellationToken.None, includeSubdirectories: true)).ToList();
        Assert.Equal(2, recursive.Count);
        Assert.Contains(recursive, p => p.EndsWith("b.txt"));
    }

    [Fact]
    public async Task ListFilesAsync_ThrowsWhenDirectoryMissing()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => NewClient().ListFilesAsync(missing, CancellationToken.None));
    }

    [Fact]
    public async Task ExistsAndDelete_Work()
    {
        var file = Path.Combine(_root, "e.txt");
        await File.WriteAllTextAsync(file, "x");
        var client = NewClient();

        Assert.True(await client.ExistsAsync(file, CancellationToken.None));
        await client.DeleteAsync(file, CancellationToken.None);
        Assert.False(await client.ExistsAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task UploadAsync_LeavesNoTempFile_WhenSourceMissing()
    {
        // コピー元が無ければ失敗するが、一時ファイルを残さないこと
        var src = Path.Combine(_root, "nope.txt");
        var dest = Path.Combine(_root, "dest.txt");

        await Assert.ThrowsAnyAsync<IOException>(
            () => NewClient().UploadAsync(src, dest, CancellationToken.None));

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp.*"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
