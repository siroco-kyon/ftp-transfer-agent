using System.Diagnostics;

namespace FtpTransferAgent.Tests;

public class ProgramStartupTests
{
    [Fact]
    public async Task Program_ShouldExitWithError_WhenTransferConcurrencyIsInvalid()
    {
        var result = await RunProgramAsync("--Transfer:Concurrency=0 --Logging:Level=NotALevel");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid log level 'NotALevel'", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Concurrency", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Program_ShouldExitCleanly_WhenTransferCompletesWithNoFiles()
    {
        // 転送対象が無い put 実行を最後まで通す。host.Run() がホスト (IServiceProvider) を
        // Dispose した後に host.Services を参照して ObjectDisposedException で異常終了し、
        // 終了コードが常に 1 になっていた回帰を防ぐ。
        var watchDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(watchDir);
        var lockFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".lock");

        try
        {
            var args = $"--Watch:Path=\"{watchDir}\" --App:LockFilePath=\"{lockFile}\" " +
                       "--Transfer:Direction=put --Logging:RollingFilePath=";
            var result = await RunProgramAsync(args);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("terminated unexpectedly", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("disposed", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(watchDir, true); } catch { /* ベストエフォート */ }
            try { File.Delete(lockFile); } catch { /* ベストエフォート */ }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProgramAsync(string arguments)
    {
        var programDllPath = Path.Combine(AppContext.BaseDirectory, "FtpTransferAgent.dll");
        Assert.True(File.Exists(programDllPath), $"Program DLL not found: {programDllPath}");

        var psi = new ProcessStartInfo("dotnet", $"\"{programDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("Program did not exit within the expected timeout.");
        }

        var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
        return (process.ExitCode, output);
    }
}
