namespace Logy.Core.Git;

/// <summary>Parsed output of <c>git status --porcelain=v2 --branch</c> (design doc 6.3).</summary>
public sealed record RepoStatus
{
    public static readonly IReadOnlyList<string> Args = ["status", "--porcelain=v2", "--branch"];

    public string? HeadHash { get; init; }

    public string? Branch { get; init; }

    public bool IsDetached { get; init; }

    public string? Upstream { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    /// <summary>Upstream configured but its remote-tracking ref is gone (no branch.ab line).</summary>
    public bool UpstreamGone { get; init; }

    /// <summary>Number of changed entries (every non-header line).</summary>
    public int Dirty { get; init; }

    public bool HasUpstream => Upstream is not null && !UpstreamGone;

    public bool Diverged => HasUpstream && Ahead > 0 && Behind > 0;

    public static RepoStatus Parse(string text)
    {
        string? oid = null, head = null, upstream = null;
        var detached = false;
        var hasAb = false;
        int ahead = 0, behind = 0, dirty = 0;

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith('#'))
            {
                dirty++;
                continue;
            }

            var value = (string prefix) => line[prefix.Length..].Trim();
            if (line.StartsWith("# branch.oid "))
            {
                var v = value("# branch.oid ");
                oid = v == "(initial)" ? null : v;
            }
            else if (line.StartsWith("# branch.head "))
            {
                var v = value("# branch.head ");
                if (v == "(detached)")
                {
                    detached = true;
                }
                else
                {
                    head = v;
                }
            }
            else if (line.StartsWith("# branch.upstream "))
            {
                upstream = value("# branch.upstream ");
            }
            else if (line.StartsWith("# branch.ab "))
            {
                hasAb = true;
                foreach (var part in value("# branch.ab ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length > 1 && int.TryParse(part[1..], out var n))
                    {
                        if (part[0] == '+')
                        {
                            ahead = n;
                        }
                        else if (part[0] == '-')
                        {
                            behind = n;
                        }
                    }
                }
            }
        }

        return new RepoStatus
        {
            HeadHash = oid,
            Branch = head,
            IsDetached = detached,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            UpstreamGone = upstream is not null && !hasAb,
            Dirty = dirty,
        };
    }
}
