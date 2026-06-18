using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FtpTransferAgent.Services;

/// <summary>
/// 複数宛先 (ファンアウト) の「宛先別配信トラッキング」用の永続状態ストア。
///
/// バッチはステートレスに毎回ファイルを列挙するため、「どのファイルをどの宛先まで送れたか」を
/// プロセス外 (ディスク) に残す必要がある。本ストアは (ファイル × 宛先) 単位の小さな
/// マーカーファイルでそれを表現する。
///
/// 設計上の要点:
/// - マーカーは <b>部分失敗時にだけ</b> 作成される (全宛先成功時は作らない → 通常時の追加コストはゼロ)。
/// - 起動時に状態ディレクトリを 1 回だけ走査してメモリ (<see cref="_markers"/>) に載せ、以降はメモリ照合。
///   ファイルごとのディスク探索は行わない。マーカーは詰まったファイル分しか存在しないため軽量。
/// - マーカーには送信時のファイル<b>指紋</b> (サイズ+mtime もしくはハッシュ) を記録し、
///   同名で内容が差し替わった場合 (上書き) は指紋不一致として「未配信」とみなし全宛先へ再送する。
/// - 対応する元ファイルが消えたマーカー (孤児) は起動時に掃除する。
///
/// スレッド安全性: 列挙 (<see cref="GetDeliveredDestinations"/>) は単一スレッド、
/// 記録/全削除 (<see cref="RecordDelivered"/>/<see cref="RemoveAll"/>) はファンアウト完了コールバックから
/// 複数スレッドで呼ばれ得るが、1 ファイルのマーカー操作は同時に 1 か所からしか起きない
/// (各ファイルのファンアウト完了は 1 回)。異なるファイルは独立キー・独立ファイルなので競合しない。
/// </summary>
public sealed class DeliveryStateStore
{
    public const string SignatureModeSizeTime = "sizetime";
    public const string SignatureModeHash = "hash";

    private const string MarkerExtension = ".marker";

    private readonly string _stateDir;
    private readonly string _watchPath;
    private readonly string _signatureMode;
    private readonly string _hashAlgorithm;
    private readonly ILogger _logger;

    // キー: "<相対パス>\0<宛先名>" → マーカー情報
    private readonly ConcurrentDictionary<string, MarkerEntry> _markers = new(StringComparer.Ordinal);

    public string StateDirectory => _stateDir;

    public DeliveryStateStore(string stateDirectory, string watchPath, string signatureMode, string hashAlgorithm, ILogger logger)
    {
        _stateDir = Path.GetFullPath(stateDirectory);
        _watchPath = Path.GetFullPath(watchPath);
        _signatureMode = string.Equals(signatureMode, SignatureModeHash, StringComparison.OrdinalIgnoreCase)
            ? SignatureModeHash
            : SignatureModeSizeTime;
        _hashAlgorithm = hashAlgorithm;
        _logger = logger;
    }

    /// <summary>
    /// 状態ディレクトリを走査し、マーカーをメモリへ読み込む。
    /// 対応する元ファイルが存在しないマーカー (孤児) と破損マーカーはこの時点で削除する。
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_stateDir);

        string[] files;
        try
        {
            files = Directory.GetFiles(_stateDir, "*" + MarkerExtension, SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        int loaded = 0;
        int orphans = 0;
        foreach (var path in files)
        {
            MarkerData? data = TryReadMarker(path);
            if (data is null || string.IsNullOrEmpty(data.RelativePath) || string.IsNullOrEmpty(data.DestinationName))
            {
                TryDeleteFile(path);
                continue;
            }

            // 孤児マーカー (元ファイルが消えている) は掃除する
            var dataFullPath = Path.GetFullPath(Path.Combine(_watchPath, data.RelativePath));
            if (!File.Exists(dataFullPath))
            {
                TryDeleteFile(path);
                orphans++;
                continue;
            }

            _markers[Key(data.RelativePath, data.DestinationName)] =
                new MarkerEntry(data.RelativePath, data.DestinationName, data.Signature ?? string.Empty, path);
            loaded++;
        }

        _logger.LogDebug("DeliveryStateStore initialized: {Loaded} marker(s) loaded, {Orphans} orphan(s) removed from {Dir}",
            loaded, orphans, _stateDir);
    }

    /// <summary>
    /// 設定された方式でファイルの指紋を算出する。
    /// "sizetime" はサイズ + 最終更新時刻 (UTC ticks)、"hash" はファイルハッシュ。
    /// </summary>
    public async Task<string> ComputeSignatureAsync(string localPath, CancellationToken ct)
    {
        if (_signatureMode == SignatureModeHash)
        {
            var hash = await HashUtil.ComputeHashAsync(localPath, _hashAlgorithm, ct).ConfigureAwait(false);
            return "h:" + hash;
        }

        var info = new FileInfo(localPath);
        return string.Format(
            CultureInfo.InvariantCulture,
            "st:{0}-{1}",
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>
    /// 指定ファイル (相対パス) について、現在の指紋と一致するマーカーを持つ宛先名の集合を返す。
    /// 指紋が一致しない (＝内容が差し替わった) マーカーは陳腐化とみなし、ここで削除する。
    /// </summary>
    public IReadOnlyCollection<string> GetDeliveredDestinations(string relativePath, string currentSignature)
    {
        var delivered = new HashSet<string>(StringComparer.Ordinal);

        // 列挙中の変更に備えてスナップショットで走査する
        foreach (var entry in _markers.Values.ToArray())
        {
            if (!string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(entry.Signature, currentSignature, StringComparison.Ordinal))
            {
                delivered.Add(entry.DestinationName);
            }
            else
            {
                // 内容が変わっている → 古い配信記録は無効。削除して再送対象にする
                if (_markers.TryRemove(Key(entry.RelativePath, entry.DestinationName), out _))
                {
                    TryDeleteFile(entry.MarkerFilePath);
                    _logger.LogInformation(
                        "Delivery marker for {File} -> {Dest} is stale (file content changed); will re-send.",
                        relativePath, entry.DestinationName);
                }
            }
        }

        return delivered;
    }

    /// <summary>
    /// 1 宛先への配信成功を記録する (アトミック書き込み)。
    /// </summary>
    public void RecordDelivered(string relativePath, string destinationName, string signature)
    {
        var markerPath = MarkerFilePath(relativePath, destinationName);
        var data = new MarkerData
        {
            RelativePath = relativePath,
            DestinationName = destinationName,
            Signature = signature,
            DeliveredAtUtc = DateTime.UtcNow
        };

        try
        {
            Directory.CreateDirectory(_stateDir);
            var json = JsonSerializer.Serialize(data);
            var tempPath = markerPath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            // 一時ファイル → 本番名へリネームすることで、書きかけマーカーが読まれないようにする
            File.Move(tempPath, markerPath, overwrite: true);
            _markers[Key(relativePath, destinationName)] =
                new MarkerEntry(relativePath, destinationName, signature, markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // マーカー書き込み失敗は致命的ではない (次回その宛先へ再送されるだけ)。警告に留める。
            _logger.LogWarning("Failed to write delivery marker for {File} -> {Dest}: {Error}",
                relativePath, destinationName, ex.Message);
        }
    }

    /// <summary>
    /// 指定ファイルに紐づく全宛先のマーカーを削除する (全宛先配信完了/掃除時)。
    /// </summary>
    public void RemoveAll(string relativePath)
    {
        foreach (var entry in _markers.Values.ToArray())
        {
            if (!string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (_markers.TryRemove(Key(entry.RelativePath, entry.DestinationName), out _))
            {
                TryDeleteFile(entry.MarkerFilePath);
            }
        }
    }

    /// <summary>
    /// テスト・診断用: 指定ファイルに現存するマーカーの宛先名一覧。
    /// </summary>
    public IReadOnlyCollection<string> GetMarkedDestinations(string relativePath) =>
        _markers.Values
            .Where(e => string.Equals(e.RelativePath, relativePath, StringComparison.Ordinal))
            .Select(e => e.DestinationName)
            .ToArray();

    private static string Key(string relativePath, string destinationName) =>
        relativePath + "\0" + destinationName;

    private string MarkerFilePath(string relativePath, string destinationName)
    {
        // (相対パス, 宛先名) から決定論的かつ衝突しないファイル名を作る。
        // パス区切りや特殊文字を避けるためハッシュ化する。
        var bytes = Encoding.UTF8.GetBytes(relativePath + "\0" + destinationName);
        var hash = SHA256.HashData(bytes);
        var name = Convert.ToHexString(hash).ToLowerInvariant();
        return Path.Combine(_stateDir, name + MarkerExtension);
    }

    private MarkerData? TryReadMarker(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<MarkerData>(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Failed to read delivery marker {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Failed to delete delivery marker {Path}: {Error}", path, ex.Message);
        }
    }

    private sealed record MarkerEntry(string RelativePath, string DestinationName, string Signature, string MarkerFilePath);

    /// <summary>マーカーファイルに JSON で保存する内容。</summary>
    internal sealed class MarkerData
    {
        public string RelativePath { get; set; } = string.Empty;
        public string DestinationName { get; set; } = string.Empty;
        public string? Signature { get; set; }
        public DateTime DeliveredAtUtc { get; set; }
    }

    /// <summary>
    /// 設定値から状態ディレクトリの実パスを解決する。
    /// 明示指定が無ければ LocalApplicationData 配下に watch パス由来のサブフォルダを作る
    /// (異なる watch フォルダのマーカーが相対パスで衝突しないようにする)。
    /// </summary>
    public static string ResolveStateDirectory(string? configured, string watchPath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        var watchFull = Path.GetFullPath(watchPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(watchFull.ToLowerInvariant()));
        var sub = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        return Path.Combine(baseDir, "FtpTransferAgent", "delivery-state", sub);
    }
}
