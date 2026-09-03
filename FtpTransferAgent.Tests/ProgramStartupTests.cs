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

    /// <summary>
    /// 設定ファイルに空配列を書いたら C# 側の既定値を置き換えることを保証する。
    /// 空配列は値も子も持たないため IConfigurationSection.Exists() が false になり、
    /// 素朴に実装すると「書いていない」と区別できず既定の .END / .end が残ってしまう。
    /// </summary>
    [Fact]
    public async Task Program_ShouldTreatExplicitlyEmptyArray_AsReplacingTheDefaults()
    {
        var workDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var watchDir = Path.Combine(workDir, "watch");
        Directory.CreateDirectory(watchDir);
        var lockFile = Path.Combine(workDir, "agent.lock");

        try
        {
            // EndFileExtensions を空配列にしたうえで RequireEndFile を有効にする。
            // 空配列が既定値を置き換えていれば「END 拡張子が未設定」としてエラーになる。
            // 置き換えられていなければ既定の .END / .end が残り、そのまま起動してしまう。
            var watchJson = System.Text.Json.JsonSerializer.Serialize(watchDir);
            var lockJson = System.Text.Json.JsonSerializer.Serialize(lockFile);
            var settings =
                "{" +
                "  \"Watch\": { \"Path\": " + watchJson + ", \"AllowedExtensions\": [ \".txt\" ]," +
                "               \"RequireEndFile\": true, \"EndFileExtensions\": [] }," +
                "  \"Transfer\": { \"Mode\": \"ftp\", \"Direction\": \"put\", \"Host\": \"localhost\", \"Port\": 21," +
                "                  \"Username\": \"user\", \"Password\": \"pass\", \"RemotePath\": \"/remote\", \"Concurrency\": 1 }," +
                "  \"App\": { \"LockFilePath\": " + lockJson + " }," +
                "  \"Retry\": { \"MaxAttempts\": 1, \"DelaySeconds\": 1 }," +
                "  \"Hash\": { \"Enabled\": false, \"Algorithm\": \"SHA256\" }," +
                "  \"Cleanup\": { \"DeleteAfterVerify\": false }," +
                "  \"Smtp\": { \"Enabled\": false, \"From\": \"a@example.com\", \"To\": [ \"b@example.com\" ] }," +
                "  \"Logging\": { \"Level\": \"Information\", \"RollingFilePath\": \"\" }" +
                "}";
            await File.WriteAllTextAsync(Path.Combine(workDir, "appsettings.json"), settings);

            var result = await RunProgramAsync(string.Empty, workDir);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("END file extensions must be specified", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* ベストエフォート */ }
        }
    }

    private static Task<(int ExitCode, string Output)> RunProgramAsync(string arguments)
        => RunProgramAsync(arguments, AppContext.BaseDirectory);

    private static async Task<(int ExitCode, string Output)> RunProgramAsync(string arguments, string workingDirectory)
    {
        var programDllPath = Path.Combine(AppContext.BaseDirectory, "FtpTransferAgent.dll");
        Assert.True(File.Exists(programDllPath), $"Program DLL not found: {programDllPath}");

        var psi = new ProcessStartInfo("dotnet", $"\"{programDllPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
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
