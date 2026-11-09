using System.Diagnostics;
using System.Text;

namespace Logy.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs the user's own git.exe, so existing credentials, hooks and the git-remote-codecommit
/// helper behave exactly as in a terminal. See design doc 6.7 for the contract.
/// </summary>
public sealed class GitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly string _gitExe;

    public GitRunner(string gitExe = "git")
    {
        _gitExe = gitExe;
    }

    /// <summary>
    /// Full argument list. <paramref name="skipCredentialStore"/> blanks credential.helper for this
    /// one command (D10): CodeCommit's signed one-time passwords make Git Credential Manager print
    /// "Failed to write item to store" on every call.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(string repoPath, IEnumerable<string> args, bool skipCredentialStore)
    {
        var list = new List<string> { "-C", repoPath, "-c", "color.ui=never", "-c", "core.quotepath=off" };
        if (skipCredentialStore)
        {
            list.Add("-c");
            list.Add("credential.helper=");
        }

        list.AddRange(args);
        return list;
    }

    /// <summary>
    /// Runs git and returns its output. A timeout kills the process tree and returns
    /// <see cref="GitResult.TimedOut"/>; cancelling <paramref name="ct"/> kills it and throws.
    /// </summary>
    public async Task<GitResult> RunAsync(
        string repoPath,
        IEnumerable<string> args,
        bool skipCredentialStore = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        string? stdin = null,
        Dictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(_gitExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArgs(repoPath, args, skipCredentialStore))
        {
            psi.ArgumentList.Add(a);
        }

        // Never wait on a hidden terminal prompt, and never let our own status calls write the index.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var (k, v) in extraEnv ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        if (stdin is not null)
        {
            // Only for `git credential fill`; everything else gets end-of-file at once.
            proc.StandardInput.Write(stdin);
        }

        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            ct.ThrowIfCancellationRequested();
            return new GitResult(-1, "", "timed out", TimedOut: true);
        }

        return new GitResult(proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>True when <c>git --version</c> runs (startup check, doc section 9).</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var r = await RunAsync(Environment.CurrentDirectory, ["--version"], timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return r.Ok;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
