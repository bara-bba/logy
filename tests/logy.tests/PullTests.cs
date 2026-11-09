using Logy.Core.Pulls;
using Logy.Core.Repos;

namespace Logy.Tests;

public sealed class PullTests
{
    [Theory]
    [InlineData("https://github.com/bara-bba/picky.git", "bara-bba", "picky")]
    [InlineData("https://github.com/bara-bba/wally", "bara-bba", "wally")]
    [InlineData("git@github.com:google/elephant-goldfish-model.git", "google", "elephant-goldfish-model")]
    public void GitHub_urls_give_owner_and_name(string url, string owner, string name)
    {
        var s = PullSource.From(RemoteInfo.Parse(url));
        Assert.NotNull(s);
        Assert.Equal(RemoteKind.GitHub, s!.Kind);
        Assert.Equal(owner, s.Owner);
        Assert.Equal(name, s.Name);
    }

    [Fact]
    public void CodeCommit_url_gives_region_profile_and_repo()
    {
        var s = PullSource.From(RemoteInfo.Parse("codecommit::eu-west-1://admin-barbara@trn3-graph-db"));
        Assert.NotNull(s);
        Assert.Equal("eu-west-1", s!.Region);
        Assert.Equal("admin-barbara", s.Profile);
        Assert.Equal("trn3-graph-db", s.Name);
        Assert.StartsWith("https://eu-west-1.console.aws.amazon.com/codesuite/codecommit/repositories/trn3-graph-db/pull-requests/7/", s.CodeCommitConsoleUrl("7"));
    }

    [Fact]
    public void CodeCommit_https_url_is_understood()
    {
        var s = PullSource.From(RemoteInfo.Parse("https://git-codecommit.eu-central-1.amazonaws.com/v1/repos/my-repo"));
        Assert.Equal("eu-central-1", s!.Region);
        Assert.Equal("my-repo", s.Name);
    }

    [Fact]
    public void Other_and_missing_remotes_have_no_pull_source()
    {
        Assert.Null(PullSource.From(RemoteInfo.Parse("https://gitlab.com/x/y.git")));
        Assert.Null(PullSource.From(RemoteInfo.None));
    }

    [Fact]
    public void GitHub_json_is_parsed()
    {
        const string json = """
            [{"number":12,"title":"Add gallery","draft":true,"html_url":"https://github.com/o/r/pull/12",
              "updated_at":"2026-11-08T10:00:00Z","user":{"login":"bara-bba"},
              "head":{"ref":"feature/gallery"},"base":{"ref":"master"}}]
            """;
        var pr = Assert.Single(GitHubPullParser.Parse(json));
        Assert.Equal("12", pr.Id);
        Assert.Equal("Add gallery", pr.Title);
        Assert.Equal("bara-bba", pr.Author);
        Assert.Equal("feature/gallery", pr.SourceBranch);
        Assert.Equal("master", pr.TargetBranch);
        Assert.True(pr.IsDraft);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), pr.Updated);
    }

    [Fact]
    public void Arn_and_ref_helpers()
    {
        Assert.Equal("lorettin", PullRequestService.ShortArn("arn:aws:sts::1:assumed-role/Admin/lorettin"));
        Assert.Equal("main", PullRequestService.StripHeads("refs/heads/main"));
    }
}
