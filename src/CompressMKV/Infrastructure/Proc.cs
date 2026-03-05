using System.Diagnostics;
using System.Text;

namespace CompressMkv;

/// <summary>
/// Process runner — executes external processes and captures stdout/stderr.
/// </summary>
public static class Proc
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var tcsOut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsErr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.OutputDataReceived += (_, e) => { if (e.Data == null) tcsOut.TrySetResult(true); else stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) tcsErr.TrySetResult(true); else stderr.AppendLine(e.Data); };

        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await using var _ = ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });

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
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stderr = new StringBuilder();
        var tcsErr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) tcsErr.TrySetResult(true);
            else stderr.AppendLine(e.Data);
        };

        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        p.BeginErrorReadLine();

        await using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        // Stream stdout line-by-line without buffering the whole output.
        using var reader = p.StandardOutput;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            onStdoutLine(line);
        }

        await Task.WhenAll(p.WaitForExitAsync(ct), tcsErr.Task);
        return (p.ExitCode, stderr.ToString());
    }
}
