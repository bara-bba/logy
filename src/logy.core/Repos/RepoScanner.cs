namespace Logy.Core.Repos;

/// <summary>
/// A working-tree repo found under the root folder.
/// <see cref="Remote"/> is from origin's URL, else the first remote's URL.
/// </summary>
public sealed record RepoInfo(string Name, string Path, string GitDir, RemoteInfo Remote)
{
    /// <summary>All remote names from config, in config order.</summary>
    public IReadOnlyList<string> RemoteNames { get; init; } = [];

    /// <summary>Linked-worktree common dir (from the <c>commondir</c> file), else null.</summary>
    public string? CommonDir { get; init; }

    /// <summary>True when there is at least one remote and every remote URL is CodeCommit (D10).</summary>
    public bool AllCodeCommit { get; init; }
}

/// <summary>
/// Lists the repos directly under a folder by reading files only (no git process per repo),
/// so the + picker opens instantly even with many CodeCommit repos.
/// </summary>
public static class RepoScanner
{
    /// <summary>
    /// Direct subfolders of <paramref name="root"/> that have a .git folder or a .git pointer file,
    /// sorted by folder name (case-insensitive). Bare repos have no .git entry and are skipped.
    /// </summary>
    public static IReadOnlyList<RepoInfo> Scan(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var repos = new List<RepoInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (TryOpen(dir) is { } repo)
                {
                    repos.Add(repo);
                }
            }
            catch (IOException)
            {
                // Unreadable folder: leave it out rather than fail the whole list.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static RepoInfo? TryOpen(string path)
    {
        var gitDir = ResolveGitDir(path);
        if (gitDir is null)
        {
            return null;
        }

        var commonDir = ResolveCommonDir(gitDir);
        var config = System.IO.Path.Combine(commonDir ?? gitDir, "config");
        var remotes = File.Exists(config)
            ? GitConfigReader.RemotesFromText(File.ReadAllText(config))
            : [];

        var url = remotes.FirstOrDefault(r => r.Name.Equals("origin", StringComparison.OrdinalIgnoreCase)).Url
                  ?? remotes.Select(r => r.Url).FirstOrDefault(u => u is not null);
        var withUrl = remotes.Where(r => r.Url is not null).ToList();

        return new RepoInfo(new DirectoryInfo(path).Name, System.IO.Path.GetFullPath(path), gitDir, RemoteInfo.Parse(url))
        {
            RemoteNames = remotes.Select(r => r.Name).ToList(),
            CommonDir = commonDir,
            AllCodeCommit = withUrl.Count > 0 && withUrl.All(r => RemoteInfo.Parse(r.Url).Kind == RemoteKind.CodeCommit),
        };
    }

    /// <summary>The repo's git directory: ./.git, or the target of a "gitdir: ..." pointer file.</summary>
    internal static string? ResolveGitDir(string path)
    {
        var dotGit = System.IO.Path.Combine(path, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
        const string prefix = "gitdir:";
        if (line is null || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = line[prefix.Length..].Trim();
        if (!System.IO.Path.IsPathRooted(target))
        {
            target = System.IO.Path.Combine(path, target);
        }

        return System.IO.Path.GetFullPath(target);
    }

    /// <summary>Linked worktrees keep refs and config in the dir named by their "commondir" file.</summary>
    internal static string? ResolveCommonDir(string gitDir)
    {
        var commonFile = System.IO.Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonFile))
        {
            return null;
        }

        var common = File.ReadAllText(commonFile).Trim();
        if (common.Length == 0)
        {
            return null;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(common) ? common : System.IO.Path.Combine(gitDir, common));
    }
}

/// <summary>Just enough of git's config format to find remotes.</summary>
internal static class GitConfigReader
{
    /// <summary>origin's URL, else the first remote's URL, else null.</summary>
    public static string? RemoteUrl(string configPath) =>
        File.Exists(configPath) ? RemoteUrlFromText(File.ReadAllText(configPath)) : null;

    internal static string? RemoteUrlFromText(string text)
    {
        var remotes = RemotesFromText(text);
        return remotes.FirstOrDefault(r => r.Name.Equals("origin", StringComparison.OrdinalIgnoreCase)).Url
               ?? remotes.Select(r => r.Url).FirstOrDefault(u => u is not null);
    }

    /// <summary>Every remote URL, in config order.</summary>
    internal static IReadOnlyList<string> RemoteUrls(string text) =>
        RemotesFromText(text).Where(r => r.Url is not null).Select(r => r.Url!).ToList();

    /// <summary>Remotes in config order; the URL is the first url= line of the section.</summary>
    internal static List<(string Name, string? Url)> RemotesFromText(string text)
    {
        var remotes = new List<(string Name, string? Url)>();
        var current = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                current = -1;
                var section = line.Trim('[', ']').Trim();
                if (section.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                {
                    var name = section[7..].Trim().Trim('"');
                    current = remotes.FindIndex(r => r.Name == name);
                    if (current < 0)
                    {
                        remotes.Add((name, null));
                        current = remotes.Count - 1;
                    }
                }

                continue;
            }

            if (current < 0)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (remotes[current].Url is null)
            {
                remotes[current] = (remotes[current].Name, line[(eq + 1)..].Trim().Trim('"'));
            }
        }

        return remotes;
    }
}
