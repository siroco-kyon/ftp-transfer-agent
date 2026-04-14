using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FtpTransferAgent.Services;

/// <summary>
/// 1 つのソースファイルを複数宛先へ送信する際、
/// すべての宛先の結果が出揃った時点で集約アクション (例: ローカル削除) を実行する調整役。
/// </summary>
public sealed class FanoutCoordinator
{
    private readonly ConcurrentDictionary<string, FanoutState> _groups = new();

    public sealed record DestinationResult(string DestinationLabel, bool Success, Exception? Error);

    private sealed class FanoutState
    {
        public string SourcePath { get; init; } = string.Empty;
        public int Remaining;
        public ConcurrentBag<DestinationResult> Results { get; } = new();
        public Action<string, IReadOnlyList<DestinationResult>>? OnComplete;
        public int Completed;
    }

    /// <summary>
    /// 新しいファンアウトグループを登録する。
    /// </summary>
    /// <param name="groupId">一意な ID</param>
    /// <param name="sourcePath">ソースファイルパス</param>
    /// <param name="destinationCount">宛先総数</param>
    /// <param name="onComplete">全宛先完了後に 1 度だけ呼ばれるコールバック</param>
    public void Register(
        string groupId,
        string sourcePath,
        int destinationCount,
        Action<string, IReadOnlyList<DestinationResult>> onComplete)
    {
        if (destinationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationCount));
        }
        var state = new FanoutState
        {
            SourcePath = sourcePath,
            Remaining = destinationCount,
            OnComplete = onComplete
        };
        if (!_groups.TryAdd(groupId, state))
        {
            throw new InvalidOperationException($"GroupId already registered: {groupId}");
        }
    }

    /// <summary>
    /// 1 宛先の結果を報告する。
    /// 残り件数が 0 になった時点でコールバックを実行する。
    /// </summary>
    public void ReportResult(string groupId, DestinationResult result)
    {
        if (!_groups.TryGetValue(groupId, out var state))
        {
            return;
        }

        state.Results.Add(result);
        var remaining = Interlocked.Decrement(ref state.Remaining);
        if (remaining == 0 && Interlocked.Exchange(ref state.Completed, 1) == 0)
        {
            _groups.TryRemove(groupId, out _);
            var snapshot = state.Results.ToList();
            state.OnComplete?.Invoke(state.SourcePath, snapshot);
        }
    }
}
