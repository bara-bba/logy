using Logy.Core.Repos;

namespace Logy.Tests;

public sealed class RepoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logy-tests-" + Guid.NewGuid().ToString("N"));

    public RepoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RemoteInfo_parses_codecommit_github_and_empty()
    {
        var cc = RemoteInfo.Parse("codecommit::eu-west-1://admin-barbara@trn3-graph-db");
        Assert.Equal(RemoteKind.CodeCommit, cc.Kind);
        Assert.Equal("admin-barbara", cc.Profile);
        Assert.Equal("CC", cc.Chip);

        var gh = RemoteInfo.Parse("https://github.com/bara-bba/picky.git");
        Assert.Equal(RemoteKind.GitHub, gh.Kind);
        Assert.Equal("GH", gh.Chip);

        Assert.Equal(RemoteKind.None, RemoteInfo.Parse("").Kind);
        Assert.Equal("", RemoteInfo.Parse(null).Chip);
        Assert.Equal("GIT", RemoteInfo.Parse("https://gitlab.example/x.git").Chip);
    }

    [Fact]
    public void Scanner_finds_dir_and_pointer_repos_and_skips_bare_and_plain()
    {
        MakeRepo("Zeta", "[remote \"origin\"]\n\turl = https://github.com/a/zeta.git\n");

        var target = Path.Combine(_root, "_gitdirs", "alpha.git");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "config"), "[core]\n");
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", ".git"), "gitdir: " + target);

        var bare = Path.Combine(_root, "x.git");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(_root, "plain"));

        var repos = RepoScanner.Scan(_root);

        Assert.Equal(["alpha", "Zeta"], repos.Select(r => r.Name).ToArray());
        Assert.Equal(Path.GetFullPath(target), repos[0].GitDir);
        Assert.Equal(RemoteKind.GitHub, repos[1].Remote.Kind);
        Assert.Equal(["origin"], repos[1].RemoteNames.ToArray());
    }

    [Fact]
    public void AllCodeCommit_only_when_every_remote_is_codecommit()
    {
        var onlyCc = MakeRepo("cc", "[remote \"origin\"]\n\turl = codecommit::eu-west-1://p@a\n[remote \"b\"]\n\turl = codecommit://p@b\n");
        var mixed = MakeRepo("mixed", "[remote \"origin\"]\n\turl = codecommit::eu-west-1://p@a\n[remote \"gh\"]\n\turl = https://github.com/a/b.git\n");
        var none = MakeRepo("none", "[core]\n\tbare = false\n");

        Assert.True(RepoScanner.TryOpen(onlyCc)!.AllCodeCommit);
        Assert.False(RepoScanner.TryOpen(mixed)!.AllCodeCommit);
        Assert.False(RepoScanner.TryOpen(none)!.AllCodeCommit);
        Assert.Equal(RemoteKind.None, RepoScanner.TryOpen(none)!.Remote.Kind);
    }

    [Fact]
    public void ConfigReader_prefers_origin_and_lists_all_urls()
    {
        const string text = "[remote \"upstream\"]\n\turl = https://github.com/u/x.git\n[remote \"origin\"]\n\turl = https://github.com/o/x.git\n";
        Assert.Equal("https://github.com/o/x.git", GitConfigReader.RemoteUrlFromText(text));
        Assert.Equal(2, GitConfigReader.RemoteUrls(text).Count);
        Assert.Null(GitConfigReader.RemoteUrlFromText("[core]\n\tbare = false\n"));
    }

    private string MakeRepo(string name, string config)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, ".git", "config"), config);
        return path;
    }
}
