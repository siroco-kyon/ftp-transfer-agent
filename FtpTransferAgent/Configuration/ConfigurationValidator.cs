using System.ComponentModel.DataAnnotations;
using System.Security;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Configuration;

/// <summary>
/// 設定の整合性チェックを行うバリデーター
/// </summary>
public class ConfigurationValidator
{
    private readonly ILogger<ConfigurationValidator> _logger;

    public ConfigurationValidator(ILogger<ConfigurationValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 設定の包括的なバリデーションを実行
    /// </summary>
    public ConfigurationValidationResult ValidateConfiguration(
        WatchOptions watch,
        TransferOptions transfer,
        RetryOptions retry,
        HashOptions hash,
        CleanupOptions cleanup)
    {
        var result = new ConfigurationValidationResult();

        // 基本的な設定チェック
        ValidateBasicConfiguration(watch, transfer, result);

        // パフォーマンス関連の設定チェック
        ValidatePerformanceConfiguration(transfer, retry, result);

        // セキュリティ関連の設定チェック
        ValidateSecurityConfiguration(transfer, hash, cleanup, result);

        // 設定の組み合わせチェック
        ValidateConfigurationCombinations(watch, transfer, hash, cleanup, result);

        // 追加宛先のバリデーション
        ValidateAdditionalDestinations(transfer, result);

        return result;
    }

    private void ValidateAdditionalDestinations(TransferOptions transfer, ConfigurationValidationResult result)
    {
        if (transfer.AdditionalDestinations is null || transfer.AdditionalDestinations.Count == 0)
        {
            return;
        }

        if (transfer.Direction is "get" or "both")
        {
            result.Warnings.Add($"AdditionalDestinations is set ({transfer.AdditionalDestinations.Count} entries) but Direction is '{transfer.Direction}'. Additional destinations are used only for uploads.");
        }

        for (int i = 0; i < transfer.AdditionalDestinations.Count; i++)
        {
            var d = transfer.AdditionalDestinations[i];
            var label = $"[Destination#{i + 1} host={d.Host}]";

            if (d is null)
            {
                result.Errors.Add($"{label} is null");
                continue;
            }
            if (string.IsNullOrWhiteSpace(d.Host))
            {
                result.Errors.Add($"{label} Host is required");
            }
            if (string.IsNullOrWhiteSpace(d.Username))
            {
                result.Errors.Add($"{label} Username is required");
            }
            if (string.IsNullOrWhiteSpace(d.RemotePath))
            {
                result.Errors.Add($"{label} RemotePath is required");
            }
            if (!(d.Mode == "ftp" || d.Mode == "sftp"))
            {
                result.Errors.Add($"{label} Mode must be 'ftp' or 'sftp' (got '{d.Mode}')");
            }
            if (d.Port < 1 || d.Port > 65535)
            {
                result.Errors.Add($"{label} Invalid port: {d.Port}");
            }
            if (d.Mode == "ftp" && string.IsNullOrEmpty(d.Password))
            {
                result.Errors.Add($"{label} Password is required for FTP mode");
            }
            if (d.Mode == "sftp"
                && string.IsNullOrEmpty(d.Password)
                && string.IsNullOrEmpty(d.PrivateKeyPath))
            {
                result.Errors.Add($"{label} Password or PrivateKeyPath must be specified for SFTP mode");
            }
            if (d.Mode == "sftp" && !string.IsNullOrEmpty(d.PrivateKeyPath) && !File.Exists(d.PrivateKeyPath))
            {
                result.Errors.Add($"{label} Private key file not found: {d.PrivateKeyPath}");
            }
        }
    }

    private void ValidateBasicConfiguration(WatchOptions watch, TransferOptions transfer, ConfigurationValidationResult result)
    {
        // ローカルパスの存在チェック
        if (transfer.Direction is "put" or "both")
        {
            if (!Directory.Exists(watch.Path))
            {
                result.Errors.Add($"Watch directory does not exist: {watch.Path}");
            }
            else if (!HasReadPermission(watch.Path))
            {
                result.Warnings.Add($"May not have read permission for watch directory: {watch.Path}");
            }
        }
        
        // ダウンロード先ディレクトリの存在チェック
        if (transfer.Direction is "get" or "both")
        {
            if (!Directory.Exists(watch.Path))
            {
                result.Errors.Add($"Download directory does not exist: {watch.Path}");
            }
            else if (!HasWritePermission(watch.Path))
            {
                result.Warnings.Add($"May not have write permission for download directory: {watch.Path}");
            }
        }

        // ファイル拡張子の有効性チェック
        if (watch.AllowedExtensions?.Any() == true)
        {
            var invalidExtensions = watch.AllowedExtensions
                .Where(ext => string.IsNullOrWhiteSpace(ext) || ext.Contains(' '))
                .ToList();

            if (invalidExtensions.Any())
            {
                result.Errors.Add($"Invalid file extensions: {string.Join(", ", invalidExtensions)}");
            }
        }

        // ENDファイル設定の有効性チェック
        if (watch.RequireEndFile)
        {
            if (watch.EndFileExtensions == null || !watch.EndFileExtensions.Any())
            {
                result.Errors.Add("END file extensions must be specified when RequireEndFile is enabled");
            }
            else
            {
                var invalidEndExtensions = watch.EndFileExtensions
                    .Where(ext => string.IsNullOrWhiteSpace(ext) || 
                                  ext.Contains(' ') || 
                                  ext.Contains("..") || 
                                  ext.Contains('/') || 
                                  ext.Contains('\\') ||
                                  ext.Length > 50) // 異常に長い拡張子を防ぐ
                    .Select(ext => ext ?? "<null>") // null値を安全に表示
                    .ToList();

                if (invalidEndExtensions.Any())
                {
                    result.Errors.Add($"Invalid END file extensions: {string.Join(", ", invalidEndExtensions)}");
                }

                // 重複チェック
                var duplicateExtensions = watch.EndFileExtensions
                    .Where(ext => !string.IsNullOrWhiteSpace(ext))
                    .GroupBy(ext => ext.ToLowerInvariant())
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                if (duplicateExtensions.Any())
                {
                    result.Warnings.Add($"Duplicate END file extensions found: {string.Join(", ", duplicateExtensions)}");
                }
            }

        }

        // TransferEndFiles の設定検証（RequireEndFileに関係なく独立してチェック）
        if (watch.TransferEndFiles && !watch.RequireEndFile)
        {
            result.Warnings.Add("TransferEndFiles is enabled but RequireEndFile is disabled. END files will be transferred even without corresponding data files");
        }

        if (watch.TransferEndFiles && (watch.EndFileExtensions == null || watch.EndFileExtensions.Length == 0))
        {
            result.Errors.Add("TransferEndFiles is enabled but EndFileExtensions is empty. Please specify END file extensions");
        }

        // ポート番号の有効性チェック
        if (transfer.Port < 1 || transfer.Port > 65535)
        {
            result.Errors.Add($"Invalid port number: {transfer.Port}. Must be between 1 and 65535.");
        }

        // SFTP なのに FTP 標準ポート(21)を使っている場合に警告
        if (transfer.Mode == "sftp" && transfer.Port == 21)
        {
            result.Warnings.Add("Mode is 'sftp' but Port is 21 (FTP default). SFTP typically uses port 22. Verify this is intentional.");
        }

        // FTP なのに SSH 標準ポート(22)を使っている場合に警告
        if (transfer.Mode == "ftp" && transfer.Port == 22)
        {
            result.Warnings.Add("Mode is 'ftp' but Port is 22 (SSH/SFTP default). FTP typically uses port 21. Verify this is intentional.");
        }
    }

    private void ValidatePerformanceConfiguration(TransferOptions transfer, RetryOptions retry, ConfigurationValidationResult result)
    {
        // 並行処理とリトライ設定の組み合わせチェック
        if (transfer.Concurrency > 8 && retry.MaxAttempts > 5)
        {
            result.Warnings.Add("High concurrency with many retry attempts may cause excessive server load");
        }

        // 双方向転送で並行処理数が多い場合の警告
        if (transfer.Direction == "both" && transfer.Concurrency > 4)
        {
            result.Warnings.Add("High concurrency with bidirectional transfer may cause connection issues");
        }

        // リトライ間隔の設定チェック
        if (retry.DelaySeconds < 1)
        {
            result.Warnings.Add("Very short retry delay may cause server to reject connections");
        }
        else if (retry.DelaySeconds > 300)
        {
            result.Warnings.Add("Very long retry delay may cause excessively long execution times");
        }
    }

    private void ValidateSecurityConfiguration(TransferOptions transfer, HashOptions hash, CleanupOptions cleanup, ConfigurationValidationResult result)
    {
        // 認証情報の安全性チェック
        if (transfer.Mode == "ftp" && !string.IsNullOrEmpty(transfer.Password))
        {
            result.Warnings.Add("FTP transmits passwords in plain text. Consider using SFTP for better security.");
        }

        // 秘密鍵ファイルの存在チェック
        if (transfer.Mode == "sftp" && !string.IsNullOrEmpty(transfer.PrivateKeyPath))
        {
            if (!File.Exists(transfer.PrivateKeyPath))
            {
                result.Errors.Add($"Private key file not found: {transfer.PrivateKeyPath}");
            }
            else if (!HasReadPermission(transfer.PrivateKeyPath))
            {
                result.Errors.Add($"Cannot read private key file: {transfer.PrivateKeyPath}");
            }
        }

        // ハッシュ検証が無効の場合はInfoとして通知
        if (!hash.Enabled)
        {
            result.Infos.Add("Hash verification is disabled. File integrity will not be verified after transfer.");
        }

        // ハッシュアルゴリズムのセキュリティチェック（有効時のみ）
        if (hash.Enabled && hash.Algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add("MD5 hash algorithm is cryptographically insecure and has been disabled. Please use SHA256 or SHA512.");
        }

        // 危険な設定の組み合わせチェック
        if (cleanup.DeleteAfterVerify && cleanup.DeleteRemoteAfterDownload)
        {
            result.Warnings.Add("Both local and remote file deletion are enabled. Ensure you have proper backups.");
        }
    }

    private void ValidateConfigurationCombinations(
        WatchOptions watch,
        TransferOptions transfer,
        HashOptions hash,
        CleanupOptions cleanup,
        ConfigurationValidationResult result)
    {
        // サブフォルダー処理とフォルダー構造維持の組み合わせ
        if (!watch.IncludeSubfolders && transfer.PreserveFolderStructure)
        {
            result.Warnings.Add("PreserveFolderStructure is enabled but IncludeSubfolders is disabled");
        }

        // 双方向転送での潜在的な問題
        if (transfer.Direction == "both")
        {
            if (cleanup.DeleteAfterVerify && cleanup.DeleteRemoteAfterDownload)
            {
                result.Warnings.Add("Bidirectional transfer with file deletion may cause data loss");
            }
        }

        // 大量ファイル処理の警告
        if (watch.IncludeSubfolders && transfer.Concurrency > 8)
        {
            result.Warnings.Add("High concurrency with subfolder scanning may cause high memory usage");
        }

        // ダウンロード方向でのサブディレクトリ設定検証
        if (transfer.Direction is "get" or "both")
        {
            if (watch.IncludeSubfolders && !transfer.PreserveFolderStructure)
            {
                result.Warnings.Add("IncludeSubfolders is enabled for download but PreserveFolderStructure is disabled. Files from subdirectories will be saved to root directory and may overwrite each other.");
            }
            
            if (!watch.IncludeSubfolders && transfer.PreserveFolderStructure)
            {
                result.Warnings.Add("PreserveFolderStructure is enabled for download but IncludeSubfolders is disabled. Only root directory files will be downloaded.");
            }
        }

        // アップロード方向でのサブディレクトリ設定検証
        if (transfer.Direction is "put" or "both")
        {
            if (watch.IncludeSubfolders && !transfer.PreserveFolderStructure)
            {
                result.Warnings.Add("IncludeSubfolders is enabled for upload but PreserveFolderStructure is disabled. All files will be uploaded to remote root directory.");
            }
        }
        
        // UseServerCommand設定の適用範囲チェック（ハッシュ検証有効時のみ）
        if (hash.Enabled && hash.UseServerCommand && transfer.Mode == "sftp")
        {
            result.Warnings.Add("UseServerCommand is enabled but SFTP does not support server-side hash commands. Local hash calculation will be used.");
        }
        
        // タイムアウト設定の妥当性チェック
        if (transfer.TimeoutSeconds < 30)
        {
            result.Warnings.Add("Very short timeout may cause failures with large files or slow networks.");
        }
        else if (transfer.TimeoutSeconds > 1800)
        {
            result.Warnings.Add("Very long timeout may mask network connectivity issues.");
        }
        
        // AllowedExtensions設定が空の場合の警告
        if (watch.AllowedExtensions.Length == 0)
        {
            result.Warnings.Add("No file extensions specified in AllowedExtensions. All files will be processed.");
        }
        
        // ENDファイル機能の使用状況（null安全性を確保）
        if (watch.TransferEndFiles && (watch.EndFileExtensions == null || watch.EndFileExtensions.Length == 0))
        {
            result.Errors.Add("TransferEndFiles is enabled but no END file extensions are configured.");
        }
        
        // ENDファイル機能の設定整合性チェック
        if (watch.TransferEndFiles && !watch.RequireEndFile)
        {
            result.Warnings.Add("TransferEndFiles is enabled but RequireEndFile is disabled. This may result in unexpected behavior.");
        }
        
        // 並列度とタイムアウトの組み合わせ警告
        if (transfer.Concurrency > 8 && transfer.TimeoutSeconds < 120)
        {
            result.Warnings.Add("High concurrency with short timeout may cause connection pool exhaustion.");
        }
    }

    /// <summary>
    /// 設定変更の影響を評価
    /// </summary>
    public ChangeImpactAssessment AssessConfigurationChange(
        TransferOptions oldConfig,
        TransferOptions newConfig)
    {
        var assessment = new ChangeImpactAssessment();

        // 接続設定の変更
        if (oldConfig.Host != newConfig.Host || oldConfig.Port != newConfig.Port)
        {
            assessment.RequiresRestart = true;
            assessment.Impacts.Add("Connection settings changed - restart required");
        }

        // 認証設定の変更
        if (oldConfig.Username != newConfig.Username ||
            oldConfig.Password != newConfig.Password ||
            oldConfig.PrivateKeyPath != newConfig.PrivateKeyPath)
        {
            assessment.RequiresRestart = true;
            assessment.Impacts.Add("Authentication settings changed - restart required");
        }

        // 並行処理数の変更
        if (oldConfig.Concurrency != newConfig.Concurrency)
        {
            assessment.RequiresRestart = true;
            assessment.Impacts.Add($"Concurrency changed from {oldConfig.Concurrency} to {newConfig.Concurrency}");

            if (newConfig.Concurrency > oldConfig.Concurrency * 2)
            {
                assessment.Warnings.Add("Significant increase in concurrency may impact server performance");
            }
        }

        // 転送方向の変更
        if (oldConfig.Direction != newConfig.Direction)
        {
            assessment.RequiresRestart = true;
            assessment.Impacts.Add($"Transfer direction changed from {oldConfig.Direction} to {newConfig.Direction}");
        }

        return assessment;
    }

    private bool HasReadPermission(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
                return true;
            }
            else if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 指定されたパスに書き込み権限があるかチェック
    /// </summary>
    private static bool HasWritePermission(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // 一時ファイルを作成して書き込み権限をテスト
                var tempFile = Path.Combine(path, Path.GetRandomFileName());
                using (File.Create(tempFile))
                {
                    // ファイル作成成功
                }
                File.Delete(tempFile);
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// 設定バリデーションの結果
/// </summary>
public class ConfigurationValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();

    public bool IsValid => !Errors.Any();
    public bool HasWarnings => Warnings.Any();
    public bool HasInfos => Infos.Any();
}

/// <summary>
/// 設定変更の影響評価
/// </summary>
public class ChangeImpactAssessment
{
    public bool RequiresRestart { get; set; }
    public List<string> Impacts { get; } = new();
    public List<string> Warnings { get; } = new();
}