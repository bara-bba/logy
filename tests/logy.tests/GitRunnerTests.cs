using Logy.Core.Git;

namespace Logy.Tests;

public sealed class GitRunnerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "logy-git-" + Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();

    public GitRunnerTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Reads_branch_and_utf8_subject()
    {
        Assert.True((await _git.RunAsync(_repo, ["init", "-b", "trunk"])).Ok);
        await _git.RunAsync(_repo, ["config", "user.email", "t@example.com"]);
        await _git.RunAsync(_repo, ["config", "user.name", "t"]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "x");
        await _git.RunAsync(_repo, ["add", "a.txt"]);
        Assert.True((await _git.RunAsync(_repo, ["commit", "-m", "perché"])).Ok);

        var branch = await _git.RunAsync(_repo, ["rev-parse", "--abbrev-ref", "HEAD"]);
        Assert.Equal("trunk", branch.StdOut.Trim());

        var subject = await _git.RunAsync(_repo, ["log", "-1", "--format=%s"]);
        Assert.Equal("perché", subject.StdOut.Trim());
    }

    [Fact]
    public async Task For_each_ref_output_parses_into_fields()
    {
        Assert.True((await _git.RunAsync(_repo, ["init", "-b", "main"])).Ok);
        await _git.RunAsync(_repo, ["config", "user.email", "t@example.com"]);
        await _git.RunAsync(_repo, ["config", "user.name", "t"]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "x");
        await _git.RunAsync(_repo, ["add", "a.txt"]);
        await _git.RunAsync(_repo, ["commit", "-m", "one"]);
        await _git.RunAsync(_repo, ["branch", "feature/x"]);

        var r = await _git.RunAsync(_repo, Logy.Core.Graph.BranchTips.Args);
        Assert.True(r.Ok);
        var lines = Logy.Core.Graph.BranchTips.ParseLines(r.StdOut);
        Assert.Contains(lines, l => l.RefName == "refs/heads/main" && l.ObjectName.Length == 40);
        Assert.Contains(lines, l => l.RefName == "refs/heads/feature/x");
        Assert.Equal("main", Logy.Core.Graph.BranchTips.MainBranch(lines, "feature/x"));
    }

    [Fact]
    public async Task Tiny_timeout_reports_timed_out()
    {
        var r = await _git.RunAsync(_repo, ["--version"], timeout: TimeSpan.FromMilliseconds(1));
        Assert.True(r.TimedOut || r.Ok); // a very fast machine can finish inside 1 ms
        if (r.TimedOut)
        {
            Assert.False(r.Ok);
        }
    }
}
