using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

public class HashOptions
{
    [Required]
    [RegularExpression("MD5|SHA256")]
    public string Algorithm { get; set; } = "MD5";
}
