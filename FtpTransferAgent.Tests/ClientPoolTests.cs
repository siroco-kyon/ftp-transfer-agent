using FtpTransferAgent.Services;
using Moq;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="ClientPool"/> の接続再利用・破棄・後始末を検証する
/// </summary>
public class ClientPoolTests
{
    [Fact]
    public void Rent_WhenEmpty_InvokesFactory()
    {
        using var pool = new ClientPool();
        var client = Mock.Of<IFileTransferClient>();
        var factoryCalls = 0;

        var rented = pool.Rent(() =>
        {
            factoryCalls++;
            return client;
        });

        Assert.Same(client, rented);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void Rent_AfterReusableReturn_ReusesInstanceWithoutFactory()
    {
        using var pool = new ClientPool();
        var client = Mock.Of<IFileTransferClient>();
        var factoryCalls = 0;
        Func<IFileTransferClient> factory = () =>
        {
            factoryCalls++;
            return client;
        };

        var first = pool.Rent(factory);
        pool.Return(first, reusable: true);
        var second = pool.Rent(factory);

        Assert.Same(client, second);
        Assert.Equal(1, factoryCalls); // 2 回目はプールから再利用するため factory は呼ばれない
    }

    [Fact]
    public void Return_WhenNotReusable_DisposesClient()
    {
        using var pool = new ClientPool();
        var mock = new Mock<IFileTransferClient>();

        var client = pool.Rent(() => mock.Object);
        pool.Return(client, reusable: false);

        mock.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public void Rent_AfterNotReusableReturn_CreatesNewInstance()
    {
        using var pool = new ClientPool();
        var factoryCalls = 0;
        Func<IFileTransferClient> factory = () =>
        {
            factoryCalls++;
            return Mock.Of<IFileTransferClient>();
        };

        var first = pool.Rent(factory);
        pool.Return(first, reusable: false); // 壊れた接続として破棄
        var second = pool.Rent(factory);     // 空なので新規生成が必要

        Assert.NotSame(first, second);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void Dispose_DisposesAllPooledClients()
    {
        var pool = new ClientPool();
        var mock1 = new Mock<IFileTransferClient>();
        var mock2 = new Mock<IFileTransferClient>();

        var c1 = pool.Rent(() => mock1.Object);
        var c2 = pool.Rent(() => mock2.Object);
        pool.Return(c1, reusable: true);
        pool.Return(c2, reusable: true);

        pool.Dispose();

        mock1.Verify(c => c.Dispose(), Times.Once);
        mock2.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public void Return_AfterDispose_DisposesClientInsteadOfPooling()
    {
        var pool = new ClientPool();
        var mock = new Mock<IFileTransferClient>();
        var client = pool.Rent(() => mock.Object);

        pool.Dispose();
        pool.Return(client, reusable: true); // 破棄済みプールには戻さず Dispose する

        mock.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Rent_UnderHighConcurrency_NeverSharesInstanceConcurrently()
    {
        // SftpClient / AsyncFtpClient はスレッドセーフでないため、1 接続が同時に
        // 2 ワーカーへ渡ると転送が壊れる。プールが構造的にそれを防ぐことを高並列で実証する。
        using var pool = new ClientPool();
        var violations = 0;

        IFileTransferClient Factory() => new ExclusiveUseClient(() => Interlocked.Increment(ref violations));

        const int workers = 16;
        const int iterationsPerWorker = 1000;

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < iterationsPerWorker; i++)
            {
                var client = pool.Rent(Factory);
                var resource = (ExclusiveUseClient)client;
                resource.BeginUse();   // 使用開始（同時に他から使われていれば違反を記録）
                await Task.Yield();    // 他タスクへ実行機会を与え、競合を誘発する
                resource.EndUse();     // 使用終了
                pool.Return(client, reusable: true);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // 借りた接続は Return まで他から取得されないため、同時使用は 0 になる
        Assert.Equal(0, violations);
    }

    /// <summary>使用中（Begin～End の間）に別スレッドからも使われたら違反として記録するクライアント</summary>
    private sealed class ExclusiveUseClient : IFileTransferClient
    {
        private readonly Action _onViolation;
        private int _active;

        public ExclusiveUseClient(Action onViolation) => _onViolation = onViolation;

        public void BeginUse()
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                _onViolation();
            }
        }

        public void EndUse() => Interlocked.Decrement(ref _active);

        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct) => Task.CompletedTask;

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetRemoteHashAsync(string remotePath, string algorithm, CancellationToken ct, bool useServerCommand = false)
            => Task.FromResult(string.Empty);

        public Task<IEnumerable<string>> ListFilesAsync(string remotePath, CancellationToken ct, bool includeSubdirectories = false)
            => Task.FromResult<IEnumerable<string>>(Array.Empty<string>());

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct) => Task.FromResult(true);

        public Task DeleteAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
