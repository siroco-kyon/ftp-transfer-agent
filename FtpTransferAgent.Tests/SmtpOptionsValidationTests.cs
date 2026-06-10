using System.ComponentModel.DataAnnotations;
using FtpTransferAgent.Configuration;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="SmtpOptions"/> の条件付きバリデーション
/// (Enabled = true の場合のみ接続・宛先設定を必須にする) を検証する
/// </summary>
public class SmtpOptionsValidationTests
{
    private static List<ValidationResult> ValidateObject(object obj)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Disabled_WithEmptySettings_IsValid()
    {
        // メール通知を使わない構成では宛先等を要求しない (空の Smtp セクションで起動可能)
        var options = new SmtpOptions { Enabled = false };
        Assert.Empty(ValidateObject(options));
    }

    [Fact]
    public void Enabled_WithValidSettings_IsValid()
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "smtp.example.com",
            RelayPort = 587,
            From = "noreply@example.com",
            To = new[] { "admin@example.com", "ops@example.com" }
        };
        Assert.Empty(ValidateObject(options));
    }

    [Fact]
    public void Enabled_WithoutRelayHost_IsInvalid()
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "",
            From = "noreply@example.com",
            To = new[] { "admin@example.com" }
        };
        Assert.Contains(ValidateObject(options), r => r.MemberNames.Contains(nameof(SmtpOptions.RelayHost)));
    }

    [Fact]
    public void Enabled_WithoutRecipients_IsInvalid()
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "smtp.example.com",
            From = "noreply@example.com",
            To = Array.Empty<string>()
        };
        Assert.Contains(ValidateObject(options), r => r.MemberNames.Contains(nameof(SmtpOptions.To)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Enabled_WithInvalidFrom_IsInvalid(string from)
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "smtp.example.com",
            From = from,
            To = new[] { "admin@example.com" }
        };
        Assert.Contains(ValidateObject(options), r => r.MemberNames.Contains(nameof(SmtpOptions.From)));
    }

    [Fact]
    public void Enabled_WithInvalidRecipient_IsInvalid()
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "smtp.example.com",
            From = "noreply@example.com",
            To = new[] { "admin@example.com", "broken-address" }
        };
        Assert.Contains(ValidateObject(options), r => r.MemberNames.Contains(nameof(SmtpOptions.To)));
    }

    [Fact]
    public void Enabled_WithInvalidRelayPort_IsInvalid()
    {
        var options = new SmtpOptions
        {
            Enabled = true,
            RelayHost = "smtp.example.com",
            RelayPort = 0,
            From = "noreply@example.com",
            To = new[] { "admin@example.com" }
        };
        Assert.Contains(ValidateObject(options), r => r.MemberNames.Contains(nameof(SmtpOptions.RelayPort)));
    }

    [Fact]
    public void MaxEmailsPerRun_DefaultsTo100()
    {
        Assert.Equal(100, new SmtpOptions().MaxEmailsPerRun);
    }
}
