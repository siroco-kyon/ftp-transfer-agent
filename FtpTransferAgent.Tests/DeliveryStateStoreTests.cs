using FtpTransferAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="DeliveryStateStore"/> のマーカー管理 (指紋・配信済み判定・陳腐化/孤児掃除) を検証する。
/// </summary>
public class DeliveryStateStoreTests : IDisposable
{
    private readonly string _watchDir;
    private readonly string _stateDir;
    private readonly string _retryDir;

    public DeliveryStateStoreTests()
    {
        _watchDir = Path.Combine(Path.GetTempPath(), "dss-watch-" + Path.GetRandomFileName());
        _stateDir = Path.Combine(Path.GetTempPath(), "dss-state-" + Path.GetRandomFileName());
        _retryDir = Path.Combine(_watchDir, "retry");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_stateDir);
    }

    private DeliveryStateStore CreateStore(string mode = DeliveryStateStore.SignatureModeSizeTime, string? retryDirectory = null)
    {
        var store = new DeliveryStateStore(_stateDir, _watchDir, mode, "SHA256", NullLogger.Instance, retryDirectory);
        store.Initialize();
        return store;
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_watchDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task RecordDelivered_ThenGetDelivered_ReturnsDestination()
    {
        var store = CreateStore();
        var path = CreateFile("a.txt", "hello");
        var sig = await store.ComputeSignatureAsync(path, CancellationToken.None);

        Assert.True(store.RecordDelivered("a.txt", "primary", sig));

        var delivered = store.GetDeliveredDestinations("a.txt", sig);
        Assert.Contains("primary", delivered);
    }

    [Fact]
    public void RecordDelivered_WhenStatePathIsAFile_ReturnsFalse()
    {
        var invalidStatePath = Path.Combine(_watchDir, "state-as-file");
        File.WriteAllText(invalidStatePath, "not a directory");
        var store = new DeliveryStateStore(invalidStatePath, _watchDir, DeliveryStateStore.SignatureModeSizeTime, "SHA256", NullLogger.Instance);

        var recorded = store.RecordDelivered("a.txt", "primary", "st:1-2");

        Assert.False(recorded);
    }

    [Fact]
    public async Task GetDelivered_WithDifferentSignature_TreatsAsNotDelivered_AndRemovesStaleMarker()
    {
        var store = CreateStore();
        var path = CreateFile("a.txt", "hello");
        var oldSig = await store.ComputeSignatureAsync(path, CancellationToken.None);
        store.RecordDelivered("a.txt", "primary", oldSig);

        // 内容が差し替わって指紋が変わると、配信済み扱いにならず古いマーカーは削除される
        var newSig = oldSig + "-changed";
        var delivered = store.GetDeliveredDestinations("a.txt", newSig);

        Assert.Empty(delivered);
        Assert.Empty(store.GetMarkedDestinations("a.txt"));
    }

    [Fact]
    public async Task ComputeSignature_SizeTime_ChangesWhenContentChanges()
    {
        var store = CreateStore();
        var path = CreateFile("a.txt", "hello");
        var sig1 = await store.ComputeSignatureAsync(path, CancellationToken.None);

        File.WriteAllText(path, "hello world (longer content)");
        var sig2 = await store.ComputeSignatureAsync(path, CancellationToken.None);

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public async Task ComputeSignature_Hash_SameContentSameSignature_DifferentContentDiffers()
    {
        var store = CreateStore(DeliveryStateStore.SignatureModeHash);
        var path = CreateFile("a.txt", "hello");
        var sig1 = await store.ComputeSignatureAsync(path, CancellationToken.None);
        var sig1Again = await store.ComputeSignatureAsync(path, CancellationToken.None);
        Assert.Equal(sig1, sig1Again);

        File.WriteAllText(path, "different");
        var sig2 = await store.ComputeSignatureAsync(path, CancellationToken.None);
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void Initialize_RemovesOrphanMarkers_WhenSourceFileMissing()
    {
        var store = CreateStore();
        // 実ファイルを作らずにマーカーだけ書く (孤児)
        store.RecordDelivered("ghost.txt", "primary", "st:1-2");
        Assert.NotEmpty(store.GetMarkedDestinations("ghost.txt"));

        // 新しいストアで読み直すと孤児は掃除される
        var reloaded = CreateStore();
        Assert.Empty(reloaded.GetMarkedDestinations("ghost.txt"));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public void Initialize_KeepsMarker_WhenSourceFileExistsInRetryDirectory()
    {
        var store = CreateStore(retryDirectory: _retryDir);
        var watchFile = CreateFile("a.txt", "hello");
        store.RecordDelivered("a.txt", "primary", "st:1-2");

        Directory.CreateDirectory(_retryDir);
        File.Move(watchFile, Path.Combine(_retryDir, "a.txt"));

        var reloaded = CreateStore(retryDirectory: _retryDir);
        Assert.Contains("primary", reloaded.GetMarkedDestinations("a.txt"));

        File.Delete(Path.Combine(_retryDir, "a.txt"));
        var reloadedAfterDelete = CreateStore(retryDirectory: _retryDir);
        Assert.Empty(reloadedAfterDelete.GetMarkedDestinations("a.txt"));
        Assert.Empty(Directory.GetFiles(_stateDir, "*.marker"));
    }

    [Fact]
    public async Task RemoveAll_DeletesAllMarkersForFile()
    {
        var store = CreateStore();
        var path = CreateFile("a.txt", "hello");
        var sig = await store.ComputeSignatureAsync(path, CancellationToken.None);
        store.RecordDelivered("a.txt", "primary", sig);
        store.RecordDelivered("a.txt", "backup", sig);

        store.RemoveAll("a.txt");

        Assert.Empty(store.GetDeliveredDestinations("a.txt", sig));
        Assert.Empty(store.GetMarkedDestinations("a.txt"));
    }

    [Fact]
    public async Task Markers_PersistAcrossStoreInstances()
    {
        var path = CreateFile("a.txt", "hello");
        string sig;
        {
            var store = CreateStore();
            sig = await store.ComputeSignatureAsync(path, CancellationToken.None);
            store.RecordDelivered("a.txt", "primary", sig);
        }

        // 別インスタンス (= 次回バッチ相当) でも配信済みが見える
        var reloaded = CreateStore();
        Assert.Contains("primary", reloaded.GetDeliveredDestinations("a.txt", sig));
    }

    [Fact]
    public void ResolveStateDirectory_UsesConfiguredPathWhenProvided()
    {
        var resolved = DeliveryStateStore.ResolveStateDirectory(_stateDir, _watchDir);
        Assert.Equal(Path.GetFullPath(_stateDir), resolved);
    }

    [Fact]
    public void ResolveRetryDirectory_DefaultUsesDirectoryOutsideWatch()
    {
        var resolved = DeliveryStateStore.ResolveRetryDirectory(null, _watchDir);

        Assert.NotNull(resolved);
        Assert.Contains(Path.Combine("FtpTransferAgent", "delivery-retry"), resolved);
        Assert.False(Path.GetFullPath(resolved!).StartsWith(Path.GetFullPath(_watchDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveRetryDirectory_EmptyStringDisablesMove()
    {
        Assert.Null(DeliveryStateStore.ResolveRetryDirectory("", _watchDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_watchDir)) Directory.Delete(_watchDir, true); } catch { /* ignore */ }
        try { if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, true); } catch { /* ignore */ }
    }
}
