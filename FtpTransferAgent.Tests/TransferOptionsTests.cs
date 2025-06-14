using System.ComponentModel.DataAnnotations;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="TransferOptions"/> の検証ロジックをテストする
/// </summary>
public class TransferOptionsTests
{
    /// <summary>
    /// 必要な設定値が不足している場合に検証エラーとなることを確認
    /// </summary>
    [Fact]
    public void Validate_InvalidConfiguration_ReturnsErrors()
    {
        var options = new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "host",
            Username = "user",
            RemotePath = "/remote"
            // FTP モードでは Password が必須
        };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        bool valid = Validator.TryValidateObject(options, context, results, true);
        Assert.False(valid);
        Assert.Contains(results, r => r.ErrorMessage!.Contains("Password is required"));
    }

    /// <summary>
    /// 有効な設定値を与えた場合に検証が成功することを確認
    /// </summary>
    [Fact]
    public void Validate_ValidConfiguration_Passes()
    {
        var options = new TransferOptions
        {
            Mode = "sftp",
            Direction = "get",
            Host = "host",
            Username = "user",
            Password = "pass",
            RemotePath = "/remote"
        };
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        bool valid = Validator.TryValidateObject(options, context, results, true);
        Assert.True(valid);
    }
}
