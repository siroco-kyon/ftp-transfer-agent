using System.Text;
using FtpTransferAgent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// END の末尾文字列指定と拡張子指定の動作を検証する。
/// </summary>
public class EndFileSuffixConfigurationTests
{
    [Fact]
    public void ValidateConfiguration_SuffixesWithoutExplicitExtensions_ShouldAcceptExtensionDefaults()
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { ".END", "_end" };

        var result = Validate(watch);

        Assert.Equal(new[] { ".END", ".end" }, watch.EndFileExtensions);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitExtensionsWithoutSuffixDeclaration_ShouldUseExtensionMatching()
    {
        var watch = CreateWatch();
        watch.EndFileExtensions = new[] { "END", "_end" };

        var result = Validate(watch);

        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateConfiguration_WithoutEndDeclarations_ShouldUseDefaultDotEndSuffix()
    {
        var watch = CreateWatch();

        var result = Validate(watch);

        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateConfiguration_InvalidDefaultSuffixValue_ShouldRejectEvenWithoutSetterCall()
    {
        var watch = CreateWatch();
        Assert.NotNull(watch.EndFileSuffixes);
        watch.EndFileSuffixes[0] = "folder/_end";

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitExtensionsAndSuffixes_ShouldRejectBothSettings()
    {
        var watch = CreateWatch();
        watch.EndFileExtensions = new[] { ".END" };
        watch.EndFileSuffixes = new[] { "_end" };

        AssertBothSettingsRejected(Validate(watch));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitEmptyExtensionsAndSuffixes_ShouldRejectBothSettings()
    {
        var watch = CreateWatch();
        watch.EndFileExtensions = Array.Empty<string>();
        watch.EndFileSuffixes = new[] { "_end" };

        AssertBothSettingsRejected(Validate(watch));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitNullExtensionsAndSuffixes_ShouldRejectBothSettings()
    {
        var watch = CreateWatch();
        watch.EndFileExtensions = null!;
        watch.EndFileSuffixes = new[] { "_end" };

        AssertBothSettingsRejected(Validate(watch));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitExtensionsAndNullSuffixes_ShouldRejectBothSettings()
    {
        var watch = CreateWatch();
        watch.EndFileExtensions = new[] { ".END" };
        watch.EndFileSuffixes = null;

        AssertBothSettingsRejected(Validate(watch));
    }

    [Fact]
    public void ValidateConfiguration_BothSettingsWithoutActiveEndFeature_ShouldStillReject()
    {
        var watch = CreateWatch();
        watch.RequireEndFile = false;
        watch.TransferEndFiles = false;
        watch.EndFileExtensions = new[] { ".END" };
        watch.EndFileSuffixes = new[] { "_end" };

        AssertBothSettingsRejected(Validate(watch));
    }

    [Theory]
    [InlineData("_end", ".csv_end")]
    [InlineData(".csv_end", "_end")]
    [InlineData("_END", ".csv_end")]
    [InlineData(".END", "_ready.end")]
    public void ValidateConfiguration_OverlappingSuffixes_ShouldRejectAmbiguousMapping(string first, string second)
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { first, second };

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".END", "_end")]
    [InlineData("_end", "_done")]
    [InlineData("_ready_end", "_ready_done")]
    public void ValidateConfiguration_DistinctSuffixes_ShouldAccept(string first, string second)
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { first, second };

        var result = Validate(watch);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateConfiguration_CaseVariantsAndDuplicates_ShouldNotRejectAsOverlap()
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { "_end", "_END", "_end" };

        var result = Validate(watch);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../_end")]
    [InlineData("folder/_end")]
    [InlineData("folder\\_end")]
    [InlineData("..")]
    [InlineData("_..end")]
    [InlineData("*_end")]
    [InlineData("?_end")]
    [InlineData(":end")]
    [InlineData("<end")]
    [InlineData(">end")]
    [InlineData("\"end")]
    [InlineData("|end")]
    [InlineData("_end\0")]
    [InlineData("_end\u0001")]
    [InlineData("_end\u007f")]
    [InlineData("_ end")]
    [InlineData("_end\t")]
    [InlineData("_end\r\n")]
    [InlineData("_end\u3000")]
    public void ValidateConfiguration_InvalidSuffix_ShouldReject(string? suffix)
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { "_end", suffix! };

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_InvalidSuffixWithoutActiveEndFeature_ShouldStillReject()
    {
        var watch = CreateWatch();
        watch.RequireEndFile = false;
        watch.TransferEndFiles = false;
        watch.EndFileSuffixes = new[] { "folder/_end" };

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_OverlappingSuffixesWithoutActiveEndFeature_ShouldStillReject()
    {
        var watch = CreateWatch();
        watch.RequireEndFile = false;
        watch.TransferEndFiles = false;
        watch.EndFileSuffixes = new[] { "_end", ".csv_end" };

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void ValidateConfiguration_SuffixLength_ShouldEnforceMaximum(int length, bool expectedValid)
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = new[] { new string('_', length) };

        var result = Validate(watch);

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ValidateConfiguration_ActiveEndFeatureWithEmptySuffixes_ShouldReject(bool requireEndFile, bool transferEndFiles)
    {
        var watch = CreateWatch();
        watch.RequireEndFile = requireEndFile;
        watch.TransferEndFiles = transferEndFiles;
        watch.EndFileSuffixes = Array.Empty<string>();

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitNullSuffixesWithActiveEndFeature_ShouldReject()
    {
        var watch = CreateWatch();
        watch.EndFileSuffixes = null;

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_EmptySuffixesWithoutActiveEndFeature_ShouldAccept()
    {
        var watch = CreateWatch();
        watch.RequireEndFile = false;
        watch.TransferEndFiles = false;
        watch.EndFileSuffixes = Array.Empty<string>();

        var result = Validate(watch);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateConfiguration_ExplicitNullSuffixesWithoutActiveEndFeature_ShouldReject()
    {
        var watch = CreateWatch();
        watch.RequireEndFile = false;
        watch.TransferEndFiles = false;
        watch.EndFileSuffixes = null;

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void Bind_OnlySuffixesDeclared_ShouldAcceptWithoutTreatingExtensionDefaultsAsExplicit()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true, "EndFileSuffixes": [ ".END", "_end" ] } }""");

        var result = Validate(watch);

        Assert.Equal(new[] { ".END", "_end" }, watch.EndFileSuffixes);
        Assert.Equal(new[] { ".END", ".end" }, watch.EndFileExtensions);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_WithoutEndDeclarations_ShouldRetainDefaultSuffixes()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true } }""");

        var result = Validate(watch);

        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_WithoutEndDeclarationsThenSetExtensions_ShouldNotTreatDefaultSuffixesAsExplicit()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true } }""");
        watch.EndFileExtensions = new[] { "TRG" };

        var result = Validate(watch);

        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_OnlyExtensionsDeclared_ShouldAcceptWithoutTreatingDefaultSuffixesAsExplicit()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true, "EndFileExtensions": [ "TRG", "_end" ] } }""");

        var result = Validate(watch);

        Assert.Equal(new[] { "TRG", "_end" }, watch.EndFileExtensions);
        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_EmptyExtensionsDeclared_ShouldRejectActiveEndFeatureDespiteDefaultSuffixes()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true, "EndFileExtensions": [] } }""");

        var result = Validate(watch);

        Assert.Equal(new[] { ".END" }, watch.EndFileSuffixes);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("END file extensions", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("cannot both", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[ \".END\" ]")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Bind_BothSettingsDeclared_ShouldRejectEvenWhenExtensionArrayHasNoValues(string extensionsJson)
    {
        var watch = Bind($$"""{ "Watch": { "RequireEndFile": true, "EndFileExtensions": {{extensionsJson}}, "EndFileSuffixes": [ "_end" ] } }""");

        AssertBothSettingsRejected(Validate(watch));
    }

    [Fact]
    public void Bind_EmptySuffixArray_ShouldCountAsDeclarationAndRejectActiveEndFeature()
    {
        var watch = Bind("""{ "Watch": { "RequireEndFile": true, "EndFileSuffixes": [] } }""");

        var result = Validate(watch);

        Assert.NotNull(watch.EndFileSuffixes);
        Assert.Empty(watch.EndFileSuffixes);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void Bind_NullSuffixArrayWithoutActiveEndFeature_ShouldReject()
    {
        var watch = Bind("""{ "Watch": { "EndFileSuffixes": null } }""");

        var result = Validate(watch);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void Bind_EmptySuffixArrayWithoutActiveEndFeature_ShouldAccept()
    {
        var watch = Bind("""{ "Watch": { "EndFileSuffixes": [] } }""");

        var result = Validate(watch);

        Assert.NotNull(watch.EndFileSuffixes);
        Assert.Empty(watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_HigherPriorityEmptySuffixArray_ShouldOverrideLowerPriorityValues()
    {
        var watch = Bind(
            """{ "Watch": { "RequireEndFile": true, "EndFileSuffixes": [ ".END", "_end" ] } }""",
            """{ "Watch": { "EndFileSuffixes": [] } }""");

        Assert.NotNull(watch.EndFileSuffixes);
        Assert.Empty(watch.EndFileSuffixes);
        Assert.False(Validate(watch).IsValid);
    }

    [Fact]
    public void Bind_HigherPrioritySuffixArray_ShouldReplaceAllLowerPriorityValues()
    {
        var watch = Bind(
            """{ "Watch": { "RequireEndFile": true, "EndFileSuffixes": [ ".END", ".csv_end" ] } }""",
            """{ "Watch": { "EndFileSuffixes": [ "_end" ] } }""");

        var result = Validate(watch);

        Assert.Equal(new[] { "_end" }, watch.EndFileSuffixes);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Bind_ExtensionsInBaseAndSuffixesInOverride_ShouldRejectBothDeclarations()
    {
        var watch = Bind(
            """{ "Watch": { "RequireEndFile": true, "EndFileExtensions": [ ".END" ] } }""",
            """{ "Watch": { "EndFileSuffixes": [ "_end" ] } }""");

        AssertBothSettingsRejected(Validate(watch));
    }

    private static WatchOptions CreateWatch() => new()
    {
        Path = Path.GetTempPath(),
        RequireEndFile = true
    };

    private static ConfigurationValidationResult Validate(WatchOptions watch)
    {
        var validator = new ConfigurationValidator(NullLogger<ConfigurationValidator>.Instance);
        return validator.ValidateConfiguration(
            watch,
            new TransferOptions
            {
                Mode = "sftp",
                Direction = "put",
                Host = "example.com",
                Username = "user",
                Password = "pass",
                HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                RemotePath = "/remote"
            },
            new RetryOptions { MaxAttempts = 3, DelaySeconds = 5 },
            new HashOptions { Algorithm = "SHA256" },
            new CleanupOptions());
    }

    private static void AssertBothSettingsRejected(ConfigurationValidationResult result)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("EndFileExtensions", StringComparison.Ordinal)
            && error.Contains("EndFileSuffixes", StringComparison.Ordinal));
    }

    private static WatchOptions Bind(params string[] jsonDocuments)
    {
        var builder = new ConfigurationBuilder();
        foreach (var json in jsonDocuments)
        {
            builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        var configuration = builder.Build();
        var watch = new WatchOptions { Path = Path.GetTempPath() };
        configuration.GetSection("Watch").Bind(watch);
        ConfigurationArrayBinder.ReplaceIfDeclared(configuration, "Watch:EndFileExtensions", values => watch.EndFileExtensions = values);
        ConfigurationArrayBinder.ReplaceNullableIfDeclared(configuration, "Watch:EndFileSuffixes", values => watch.EndFileSuffixes = values);
        return watch;
    }
}
