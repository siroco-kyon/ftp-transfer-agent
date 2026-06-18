using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// SMTP 通知に関する設定。
/// 接続・宛先の必須チェックは <see cref="Enabled"/> が true の場合のみ行う
/// (メール通知を使わない構成で設定を要求しないため)。
/// </summary>
public class SmtpOptions : IValidatableObject
{
    /// <summary>
    /// エラーメール送信を有効にするかどうか
    /// </summary>
    public bool Enabled { get; set; }
    public string RelayHost { get; set; } = string.Empty;
    public int RelayPort { get; set; } = 25;
    public bool UseTls { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string[] To { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 1 回のバッチ実行で送信するエラーメールの上限。
    /// 大量エラー時のメール洪水を防ぐ。0 以下で無制限。
    /// </summary>
    public int MaxEmailsPerRun { get; set; } = 100;

    /// <summary>
    /// 複数宛先 (AdditionalDestinations あり) で個々の宛先への転送が失敗した際の
    /// エラーメールを抑制する。true にすると、ある宛先がメンテ等で継続的に失敗しても
    /// その「宛先失敗」メールだけを止め、設定不備・認証など他のエラーメールは送信し続ける。
    /// ファイルログ・終了コードには影響しない (障害自体は記録される)。既定 false。
    /// </summary>
    public bool SuppressMultiDestinationFailureEmails { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(RelayHost))
        {
            yield return new ValidationResult("RelayHost is required when Smtp is enabled", new[] { nameof(RelayHost) });
        }

        if (RelayPort < 1 || RelayPort > 65535)
        {
            yield return new ValidationResult($"Invalid RelayPort: {RelayPort}. Must be between 1 and 65535.", new[] { nameof(RelayPort) });
        }

        var emailValidator = new EmailAddressAttribute();
        if (string.IsNullOrWhiteSpace(From) || !emailValidator.IsValid(From))
        {
            yield return new ValidationResult($"From must be a valid email address when Smtp is enabled (got '{From}')", new[] { nameof(From) });
        }

        if (To is null || To.Length == 0)
        {
            yield return new ValidationResult("At least one recipient email address is required when Smtp is enabled", new[] { nameof(To) });
        }
        else
        {
            foreach (var recipient in To)
            {
                if (string.IsNullOrWhiteSpace(recipient) || !emailValidator.IsValid(recipient))
                {
                    yield return new ValidationResult($"Invalid recipient email address: '{recipient}'", new[] { nameof(To) });
                }
            }
        }
    }
}
