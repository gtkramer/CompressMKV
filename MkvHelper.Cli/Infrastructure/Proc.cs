using System.Diagnostics;
using System.Text;

namespace MkvHelper;

/// <summary>
/// Process runner — executes external processes and captures stdout/stderr.
/// </summary>
public static class Proc
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            UseShellExecute = false,
            // Redirect stdin so the child never inherits our terminal handle.
            // Without this, ffmpeg in particular flips the terminal into
            // non-canonical mode to listen for its `q`/`+`/`-` shortcuts and
            // — if killed via cancellation — never restores it, leaving the
            // user's shell with echo disabled after the run.  See proc(5)
            // termios behavior for why this happens at child exit time.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };

        StringBuilder stdout = new();
        StringBuilder stderr = new();
        TaskCompletionSource<bool> tcsOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> tcsErr = new(TaskCreationOptions.RunContinuationsAsynchronously);

        p.OutputDataReceived += (_, e) => { if (e.Data == null) tcsOut.TrySetResult(true); else stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) tcsErr.TrySetResult(true); else stderr.AppendLine(e.Data); };

        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        // Close the stdin pipe immediately so any child that tries to read
        // sees EOF instead of blocking.  We never feed data to subprocesses.
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await using CancellationTokenRegistration _ = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });

        await Task.WhenAll(p.WaitForExitAsync(ct), tcsOut.Task, tcsErr.Task);
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs a process and streams stdout lines to a callback while capturing stderr.
    /// Designed for long-running ffmpeg decodes where buffering all stdout would be wasteful.
    /// </summary>
    public static async Task<(int ExitCode, string StdErr)> RunStreamingAsync(
        string exe, string[] args, Action<string> onStdoutLine, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = exe,
            UseShellExecute = false,
            // See RunAsync above for why we redirect stdin.  Same rationale
            // applies — these long-running ffmpeg decodes are exactly the
            // ones most likely to be killed mid-flight via cancellation.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };

        StringBuilder stderr = new();
        TaskCompletionSource<bool> tcsErr = new(TaskCreationOptions.RunContinuationsAsynchronously);

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) tcsErr.TrySetResult(true);
            else stderr.AppendLine(e.Data);
        };

        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        p.StandardInput.Close();
        p.BeginErrorReadLine();

        await using CancellationTokenRegistration reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        // Stream stdout line-by-line without buffering the whole output.
        using StreamReader reader = p.StandardOutput;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            onStdoutLine(line);
        }

        await Task.WhenAll(p.WaitForExitAsync(ct), tcsErr.Task);
        return (p.ExitCode, stderr.ToString());
    }
}
