using System.ComponentModel.DataAnnotations;
using System.Security;
using FtpTransferAgent.Services;
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
        ValidateBasicConfiguration(watch, transfer, cleanup, result);

        // パフォーマンス関連の設定チェック
        ValidatePerformanceConfiguration(transfer, retry, result);

        // セキュリティ関連の設定チェック
        ValidateSecurityConfiguration(transfer, hash, cleanup, result);

        // 設定の組み合わせチェック
        ValidateConfigurationCombinations(watch, transfer, hash, cleanup, result);

        // 追加宛先のバリデーション
        ValidateAdditionalDestinations(watch, transfer, result);

        // 宛先別配信トラッキングのバリデーション
        ValidateDeliveryTracking(watch, transfer, cleanup, result);

        return result;
    }

    /// <summary>
    /// 宛先別配信トラッキング有効時の整合性をチェックする。
    /// 複数宛先 (ファンアウト) の put では常時有効。マーカーは宛先 Name をキーにするため、
    /// 全宛先で Name が必須かつ一意であること。単一宛先では PerDestinationDeliveryTracking
    /// フラグで明示的に有効化したときのみ適用する。
    /// </summary>
    private void ValidateDeliveryTracking(WatchOptions watch, TransferOptions transfer, CleanupOptions cleanup, ConfigurationValidationResult result)
    {
        var additionalCount = transfer.AdditionalDestinations?.Count ?? 0;
        var multiDestination = transfer.Direction is "put" && additionalCount > 0;
        var trackingActive = transfer.Direction is "put" && (multiDestination || transfer.PerDestinationDeliveryTracking);

        // put 以外でフラグだけ立っている場合は無視されることを警告する。
        if (transfer.PerDestinationDeliveryTracking && transfer.Direction is not "put")
        {
            result.Warnings.Add($"PerDestinationDeliveryTracking is enabled but Direction is '{transfer.Direction}'. Delivery tracking applies only to uploads (put) and will be ignored.");
        }

        // スナップショットはトラッキング経路でのみ有効。トラッキングが効かない構成で
        // EnableUploadSnapshot だけ立っていると無視されるため警告する。
        if (transfer.EnableUploadSnapshot && !trackingActive)
        {
            result.Warnings.Add("EnableUploadSnapshot is enabled but per-destination delivery tracking is not active (it requires a put with multiple destinations, or PerDestinationDeliveryTracking). Upload snapshots apply only to tracked uploads and will be ignored.");
        }

        if (!trackingActive)
        {
            return;
        }

        // 署名モードの妥当性
        var mode = transfer.DeliverySignatureMode;
        if (!string.Equals(mode, "sizetime", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "hash", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add($"DeliverySignatureMode must be 'sizetime' or 'hash' (got '{mode}').");
        }

        ValidateDeliveryStateDirectory(watch, transfer, result);
        ValidateDeliveryRetryDirectory(watch, transfer, result);

        // 全宛先 (primary + 追加) の Name を収集
        var named = new List<(string Label, string? Name)>
        {
            ("primary", transfer.Name)
        };
        var additional = transfer.AdditionalDestinations ?? new List<DestinationOptions>();
        for (int i = 0; i < additional.Count; i++)
        {
            if (additional[i] is null)
            {
                continue;
            }
            named.Add(($"destination#{i + 1}", additional[i].Name));
        }

        // Name 必須チェック (複数宛先の put は配信トラッキングが常時有効のため Name が必須)
        var missing = named.Where(n => string.IsNullOrWhiteSpace(n.Name)).Select(n => n.Label).ToList();
        if (missing.Count > 0)
        {
            result.Errors.Add($"Delivery tracking requires a unique 'Name' for every destination (multiple destinations enable tracking by default). Missing Name on: {string.Join(", ", missing)}.");
        }

        // Name 一意チェック (大文字小文字を無視。紛らわしい重複を防ぐ)
        var duplicates = named
            .Where(n => !string.IsNullOrWhiteSpace(n.Name))
            .GroupBy(n => n.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            result.Errors.Add($"Delivery tracking requires destination Names to be unique. Duplicate Name(s): {string.Join(", ", duplicates)}.");
        }

        // DeleteAfterVerify=false のとき、部分失敗ファイルがリトライディレクトリへ退避される旨を周知する。
        // (全宛先配信完了時には watch.Path へ復元されるが、利用者が場所を把握できるよう警告する)
        if (!cleanup.DeleteAfterVerify)
        {
            var resolvedRetry = DeliveryStateStore.ResolveRetryDirectory(transfer.RetryDirectory, watch.Path);
            if (resolvedRetry is not null)
            {
                result.Warnings.Add($"DeleteAfterVerify is false with delivery tracking: partially-delivered files are moved to the retry directory ({resolvedRetry}) and restored to Watch.Path once every destination succeeds. Set Transfer.RetryDirectory to \"\" to keep partial-failure files in Watch.Path instead.");
            }
        }
    }

    private static void ValidateDeliveryStateDirectory(WatchOptions watch, TransferOptions transfer, ConfigurationValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(transfer.StateDirectory))
        {
            return;
        }

        try
        {
            var watchPath = NormalizeDirectoryPath(Path.GetFullPath(watch.Path));
            var statePath = NormalizeDirectoryPath(DeliveryStateStore.ResolveStateDirectory(transfer.StateDirectory, watch.Path));

            if (string.Equals(statePath, watchPath, StringComparison.OrdinalIgnoreCase)
                || IsAncestorDirectory(statePath, watchPath))
            {
                result.Errors.Add("Transfer.StateDirectory must not be the same as Watch.Path or a parent directory of Watch.Path. Put delivery state in a child folder of Watch.Path or, preferably, outside Watch.Path.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            result.Errors.Add($"Transfer.StateDirectory is invalid: {ex.Message}");
        }
    }

    private static void ValidateDeliveryRetryDirectory(WatchOptions watch, TransferOptions transfer, ConfigurationValidationResult result)
    {
        try
        {
            var resolvedRetryDirectory = DeliveryStateStore.ResolveRetryDirectory(transfer.RetryDirectory, watch.Path);
            if (resolvedRetryDirectory is null)
            {
                return;
            }

            var watchPath = NormalizeDirectoryPath(Path.GetFullPath(watch.Path));
            var retryPath = NormalizeDirectoryPath(resolvedRetryDirectory);

            if (string.Equals(retryPath, watchPath, StringComparison.OrdinalIgnoreCase)
                || IsAncestorDirectory(retryPath, watchPath))
            {
                result.Errors.Add("Transfer.RetryDirectory must not be the same as Watch.Path or a parent directory of Watch.Path.");
            }

            var statePath = NormalizeDirectoryPath(DeliveryStateStore.ResolveStateDirectory(transfer.StateDirectory, watch.Path));
            if (string.Equals(retryPath, statePath, StringComparison.OrdinalIgnoreCase)
                || IsAncestorDirectory(retryPath, statePath)
                || IsAncestorDirectory(statePath, retryPath))
            {
                result.Errors.Add("Transfer.RetryDirectory must not be the same as or overlap with Transfer.StateDirectory.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            result.Errors.Add($"Transfer.RetryDirectory is invalid: {ex.Message}");
        }
    }

    private static string NormalizeDirectoryPath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsAncestorDirectory(string ancestor, string child) =>
        child.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(ancestor + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void ValidateAdditionalDestinations(WatchOptions watch, TransferOptions transfer, ConfigurationValidationResult result)
    {
        if (transfer.AdditionalDestinations is null || transfer.AdditionalDestinations.Count == 0)
        {
            return;
        }

        if (transfer.Direction is "get")
        {
            result.Warnings.Add($"AdditionalDestinations is set ({transfer.AdditionalDestinations.Count} entries) but Direction is '{transfer.Direction}'. Additional destinations are used only for uploads.");
        }

        for (int i = 0; i < transfer.AdditionalDestinations.Count; i++)
        {
            var d = transfer.AdditionalDestinations[i];
            var label = $"[Destination#{i + 1}]";

            if (d is null)
            {
                result.Errors.Add($"{label} is null");
                continue;
            }
            var isLocal = d.Mode == "local";
            label = isLocal ? $"[Destination#{i + 1} local={d.RemotePath}]" : $"[Destination#{i + 1} host={d.Host}]";
            // Host/Username は ftp/sftp のみ必須。local は OS の実行ユーザー権限でアクセスするため不要。
            if (!isLocal && string.IsNullOrWhiteSpace(d.Host))
            {
                result.Errors.Add($"{label} Host is required");
            }
            if (!isLocal && string.IsNullOrWhiteSpace(d.Username))
            {
                result.Errors.Add($"{label} Username is required");
            }
            if (string.IsNullOrWhiteSpace(d.RemotePath))
            {
                result.Errors.Add($"{label} RemotePath is required");
            }
            else if (isLocal && !Path.IsPathFullyQualified(d.RemotePath))
            {
                result.Warnings.Add($"{label} local RemotePath is not an absolute/UNC path: {d.RemotePath}");
            }
            if (isLocal)
            {
                ValidateLocalDestinationNotInsideWatch(watch, d.Mode, d.RemotePath, $"{label} ", result);
            }
            if (!(d.Mode == "ftp" || d.Mode == "sftp" || d.Mode == "local"))
            {
                result.Errors.Add($"{label} Mode must be 'ftp', 'sftp' or 'local' (got '{d.Mode}')");
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
            // DataAnnotations はネストされたオブジェクトには適用されないため、
            // ルートの TransferOptions と同じ範囲チェックをここで行う
            if (d.Concurrency < 1 || d.Concurrency > 16)
            {
                result.Errors.Add($"{label} Concurrency must be between 1 and 16 (got {d.Concurrency})");
            }
            if (d.TimeoutSeconds < 1 || d.TimeoutSeconds > 3600)
            {
                result.Errors.Add($"{label} TimeoutSeconds must be between 1 and 3600 (got {d.TimeoutSeconds})");
            }
            if (d.KeepAliveSeconds < 0 || d.KeepAliveSeconds > 3600)
            {
                result.Errors.Add($"{label} KeepAliveSeconds must be between 0 and 3600 (got {d.KeepAliveSeconds})");
            }
        }

        ValidateDuplicateDestinationTargets(transfer, result);
    }

    private static void ValidateDuplicateDestinationTargets(TransferOptions transfer, ConfigurationValidationResult result)
    {
        if (transfer.Direction is not "put")
        {
            return;
        }

        var destinations = new List<(string Label, DestinationOptions Destination)>
        {
            ("primary", transfer)
        };

        if (transfer.AdditionalDestinations is not null)
        {
            for (var i = 0; i < transfer.AdditionalDestinations.Count; i++)
            {
                var destination = transfer.AdditionalDestinations[i];
                if (destination is not null)
                {
                    destinations.Add(($"destination#{i + 1}", destination));
                }
            }
        }

        var duplicateTargets = destinations
            .GroupBy(d => DestinationTargetKey(d.Destination), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(", ", g.Select(d => d.Label)))
            .ToList();

        foreach (var labels in duplicateTargets)
        {
            result.Warnings.Add($"Multiple destinations appear to target the same remote location ({labels}). This can cause concurrent overwrites or duplicate END-file delivery.");
        }
    }

    private static string DestinationTargetKey(DestinationOptions destination)
    {
        var mode = destination.Mode?.Trim().ToLowerInvariant() ?? string.Empty;
        var host = destination.Host?.Trim().ToLowerInvariant() ?? string.Empty;
        var remotePath = (destination.RemotePath ?? string.Empty)
            .Replace('\\', '/')
            .TrimEnd('/');
        return $"{mode}|{host}|{destination.Port}|{remotePath}";
    }

    // 転送方式 (Mode) ごとの必須項目・制約を検証する (primary 宛先)。
    // DestinationOptions は local モードのため Host/Username を [Required] にしていないので、
    // ftp/sftp の必須項目はここで担保する。
    private static void ValidatePrimaryModeRequirements(TransferOptions transfer, ConfigurationValidationResult result)
    {
        var mode = transfer.Mode?.Trim().ToLowerInvariant();
        if (mode is "ftp" or "sftp")
        {
            if (string.IsNullOrWhiteSpace(transfer.Host))
            {
                result.Errors.Add("Host is required for ftp/sftp mode.");
            }
            if (string.IsNullOrWhiteSpace(transfer.Username))
            {
                result.Errors.Add("Username is required for ftp/sftp mode.");
            }
        }
        else if (mode is "local")
        {
            // local は put 専用。get 経路はリモートパスを '/' 前提で解決するため未対応。
            if (transfer.Direction is "get")
            {
                result.Errors.Add("Mode 'local' is currently supported only for Direction 'put'.");
            }
            if (string.IsNullOrWhiteSpace(transfer.RemotePath))
            {
                result.Errors.Add("RemotePath (destination directory) is required for local mode.");
            }
            else if (!Path.IsPathFullyQualified(transfer.RemotePath))
            {
                result.Warnings.Add($"local RemotePath '{transfer.RemotePath}' is not an absolute/UNC path. Use a full path like 'D:\\out' or '\\\\server\\share\\in'.");
            }
            result.Infos.Add("Mode 'local' writes through the OS file system (including SMB/UNC shares). No CIFS/SMB1 is implemented; ensure SMB1 is disabled and enable SMB encryption on the server if on-the-wire confidentiality is required.");
        }
    }

    /// <summary>
    /// local 宛先の RemotePath が監視フォルダ (Watch.Path) と同一、またはその配下にないことを検証する。
    /// 同一だと転送先がアップロード元と一致し、Cleanup.DeleteAfterVerify によって唯一のファイルが
    /// 削除されてデータ消失する。配下だと書き出したファイルが (特に IncludeSubfolders 有効時に)
    /// 後続実行で再取り込みされてしまう。いずれも起動時エラーとして弾く。
    /// </summary>
    private static void ValidateLocalDestinationNotInsideWatch(
        WatchOptions watch,
        string? mode,
        string? remotePath,
        string labelPrefix,
        ConfigurationValidationResult result)
    {
        if (!string.Equals(mode?.Trim(), "local", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            // RemotePath 必須チェックは別途行うため、ここでは何もしない。
            return;
        }

        try
        {
            var watchPath = NormalizeDirectoryPath(Path.GetFullPath(watch.Path));
            var destPath = NormalizeDirectoryPath(Path.GetFullPath(remotePath));

            if (string.Equals(destPath, watchPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"{labelPrefix}local RemotePath '{remotePath}' must not be the same as Watch.Path ('{watch.Path}'). The uploaded file would target its own source, and Cleanup.DeleteAfterVerify would then delete the only copy. Choose a destination outside Watch.Path.");
            }
            else if (IsAncestorDirectory(watchPath, destPath))
            {
                result.Errors.Add($"{labelPrefix}local RemotePath '{remotePath}' must not be inside Watch.Path ('{watch.Path}'). Files written under the watch folder can be picked up again by later runs (especially with IncludeSubfolders). Choose a destination outside Watch.Path.");
            }
            else if (IsAncestorDirectory(destPath, watchPath))
            {
                result.Errors.Add($"{labelPrefix}local RemotePath '{remotePath}' must not be a parent of Watch.Path ('{watch.Path}'). With folder-structure preservation and IncludeSubfolders, transferred files can land back inside the watch folder and be picked up by later runs. Choose a destination outside the Watch.Path tree.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            result.Errors.Add($"{labelPrefix}local RemotePath is invalid: {ex.Message}");
        }
    }

    private void ValidateBasicConfiguration(WatchOptions watch, TransferOptions transfer, CleanupOptions cleanup, ConfigurationValidationResult result)
    {
        if (transfer.Direction is not "put" and not "get")
        {
            result.Errors.Add($"Direction must be 'put' or 'get' (got '{transfer.Direction}')");
        }

        // 転送方式ごとの必須項目・制約チェック (primary 宛先)
        ValidatePrimaryModeRequirements(transfer, result);

        // local primary 宛先が監視フォルダと重ならないこと (自己上書き・再取り込み防止)
        ValidateLocalDestinationNotInsideWatch(watch, transfer.Mode, transfer.RemotePath, string.Empty, result);

        // ローカルパスの存在チェック
        if (transfer.Direction is "put")
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
        if (transfer.Direction is "get")
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
        if (watch.AllowedExtensions == null)
        {
            result.Errors.Add("AllowedExtensions must not be null. Use an empty array to process all files.");
        }
        else if (watch.AllowedExtensions.Any())
        {
            var invalidExtensions = watch.AllowedExtensions
                .Where(string.IsNullOrWhiteSpace)
                .ToList();

            if (invalidExtensions.Any())
            {
                result.Errors.Add($"Invalid file extensions: {string.Join(", ", invalidExtensions)}");
            }
        }

        var usesEndFileExtensions = watch.RequireEndFile ||
                                    watch.TransferEndFiles ||
                                    cleanup.DeleteLocalSkippedEndFiles ||
                                    cleanup.DeleteRemoteEndFiles;

        // ENDファイル設定の有効性チェック
        if (watch.RequireEndFile)
        {
            if (watch.EndFileExtensions == null || !watch.EndFileExtensions.Any())
            {
                result.Errors.Add("END file extensions must be specified when RequireEndFile is enabled");
            }

        }

        var endFileExtensions = watch.EndFileExtensions;
        if (usesEndFileExtensions && endFileExtensions is { Length: > 0 })
        {
            var invalidEndExtensions = endFileExtensions
                .Where(ext => string.IsNullOrWhiteSpace(ext) ||
                              ext.Contains(' ') ||
                              ext.Contains("..") ||
                              ext.Contains('/') ||
                              ext.Contains('\\') ||
                              ext.Length > 50)
                .Select(ext => ext ?? "<null>")
                .ToList();

            if (invalidEndExtensions.Any())
            {
                result.Errors.Add($"Invalid END file extensions: {string.Join(", ", invalidEndExtensions)}");
            }

            // 重複チェック (完全一致のみ)。大文字小文字違い (".END" と ".end" 等) は
            // 大文字小文字を区別するファイルシステムでは別ファイルを指すため重複とみなさない
            var duplicateExtensions = endFileExtensions
                .Where(ext => !string.IsNullOrWhiteSpace(ext))
                .GroupBy(ext => ext, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateExtensions.Any())
            {
                result.Warnings.Add($"Duplicate END file extensions found: {string.Join(", ", duplicateExtensions)}");
            }
        }

        // TransferEndFiles の設定検証（RequireEndFileに関係なく独立してチェック）
        if (watch.TransferEndFiles && !watch.RequireEndFile)
        {
            result.Warnings.Add("TransferEndFiles is enabled but RequireEndFile is disabled. END files will only be transferred when their corresponding data files are also selected for transfer.");
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
        if (string.IsNullOrWhiteSpace(hash.Algorithm))
        {
            result.Errors.Add("Hash algorithm is required. Please use SHA256 or SHA512.");
        }
        else if (hash.Enabled && hash.Algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
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
        // 大量ファイル処理の警告
        if (watch.IncludeSubfolders && transfer.Concurrency > 8)
        {
            result.Warnings.Add("High concurrency with subfolder scanning may cause high memory usage");
        }

        // ダウンロード方向でのサブディレクトリ設定検証
        if (transfer.Direction is "get")
        {
            if (watch.IncludeSubfolders && !transfer.PreserveFolderStructure)
            {
                result.Errors.Add("IncludeSubfolders cannot be enabled for download when PreserveFolderStructure is false. Remote files from different subdirectories may overwrite the same local file.");
            }

            if (watch.IncludeSubfolders &&
                !transfer.PreserveFolderStructure &&
                cleanup.DeleteRemoteAfterDownload)
            {
                result.Errors.Add("DeleteRemoteAfterDownload cannot be enabled when IncludeSubfolders is true and PreserveFolderStructure is false. Remote files from different subdirectories may overwrite the same local file before being deleted.");
            }

            if (watch.IncludeSubfolders &&
                !transfer.PreserveFolderStructure &&
                cleanup.DeleteRemoteEndFiles &&
                watch.TransferEndFiles)
            {
                result.Errors.Add("DeleteRemoteEndFiles cannot be enabled with TransferEndFiles when IncludeSubfolders is true and PreserveFolderStructure is false. Remote END files may overwrite the same local file before being deleted.");
            }

            if (!watch.IncludeSubfolders && transfer.PreserveFolderStructure)
            {
                result.Warnings.Add("PreserveFolderStructure is enabled for download but IncludeSubfolders is disabled. Only root directory files will be downloaded.");
            }
        }

        // アップロード方向でのサブディレクトリ設定検証
        if (transfer.Direction is "put")
        {
            if (watch.IncludeSubfolders)
            {
                var unsafeDestinations = new List<string>();
                if (!transfer.PreserveFolderStructure)
                {
                    unsafeDestinations.Add("primary");
                }

                var additionalDestinations = transfer.AdditionalDestinations ?? new List<DestinationOptions>();
                for (int i = 0; i < additionalDestinations.Count; i++)
                {
                    var destination = additionalDestinations[i];
                    if (destination is null)
                    {
                        continue;
                    }

                    if (!destination.PreserveFolderStructure)
                    {
                        unsafeDestinations.Add($"destination#{i + 1}");
                    }
                }

                if (unsafeDestinations.Count > 0)
                {
                    result.Errors.Add($"IncludeSubfolders cannot be enabled for upload when any destination has PreserveFolderStructure=false ({string.Join(", ", unsafeDestinations)}). Local files with the same name in different subdirectories may overwrite each other remotely.");
                }
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
        if (watch.AllowedExtensions?.Length == 0)
        {
            result.Warnings.Add("No file extensions specified in AllowedExtensions. All files will be processed.");
        }

        // ENDファイル機能の使用状況（null安全性を確保）
        if (watch.TransferEndFiles && (watch.EndFileExtensions == null || watch.EndFileExtensions.Length == 0))
        {
            result.Errors.Add("TransferEndFiles is enabled but no END file extensions are configured.");
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
