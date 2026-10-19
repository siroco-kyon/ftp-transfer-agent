using System.ComponentModel.DataAnnotations;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// フォルダ監視に関する設定
/// </summary>
public class WatchOptions
{
    private string[] _endFileExtensions = new[] { ".END", ".end" };
    private string[]? _endFileSuffixes = new[] { ".END" };

    [Required]
    public string Path { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; }

    /// <summary>
    /// 転送対象のファイルパターン。以下を受け入れる:
    ///   - 拡張子のみ: "txt" / ".txt" → 拡張子完全一致
    ///   - ワイルドカード: "*.txt" / "data_*.csv" / "?.log" → ファイル名グロブ一致
    /// 空配列の場合は全ファイルが対象。
    /// </summary>
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();

    /// <summary>
    /// ENDファイルがある場合のみ転送対象にするかどうか
    /// </summary>
    public bool RequireEndFile { get; set; } = false;

    /// <summary>
    /// 拡張子で END を指定する場合に使う。ドットがない値にはドットを補う。
    /// 明示時はこの方式を使う。EndFileSuffixes との同時指定は不可。
    /// </summary>
    public string[] EndFileExtensions
    {
        get => _endFileExtensions;
        set
        {
            // ConfigurationBinder は未宣言の配列にも同じ参照を再代入する。
            // 宣言された値は PostConfigure で必ず新配列に置き換えるため、同じ参照は明示扱いにしない。
            if (!ReferenceEquals(_endFileExtensions, value))
            {
                HasExplicitEndFileExtensions = true;
            }
            _endFileExtensions = value;
        }
    }

    /// <summary>
    /// END の末尾文字列（既定 .END）。データファイルの完全な名前にそのまま追加する。
    /// 例: [".END", "_end"] は data.csv.END / data.csv_end に対応する。
    /// 両設定の省略時もこの方式を使う。EndFileExtensions との同時指定や末尾が重なる値は不可。
    /// </summary>
    public string[]? EndFileSuffixes
    {
        get => _endFileSuffixes;
        set
        {
            // Binder による未宣言配列の再代入では、既定値を明示設定とみなさない。
            if (!ReferenceEquals(_endFileSuffixes, value))
            {
                HasExplicitEndFileSuffixes = true;
            }
            _endFileSuffixes = value;
        }
    }

    // C# の既定値と、null / [] を含む明示的な設定を区別する。
    internal bool HasExplicitEndFileExtensions { get; private set; }
    internal bool HasExplicitEndFileSuffixes { get; private set; }
    internal bool UsesEndFileSuffixes => HasExplicitEndFileSuffixes || !HasExplicitEndFileExtensions;

    /// <summary>
    /// ENDファイル自体も転送するかどうか（デフォルトは false）
    /// trueの場合、対象ファイルの後にENDファイルが転送される
    /// </summary>
    public bool TransferEndFiles { get; set; } = false;
}
