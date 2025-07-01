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

    /// <summary>
    /// ENDファイルがある場合のみ転送対象にするかどうか
    /// </summary>
    public bool RequireEndFile { get; set; } = false;

    /// <summary>
    /// ENDファイルの拡張子リスト（デフォルトは .END と .end）
    /// </summary>
    public string[] EndFileExtensions { get; set; } = new[] { ".END", ".end" };

    /// <summary>
    /// ENDファイル自体も転送するかどうか（デフォルトは false）
    /// trueの場合、対象ファイルの後にENDファイルが転送される
    /// </summary>
    public bool TransferEndFiles { get; set; } = false;
}
