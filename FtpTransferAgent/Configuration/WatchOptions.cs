using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

public class WatchOptions
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; }

    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
}
