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
