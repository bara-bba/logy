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

public sealed class PullActionTests
{
    [Fact]
    public void Created_pull_request_is_parsed_with_body_and_head_sha()
    {
        const string json = """
            {"number":5,"title":"Thin icon","body":"Strokes 64 to 40","html_url":"https://github.com/o/r/pull/5",
             "updated_at":"2026-11-12T09:00:00Z","user":{"login":"bara-bba"},
             "head":{"ref":"pr-actions","sha":"abc123"},"base":{"ref":"main"}}
            """;
        var pr = GitHubPullParser.ParseOne(json);
        Assert.NotNull(pr);
        Assert.Equal("5", pr!.Id);
        Assert.Equal("Strokes 64 to 40", pr.Description);
        Assert.Equal("abc123", pr.SourceCommit);
        Assert.Equal("pr-actions", pr.SourceBranch);
    }

    [Theory]
    [InlineData(422, """{"message":"Unprocessable Entity","errors":["Can not approve your own pull request"]}""", "you can't approve your own pull request")]
    [InlineData(422, """{"message":"Validation Failed","errors":[{"message":"A pull request already exists for bara-bba:x."}]}""", "a pull request for this branch already exists")]
    [InlineData(422, """{"message":"Validation Failed","errors":[{"message":"No commits between main and x"}]}""", "no changes between the branches")]
    [InlineData(405, """{"message":"Pull Request is not mergeable"}""", "not mergeable: resolve conflicts in a terminal")]
    [InlineData(409, """{"message":"Head branch was modified. Review and try the merge again."}""", "the branch changed: refresh and try again")]
    [InlineData(403, """{"message":"Resource not accessible"}""", "GitHub: authentication failed or no permission")]
    [InlineData(500, "not json", "GitHub returned 500")]
    public void GitHub_errors_are_mapped(int status, string body, string expected) =>
        Assert.Equal(expected, GitHubPullParser.Friendly(status, body));

    [Theory]
    [InlineData("PullRequestCannotBeApprovedByAuthorException", "you can't approve your own pull request")]
    [InlineData("ManualMergeRequired", "conflicts: merge in a terminal")]
    [InlineData("TipOfSourceReferenceIsDifferentException", "the branch changed: refresh and try again")]
    [InlineData("ReferenceDoesNotExistException", "branch not found on CodeCommit: push it first")]
    [InlineData("AccessDeniedException", "admin-barbara: no permission for this pull request action")]
    [InlineData("ExpiredTokenException", "admin-barbara: credentials expired or missing")]
    public void CodeCommit_errors_are_mapped(string code, string expected) =>
        Assert.Equal(expected, PullRequestService.CodeCommitFriendly(code, "raw", "admin-barbara"));

    [Fact]
    public void Unknown_CodeCommit_error_keeps_the_message() =>
        Assert.Equal("raw text", PullRequestService.CodeCommitFriendly("SomethingNewException", "raw text", "p"));

    [Fact]
    public void Merge_methods_per_host()
    {
        Assert.Equal([MergeMethod.Merge, MergeMethod.Squash, MergeMethod.Rebase], MergeMethods.For(RemoteKind.GitHub));
        Assert.Equal(MergeMethod.FastForward, MergeMethods.For(RemoteKind.CodeCommit)[0]);
        Assert.Equal("squash", MergeMethods.GitHubName(MergeMethod.Squash));
        Assert.Equal("merge", MergeMethods.GitHubName(MergeMethod.FastForward));
    }
}
