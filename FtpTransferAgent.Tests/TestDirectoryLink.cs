using System.Diagnostics;

namespace FtpTransferAgent.Tests;

internal static class TestDirectoryLink
{
    public static void Create(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        // Windowsでは管理者権限なしで作れるジャンクションで同じReparsePointの経路を検証する。
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start junction creation.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Junction creation timed out.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"Junction creation failed: {process.StandardError.ReadToEnd()}");
    }
}
