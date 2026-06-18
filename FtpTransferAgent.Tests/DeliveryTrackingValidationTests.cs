using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// 宛先別配信トラッキングの設定バリデーションと、メール抑制用 EventId の判定を検証する。
/// </summary>
public class DeliveryTrackingValidationTests : IDisposable
{
    private readonly string _watchDir;

    public DeliveryTrackingValidationTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "valid-watch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_watchDir);
    }

    private ConfigurationValidationResult Validate(TransferOptions transfer)
    {
        var validator = new ConfigurationValidator(NullLogger<ConfigurationValidator>.Instance);
        return validator.ValidateConfiguration(
            new WatchOptions { Path = _watchDir, AllowedExtensions = new[] { ".txt" } },
            transfer,
            new RetryOptions(),
            new HashOptions { Enabled = true, Algorithm = "SHA256" },
            new CleanupOptions());
    }

    private DestinationOptions ValidBackup(string? name) => new()
    {
        Name = name,
        Mode = "ftp",
        Host = "backup",
        Username = "u",
        Password = "p",
        RemotePath = "/backup"
    };

    private TransferOptions ValidPrimary(string? name) => new()
    {
        Name = name,
        Mode = "ftp",
        Direction = "put",
        Host = "primary",
        Username = "u",
        Password = "p",
        RemotePath = "/primary",
        PerDestinationDeliveryTracking = true,
        DeliverySignatureMode = "sizetime"
    };

    [Fact]
    public void MissingDestinationName_ProducesError()
    {
        var transfer = ValidPrimary("primary");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup(name: null) };

        var result = Validate(transfer);

        Assert.Contains(result.Errors, e => e.Contains("Missing Name"));
    }

    [Fact]
    public void DuplicateDestinationNames_ProducesError()
    {
        var transfer = ValidPrimary("dup");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("DUP") }; // 大小無視で重複

        var result = Validate(transfer);

        Assert.Contains(result.Errors, e => e.Contains("Duplicate Name"));
    }

    [Fact]
    public void InvalidSignatureMode_ProducesError()
    {
        var transfer = ValidPrimary("primary");
        transfer.DeliverySignatureMode = "weird";
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("backup") };

        var result = Validate(transfer);

        Assert.Contains(result.Errors, e => e.Contains("DeliverySignatureMode"));
    }

    [Fact]
    public void StateDirectory_SameAsWatchPath_ProducesError()
    {
        var transfer = ValidPrimary("primary");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("backup") };
        transfer.StateDirectory = _watchDir;

        var result = Validate(transfer);

        Assert.Contains(result.Errors, e => e.Contains("StateDirectory") && e.Contains("Watch.Path"));
    }

    [Fact]
    public void StateDirectory_ParentOfWatchPath_ProducesError()
    {
        var transfer = ValidPrimary("primary");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("backup") };
        transfer.StateDirectory = Path.GetDirectoryName(_watchDir);

        var result = Validate(transfer);

        Assert.Contains(result.Errors, e => e.Contains("StateDirectory") && e.Contains("parent directory"));
    }

    [Fact]
    public void StateDirectory_ChildOfWatchPath_IsAllowed()
    {
        var transfer = ValidPrimary("primary");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("backup") };
        transfer.StateDirectory = Path.Combine(_watchDir, ".delivery-state");

        var result = Validate(transfer);

        Assert.DoesNotContain(result.Errors, e => e.Contains("StateDirectory"));
    }

    [Fact]
    public void TrackingWithGetDirection_ProducesWarning()
    {
        var transfer = ValidPrimary("primary");
        transfer.Direction = "get";

        var result = Validate(transfer);

        Assert.Contains(result.Warnings, w => w.Contains("Delivery tracking applies only to uploads"));
    }

    [Fact]
    public void UniqueNames_NoDeliveryTrackingErrors()
    {
        var transfer = ValidPrimary("primary");
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup("backup") };

        var result = Validate(transfer);

        Assert.DoesNotContain(result.Errors, e => e.Contains("PerDestinationDeliveryTracking"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("DeliverySignatureMode"));
    }

    [Fact]
    public void Tracking_Disabled_SkipsNameRequirement()
    {
        var transfer = ValidPrimary(name: null);
        transfer.PerDestinationDeliveryTracking = false;
        transfer.AdditionalDestinations = new List<DestinationOptions> { ValidBackup(name: null) };

        var result = Validate(transfer);

        Assert.DoesNotContain(result.Errors, e => e.Contains("Missing Name"));
    }

    [Theory]
    [InlineData(1001, true)]
    [InlineData(1002, true)]
    [InlineData(0, false)]
    [InlineData(500, false)]
    public void LogEvents_IsMultiDestinationFailure_IdentifiesFailureEvents(int eventId, bool expected)
    {
        Assert.Equal(expected, LogEvents.IsMultiDestinationFailure(new EventId(eventId)));
    }

    [Fact]
    public void SuppressPerDestinationFailureDetailEmails_DefaultsFalse()
    {
        Assert.False(new SmtpOptions().SuppressPerDestinationFailureDetailEmails);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_watchDir)) Directory.Delete(_watchDir, true); } catch { /* ignore */ }
    }
}
