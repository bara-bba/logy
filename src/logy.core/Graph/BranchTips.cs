namespace Logy.Core.Graph;

/// <summary>One line of the refresh's <c>git for-each-ref</c> output.</summary>
public sealed record RefLine(string ObjectName, string RefName, string UpstreamRemote, string UpstreamRef);

/// <summary>
/// The refresh's single <c>for-each-ref</c> call (design doc 6.3/6.4). Its output gives the
/// fingerprint, the branch tips map, each local branch's upstream and whether main/master exist.
/// </summary>
public static class BranchTips
{
    public static readonly IReadOnlyList<string> Args =
    [
        "for-each-ref",
        "--format=%(objectname)%1f%(refname)%1f%(upstream:remotename)%1f%(upstream:remoteref)",
        "refs/heads", "refs/remotes", "refs/tags",
    ];

    public static List<RefLine> ParseLines(string text)
    {
        var lines = new List<RefLine>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var f = raw.Split('\x1f');
            if (f.Length < 2)
            {
                continue;
            }

            lines.Add(new RefLine(f[0], f[1], f.Length > 2 ? f[2] : "", f.Length > 3 ? f[3] : ""));
        }

        return lines;
    }

    /// <summary>
    /// hash → branch name from refs/heads/x (→ x) and refs/remotes/&lt;r&gt;/x (→ x), skipping
    /// &lt;r&gt;/HEAD and every other ref. Local beats remote, then the alphabetically first name.
    /// </summary>
    public static Dictionary<string, string> Parse(string text, IReadOnlyCollection<string> remoteNames) =>
        FromLines(ParseLines(text), remoteNames);

    public static Dictionary<string, string> FromLines(IEnumerable<RefLine> lines, IReadOnlyCollection<string> remoteNames)
    {
        var best = new Dictionary<string, (bool Local, string Name)>();
        foreach (var line in lines)
        {
            string? name = null;
            var local = false;
            if (line.RefName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                name = line.RefName["refs/heads/".Length..];
                local = true;
            }
            else if (line.RefName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = line.RefName["refs/remotes/".Length..];
                var remote = remoteNames.FirstOrDefault(r => rest.StartsWith(r + "/", StringComparison.Ordinal));
                var slash = remote is null ? rest.IndexOf('/') : remote.Length;
                if (slash <= 0)
                {
                    continue;
                }

                name = rest[(slash + 1)..];
                if (name == "HEAD")
                {
                    continue;
                }
            }

            if (name is null || name.Length == 0)
            {
                continue;
            }

            if (!best.TryGetValue(line.ObjectName, out var cur) ||
                (local && !cur.Local) ||
                (local == cur.Local && string.CompareOrdinal(name, cur.Name) < 0))
            {
                best[line.ObjectName] = (local, name);
            }
        }

        return best.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }

    /// <summary>main, else master if it has a refs/heads line, else the current branch, else "HEAD".</summary>
    public static string MainBranch(IEnumerable<RefLine> lines, string? currentBranch)
    {
        var heads = lines.Where(l => l.RefName.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(l => l.RefName["refs/heads/".Length..]).ToHashSet(StringComparer.Ordinal);
        if (heads.Contains("main"))
        {
            return "main";
        }

        if (heads.Contains("master"))
        {
            return "master";
        }

        return currentBranch ?? "HEAD";
    }

    /// <summary>Upstream (remote, branch name) of a local branch, or null when it has none.</summary>
    public static (string Remote, string Name)? UpstreamOf(IEnumerable<RefLine> lines, string branch)
    {
        var line = lines.FirstOrDefault(l => l.RefName == "refs/heads/" + branch);
        if (line is null || line.UpstreamRemote.Length == 0 || line.UpstreamRef.Length == 0)
        {
            return null;
        }

        var name = line.UpstreamRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? line.UpstreamRef["refs/heads/".Length..]
            : line.UpstreamRef;
        return (line.UpstreamRemote, name);
    }
}
