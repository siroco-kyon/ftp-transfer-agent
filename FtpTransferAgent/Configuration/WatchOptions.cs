using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// フォルダ監視に関する設定
/// </summary>
public class WatchOptions
{
    [Required]
    public string Path { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; }

    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
}
