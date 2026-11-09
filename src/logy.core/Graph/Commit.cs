namespace Logy.Core.Graph;

public enum RefKind
{
    Head,
    Local,
    Remote,
    Tag,
}

public sealed record Ref(string Name, RefKind Kind);

public sealed record Commit(
    string Hash,
    IReadOnlyList<string> Parents,
    string Author,
    long UnixTime,
    IReadOnlyList<Ref> Refs,
    string Subject)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    public bool IsHead => Refs.Any(r => r.Kind == RefKind.Head);
}

/// <summary>Parses the custom <c>git log</c> format from design doc 6.4.</summary>
public static class LogParser
{
    public const int MaxCommits = 2000;

    public static IReadOnlyList<string> Args =>
    [
        "log", "--branches", "--remotes", "--tags", "HEAD", "--topo-order", "-n", MaxCommits.ToString(),
        "--format=%H%x1f%P%x1f%an%x1f%at%x1f%D%x1f%s%x1e",
    ];

    public static List<Commit> Parse(string text, IReadOnlyCollection<string> remoteNames)
    {
        var commits = new List<Commit>();
        foreach (var rawRecord in text.Split('\x1e'))
        {
            // Not Trim(): .NET counts \x1f as whitespace and would eat empty trailing fields.
            var record = rawRecord.Trim('\r', '\n', ' ');
            if (record.Length == 0)
            {
                continue;
            }

            var f = record.Split('\x1f');
            if (f.Length < 6)
            {
                continue;
            }

            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long.TryParse(f[3], out var time);
            commits.Add(new Commit(f[0].Trim(), parents, f[2], time, ParseRefs(f[4], remoteNames), f[5]));
        }

        return commits;
    }

    internal static List<Ref> ParseRefs(string decoration, IReadOnlyCollection<string> remoteNames)
    {
        var refs = new List<Ref>();
        foreach (var raw in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim();
            if (d.Length == 0)
            {
                continue;
            }

            if (d.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                refs.Add(new Ref("HEAD", RefKind.Head));
                d = d["HEAD -> ".Length..];
            }
            else if (d == "HEAD")
            {
                refs.Add(new Ref("HEAD", RefKind.Head));
                continue;
            }

            if (d.StartsWith("tag: ", StringComparison.Ordinal))
            {
                refs.Add(new Ref(d["tag: ".Length..], RefKind.Tag));
                continue;
            }

            if (d.StartsWith("refs/", StringComparison.Ordinal))
            {
                continue; // refs/stash and other hidden refs
            }

            var remote = remoteNames.FirstOrDefault(r => d.StartsWith(r + "/", StringComparison.Ordinal));
            if (remote is not null)
            {
                if (d == remote + "/HEAD")
                {
                    continue;
                }

                refs.Add(new Ref(d, RefKind.Remote));
                continue;
            }

            refs.Add(new Ref(d, RefKind.Local));
        }

        return refs;
    }

    /// <summary>Colour key of a branch ref: the local name, or the remote name without "&lt;r&gt;/".</summary>
    public static string BranchName(Ref r)
    {
        if (r.Kind != RefKind.Remote)
        {
            return r.Name;
        }

        var slash = r.Name.IndexOf('/');
        return slash < 0 ? r.Name : r.Name[(slash + 1)..];
    }
}
