using System.IO;
using System.Linq;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// Mode=local (ローカル / UNC 宛先) に関する起動時バリデーションを検証する。
/// local 追加に伴い Host/Username の [Required] を外したため、ftp/sftp の必須項目が
/// ConfigurationValidator 側で引き続き担保されることも回帰として確認する。
/// </summary>
public class LocalDestinationValidationTests : IDisposable
{
    private readonly string _watchDir;
    private readonly ConfigurationValidator _validator = new(NullLogger<ConfigurationValidator>.Instance);

    public LocalDestinationValidationTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "local-val-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_watchDir);
    }

    private ConfigurationValidationResult Validate(TransferOptions transfer) =>
        _validator.ValidateConfiguration(
            new WatchOptions { Path = _watchDir },
            transfer,
            new RetryOptions(),
            new HashOptions { Enabled = true, Algorithm = "SHA256" },
            new CleanupOptions());

    [Fact]
    public void Local_Put_ValidConfiguration_HasNoErrors_AndSecurityInfo()
    {
        var result = Validate(new TransferOptions
        {
            Mode = "local",
            Direction = "put",
            RemotePath = Path.Combine(_watchDir, "out"),  // 絶対パス
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Infos, i => i.Contains("SMB1"));
    }

    [Fact]
    public void Local_Get_IsRejected()
    {
        var result = Validate(new TransferOptions
        {
            Mode = "local",
            Direction = "get",
            RemotePath = Path.Combine(_watchDir, "out"),
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Direction 'put'"));
    }

    [Fact]
    public void Local_RelativeRemotePath_ProducesWarning()
    {
        var result = Validate(new TransferOptions
        {
            Mode = "local",
            Direction = "put",
            RemotePath = "relative-out",  // 絶対/UNC ではない
        });

        Assert.Contains(result.Warnings, w => w.Contains("absolute/UNC"));
    }

    [Fact]
    public void Ftp_MissingHost_IsRejected_EvenWithoutRequiredAttribute()
    {
        var result = Validate(new TransferOptions
        {
            Mode = "ftp",
            Direction = "put",
            Host = "",          // [Required] を外したため、ここで弾けることを確認
            Username = "user",
            Password = "pass",
            RemotePath = "/upload",
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Host is required"));
    }

    [Fact]
    public void Sftp_MissingUsername_IsRejected()
    {
        var result = Validate(new TransferOptions
        {
            Mode = "sftp",
            Direction = "put",
            Host = "sftp.example.com",
            Port = 22,
            Username = "",
            Password = "pass",
            RemotePath = "/in",
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Username is required"));
    }

    [Fact]
    public void AdditionalLocalDestination_WithoutHost_IsAccepted()
    {
        var result = Validate(new TransferOptions
        {
            Name = "primary",
            Mode = "sftp",
            Direction = "put",
            Host = "sftp.example.com",
            Port = 22,
            Username = "user",
            Password = "pass",
            RemotePath = "/in",
            AdditionalDestinations = new List<DestinationOptions>
            {
                new() { Name = "share", Mode = "local", RemotePath = Path.Combine(_watchDir, "share") }
            }
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e => e.Contains("Host is required"));
    }

    [Fact]
    public void AdditionalLocalDestination_WithoutRemotePath_IsRejected()
    {
        var result = Validate(new TransferOptions
        {
            Name = "primary",
            Mode = "sftp",
            Direction = "put",
            Host = "sftp.example.com",
            Port = 22,
            Username = "user",
            Password = "pass",
            RemotePath = "/in",
            AdditionalDestinations = new List<DestinationOptions>
            {
                new() { Name = "share", Mode = "local", RemotePath = "" }
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("RemotePath is required"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchDir, true); } catch { /* best effort */ }
    }
}
