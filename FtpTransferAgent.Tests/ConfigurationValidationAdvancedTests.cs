using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 高度な設定バリデーションのテストクラス
/// </summary>
public class ConfigurationValidationAdvancedTests : IDisposable
{
    private readonly Mock<ILogger<ConfigurationValidator>> _loggerMock;
    private readonly ConfigurationValidator _validator;
    private readonly string _testDirectory;

    public ConfigurationValidationAdvancedTests()
    {
        _loggerMock = new Mock<ILogger<ConfigurationValidator>>();
        _validator = new ConfigurationValidator(_loggerMock.Object);
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ConfigTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void ValidateConfiguration_WithValidSettings_ShouldPass()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory, AllowedExtensions = new[] { "txt", "csv" } };
        var transfer = new TransferOptions 
        { 
            Mode = "sftp", 
            Direction = "put", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass",
            Concurrency = 2 
        };
        var retry = new RetryOptions { MaxAttempts = 3, DelaySeconds = 5 };
        var hash = new HashOptions { Algorithm = "SHA256" };
        var cleanup = new CleanupOptions { DeleteAfterVerify = false };

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.True(result.IsValid);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public void ValidateConfiguration_WithNonExistentDirectory_ShouldFail()
    {
        // Arrange
        var watch = new WatchOptions { Path = "/non/existent/path" };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Direction = "put", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass" 
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Watch directory does not exist"));
    }

    [Fact]
    public void ValidateConfiguration_WithHighConcurrencyAndManyRetries_ShouldWarn()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Direction = "put", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass",
            Concurrency = 10 
        };
        var retry = new RetryOptions { MaxAttempts = 10 };
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.True(result.IsValid);
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Warnings, w => w.Contains("High concurrency with many retry attempts"));
    }

    [Fact]
    public void ValidateConfiguration_WithBidirectionalAndHighConcurrency_ShouldWarn()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "sftp", 
            Direction = "both", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass",
            Concurrency = 8 
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Warnings, w => w.Contains("High concurrency with bidirectional transfer"));
    }

    [Fact]
    public void ValidateConfiguration_WithFtpMode_ShouldWarnAboutSecurity()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Host = "example.com", 
            Username = "user", 
            Password = "plaintext_password" 
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Warnings, w => w.Contains("FTP transmits passwords in plain text"));
    }

    [Fact]
    public void ValidateConfiguration_WithDeleteWithoutHash_ShouldFail()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "sftp", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass" 
        };
        var retry = new RetryOptions();
        var hash = new HashOptions { Algorithm = "" }; // ハッシュ無効
        var cleanup = new CleanupOptions { DeleteAfterVerify = true }; // ファイル削除有効

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Cannot delete files after verification when hash verification is disabled"));
    }

    [Fact]
    public void ValidateConfiguration_WithInvalidExtensions_ShouldFail()
    {
        // Arrange
        var watch = new WatchOptions 
        { 
            Path = _testDirectory, 
            AllowedExtensions = new[] { "txt", "", " ", "valid.ext" } 
        };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass" 
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid file extensions"));
    }

    [Fact]
    public void ValidateConfiguration_WithInvalidPort_ShouldFail()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass",
            Port = 70000 // 無効なポート番号
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid port number"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(301, true)]
    [InlineData(1, false)]
    [InlineData(30, false)]
    [InlineData(300, false)]
    public void ValidateConfiguration_WithRetryDelay_ShouldValidateCorrectly(int delaySeconds, bool shouldWarn)
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "ftp", 
            Host = "example.com", 
            Username = "user", 
            Password = "pass" 
        };
        var retry = new RetryOptions { DelaySeconds = delaySeconds };
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(shouldWarn, result.HasWarnings);
        
        if (shouldWarn)
        {
            Assert.Contains(result.Warnings, w => w.Contains("retry delay"));
        }
    }

    [Fact]
    public void AssessConfigurationChange_WithConnectionChanges_ShouldRequireRestart()
    {
        // Arrange
        var oldConfig = new TransferOptions 
        { 
            Host = "old.example.com", 
            Port = 21, 
            Username = "old_user",
            Concurrency = 1
        };
        var newConfig = new TransferOptions 
        { 
            Host = "new.example.com", 
            Port = 22, 
            Username = "new_user",
            Concurrency = 1
        };

        // Act
        var assessment = _validator.AssessConfigurationChange(oldConfig, newConfig);

        // Assert
        Assert.True(assessment.RequiresRestart);
        Assert.Contains(assessment.Impacts, i => i.Contains("Connection settings changed"));
        Assert.Contains(assessment.Impacts, i => i.Contains("Authentication settings changed"));
    }

    [Fact]
    public void AssessConfigurationChange_WithConcurrencyIncrease_ShouldWarnAboutPerformance()
    {
        // Arrange
        var oldConfig = new TransferOptions { Concurrency = 2 };
        var newConfig = new TransferOptions { Concurrency = 8 }; // 4倍増加

        // Act
        var assessment = _validator.AssessConfigurationChange(oldConfig, newConfig);

        // Assert
        Assert.True(assessment.RequiresRestart);
        Assert.Contains(assessment.Warnings, w => w.Contains("Significant increase in concurrency"));
    }

    [Fact]
    public void AssessConfigurationChange_WithDirectionChange_ShouldRequireRestart()
    {
        // Arrange
        var oldConfig = new TransferOptions { Direction = "put" };
        var newConfig = new TransferOptions { Direction = "both" };

        // Act
        var assessment = _validator.AssessConfigurationChange(oldConfig, newConfig);

        // Assert
        Assert.True(assessment.RequiresRestart);
        Assert.Contains(assessment.Impacts, i => i.Contains("Transfer direction changed"));
    }

    [Fact]
    public void ValidateConfiguration_WithMissingPrivateKeyFile_ShouldFail()
    {
        // Arrange
        var watch = new WatchOptions { Path = _testDirectory };
        var transfer = new TransferOptions 
        { 
            Mode = "sftp", 
            Host = "example.com", 
            Username = "user",
            PrivateKeyPath = "/non/existent/key.pem" // 存在しない鍵ファイル
        };
        var retry = new RetryOptions();
        var hash = new HashOptions();
        var cleanup = new CleanupOptions();

        // Act
        ConfigurationValidationResult result = _validator.ValidateConfiguration(watch, transfer, retry, hash, cleanup);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Private key file not found"));
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