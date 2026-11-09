using Logy.Core.Config;
using Logy.Core.Git;
using Logy.Core.Graph;
using Logy.Core.Repos;
using Logy.Core.Theming;

namespace Logy.Tests;

public sealed class CoreTests
{
    [Fact]
    public void GitErrors_drop_gcm_noise_and_map_messages()
    {
        var cc = RemoteInfo.Parse("codecommit::eu-west-1://admin-barbara@x");
        var noisy = "fatal: Failed to write item to store. [0x6c6]\nfatal: The array bounds are invalid\n";
        Assert.Equal("", GitErrors.CleanStdErr(noisy));

        Assert.Equal("admin-barbara: credentials expired or missing",
            GitErrors.Friendly(new GitResult(128, "", "An error occurred (ExpiredToken) when calling"), cc));
        Assert.Equal("branches diverged",
            GitErrors.Friendly(new GitResult(128, "", "fatal: Not possible to fast-forward, aborting."), RemoteInfo.None));
        Assert.Equal("authentication failed",
            GitErrors.Friendly(new GitResult(128, "", "fatal: The requested URL returned error: 403"), RemoteInfo.None));
        Assert.NotEqual("authentication failed",
            GitErrors.Friendly(new GitResult(1, "", "error: object 4031abc is missing"), RemoteInfo.None));
        Assert.Equal("timed out", GitErrors.Friendly(new GitResult(-1, "", "", TimedOut: true), RemoteInfo.None));
    }

    [Fact]
    public void Palettes_random_ink_and_blend()
    {
        var colors = Palettes.Random(new Random(42), 8);
        Assert.Equal(8, colors.Count);
        Assert.All(colors, c => Assert.True(Palettes.IsValid(c)));
        Assert.Equal(8, colors.Distinct().Count());

        Assert.Equal("#202020", Palettes.Ink("#FFFFFF"));
        Assert.Equal("#FFFFFF", Palettes.Ink("#0078D4"));
        Assert.Equal("#808080", Palettes.Blend("#000000", "#FFFFFF", 0.5));
    }

    [Fact]
    public void TreeColors_uses_custom_only_when_present()
    {
        var s = new LogySettings { TreePreset = Palettes.CustomName, CustomColors = ["#112233", "nope"] };
        Assert.Equal(["#112233"], s.TreeColors().ToArray());

        s.CustomColors = [];
        Assert.Equal(Palettes.Get("Fluent").Colors, s.TreeColors());

        s.TreePreset = "unknown";
        Assert.Equal(Palettes.Get("Fluent").Colors, s.TreeColors());
    }

    [Fact]
    public void SettingsStore_update_keeps_fields_changed_elsewhere()
    {
        var folder = Path.Combine(Path.GetTempPath(), "logy-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var json = new JsonStore(folder);
            json.Save(LogySettings.FileName, new LogySettings { Accent = "#0078D4" });

            var inMemory = new LogySettings { Accent = "#FFFFFF" };
            _ = inMemory;
            new SettingsStore(json).Update(s => s.Width = 800);

            var disk = new SettingsStore(json).Load();
            Assert.Equal("#0078D4", disk.Accent);
            Assert.Equal(800, disk.Width);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RepoStatus_parses_porcelain_v2()
    {
        var s = RepoStatus.Parse(
            "# branch.oid 4b63d7c06b27552ba62b75f2bbd387101ec8d5ed\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -3\n1 .M N... 100644 100644 100644 a b f.txt\n? new.txt\n");
        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(3, s.Behind);
        Assert.Equal(2, s.Dirty);
        Assert.True(s.Diverged);
        Assert.False(s.UpstreamGone);

        var detached = RepoStatus.Parse("# branch.oid abc\n# branch.head (detached)\n");
        Assert.True(detached.IsDetached);
        Assert.Null(detached.Upstream);
        Assert.Equal(0, detached.Ahead);
        Assert.False(detached.UpstreamGone);

        var gone = RepoStatus.Parse("# branch.oid abc\n# branch.head x\n# branch.upstream origin/x\n");
        Assert.True(gone.UpstreamGone);
        Assert.False(gone.HasUpstream);
    }

    [Fact]
    public void LogParser_reads_records_and_refs()
    {
        var text = "aaa\u001fbbb ccc\u001fLorenzo\u001f1700000000\u001fHEAD -> main, origin/main, origin/HEAD, tag: v1, feature/x, refs/stash\u001fFirst subject\u001e\n" +
                   "bbb\u001f\u001fLorenzo\u001f1690000000\u001f\u001froot\u001e\n";
        var commits = LogParser.Parse(text, ["origin"]);

        Assert.Equal(2, commits.Count);
        Assert.Equal(["bbb", "ccc"], commits[0].Parents.ToArray());
        Assert.Equal(
            [new Ref("HEAD", RefKind.Head), new Ref("main", RefKind.Local), new Ref("origin/main", RefKind.Remote),
             new Ref("v1", RefKind.Tag), new Ref("feature/x", RefKind.Local)],
            commits[0].Refs.ToArray());
        Assert.Equal("First subject", commits[0].Subject);
        Assert.Empty(commits[1].Parents);
        Assert.Equal("main", LogParser.BranchName(new Ref("origin/main", RefKind.Remote)));
    }

    [Fact]
    public void RelativeTime_ranges()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        long Ago(TimeSpan t) => (now - t).ToUnixTimeSeconds();

        Assert.Equal("now", RelativeTime.Format(Ago(TimeSpan.FromSeconds(30)), now));
        Assert.Equal("5m", RelativeTime.Format(Ago(TimeSpan.FromMinutes(5)), now));
        Assert.Equal("3h", RelativeTime.Format(Ago(TimeSpan.FromHours(3)), now));
        Assert.Equal("2d", RelativeTime.Format(Ago(TimeSpan.FromDays(2)), now));
        Assert.Equal("4w", RelativeTime.Format(Ago(TimeSpan.FromDays(28)), now));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", RelativeTime.Format(Ago(TimeSpan.FromDays(400)), now));
        Assert.Equal("now", RelativeTime.Format(Ago(TimeSpan.FromMinutes(-5)), now));
    }

    [Fact]
    public void BranchTips_local_beats_remote_and_skips_head_and_tags()
    {
        var text = "c1\u001frefs/remotes/origin/zeta\u001f\u001f\n" +
                   "c1\u001frefs/heads/alpha\u001forigin\u001frefs/heads/alpha\n" +
                   "c2\u001frefs/remotes/origin/HEAD\u001f\u001f\n" +
                   "c3\u001frefs/remotes/origin/x\u001f\u001f\n" +
                   "c4\u001frefs/tags/v1\u001f\u001f\n";
        var tips = BranchTips.Parse(text, ["origin"]);

        Assert.Equal("alpha", tips["c1"]);
        Assert.False(tips.ContainsKey("c2"));
        Assert.Equal("x", tips["c3"]);
        Assert.False(tips.ContainsKey("c4"));

        var lines = BranchTips.ParseLines(text);
        Assert.Equal(("origin", "alpha"), BranchTips.UpstreamOf(lines, "alpha"));
        Assert.Null(BranchTips.UpstreamOf(lines, "zeta"));
        Assert.Equal("feature", BranchTips.MainBranch(lines, "feature"));
    }
}
