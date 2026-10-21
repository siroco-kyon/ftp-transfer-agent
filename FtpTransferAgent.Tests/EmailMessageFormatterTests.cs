using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

public class EmailMessageFormatterTests
{
    [Fact]
    public void EmptySettings_PreserveLegacySubjectAndBody()
    {
        var options = new SmtpOptions { JobName = " ", SubjectPrefix = "", SourceServerName = null };
        var exception = new InvalidOperationException("details");
        var result = new EmailMessageFormatter(options).Format("Category", "original", exception,
            EmailTransferContext.For(new DestinationOptions { DisplayName = "大阪FTP" }, TransferAction.Upload));
        Assert.Equal("[Category] Error", result.Subject);
        Assert.Equal("original\n" + exception, result.Body);
    }

    [Fact]
    public void DefaultTemplate_UsesMachineNameAndNoProductionPrefix()
    {
        var result = new EmailMessageFormatter(new SmtpOptions { JobName = "売上データ連携" })
            .Format("Category", "details", null, null);
        Assert.Equal($"[売上データ連携][実行元: {Environment.MachineName}] 転送処理でエラー", result.Subject);
        Assert.Contains("概要: 転送処理でエラー", result.Body);
    }

    [Theory]
    [InlineData("{Unknown}")]
    [InlineData("{JobName")]
    [InlineData("test\r\nInjected: value")]
    [InlineData("test\u2028line")]
    public void InvalidTemplate_FallsBackWithoutThrowing(string template)
    {
        var result = new EmailMessageFormatter(new SmtpOptions { SubjectTemplate = template, SourceServerName = "APP01" })
            .Format("Category", "details", null, null);
        Assert.Equal("[FtpTransferAgent][実行元: APP01] 転送処理でエラー", result.Subject);
    }

    [Fact]
    public void PrefixAndCustomTemplate_ReplaceOnlyTemplateTokensOnce()
    {
        var result = new EmailMessageFormatter(new SmtpOptions
        {
            JobName = "業務{Summary}",
            SourceServerName = "APP01",
            SubjectPrefix = "検証",
            SubjectTemplate = "{Summary} / {JobName} / {SourceServerName}"
        }).Format("Category", "details", null, new EmailTransferContext("大阪", "host", "大阪へ転送失敗"));
        Assert.Equal("[検証]大阪へ転送失敗 / 業務{Summary} / APP01", result.Subject);
    }

    [Fact]
    public void FixedSubject_IsSupported()
    {
        var result = new EmailMessageFormatter(new SmtpOptions { SubjectTemplate = "ファイル連携のエラー通知" })
            .Format("Category", "details", null, null);
        Assert.Equal("ファイル連携のエラー通知", result.Subject);
    }

    [Fact]
    public void OversizedTemplate_UsesDefaultAndKeepsOriginalDetails()
    {
        var result = new EmailMessageFormatter(new SmtpOptions { SubjectTemplate = new string('x', 1001), SourceServerName = "APP01" })
            .Format("Category", "original", null, null);
        Assert.Equal("[FtpTransferAgent][実行元: APP01] 転送処理でエラー", result.Subject);
        Assert.EndsWith("original", result.Body);
    }

    [Fact]
    public void DisplayValues_AreSingleLineAndLongSubjectPreservesUnicodeAndFullBody()
    {
        var name = "連携\r\nInjected\u2028" + string.Concat(Enumerable.Repeat("🛰", 250));
        var result = new EmailMessageFormatter(new SmtpOptions { JobName = name, SubjectPrefix = "検証\n", SourceServerName = "APP01" })
            .Format("Category", "original", null, null);
        Assert.DoesNotContain('\r', result.Subject);
        Assert.DoesNotContain('\n', result.Subject);
        Assert.DoesNotContain('\u2028', result.Subject);
        Assert.Equal(200, result.Subject.EnumerateRunes().Count());
        Assert.EndsWith("…", result.Subject);
        Assert.Contains(string.Concat(Enumerable.Repeat("🛰", 250)), result.Body);
        Assert.EndsWith("original", result.Body);
    }

    [Theory]
    [InlineData("大阪FTP", "primary", "ftp", "host", "/out", "大阪FTP")]
    [InlineData(" ", "primary", "ftp", "host", "/out", "primary")]
    [InlineData(null, null, "sftp", "server", "/out", "server")]
    [InlineData(null, null, "local", "", "\\\\server\\share", "\\\\server\\share")]
    public void PeerName_UsesDisplayNameThenStableNameThenEndpoint(string? display, string? name, string mode, string host, string path, string expected)
    {
        var destination = new DestinationOptions { DisplayName = display, Name = name, Mode = mode, Host = host, RemotePath = path };
        Assert.Equal(expected, EmailTransferContext.DisplayName(destination));
        Assert.Equal(expected + "へ転送失敗", EmailTransferContext.For(destination, TransferAction.Upload).Summary);
        Assert.Equal(expected + "からの取得失敗", EmailTransferContext.For(destination, TransferAction.Download).Summary);
    }
}
