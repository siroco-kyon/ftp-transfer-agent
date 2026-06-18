using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using FtpTransferAgent.Configuration;
using FtpTransferAgent.Logging;
using FtpTransferAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FtpTransferAgent;

// ファイル転送を実行するバックグラウンドサービス
public class Worker : BackgroundService
{
    private const int QueueCapacity = 1000;
    private readonly ILogger<Worker> _logger;
    private readonly WatchOptions _watch;
    private readonly TransferOptions _transfer;
    private readonly RetryOptions _retry;
    private readonly HashOptions _hash;
    private readonly CleanupOptions _cleanup;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ApplicationExitCode? _exitCode;
    private readonly FanoutCoordinator _fanout = new();

    // 宛先別配信トラッキング有効時のマーカーストア (put 方向のみ)。無効時は null。
    private DeliveryStateStore? _deliveryStore;
    private string? _retryDirectoryFullPath;

    // 転送処理用のチャンネル（容量制限でメモリリーク防止）

    // DI された各種オプションを受け取る
    public Worker(IOptions<WatchOptions> watch, IOptions<TransferOptions> transfer, IOptions<RetryOptions> retry, IOptions<HashOptions> hash, IOptions<CleanupOptions> cleanup, IServiceProvider services, ILogger<Worker> logger, IHostApplicationLifetime lifetime, ApplicationExitCode? exitCode = null)
    {
        _watch = watch.Value;
        _transfer = transfer.Value;
        _retry = retry.Value;
        _hash = hash.Value;
        _cleanup = cleanup.Value;
        _services = services;
        _logger = logger;
        _lifetime = lifetime;
        _exitCode = exitCode;
    }

    private sealed class QueueContext
    {
        public QueueContext(DestinationOptions destination, string name, Channel<TransferItem> channel, TransferQueue queue, ClientPool pool)
        {
            Destination = destination;
            Name = name;
            Channel = channel;
            Queue = queue;
            Pool = pool;
        }

        public DestinationOptions Destination { get; }
        public string Name { get; }
        public Channel<TransferItem> Channel { get; }
        public TransferQueue Queue { get; }

        // この宛先への接続をワーカー間で再利用するためのプール
        public ClientPool Pool { get; }
    }

    // 実クライアント生成の共通実装。virtual メソッドから分離して相互再帰を避ける。
    private IFileTransferClient CreateClientCore(DestinationOptions dest)
    {
        return dest.Mode.ToLowerInvariant() switch
        {
            "sftp" => ActivatorUtilities.CreateInstance<SftpClientWrapper>(_services, dest),
            "ftp" => ActivatorUtilities.CreateInstance<AsyncFtpClientWrapper>(_services, dest),
            _ => throw new ArgumentException($"Unsupported transfer mode: {dest.Mode}")
        };
    }

    // DI を活用してクライアントファクトリパターンを実装（primary 用）
    protected virtual IFileTransferClient CreateClient()
    {
        return CreateClientCore(_transfer);
    }

    // ファンアウト対応: 宛先ごとにクライアントを生成する
    protected virtual IFileTransferClient CreateClientFor(DestinationOptions dest)
    {
        // dest が primary と同一参照なら CreateClient() を使う (テストのモックを尊重するため)
        if (ReferenceEquals(dest, _transfer))
        {
            return CreateClient();
        }
        return CreateClientCore(dest);
    }

    // put 方向で使用する全宛先リスト (primary + 追加宛先)
    private IReadOnlyList<DestinationOptions> GetUploadDestinations()
    {
        var list = new List<DestinationOptions> { _transfer };
        if (_transfer.AdditionalDestinations is { Count: > 0 })
        {
            list.AddRange(_transfer.AdditionalDestinations);
        }
        return list;
    }

    private static string DescribeDestination(DestinationOptions d) =>
        $"{d.Mode}://{d.Host}:{d.Port}{d.RemotePath}";

    // リモートパスを比較用に正規化する (先頭に "/" を付与し、末尾の区切り文字を除去)
    private static string NormalizeRemotePath(string path)
    {
        var norm = path.StartsWith("/") ? path : "/" + path;
        return norm.TrimEnd('/', '\\');
    }

    private static Channel<TransferItem> CreateTransferChannel()
    {
        return Channel.CreateBounded<TransferItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    private QueueContext CreateQueueContext(DestinationOptions destination, string name, int concurrency, EventId finalFailureEventId)
    {
        var queueLogger = _services.GetRequiredService<ILogger<TransferQueue>>();
        var channel = CreateTransferChannel();
        var queue = new TransferQueue(channel, _retry, queueLogger, concurrency, finalFailureEventId);
        return new QueueContext(destination, name, channel, queue, new ClientPool());
    }

    private List<QueueContext> CreateQueueContexts()
    {
        // 複数宛先 (ファンアウト) の put では、個々の宛先失敗をメール抑制対象として識別できるよう
        // 専用 EventId を最終失敗ログに付与する。単一宛先や get では既定 (抑制対象外)。
        var multiDestination = _transfer.Direction is "put" && _transfer.AdditionalDestinations is { Count: > 0 };
        var failureEventId = multiDestination ? LogEvents.MultiDestinationTransferFailure : default;

        var contexts = new List<QueueContext>
        {
            CreateQueueContext(_transfer, $"primary {DescribeDestination(_transfer)}", _transfer.Concurrency, failureEventId)
        };

        if (_transfer.Direction is not "put")
        {
            return contexts;
        }

        if (_transfer.AdditionalDestinations is not { Count: > 0 })
        {
            return contexts;
        }

        for (int i = 0; i < _transfer.AdditionalDestinations.Count; i++)
        {
            var destination = _transfer.AdditionalDestinations[i];
            contexts.Add(CreateQueueContext(destination, $"destination#{i + 1} {DescribeDestination(destination)}", destination.Concurrency, failureEventId));
        }

        return contexts;
    }

    private QueueContext GetPrimaryQueueContext(IReadOnlyList<QueueContext> queueContexts)
    {
        return queueContexts[0];
    }

    private QueueContext GetQueueContextForDestination(IReadOnlyList<QueueContext> queueContexts, DestinationOptions destination)
    {
        foreach (var context in queueContexts)
        {
            if (ReferenceEquals(context.Destination, destination))
            {
                return context;
            }
        }

        throw new InvalidOperationException($"Queue context not found for destination {DescribeDestination(destination)}");
    }

    // 1 ファイルを指定宛先群に対してファンアウトしてキューへ投入する。
    // 全宛先完了時に FanoutCoordinator からコールバックされ、
    // HandleFanoutCompletion が削除/保持/マーカー更新を判断する。
    // トラッキング無効時は destinationsToSend = 全宛先、tracking = null。
    // トラッキング有効時は destinationsToSend = 未配信の宛先のみ。
    private async Task EnqueueFanoutAsync(
        string file,
        string relativePath,
        IReadOnlyList<DestinationOptions> destinationsToSend,
        IReadOnlyList<QueueContext> queueContexts,
        IReadOnlyList<string> relatedEndFiles,
        IReadOnlyList<string> relatedEndFileRelativePaths,
        DeliveryTrackingContext? tracking,
        CancellationToken token)
    {
        var groupId = Guid.NewGuid().ToString("N");
        var endFilesToDeleteOnSuccess = EndFilesToDeleteOnSuccess(relatedEndFiles);

        _fanout.Register(groupId, file, destinationsToSend.Count, (source, results) =>
        {
            HandleFanoutCompletion(source, results, endFilesToDeleteOnSuccess, relatedEndFiles, relatedEndFileRelativePaths, tracking);
        });

        foreach (var dest in destinationsToSend)
        {
            var queueContext = GetQueueContextForDestination(queueContexts, dest);
            // RelatedEndFilePaths に列挙時に確定した END ファイル(実ファイル名)を載せ、
            // アップロード経路で再探索せず同じ集合を使う。これにより「転送する END」と
            // 「成功時に削除する END」が必ず一致し、大小を区別する FS でも不整合が起きない。
            await queueContext.Channel.Writer.WriteAsync(
                new TransferItem(file, TransferAction.Upload, dest, groupId, relatedEndFiles, relativePath, relatedEndFileRelativePaths),
                token).ConfigureAwait(false);
        }
    }

    // 全宛先成功時にローカル削除する END ファイル集合を返す:
    //   - TransferEndFiles=true: 転送済みなので削除
    //   - DeleteLocalSkippedEndFiles=true: 未転送だがクリーンアップ対象として削除
    //   - どちらでもない場合は削除せずローカルに残す
    private IReadOnlyList<string> EndFilesToDeleteOnSuccess(IReadOnlyList<string> relatedEndFiles) =>
        _watch.TransferEndFiles || _cleanup.DeleteLocalSkippedEndFiles
            ? relatedEndFiles
            : Array.Empty<string>();

    // 宛先の安定識別子。トラッキングのマーカーキーに使用。Name 未設定時はラベルで代替する。
    private string GetDestinationName(DestinationOptions dest) =>
        !string.IsNullOrWhiteSpace(dest.Name) ? dest.Name! : DescribeDestination(dest);

    // watch ディレクトリ基準の正規化済み相対パス (区切りは '/')。マーカーのファイルキー。
    private string ToRelativeKey(string file) =>
        Path.GetRelativePath(_watch.Path, file).Replace('\\', '/');

    private string ToRetryRelativeKey(string file) =>
        _retryDirectoryFullPath is null
            ? ToRelativeKey(file)
            : Path.GetRelativePath(_retryDirectoryFullPath, file).Replace('\\', '/');

    private static string NormalizeDirectoryPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string DirectoryPrefix(string path) =>
        NormalizeDirectoryPath(path) + Path.DirectorySeparatorChar;

    private static bool IsUnderDirectory(string path, string root)
    {
        var fullPath = NormalizeDirectoryPath(path);
        var fullRoot = NormalizeDirectoryPath(root);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectoryPrefix(string path, string? directoryPrefix) =>
        directoryPrefix is not null
        && Path.GetFullPath(path).StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);

    // 宛先別配信トラッキングのファイル単位コンテキスト。
    private sealed record DeliveryTrackingContext(
        string RelativePath,
        string Signature,
        IReadOnlyCollection<string> AllDestinationNames,
        IReadOnlyCollection<string> AlreadyDelivered);

    private sealed record UploadCandidate(
        string PhysicalPath,
        string RelativePath,
        IReadOnlyList<string> RelatedEndFilePaths,
        IReadOnlyList<string> RelatedEndFileRelativePaths,
        bool FromRetryDirectory);

    // 既に全宛先へ配信済み (マーカーが揃っている) ファイルの後始末。
    // DeleteAfterVerify=true なら削除しマーカーも掃除。false ならローカルを残し
    // マーカーも保持して次回も送信をスキップさせる。
    private void HandleAlreadyDelivered(string sourcePath, IReadOnlyList<string> relatedEndFiles, string relativeKey)
    {
        _logger.LogInformation("All destinations already delivered for {File} (per delivery state); skipping send.", sourcePath);
        var deleted = DeleteLocalAfterSuccess(sourcePath, EndFilesToDeleteOnSuccess(relatedEndFiles));
        if (deleted)
        {
            _deliveryStore?.RemoveAll(relativeKey);
        }
    }

    // ローカルのデータファイル/END ファイルを成功後ポリシーに従って削除する。
    // 戻り値: データファイルを削除した (もう存在しない) 場合 true。
    private bool DeleteLocalAfterSuccess(string sourcePath, IReadOnlyList<string> localEndFilesToDelete)
    {
        bool dataFileDeleted = false;
        if (_cleanup.DeleteAfterVerify)
        {
            try
            {
                File.Delete(sourcePath);
                dataFileDeleted = true;
                _logger.LogInformation("Deleted local file {File} after all destinations succeeded", sourcePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Failed to delete local file {File}: {Error}", sourcePath, ex.Message);
                dataFileDeleted = !File.Exists(sourcePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied deleting local file {File}: {Error}", sourcePath, ex.Message);
                dataFileDeleted = !File.Exists(sourcePath);
            }
        }

        foreach (var endFile in localEndFilesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(endFile);
                _logger.LogInformation("Deleted local END file {File} after data transfer succeeded", endFile);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Failed to delete local END file {File}: {Error}", endFile, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied deleting local END file {File}: {Error}", endFile, ex.Message);
            }
        }

        return dataFileDeleted;
    }

    // データファイルに対応する END ファイルを、列挙時に構築した END ファイル集合から解決する。
    // 集合は IsEndFile と同じ大小無視で構築され、各要素はディスク上の実ファイル名(大小を保持)を持つ。
    // そのため設定値 (例 ".END") と実ファイル (例 ".end") の大小が異なっても一致し、実名を返せる。
    // ファイルシステムを再探索しないので、列挙結果と常に一致する (転送/削除の不整合を防ぐ)。
    private IReadOnlyList<string> GetExistingEndFilesForDataFile(string dataFilePath, HashSet<string> knownEndFiles)
    {
        if (string.IsNullOrEmpty(dataFilePath) || _watch.EndFileExtensions is null || _watch.EndFileExtensions.Length == 0)
        {
            return Array.Empty<string>();
        }

        var directory = Path.GetDirectoryName(dataFilePath);
        var fileName = Path.GetFileName(dataFilePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return Array.Empty<string>();
        }

        var matches = new List<string>();
        // 解決済みの実ファイルパスで重複排除する。
        // 設定値の大小違い (".END" と ".end") から同一の実ファイルに辿り着いても二重追加しない。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endExt in _watch.EndFileExtensions)
        {
            if (string.IsNullOrWhiteSpace(endExt))
            {
                continue;
            }

            var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
            var candidate = Path.Combine(directory, fileName + normalizedEndExt);

            // 集合から実名 (格納時の大小) を取り出す。大小無視で一致し、実ファイル名を保持する。
            if (knownEndFiles.TryGetValue(candidate, out var actual) && seen.Add(actual))
            {
                matches.Add(actual);
            }
        }

        return matches;
    }

    private IReadOnlyList<UploadCandidate> BuildUploadCandidates(
        IReadOnlyList<string> files,
        Func<string, string> relativeKeyFactory,
        bool fromRetryDirectory)
    {
        var patterns = _watch.AllowedExtensions ?? System.Array.Empty<string>();
        var dataFiles = new List<string>();
        var endFiles = new List<string>();

        foreach (var file in files)
        {
            if (IsEndFile(file))
            {
                endFiles.Add(file);
                continue;
            }

            if (!FileNameMatcher.IsMatch(Path.GetFileName(file), patterns))
            {
                continue;
            }

            if (_watch.RequireEndFile && !HasEndFile(file))
            {
                _logger.LogDebug("Skipping file {File} - no corresponding END file found", file);
                continue;
            }

            dataFiles.Add(file);
        }

        var endFileSet = new HashSet<string>(endFiles, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<UploadCandidate>(dataFiles.Count);
        foreach (var file in dataFiles)
        {
            var relatedEndFiles = GetExistingEndFilesForDataFile(file, endFileSet);
            candidates.Add(new UploadCandidate(
                file,
                relativeKeyFactory(file),
                relatedEndFiles,
                relatedEndFiles.Select(relativeKeyFactory).ToArray(),
                fromRetryDirectory));
        }

        return candidates;
    }

    private void HandleFanoutCompletion(
        string sourcePath,
        IReadOnlyList<FanoutCoordinator.DestinationResult> results,
        IReadOnlyList<string> localEndFilesToDeleteOnSuccess,
        IReadOnlyList<string> relatedEndFiles,
        IReadOnlyList<string> relatedEndFileRelativePaths,
        DeliveryTrackingContext? tracking)
    {
        if (tracking is null || _deliveryStore is null)
        {
            HandleFanoutCompletionAllOrNothing(sourcePath, results, localEndFilesToDeleteOnSuccess);
            return;
        }

        HandleFanoutCompletionTracked(sourcePath, results, localEndFilesToDeleteOnSuccess, relatedEndFiles, relatedEndFileRelativePaths, tracking);
    }

    // 従来の all-or-nothing 動作: 全宛先成功時のみローカル削除、部分失敗はローカル保持。
    private void HandleFanoutCompletionAllOrNothing(
        string sourcePath,
        IReadOnlyList<FanoutCoordinator.DestinationResult> results,
        IReadOnlyList<string> localEndFilesToDeleteOnSuccess)
    {
        var total = results.Count;
        var succeeded = results.Count(r => r.Success);

        if (succeeded != total)
        {
            var failed = results.Where(r => !r.Success).Select(r => r.DestinationLabel);
            _logger.LogError("Partial failure for {File}: {Ok}/{Total} destinations succeeded. Failed: {Failed}. Local file retained for next run. Retry policy is all-or-nothing, so already-successful destinations will also be retried.",
                sourcePath, succeeded, total, string.Join(", ", failed));
            return;
        }

        _logger.LogInformation("All {Total} destinations succeeded for {File}", total, sourcePath);
        DeleteLocalAfterSuccess(sourcePath, localEndFilesToDeleteOnSuccess);
    }

    // 宛先別配信トラッキング: 「今回成功 ∪ 既存マーカー」が全宛先を満たせば完了として
    // ローカル削除＋マーカー掃除。満たさなければ成功宛先のマーカーを記録しローカル保持
    // (次回は未配信の宛先だけ再送される)。
    private void HandleFanoutCompletionTracked(
        string sourcePath,
        IReadOnlyList<FanoutCoordinator.DestinationResult> results,
        IReadOnlyList<string> localEndFilesToDeleteOnSuccess,
        IReadOnlyList<string> relatedEndFiles,
        IReadOnlyList<string> relatedEndFileRelativePaths,
        DeliveryTrackingContext tracking)
    {
        var succeededNames = results.Where(r => r.Success)
            .Select(r => r.DestinationName)
            .ToHashSet(StringComparer.Ordinal);

        var totalDelivered = new HashSet<string>(tracking.AlreadyDelivered, StringComparer.Ordinal);
        totalDelivered.UnionWith(succeededNames);

        var allDone = tracking.AllDestinationNames.All(totalDelivered.Contains);

        if (allDone)
        {
            var deleted = DeleteLocalAfterSuccess(sourcePath, localEndFilesToDeleteOnSuccess);
            if (deleted)
            {
                // ローカルを削除したのでマーカーは不要 → 掃除する
                _deliveryStore!.RemoveAll(tracking.RelativePath);
                _logger.LogInformation("All {Total} destinations delivered for {File}; local file removed and delivery markers cleared.",
                    tracking.AllDestinationNames.Count, sourcePath);
            }
            else
            {
                // ローカルを残す構成 (DeleteAfterVerify=false 等)。
                // 全宛先分のマーカーを揃えておき、次回バッチで送信をスキップさせる。
                foreach (var name in succeededNames)
                {
                    _deliveryStore!.RecordDelivered(tracking.RelativePath, name, tracking.Signature);
                }
                _logger.LogInformation("All {Total} destinations delivered for {File}; local file retained, future runs will skip re-sending.",
                    tracking.AllDestinationNames.Count, sourcePath);
            }
            return;
        }

        // 部分配信: 今回成功した宛先のマーカーを記録 (既存マーカーはそのまま) してローカル保持
        foreach (var name in succeededNames)
        {
            _deliveryStore!.RecordDelivered(tracking.RelativePath, name, tracking.Signature);
        }

        var retryAction = MovePartialFailureToRetryDirectory(sourcePath, tracking.RelativePath, relatedEndFiles, relatedEndFileRelativePaths)
            ? "Moved to retry directory; only pending destination(s) will be retried on the next run."
            : "Local file retained; only pending destination(s) will be retried on the next run.";
        var pendingNames = tracking.AllDestinationNames.Where(n => !totalDelivered.Contains(n));
        _logger.LogError(LogEvents.MultiDestinationPartialFailure,
            "Partial delivery for {File}: {Done}/{Total} destination(s) delivered. Pending: {Pending}. {RetryAction}",
            sourcePath, totalDelivered.Count, tracking.AllDestinationNames.Count, string.Join(", ", pendingNames), retryAction);
    }

    private bool MovePartialFailureToRetryDirectory(
        string sourcePath,
        string relativePath,
        IReadOnlyList<string> relatedEndFiles,
        IReadOnlyList<string> relatedEndFileRelativePaths)
    {
        if (_retryDirectoryFullPath is null)
        {
            return false;
        }

        var requests = new List<(string Source, string RelativePath, string Kind)>();

        for (var i = 0; i < relatedEndFiles.Count; i++)
        {
            var endFile = relatedEndFiles[i];
            if (!File.Exists(endFile))
            {
                continue;
            }

            var endRelativePath = i < relatedEndFileRelativePaths.Count
                ? relatedEndFileRelativePaths[i]
                : Path.GetFileName(endFile);
            requests.Add((endFile, endRelativePath, "END file"));
        }

        // Move the data file last. If an END file cannot be moved, the main retry target is not created.
        requests.Add((sourcePath, relativePath, "file"));

        var plannedMoves = new List<(string Source, string Target, string Kind)>();
        try
        {
            foreach (var request in requests)
            {
                if (!File.Exists(request.Source))
                {
                    _logger.LogWarning("Cannot move partial delivery {Kind} {File} to retry directory because it no longer exists.",
                        request.Kind, request.Source);
                    return false;
                }

                var target = GetRetryFilePath(request.RelativePath);
                if (string.Equals(Path.GetFullPath(request.Source), target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    _exitCode?.MarkFailure();
                    _logger.LogError("Cannot move partial delivery {Kind} {File} to retry directory because target already exists: {Target}",
                        request.Kind, request.Source, target);
                    return false;
                }

                plannedMoves.Add((request.Source, target, request.Kind));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            _exitCode?.MarkFailure();
            _logger.LogError(ex, "Cannot plan retry directory move: {Error}", ex.Message);
            return false;
        }

        var completedMoves = new List<(string Source, string Target, string Kind)>();
        try
        {
            foreach (var move in plannedMoves)
            {
                var targetDirectory = Path.GetDirectoryName(move.Target);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Move(move.Source, move.Target);
                completedMoves.Add(move);
                _logger.LogInformation("Moved partial delivery {Kind} {File} to retry directory: {Target}",
                    move.Kind, move.Source, move.Target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _exitCode?.MarkFailure();
            _logger.LogError(ex, "Failed to move partial delivery file(s) to retry directory: {Error}", ex.Message);
            RollBackRetryMoves(completedMoves);
            return false;
        }
    }

    private void RollBackRetryMoves(IReadOnlyList<(string Source, string Target, string Kind)> completedMoves)
    {
        foreach (var move in completedMoves.Reverse())
        {
            try
            {
                if (!File.Exists(move.Target))
                {
                    continue;
                }

                if (File.Exists(move.Source))
                {
                    _exitCode?.MarkFailure();
                    _logger.LogError("Cannot roll back retry move for {Kind}; source already exists: {Source}. Retry file remains at {Target}",
                        move.Kind, move.Source, move.Target);
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(move.Source);
                if (!string.IsNullOrEmpty(sourceDirectory))
                {
                    Directory.CreateDirectory(sourceDirectory);
                }

                File.Move(move.Target, move.Source);
                _logger.LogWarning("Rolled back retry move for {Kind}: {Target} -> {Source}",
                    move.Kind, move.Target, move.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _exitCode?.MarkFailure();
                _logger.LogError(ex, "Failed to roll back retry move for {Kind}: {Target} -> {Source}",
                    move.Kind, move.Target, move.Source);
            }
        }
    }

    private string GetRetryFilePath(string relativePath)
    {
        if (_retryDirectoryFullPath is null)
        {
            throw new InvalidOperationException("Retry directory is not configured.");
        }

        var target = Path.GetFullPath(Path.Combine(_retryDirectoryFullPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderDirectory(target, _retryDirectoryFullPath))
        {
            throw new InvalidOperationException($"Retry target path is outside the retry directory: {target}");
        }

        return target;
    }

    // Main background processing loop.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // クライアントは各転送タスク毎に作成するため、ここでは共通インスタンスを生成しない

        // 再試行付きの転送キューを開始
        var queueContexts = CreateQueueContexts();
        var primaryQueue = GetPrimaryQueueContext(queueContexts);

        // 宛先別配信トラッキング (put 方向のみ)。完了コールバックより前に初期化しておく。
        var trackingEnabled = _transfer.PerDestinationDeliveryTracking && _transfer.Direction is "put";
        string? stateDirFullPath = null;
        if (trackingEnabled)
        {
            stateDirFullPath = DeliveryStateStore.ResolveStateDirectory(_transfer.StateDirectory, _watch.Path);
            _retryDirectoryFullPath = DeliveryStateStore.ResolveRetryDirectory(_transfer.RetryDirectory, _watch.Path);
            var storeLogger = _services.GetRequiredService<ILogger<DeliveryStateStore>>();
            _deliveryStore = new DeliveryStateStore(stateDirFullPath, _watch.Path, _transfer.DeliverySignatureMode, _hash.Algorithm, storeLogger, _retryDirectoryFullPath);
            _deliveryStore.Initialize();
            _logger.LogInformation("Per-destination delivery tracking enabled (signature mode: {Mode}). State directory: {Dir}. Retry directory: {RetryDir}",
                _transfer.DeliverySignatureMode, _deliveryStore.StateDirectory, _retryDirectoryFullPath ?? "(disabled)");
        }

        // パフォーマンス監視用のCancellationTokenSourceを作成
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitorTasks = queueContexts
            .Select(context => StartPerformanceMonitoringAsync(context.Name, context.Queue, monitorCts.Token))
            .ToArray();

        // 各キュー処理では専用のクライアントを生成して処理する
        var queueTasks = queueContexts
            .Select(context => context.Queue.StartAsync(
            async (item, token) =>
            {
                // 各転送処理の識別子 (リトライ時も同一 ID でログを通しで追跡できるようアイテム由来の ID を使う)
                var id = item.Id;
                var isUpload = item.Action == TransferAction.Upload;
                // Upload は宛先ごと、Download は primary。宛先ごとのプールから接続を借りて再利用する
                var dest = isUpload ? (item.Destination ?? context.Destination) : context.Destination;
                var client = context.Pool.Rent(() => isUpload ? CreateClientFor(dest) : CreateClient());
                // 接続が壊れていなければプールへ返却し、次のアイテム/リトライで再利用する
                var reusable = true;
                try
                {
                    var bytesTransferred = isUpload
                        ? await ProcessUploadAsync(client, item, id, token).ConfigureAwait(false)
                        : await ProcessDownloadAsync(client, item, id, token).ConfigureAwait(false);
                    context.Queue.RecordBytesTransferred(bytesTransferred);
                }
                catch (Exception ex)
                {
                    // 接続そのものが壊れた場合は再利用せず破棄し、次回 Rent で張り直す
                    reusable = !RetryableExceptionClassifier.IsConnectionBroken(ex);
                    throw;
                }
                finally
                {
                    context.Pool.Return(client, reusable);
                }
            },
            onFinalOutcome: (item, ex) =>
            {
                // ファンアウト集約: Upload かつ GroupId 付きのみ報告
                if (item.Action != TransferAction.Upload || string.IsNullOrEmpty(item.GroupId))
                {
                    return;
                }
                var dest = item.Destination ?? context.Destination;
                var label = DescribeDestination(dest);
                _fanout.ReportResult(item.GroupId,
                    new FanoutCoordinator.DestinationResult(label, GetDestinationName(dest), ex == null, ex));
            },
            stoppingToken))
            .ToArray();

        Exception? enumerationException = null;

        // ファイル列挙フェーズ：例外発生時も必ず Channel を完了してワーカーを解放する
        try
        {

            // アップロード処理が有効な場合は指定フォルダ内のファイルを列挙
            if (_transfer.Direction is "put")
            {
                var patterns = _watch.AllowedExtensions ?? System.Array.Empty<string>();
                var option = _watch.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                try
                {
                    // 状態ディレクトリが watch 配下に置かれていてもマーカーを転送しないよう除外する
                    var stateDirPrefix = stateDirFullPath is null
                        ? null
                        : DirectoryPrefix(stateDirFullPath);
                    var retryDirPrefix = _retryDirectoryFullPath is null
                        ? null
                        : DirectoryPrefix(_retryDirectoryFullPath);

                    // ファイル順序を安定化するためファイル名でソート (END ファイルの振り分けは後段で行う)
                    var files = Directory.EnumerateFiles(_watch.Path, "*", option)
                        .Where(f => !IsUnderDirectoryPrefix(f, stateDirPrefix)
                                    && !IsUnderDirectoryPrefix(f, retryDirPrefix))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // データファイルとENDファイルのペアを収集
                    var dataFiles = new List<string>();
                    var endFiles = new List<string>();

                    foreach (var file in files)
                    {
                        // ENDファイルかどうかを先にチェック
                        if (IsEndFile(file))
                        {
                            // ENDファイルは常に別リストに保存（転送するかどうかは後で判断）
                            endFiles.Add(file);
                            continue;
                        }

                        // 通常ファイルに対してファイル名パターン (拡張子 or ワイルドカード) を適用
                        if (!FileNameMatcher.IsMatch(Path.GetFileName(file), patterns))
                        {
                            continue;
                        }

                        // ENDファイル検証が有効な場合は、対応するENDファイルの存在を確認
                        if (_watch.RequireEndFile)
                        {
                            if (!HasEndFile(file))
                            {
                                _logger.LogDebug("Skipping file {File} - no corresponding END file found", file);
                                continue;
                            }
                        }

                        dataFiles.Add(file);
                    }

                    var destinations = GetUploadDestinations();
                    var endFileSet = new HashSet<string>(endFiles, StringComparer.OrdinalIgnoreCase);
                    // キー比較は Ordinal にする (大文字小文字を区別するファイルシステムでは
                    // 大小違いの同名ファイルが併存し得るため、大小無視だと重複キーで列挙全体が失敗する)
                    var relatedEndFilesByDataFile = dataFiles.ToDictionary(
                        file => file,
                        file => GetExistingEndFilesForDataFile(file, endFileSet),
                        StringComparer.Ordinal);
                    var watchCandidates = dataFiles
                        .Select(file =>
                        {
                            var related = relatedEndFilesByDataFile[file];
                            return new UploadCandidate(
                                file,
                                ToRelativeKey(file),
                                related,
                                related.Select(ToRelativeKey).ToArray(),
                                FromRetryDirectory: false);
                        })
                        .ToList();

                    var retryCandidates = new List<UploadCandidate>();
                    if (_deliveryStore is not null
                        && _retryDirectoryFullPath is not null
                        && Directory.Exists(_retryDirectoryFullPath))
                    {
                        var retryFiles = Directory.EnumerateFiles(_retryDirectoryFullPath, "*", SearchOption.AllDirectories)
                            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        retryCandidates.AddRange(BuildUploadCandidates(retryFiles, ToRetryRelativeKey, fromRetryDirectory: true));
                    }

                    var retryRelativePaths = retryCandidates.Select(c => c.RelativePath).ToHashSet(StringComparer.Ordinal);
                    var shadowedWatchCandidates = watchCandidates
                        .Where(c => retryRelativePaths.Contains(c.RelativePath))
                        .ToList();
                    if (shadowedWatchCandidates.Count > 0)
                    {
                        _logger.LogWarning("Retry directory contains {Count} file(s) with the same relative path as files in Watch.Path. Watch files are deferred until retry files finish: {Files}",
                            shadowedWatchCandidates.Count,
                            string.Join(", ", shadowedWatchCandidates.Select(c => c.RelativePath)));
                    }

                    var candidates = watchCandidates
                        .Where(c => !retryRelativePaths.Contains(c.RelativePath))
                        .Concat(retryCandidates)
                        .ToList();
                    var relatedEndFileCount = candidates.Sum(c => c.RelatedEndFilePaths.Count);

                    _logger.LogInformation("Upload fanout: {DataFileCount} data file(s), {EndFileCount} related END file(s), {DestCount} destination(s), {RetryFileCount} from retry directory",
                        candidates.Count,
                        _watch.TransferEndFiles || _cleanup.DeleteLocalSkippedEndFiles ? relatedEndFileCount : 0,
                        destinations.Count,
                        retryCandidates.Count);

                    // トラッキング有効時に使う全宛先名 (Name)。マーカー突き合わせに使用。
                    var allDestinationNames = trackingEnabled
                        ? destinations.Select(GetDestinationName).ToHashSet(StringComparer.Ordinal)
                        : null;

                    // 1. データファイルを宛先へファンアウトしてキューへ投入
                    //    関連 END ファイル(列挙時に確定した実ファイル名)を渡し、転送・削除で同じ集合を使う
                    int queuedItems = 0;
                    int skippedFullyDelivered = 0;
                    foreach (var candidate in candidates)
                    {
                        var file = candidate.PhysicalPath;
                        var relativeKey = candidate.RelativePath;
                        var related = candidate.RelatedEndFilePaths;
                        var relatedRelativePaths = candidate.RelatedEndFileRelativePaths;

                        if (_deliveryStore is null)
                        {
                            // トラッキング無効: 従来どおり全宛先へ投入
                            await EnqueueFanoutAsync(file, relativeKey, destinations, queueContexts, related, relatedRelativePaths, null, stoppingToken).ConfigureAwait(false);
                            queuedItems += destinations.Count;
                            continue;
                        }

                        // トラッキング有効: 既に配信済みの宛先を除いた「未配信先」だけに送る
                        string signature;
                        HashSet<string> delivered;
                        List<DestinationOptions> pending;
                        try
                        {
                            signature = await _deliveryStore.ComputeSignatureAsync(file, stoppingToken).ConfigureAwait(false);
                            delivered = new HashSet<string>(
                                _deliveryStore.GetDeliveredDestinations(relativeKey, signature), StringComparer.Ordinal);
                            pending = destinations.Where(d => !delivered.Contains(GetDestinationName(d))).ToList();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _exitCode?.MarkFailure();
                            _logger.LogError(ex,
                                "Skipping file {File} because delivery tracking signature could not be computed: {Error}",
                                file, ex.Message);
                            continue;
                        }

                        if (pending.Count == 0)
                        {
                            // 全宛先へ配信済み (前回までに完了)。残骸の後始末だけ行う。
                            HandleAlreadyDelivered(file, related, relativeKey);
                            skippedFullyDelivered++;
                            continue;
                        }

                        var tracking = new DeliveryTrackingContext(relativeKey, signature, allDestinationNames!, delivered);
                        await EnqueueFanoutAsync(file, relativeKey, pending, queueContexts, related, relatedRelativePaths, tracking, stoppingToken).ConfigureAwait(false);
                        queuedItems += pending.Count;
                    }

                    if (trackingEnabled)
                    {
                        _logger.LogInformation("Delivery tracking: {Queued} transfer item(s) queued, {Skipped} file(s) already fully delivered (skipped).",
                            queuedItems, skippedFullyDelivered);
                    }
                }
                catch (DirectoryNotFoundException ex)
                {
                    _logger.LogError("Watch directory not found: {Path}. Error: {Error}", _watch.Path, ex.Message);
                    throw;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError("Access denied to watch directory: {Path}. Error: {Error}", _watch.Path, ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error enumerating files in {Path}: {Error}", _watch.Path, ex.Message);
                    throw;
                }
            }

            // ダウンロード方向で追加宛先が設定されていれば警告（未使用になる）
            if (_transfer.Direction is "get" && _transfer.AdditionalDestinations is { Count: > 0 })
            {
                _logger.LogWarning("AdditionalDestinations is set but Direction is '{Direction}'. Additional destinations are used only for uploads and will be ignored for downloads.",
                    _transfer.Direction);
            }

            // ダウンロード処理が有効な場合はリモート一覧を取得
            if (_transfer.Direction is "get")
            {
                try
                {
                    // リモート一覧取得用に専用のクライアントを生成する
                    using var listClient = CreateClient();
                    // リモートファイル一覧を取得
                    var files = await listClient.ListFilesAsync(_transfer.RemotePath, stoppingToken, _watch.IncludeSubfolders).ConfigureAwait(false);

                    // ファイルごとに正規化パス(先頭に "/" を付与)と元のパスを保持する辞書を作成する
                    var normalizedMap = new Dictionary<string, string>();
                    foreach (var f in files)
                    {
                        if (string.IsNullOrEmpty(f)) continue;
                        var norm = f.StartsWith("/") ? f : "/" + f;
                        // 末尾の無駄なスラッシュは除去
                        norm = norm.TrimEnd('/');
                        normalizedMap[norm] = f;
                    }

                    // AllowedExtensions (ファイル名パターン) フィルタ用
                    var patterns = _watch.AllowedExtensions ?? System.Array.Empty<string>();

                    // 正規化パスでソートしてENDファイルとデータファイルを分ける
                    var sortedNormPaths = normalizedMap.Keys
                        .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var dataFiles = new List<string>();    // キューに追加するオリジナルパス
                    var endFiles = new List<string>();     // キューに追加するオリジナルパス

                    foreach (var normPath in sortedNormPaths)
                    {
                        var originalPath = normalizedMap[normPath];

                        // ENDファイルかどうかを正規化パスで判定
                        if (IsEndFileRemote(normPath))
                        {
                            if (_watch.TransferEndFiles)
                            {
                                endFiles.Add(originalPath);
                            }
                            continue;
                        }

                        // ファイル名パターン (拡張子 or ワイルドカード) フィルタをオリジナルパスに対して適用
                        if (!FileNameMatcher.IsMatch(Path.GetFileName(originalPath), patterns))
                        {
                            continue;
                        }

                        // ENDファイル必須の場合は対応ENDファイルの存在を確認
                        if (_watch.RequireEndFile)
                        {
                            if (!HasEndFileRemote(normPath, sortedNormPaths))
                            {
                                _logger.LogDebug("Skipping remote file {File} - no corresponding END file found", originalPath);
                                continue;
                            }
                        }

                        dataFiles.Add(originalPath);
                    }

                    // 1. まずデータファイルを転送キューに追加
                    // キー比較は Ordinal にする (リモートサーバは大文字小文字違いの同名ファイルを
                    // 同時に返し得るため、大小無視の比較だと重複キーで列挙全体が失敗する)
                    var relatedEndFilesByDataFile = dataFiles.ToDictionary(
                        file => file,
                        file => (IReadOnlyList<string>)Array.Empty<string>(),
                        StringComparer.Ordinal);

                    if (_watch.TransferEndFiles && endFiles.Count > 0)
                    {
                        // END ファイル側から「対応データファイルの正規化パス → END ファイル一覧」の
                        // 辞書を構築し、データ × END の総当たり比較を避ける
                        var endFilesByDataNorm = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var endFile in endFiles)
                        {
                            var endNorm = NormalizeRemotePath(endFile);
                            var dataForEnd = GetDataFileForEndFileRemote(endNorm);
                            if (string.IsNullOrEmpty(dataForEnd))
                            {
                                continue;
                            }

                            var dataForEndNorm = NormalizeRemotePath(dataForEnd);
                            if (!endFilesByDataNorm.TryGetValue(dataForEndNorm, out var list))
                            {
                                list = new List<string>();
                                endFilesByDataNorm[dataForEndNorm] = list;
                            }
                            list.Add(endFile);
                        }

                        foreach (var file in dataFiles)
                        {
                            if (endFilesByDataNorm.TryGetValue(NormalizeRemotePath(file), out var related))
                            {
                                relatedEndFilesByDataFile[file] = related;
                            }
                        }
                    }

                    foreach (var file in dataFiles)
                    {
                        await primaryQueue.Channel.Writer.WriteAsync(new TransferItem(
                            file,
                            TransferAction.Download,
                            RelatedEndFilePaths: relatedEndFilesByDataFile[file]), stoppingToken);
                    }
                }
                catch (DirectoryNotFoundException ex)
                {
                    _logger.LogError("Remote directory not found: {RemotePath}. Error: {Error}", _transfer.RemotePath, ex.Message);
                    throw;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError("Access denied to remote directory: {RemotePath}. Error: {Error}", _transfer.RemotePath, ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error listing remote files from {RemotePath}: {Error}", _transfer.RemotePath, ex.Message);
                    throw;
                }
            }

        } // try（ファイル列挙フェーズ）
        catch (Exception ex)
        {
            enumerationException = ex;
        }
        finally
        {
            // 例外発生時もワーカーが WaitToReadAsync でブロックし続けないよう Channel を必ず完了する
            foreach (var context in queueContexts)
            {
                context.Channel.Writer.TryComplete();
            }
        }

        // 書き込みを完了してキュー処理の完了を待機（正常パスでは TryComplete が Complete の役割を担う）

        try
        {
            // まずキュー処理の完了を待機
            await Task.WhenAll(queueTasks).ConfigureAwait(false);

            // キュー処理が完了したら監視タスクをキャンセル
            monitorCts.Cancel();

            // 監視タスクの完了を少し待機（強制終了を避けるため）
            try
            {
                await Task.WhenAll(monitorTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Performance monitoring task did not complete within timeout");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Transfer was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _exitCode?.MarkFailure();
            _logger.LogError(ex, "Error during transfer: {Error}", ex.Message);
            throw;
        }
        finally
        {
            monitorCts.Cancel();

            try
            {
                await Task.WhenAll(monitorTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Performance monitoring task did not complete within timeout");
            }

            // 全ワーカー終了後、プールが保持する再利用接続をすべて切断する
            foreach (var context in queueContexts)
            {
                context.Pool.Dispose();
            }
        }

        // 最終統計情報をログ出力
        if (enumerationException is not null)
        {
            if (enumerationException is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _exitCode?.MarkFailure();
            }

            ExceptionDispatchInfo.Capture(enumerationException).Throw();
        }

        try
        {
            var finalStats = AggregateStatistics(queueContexts.Select(context => context.Queue.GetStatistics()));
            _logger.LogInformation("Transfer completed. Total: {Total}, Success: {Success}, Failed: {Failed}, Critical Errors: {Critical}, Success Rate: {Rate:F1}%, Volume: {Volume}",
                finalStats.TotalEnqueued, finalStats.TotalCompleted, finalStats.TotalFailed, finalStats.CriticalErrorCount, finalStats.SuccessRate, FormatBytes(finalStats.TotalBytesTransferred));

            if (finalStats.TotalFailed > 0 || finalStats.CriticalErrorCount > 0)
            {
                _exitCode?.MarkFailure();
                _logger.LogError("Transfer completed with failures. The process exit code will be set to 1.");
            }

            // クリティカルエラーがあれば詳細をログ出力
            var criticalExceptions = queueContexts.SelectMany(context => context.Queue.GetCriticalExceptions());
            foreach (var ex in criticalExceptions)
            {
                _logger.LogError(ex, "Critical error occurred during transfer: {Message}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            // .NET の既定 (StopHost) では ExecuteAsync の例外は Run() に再スローされず
            // プロセスが終了コード 0 で終わるため、終盤処理の想定外の例外でも失敗を記録する
            _exitCode?.MarkFailure();
            _logger.LogError(ex, "Unexpected error during finalization: {Error}", ex.Message);
            throw;
        }
        finally
        {
            // すべての処理が完了したらアプリケーションを停止
            _lifetime.StopApplication();
        }
    }

    private static TransferStatistics AggregateStatistics(IEnumerable<TransferStatistics> allStats)
    {
        var aggregated = new TransferStatistics();
        foreach (var stats in allStats)
        {
            aggregated.TotalEnqueued += stats.TotalEnqueued;
            aggregated.TotalCompleted += stats.TotalCompleted;
            aggregated.TotalFailed += stats.TotalFailed;
            aggregated.ActiveItems += stats.ActiveItems;
            aggregated.ProcessedItems += stats.ProcessedItems;
            aggregated.MemoryUsageMB = Math.Max(aggregated.MemoryUsageMB, stats.MemoryUsageMB);
            aggregated.ActiveWorkers += stats.ActiveWorkers;
            aggregated.CriticalErrorCount += stats.CriticalErrorCount;
            aggregated.TotalBytesTransferred += stats.TotalBytesTransferred;
        }

        return aggregated;
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F2} MB";
        if (bytes >= KB) return $"{bytes / KB:F2} KB";
        return $"{bytes:N0} B";
    }

    private string BuildRemotePath(DestinationOptions dest, string localPath, string? originalRelativePath = null)
    {
        var name = dest.PreserveFolderStructure
            ? originalRelativePath ?? Path.GetRelativePath(_watch.Path, localPath)
            : Path.GetFileName((originalRelativePath ?? localPath).Replace('/', Path.DirectorySeparatorChar));
        var rawBase = dest.RemotePath ?? string.Empty;
        var remoteBase = rawBase == "/" ? "/" : rawBase.TrimEnd('/', '\\');
        var remoteName = name.Replace('\\', '/');

        if (string.IsNullOrEmpty(remoteBase))
        {
            return remoteName;
        }

        return remoteBase == "/" ? $"/{remoteName}" : $"{remoteBase}/{remoteName}";
    }

    private async Task<long> UploadPathWithHashAsync(
        IFileTransferClient client,
        string localPath,
        string remotePath,
        string destLabel,
        Guid id,
        CancellationToken token)
    {
        var fileSize = new FileInfo(localPath).Length;
        _logger.LogInformation("[{Id}] Starting upload {File} ({Size}) to {Dest} ({Remote})", id, localPath, FormatBytes(fileSize), destLabel, remotePath);

        if (_hash.Enabled)
        {
            var localHash = await HashUtil.ComputeHashAsync(localPath, _hash.Algorithm, token).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Local hash calculated for {File}: {Hash}", id, localPath, localHash);

            await client.UploadAsync(localPath, remotePath, token).ConfigureAwait(false);
            _logger.LogInformation("[{Id}] Upload completed for {File} -> {Dest} ({Size})", id, localPath, destLabel, FormatBytes(fileSize));

            var remoteHash = await client.GetRemoteHashAsync(remotePath, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Remote hash calculated for {Remote}: {Hash}", id, remotePath, remoteHash);

            if (!string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                var error = $"Hash mismatch for {localPath} at {destLabel}: Local={localHash}, Remote={remoteHash}";
                _logger.LogError("[{Id}] {Error}", id, error);
                // 転送中の一過性破損で起こり得るためリトライ可能な専用例外を投げる
                throw new HashMismatchException(error);
            }

            _logger.LogInformation("[{Id}] Hash verification successful for {File} at {Dest}", id, localPath, destLabel);
            return fileSize;
        }

        await client.UploadAsync(localPath, remotePath, token).ConfigureAwait(false);
        _logger.LogInformation("[{Id}] Upload completed for {File} -> {Dest} ({Size}, hash verification disabled)", id, localPath, destLabel, FormatBytes(fileSize));
        return fileSize;
    }

    private async Task TryDeleteRemoteEndFileAsync(
        IFileTransferClient client,
        string remotePath,
        string destLabel,
        Guid id,
        CancellationToken token)
    {
        if (!_cleanup.DeleteRemoteEndFiles)
        {
            return;
        }

        try
        {
            await client.DeleteAsync(remotePath, token).ConfigureAwait(false);
            _logger.LogInformation("[{Id}] Deleted remote END file {Remote} at {Dest}", id, remotePath, destLabel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[{Id}] Failed to delete remote END file {Remote} at {Dest}: {Error}", id, remotePath, destLabel, ex.Message);
        }
    }

    /// <summary>
    /// アップロード処理（確実なハッシュ検証付き）。
    /// ファンアウト対応: item.Destination が示す 1 宛先へ送信し、
    /// FanoutCoordinator に成功/失敗を報告する。ローカル削除は coordinator 側で集約実施。
    /// </summary>
    private async Task<long> ProcessUploadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        var dest = item.Destination ?? _transfer;
        var destLabel = DescribeDestination(dest);
        var remotePath = BuildRemotePath(dest, item.Path, item.OriginalRelativePath);
        var isEndFile = IsEndFile(item.Path);

        var bytesTransferred = await UploadPathWithHashAsync(client, item.Path, remotePath, destLabel, id, token).ConfigureAwait(false);

        // 列挙時に確定した関連 END ファイルを転送する (ファイルシステムを再探索しない)。
        // これにより成功時にローカル削除される END ファイルと完全に一致する。
        if (!isEndFile && _watch.TransferEndFiles && item.RelatedEndFilePaths is { Count: > 0 })
        {
            for (var i = 0; i < item.RelatedEndFilePaths.Count; i++)
            {
                var endFile = item.RelatedEndFilePaths[i];
                var endOriginalRelativePath = item.RelatedEndFileOriginalRelativePaths is { Count: > 0 } relatedOriginals
                    && i < relatedOriginals.Count
                        ? relatedOriginals[i]
                        : null;
                var endRemotePath = BuildRemotePath(dest, endFile, endOriginalRelativePath);
                bytesTransferred += await UploadPathWithHashAsync(client, endFile, endRemotePath, destLabel, id, token).ConfigureAwait(false);
                await TryDeleteRemoteEndFileAsync(client, endRemotePath, destLabel, id, token).ConfigureAwait(false);
            }
        }

        // 転送先のENDファイル削除判定（宛先ごとの操作）
        if (isEndFile)
        {
            await TryDeleteRemoteEndFileAsync(client, remotePath, destLabel, id, token).ConfigureAwait(false);
        }

        return bytesTransferred;

        // ローカルファイル削除はファンアウト集約で実施するためここでは行わない
    }

    /// <summary>
    /// ダウンロード処理（確実なハッシュ検証付き）
    /// </summary>
    private async Task<long> ProcessDownloadAsync(IFileTransferClient client, TransferItem item, Guid id, CancellationToken token)
    {
        string localPath;

        if (_transfer.PreserveFolderStructure && _watch.IncludeSubfolders)
        {
            // サブディレクトリ構造を保持する場合
            // ListFilesAsync で生成した item.Path は先頭に '/' を付加している場合があるが、
            // Transfer.RemotePath は必ず '/' 始まりとは限らないため、RelativePath の計算時にベースパスも正規化する。
            var remoteBase = _transfer.RemotePath ?? string.Empty;
            var normalizedRemoteBase = remoteBase.StartsWith("/") ? remoteBase : "/" + remoteBase;
            // item.Path が先頭に '/' を持たない場合は付与して正規化する
            var normalizedItemPath = item.Path.StartsWith("/") ? item.Path : "/" + item.Path;
            var relativePath = Path.GetRelativePath(normalizedRemoteBase, normalizedItemPath);

            // パストラバーサル攻撃対策: ".." はパスセグメント単位で検査する
            // (単純な文字列包含だと "report..pdf" のような正当なファイル名まで誤検知する)
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Split('/', '\\').Any(segment => segment == ".."))
            {
                throw new ArgumentException($"Unsafe relative path detected: {relativePath}");
            }

            var safePath = Path.Combine(_watch.Path, relativePath);
            var fullPath = Path.GetFullPath(safePath);
            var watchFullPath = Path.GetFullPath(_watch.Path);

            if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, watchFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unsafe file path detected: {relativePath}");
            }

            localPath = safePath;

            // ディレクトリが存在しない場合は作成
            var localDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
            {
                Directory.CreateDirectory(localDir);
                _logger.LogDebug("[{Id}] Created directory: {Directory}", id, localDir);
            }
        }
        else
        {
            // 従来の動作（ファイル名のみ使用）
            var fileName = Path.GetFileName(item.Path);
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException($"Invalid file name: {item.Path}");
            }

            // ファイル名の安全性をチェック（パストラバーサル攻撃対策）
            // 危険な文字とパターンをチェック（プラットフォーム依存文字を考慮）
            // ".." を含むだけの名前 (例: "report..pdf") は正当なため、".." そのものの場合のみ拒否する
            var invalidChars = Path.GetInvalidFileNameChars();
            if (fileName is ".." or "." || fileName.Contains('/') || fileName.Contains('\\') ||
                fileName.Any(c => invalidChars.Contains(c)) || fileName.Any(c => c < 32) ||
                fileName.Length > 255) // ファイル名長制限
            {
                throw new ArgumentException($"Unsafe or invalid characters in file name: {fileName}");
            }

            var safePath = Path.Combine(_watch.Path, fileName);
            var fullPath = Path.GetFullPath(safePath);
            var watchFullPath = Path.GetFullPath(_watch.Path);

            // より厳密なパス検証
            if (!fullPath.StartsWith(watchFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, watchFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Path traversal attempt detected: {fileName}");
            }

            // 最終的な安全性確認
            var relativePath = Path.GetRelativePath(watchFullPath, fullPath);
            if (relativePath.StartsWith("..") || Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException($"Invalid relative path detected: {fileName}");
            }

            localPath = safePath;
        }

        _logger.LogInformation("[{Id}] Starting download {Remote} to {Local}", id, item.Path, localPath);

        long fileSize;
        if (_hash.Enabled)
        {
            // 事前にリモートファイルのハッシュを計算
            var remoteHash = await client.GetRemoteHashAsync(item.Path, _hash.Algorithm, token, _hash.UseServerCommand).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Remote hash calculated: {Hash}", id, remoteHash);

            // ダウンロード実行
            await client.DownloadAsync(item.Path, localPath, token).ConfigureAwait(false);
            fileSize = new FileInfo(localPath).Length;
            _logger.LogInformation("[{Id}] Download completed for {Remote} ({Size})", id, item.Path, FormatBytes(fileSize));

            // ローカルファイルのハッシュを計算して検証
            var localHash = await HashUtil.ComputeHashAsync(localPath, _hash.Algorithm, token).ConfigureAwait(false);
            _logger.LogDebug("[{Id}] Local hash calculated: {Hash}", id, localHash);

            if (!string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                var error = $"Hash mismatch for {item.Path}: Remote={remoteHash}, Local={localHash}";
                _logger.LogError("[{Id}] {Error}", id, error);
                // 転送中の一過性破損で起こり得るためリトライ可能な専用例外を投げる
                throw new HashMismatchException(error);
            }

            _logger.LogInformation("[{Id}] Hash verification successful for {Remote}", id, item.Path);
        }
        else
        {
            // ハッシュ検証なしでダウンロード
            await client.DownloadAsync(item.Path, localPath, token).ConfigureAwait(false);
            fileSize = new FileInfo(localPath).Length;
            _logger.LogInformation("[{Id}] Download completed for {Remote} ({Size}, hash verification disabled)", id, item.Path, FormatBytes(fileSize));
        }

        // ENDファイルまたは通常ファイルの削除判定
        var isEndFileRemote = IsEndFileRemote(item.Path);
        var bytesTransferred = fileSize;

        if (!isEndFileRemote && item.RelatedEndFilePaths is { Count: > 0 })
        {
            foreach (var endFilePath in item.RelatedEndFilePaths)
            {
                // DeleteRemoteEndFiles 有効時は、前回試行で取得・削除済みの END ファイルを
                // リトライで再取得しようとして失敗しないよう存在を確認する (リトライの冪等化)
                if (_cleanup.DeleteRemoteEndFiles && !await client.ExistsAsync(endFilePath, token).ConfigureAwait(false))
                {
                    _logger.LogInformation("[{Id}] Related END file {Remote} no longer exists (already processed in a previous attempt). Skipping.", id, endFilePath);
                    continue;
                }

                bytesTransferred += await ProcessDownloadAsync(
                    client,
                    new TransferItem(endFilePath, TransferAction.Download),
                    id,
                    token).ConfigureAwait(false);
            }
        }
        var shouldDeleteRemote = isEndFileRemote ? _cleanup.DeleteRemoteEndFiles : _cleanup.DeleteRemoteAfterDownload;

        if (shouldDeleteRemote)
        {
            // ダウンロード後のリモートファイル削除は失敗しても転送全体を失敗させないようにtry/catchで処理します。
            try
            {
                await client.DeleteAsync(item.Path, token).ConfigureAwait(false);
                var fileType = isEndFileRemote ? "remote END file" : "remote file";
                _logger.LogInformation("[{Id}] Deleted {FileType} {Remote}", id, fileType, item.Path);
            }
            catch (Exception ex)
            {
                // 削除に失敗した場合は警告ログのみ出力し、以後の処理を継続します。
                _logger.LogWarning("[{Id}] Failed to delete remote file {Remote}: {Error}", id, item.Path, ex.Message);
            }
        }

        return bytesTransferred;
    }

    /// <summary>
    /// 指定されたファイルがENDファイルかどうかを判定
    /// </summary>
    private bool IsEndFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || _watch.EndFileExtensions == null)
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return _watch.EndFileExtensions.Any(endExt =>
            {
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    return false;
                }
                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                return string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error checking if file is END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 指定されたファイルに対応するENDファイルが存在するかどうかを確認
    /// </summary>
    private bool HasEndFile(string filePath)
    {
        // 入力値の検証
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        // ENDファイル拡張子が設定されていない場合
        if (_watch.EndFileExtensions == null || _watch.EndFileExtensions.Length == 0)
        {
            return false;
        }

        try
        {
            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath);

            // ディレクトリまたはファイル名が取得できない場合
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            foreach (var endExt in _watch.EndFileExtensions)
            {
                // null または空の拡張子をスキップ
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var endFileExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                var endFilePath = Path.Combine(directory, fileName + endFileExt);

                if (File.Exists(endFilePath))
                {
                    return true;
                }
            }
        }
        catch (ArgumentException ex)
        {
            // 不正なパス文字が含まれる場合
            _logger.LogWarning("Invalid file path for END file check: {FilePath}. Error: {Error}", filePath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // その他の予期しないエラー
            _logger.LogWarning("Error checking END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }

        return false;
    }

    /// <summary>
    /// 指定されたリモートファイルがENDファイルかどうかを判定
    /// </summary>
    private bool IsEndFileRemote(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || _watch.EndFileExtensions == null)
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return _watch.EndFileExtensions.Any(endExt =>
            {
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    return false;
                }
                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                return string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error checking if remote file is END file for {FilePath}: {Error}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// リモートENDファイルから対応するデータファイル名を取得
    /// </summary>
    private string GetDataFileForEndFileRemote(string endFilePath)
    {
        // ENDファイルから対応するデータファイルの完全パスを取得します。
        // ディレクトリとファイル名を分離し、拡張子を除去した上で再結合します。
        if (string.IsNullOrEmpty(endFilePath) || _watch.EndFileExtensions == null)
        {
            return string.Empty;
        }

        try
        {
            // ディレクトリとファイル名を取得
            var directory = Path.GetDirectoryName(endFilePath) ?? string.Empty;
            var fileName = Path.GetFileName(endFilePath);
            var extension = Path.GetExtension(endFilePath);

            foreach (var endExt in _watch.EndFileExtensions)
            {
                // 空白や空の拡張子はスキップ
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var normalizedEndExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                if (string.Equals(extension, normalizedEndExt, StringComparison.OrdinalIgnoreCase))
                {
                    // ファイル名からEND拡張子を除去
                    var baseName = fileName.Substring(0, fileName.Length - normalizedEndExt.Length);
                    // ディレクトリと結合して完全パスを生成。セパレーターは '/' に統一
                    string fullPath;
                    if (string.IsNullOrEmpty(directory))
                    {
                        // ルートディレクトリにファイルがある場合、必ず先頭に'/'を付与
                        fullPath = $"/{baseName}";
                    }
                    else
                    {
                        var trimmedDir = directory.TrimEnd(Path.DirectorySeparatorChar, '/', '\\');
                        // ディレクトリがルート("/")の場合はそのまま、そうでなければスラッシュを付与
                        if (trimmedDir.Length == 0 || trimmedDir == "/")
                        {
                            fullPath = $"/{baseName}";
                        }
                        else
                        {
                            fullPath = $"/{trimmedDir.TrimStart('/', '\\')}/{baseName}";
                        }
                    }
                    // パス区切り文字を統一し返す
                    return fullPath.Replace('\\', '/');
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error getting data file name for remote END file {EndFile}: {Error}", endFilePath, ex.Message);
        }

        return string.Empty;
    }

    /// <summary>
    /// 指定されたリモートファイルに対応するENDファイルが存在するかどうかを確認
    /// </summary>
    private bool HasEndFileRemote(string filePath, List<string> remoteFiles)
    {
        // 入力値の検証
        if (string.IsNullOrEmpty(filePath) || remoteFiles == null)
        {
            return false;
        }

        // ENDファイル拡張子が設定されていない場合
        if (_watch.EndFileExtensions == null || _watch.EndFileExtensions.Length == 0)
        {
            return false;
        }

        try
        {
            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;

            // ファイル名が取得できない場合
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            foreach (var endExt in _watch.EndFileExtensions)
            {
                // null または空の拡張子をスキップ
                if (string.IsNullOrWhiteSpace(endExt))
                {
                    continue;
                }

                var endFileExt = endExt.StartsWith(".") ? endExt : $".{endExt}";
                var endFileName = fileName + endFileExt;

                // リモートパスはUnix形式（/）を使用（パス正規化）
                var normalizedDirectory = directory?.Replace('\\', '/');
                // ディレクトリが空でない場合、先頭の/を保持
                var endFilePath = string.IsNullOrEmpty(normalizedDirectory) ? endFileName :
                    normalizedDirectory.StartsWith("/") ? $"{normalizedDirectory}/{endFileName}" : $"/{normalizedDirectory}/{endFileName}";

                // リモートファイル一覧から対応するENDファイルを検索
                // リストの表記はサーバ実装により '/' の有無が異なる場合があるため、先頭のセパレータを除去して比較する。
                if (remoteFiles.Any(f => string.Equals(
                        f?.TrimStart('/', '\\'),
                        endFilePath.TrimStart('/', '\\'),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (ArgumentException ex)
        {
            // 不正なパス文字が含まれる場合
            _logger.LogWarning("Invalid remote file path for END file check: {FilePath}. Error: {Error}", filePath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // その他の予期しないエラー
            _logger.LogWarning("Error checking remote END file for {FilePath}: {Error}", filePath, ex.Message);
        }

        return false;
    }

    /// <summary>
    /// パフォーマンス監視タスクを開始
    /// </summary>
    private async Task StartPerformanceMonitoringAsync(string queueName, TransferQueue queue, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

                var stats = queue.GetStatistics();
                if (stats.TotalEnqueued > 0)
                {
                    _logger.LogInformation("Transfer Progress [{Queue}] - Total: {Total}, Completed: {Completed}, Failed: {Failed}, Active: {Active}, Memory: {Memory}MB, Success Rate: {Rate:F1}%",
                        queueName, stats.TotalEnqueued, stats.TotalCompleted, stats.TotalFailed, stats.ActiveItems, stats.MemoryUsageMB, stats.SuccessRate);
                }

                // 長時間実行中のアイテムを警告
                var longRunningItems = queue.GetLongRunningItems(TimeSpan.FromMinutes(5));
                foreach (var (itemKey, duration) in longRunningItems)
                {
                    _logger.LogWarning("Long running transfer detected [{Queue}]: {ItemKey} running for {Duration}", queueName, itemKey, duration);
                }

                // メモリ使用量が高い場合は警告
                if (stats.MemoryUsageMB > 500)
                {
                    _logger.LogWarning("High memory usage detected [{Queue}]: {Memory}MB", queueName, stats.MemoryUsageMB);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常な終了処理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance monitoring task failed for queue {Queue}", queueName);
        }
    }

    public override void Dispose()
    {
        // 後始末
        base.Dispose();
    }
}
