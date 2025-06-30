using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// TransferOptions のカスタムバリデーション属性
/// </summary>
public class TransferOptionsValidationAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        if (value is not TransferOptions options)
        {
            return false;
        }

        // FTP モードの場合はパスワードが必須
        if (options.Mode == "ftp" && string.IsNullOrEmpty(options.Password))
        {
            ErrorMessage = "Password is required for FTP mode";
            return false;
        }

        // SFTP モードの場合はパスワードまたは秘密鍵が必須
        if (options.Mode == "sftp" &&
            string.IsNullOrEmpty(options.Password) &&
            string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            ErrorMessage = "Password or PrivateKeyPath must be specified for SFTP mode";
            return false;
        }

        return true;
    }
}