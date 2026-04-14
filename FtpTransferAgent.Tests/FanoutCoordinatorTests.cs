using FtpTransferAgent.Services;

namespace FtpTransferAgent.Tests;

/// <summary>
/// <see cref="FanoutCoordinator"/> の結果集約と完了コールバックを検証する
/// </summary>
public class FanoutCoordinatorTests
{
    [Fact]
    public void AllSuccess_InvokesOnCompleteOnce()
    {
        var coord = new FanoutCoordinator();
        var callCount = 0;
        IReadOnlyList<FanoutCoordinator.DestinationResult>? captured = null;
        string? capturedPath = null;

        coord.Register("g1", "/src/file.txt", 2, (path, results) =>
        {
            Interlocked.Increment(ref callCount);
            captured = results;
            capturedPath = path;
        });

        coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d1", true, null));
        Assert.Equal(0, callCount);
        coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d2", true, null));

        Assert.Equal(1, callCount);
        Assert.Equal("/src/file.txt", capturedPath);
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.All(captured, r => Assert.True(r.Success));
    }

    [Fact]
    public void PartialFailure_PassesAllResultsToCallback()
    {
        var coord = new FanoutCoordinator();
        IReadOnlyList<FanoutCoordinator.DestinationResult>? captured = null;

        coord.Register("g1", "/src/file.txt", 3, (_, r) => captured = r);

        coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d1", true, null));
        coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d2", false, new IOException("boom")));
        coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d3", true, null));

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Count);
        Assert.Single(captured, r => !r.Success);
        Assert.Contains(captured, r => r.Error is IOException);
    }

    [Fact]
    public void ReportForUnknownGroup_IsIgnored()
    {
        var coord = new FanoutCoordinator();
        // 例外を出さずに黙って無視される
        coord.ReportResult("nope", new FanoutCoordinator.DestinationResult("d1", true, null));
    }

    [Fact]
    public void DuplicateRegister_Throws()
    {
        var coord = new FanoutCoordinator();
        coord.Register("g1", "/src/a", 1, (_, _) => { });
        Assert.Throws<InvalidOperationException>(
            () => coord.Register("g1", "/src/b", 1, (_, _) => { }));
    }

    [Fact]
    public void ZeroOrNegativeDestinationCount_Throws()
    {
        var coord = new FanoutCoordinator();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => coord.Register("g1", "/src/a", 0, (_, _) => { }));
    }

    [Fact]
    public void OnCompleteFiresExactlyOnce_UnderParallelReport()
    {
        var coord = new FanoutCoordinator();
        var callCount = 0;
        const int n = 50;
        coord.Register("g1", "/src/file.txt", n, (_, _) => Interlocked.Increment(ref callCount));

        Parallel.For(0, n, i =>
            coord.ReportResult("g1", new FanoutCoordinator.DestinationResult("d" + i, true, null)));

        Assert.Equal(1, callCount);
    }
}
