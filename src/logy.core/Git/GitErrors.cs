using Logy.Core.Repos;

namespace Logy.Core.Git;

/// <summary>Turns git's stderr into one short line for the panel toast.</summary>
public static class GitErrors
{
    /// <summary>Lines Git Credential Manager prints on CodeCommit even when the command succeeds.</summary>
    private static readonly string[] Noise =
    [
        "Failed to write item to store",
        "The array bounds are invalid",
    ];

    public static string CleanStdErr(string stderr) =>
        string.Join('\n', Lines(stderr).Where(l => !Noise.Any(n => l.Contains(n, StringComparison.OrdinalIgnoreCase))));

    public static string Friendly(GitResult result, RemoteInfo remote)
    {
        if (result.TimedOut)
        {
            return "timed out";
        }

        var err = CleanStdErr(result.StdErr);
        bool Has(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (remote.Kind == RemoteKind.CodeCommit &&
            (Has("ExpiredToken") || Has("token has expired") || Has("expired token") ||
             Has("Unable to locate credentials") || Has("Unable to load credentials") ||
             (Has("could not be found") && Has("profile"))))
        {
            return $"{remote.Profile ?? "AWS"}: credentials expired or missing";
        }

        if (Has("Could not resolve host") || Has("unable to access") && Has("resolve"))
        {
            return "offline: cannot reach remote";
        }

        if (Has("Not possible to fast-forward") || Has("have diverged") || Has("divergent branches"))
        {
            return "branches diverged";
        }

        if (Has("[rejected]") || Has("non-fast-forward") || Has("fetch first"))
        {
            return "push rejected: pull first";
        }

        if (Has("has no upstream branch") || Has("no tracking information"))
        {
            return "no upstream branch";
        }

        if (Has("Authentication failed") || Has("returned error: 403") || Has("Permission denied"))
        {
            return "authentication failed";
        }

        if (Has("would be overwritten") || Has("Please commit your changes or stash them"))
        {
            return "local changes block this; commit or stash first";
        }

        var last = Lines(err).LastOrDefault();
        if (last is null)
        {
            return $"git exited with code {result.ExitCode}";
        }

        foreach (var prefix in new[] { "fatal: ", "error: ", "hint: " })
        {
            if (last.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                last = last[prefix.Length..];
            }
        }

        return last;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
