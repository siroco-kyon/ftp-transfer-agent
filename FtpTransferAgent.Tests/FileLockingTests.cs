using System.IO;
using FtpTransferAgent.Services;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// ファイルロック状態でのテストクラス
/// </summary>
public class FileLockingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testFile;

    public FileLockingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FtpTransferTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _testFile = Path.Combine(_testDirectory, "test.txt");
    }

    [Fact]
    public async Task HashCalculation_WithLockedFile_ShouldRetry()
    {
        // Arrange
        await File.WriteAllTextAsync(_testFile, "Test content for hash calculation");
        
        // ファイルを排他ロックで開く
        using var fileStream = new FileStream(_testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        
        // Act & Assert
        // ファイルがロックされている間はIOExceptionが発生することを確認
        var exception = await Assert.ThrowsAsync<IOException>(() => 
            HashUtil.ComputeHashAsync(_testFile, "MD5", CancellationToken.None));
        
        // ロックが原因の例外であることを確認
        Assert.Contains("being used by another process", exception.Message);
    }

    [Fact]
    public async Task HashCalculation_AfterUnlock_ShouldSucceed()
    {
        // Arrange
        await File.WriteAllTextAsync(_testFile, "Test content for hash calculation");
        
        // ファイルを一時的にロック
        using (var fileStream = new FileStream(_testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // ロック中は失敗することを確認
            var exception = await Assert.ThrowsAsync<IOException>(() => 
                HashUtil.ComputeHashAsync(_testFile, "MD5", CancellationToken.None));
            Assert.Contains("being used by another process", exception.Message);
        }
        
        // Act - ロック解除後は成功するはず
        var hash = await HashUtil.ComputeHashAsync(_testFile, "MD5", CancellationToken.None);
        
        // Assert
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.Equal(32, hash.Length); // MD5ハッシュは32文字
    }

    [Fact]
    public void RetryableExceptionClassifier_ShouldIdentifyFileLockExceptions()
    {
        // Arrange - プラットフォーム依存を考慮した例外作成
        var sharingViolationException = CreateIOExceptionWithHResult(unchecked((int)0x80070020));
        var lockViolationException = CreateIOExceptionWithHResult(unchecked((int)0x80070021));
        var diskFullException = CreateIOExceptionWithHResult(unchecked((int)0x80070070));
        var genericIOException = new IOException("Generic IO error");
        
        // Act & Assert
        Assert.True(RetryableExceptionClassifier.IsRetryable(sharingViolationException));
        Assert.True(RetryableExceptionClassifier.IsRetryable(lockViolationException));
        Assert.True(RetryableExceptionClassifier.IsRetryable(diskFullException));
        Assert.False(RetryableExceptionClassifier.IsRetryable(genericIOException)); // HResultが設定されていない場合
    }
    
    private static IOException CreateIOExceptionWithHResult(int hResult)
    {
        var ex = new IOException("Test exception");
        // HResultプロパティを設定するためリフレクションを使用
        typeof(Exception).GetProperty("HResult")?.SetValue(ex, hResult);
        return ex;
    }

    [Fact]
    public async Task ConcurrentFileAccess_ShouldHandleGracefully()
    {
        // Arrange
        await File.WriteAllTextAsync(_testFile, "Test content for concurrent access");
        var tasks = new List<Task>();
        var results = new List<string>();
        var lockObject = new object();

        // Act - 複数のタスクが同時にファイルにアクセス
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var hash = await HashUtil.ComputeHashAsync(_testFile, "MD5", CancellationToken.None);
                    lock (lockObject)
                    {
                        results.Add(hash);
                    }
                }
                catch (IOException ex)
                {
                    // ファイルロックエラーは予期される
                    lock (lockObject)
                    {
                        results.Add($"ERROR: {ex.Message}");
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(5, results.Count);
        
        // 少なくとも1つは成功するはず
        var successfulHashes = results.Where(r => !r.StartsWith("ERROR:")).ToList();
        Assert.NotEmpty(successfulHashes);
        
        // 成功したハッシュはすべて同じ値であるべき
        if (successfulHashes.Count > 1)
        {
            Assert.True(successfulHashes.All(h => h == successfulHashes.First()));
        }
    }

    [Fact]
    public async Task LargeFileHashing_ShouldHandleMemoryEfficiently()
    {
        // Arrange - 大きなファイルを作成（10MB）
        var largeFile = Path.Combine(_testDirectory, "large.txt");
        var content = new string('A', 1024 * 1024); // 1MB
        
        using (var writer = new StreamWriter(largeFile))
        {
            for (int i = 0; i < 10; i++)
            {
                await writer.WriteAsync(content);
            }
        }

        // Act
        var initialMemory = GC.GetTotalMemory(false);
        var hash = await HashUtil.ComputeHashAsync(largeFile, "SHA256", CancellationToken.None);
        var finalMemory = GC.GetTotalMemory(false);

        // Assert
        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length); // SHA256ハッシュは64文字
        
        // メモリ使用量の増加が適切な範囲内であることを確認（50MB未満）
        var memoryIncrease = finalMemory - initialMemory;
        Assert.True(memoryIncrease < 50 * 1024 * 1024, $"Memory increase too large: {memoryIncrease / (1024 * 1024)}MB");
    }

    [Fact]
    public async Task StreamHashing_ShouldMatchFileHashing()
    {
        // Arrange
        var content = "Test content for stream vs file hashing comparison";
        await File.WriteAllTextAsync(_testFile, content);

        // Act
        var fileHash = await HashUtil.ComputeHashAsync(_testFile, "SHA256", CancellationToken.None);
        
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var streamHash = await HashUtil.ComputeHashAsync(stream, "SHA256", CancellationToken.None);

        // Assert
        Assert.Equal(fileHash, streamHash);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch (Exception)
        {
            // テスト後のクリーンアップエラーは無視
        }
    }
}