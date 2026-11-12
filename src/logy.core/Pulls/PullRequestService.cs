using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.CodeCommit;
using Amazon.CodeCommit.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Logy.Core.Git;
using Logy.Core.Repos;

namespace Logy.Core.Pulls;

/// <summary>Open pull requests, or a short error for the panel.</summary>
public sealed record PullResult(IReadOnlyList<PullRequest> Pulls, string? Error)
{
    public static PullResult Fail(string error) => new([], error);
}

/// <summary>Outcome of create / approve / merge. <see cref="Pull"/> is set by a successful create.</summary>
public sealed record PullActionResult(string? Error, PullRequest? Pull = null)
{
    public bool Ok => Error is null;

    public static PullActionResult Fail(string error) => new(error);
}

/// <summary>
/// Lists, creates, approves and merges pull requests. Every write is started by an explicit,
/// confirmed click in the panel; nothing here runs on its own.
/// GitHub uses the REST API with the token Git Credential Manager already stores for github.com
/// (no login prompt). CodeCommit uses the AWS SDK with the profile named in the codecommit:: URL,
/// so the same ~/.aws/config credentials as git-remote-codecommit.
/// </summary>
public sealed class PullRequestService
{
    /// <summary>CodeCommit needs one call per pull request, so only the newest ones are read.</summary>
    public const int MaxPulls = 30;

    private static readonly HttpClient Http = CreateHttp();

    private readonly GitRunner _git;
    private string? _githubToken;
    private bool _tokenLooked;

    public PullRequestService(GitRunner git)
    {
        _git = git;
    }

    public Task<PullResult> LoadAsync(PullSource source, string repoPath, CancellationToken ct) =>
        source.Kind == RemoteKind.GitHub ? GitHubListAsync(source, repoPath, ct) : CodeCommitListAsync(source, ct);

    public Task<PullActionResult> CreateAsync(PullSource source, string repoPath, string sourceBranch, string targetBranch,
        string title, string description, CancellationToken ct) =>
        source.Kind == RemoteKind.GitHub
            ? GitHubWriteAsync(source, repoPath, HttpMethod.Post, "pulls",
                new Dictionary<string, string> { ["title"] = title, ["head"] = sourceBranch, ["base"] = targetBranch, ["body"] = description },
                parseCreated: true, ct)
            : CodeCommitAsync(source, async client =>
            {
                var resp = await client.CreatePullRequestAsync(new CreatePullRequestRequest
                {
                    Title = title,
                    Description = description.Length > 0 ? description : null,
                    ClientRequestToken = Guid.NewGuid().ToString(),
                    Targets = [new Target { RepositoryName = source.Name, SourceReference = sourceBranch, DestinationReference = targetBranch }],
                }, ct).ConfigureAwait(false);
                return new PullActionResult(null, Map(resp.PullRequest, source));
            });

    public Task<PullActionResult> ApproveAsync(PullSource source, string repoPath, PullRequest pr, CancellationToken ct) =>
        source.Kind == RemoteKind.GitHub
            ? GitHubWriteAsync(source, repoPath, HttpMethod.Post, $"pulls/{pr.Id}/reviews",
                new Dictionary<string, string> { ["event"] = "APPROVE" }, parseCreated: false, ct)
            : CodeCommitAsync(source, async client =>
            {
                // Re-read so the approval targets the current revision, not the one the list showed.
                var fresh = (await client.GetPullRequestAsync(new GetPullRequestRequest { PullRequestId = pr.Id }, ct).ConfigureAwait(false)).PullRequest;
                await client.UpdatePullRequestApprovalStateAsync(new UpdatePullRequestApprovalStateRequest
                {
                    PullRequestId = pr.Id,
                    RevisionId = fresh.RevisionId,
                    ApprovalState = ApprovalState.APPROVE,
                }, ct).ConfigureAwait(false);
                return new PullActionResult(null);
            });

    public Task<PullActionResult> MergeAsync(PullSource source, string repoPath, PullRequest pr, MergeMethod method, CancellationToken ct)
    {
        if (source.Kind == RemoteKind.GitHub)
        {
            var body = new Dictionary<string, string> { ["merge_method"] = MergeMethods.GitHubName(method) };
            if (pr.SourceCommit is { Length: > 0 } sha)
            {
                body["sha"] = sha; // refuse if the branch moved since the list was read
            }

            return GitHubWriteAsync(source, repoPath, HttpMethod.Put, $"pulls/{pr.Id}/merge", body, parseCreated: false, ct);
        }

        return CodeCommitAsync(source, async client =>
        {
            var fresh = (await client.GetPullRequestAsync(new GetPullRequestRequest { PullRequestId = pr.Id }, ct).ConfigureAwait(false)).PullRequest;
            var tip = fresh.PullRequestTargets?.FirstOrDefault()?.SourceCommit;
            switch (method)
            {
                case MergeMethod.Squash:
                    await client.MergePullRequestBySquashAsync(new MergePullRequestBySquashRequest
                    {
                        PullRequestId = pr.Id, RepositoryName = source.Name, SourceCommitId = tip, CommitMessage = fresh.Title,
                    }, ct).ConfigureAwait(false);
                    break;
                case MergeMethod.Merge:
                    await client.MergePullRequestByThreeWayAsync(new MergePullRequestByThreeWayRequest
                    {
                        PullRequestId = pr.Id, RepositoryName = source.Name, SourceCommitId = tip, CommitMessage = fresh.Title,
                    }, ct).ConfigureAwait(false);
                    break;
                default:
                    await client.MergePullRequestByFastForwardAsync(new MergePullRequestByFastForwardRequest
                    {
                        PullRequestId = pr.Id, RepositoryName = source.Name, SourceCommitId = tip,
                    }, ct).ConfigureAwait(false);
                    break;
            }

            return new PullActionResult(null);
        });
    }

    // ---- GitHub ----

    private static string ApiBase(PullSource s) =>
        $"https://api.github.com/repos/{Uri.EscapeDataString(s.Owner)}/{Uri.EscapeDataString(s.Name)}/";

    private async Task<PullResult> GitHubListAsync(PullSource s, string repoPath, CancellationToken ct)
    {
        var token = await GitHubTokenAsync(repoPath, ct).ConfigureAwait(false);
        var url = ApiBase(s) + $"pulls?state=open&per_page={MaxPulls}&sort=updated&direction=desc";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (token is not null)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return PullResult.Fail(token is null ? "not found (private repo? sign in to GitHub with git first)" : "repo not found on GitHub");
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return PullResult.Fail(resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0"
                    ? "GitHub rate limit reached, try later"
                    : "GitHub: authentication failed");
            }

            if (!resp.IsSuccessStatusCode)
            {
                return PullResult.Fail($"GitHub returned {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new PullResult(GitHubPullParser.Parse(json), null);
        }
        catch (HttpRequestException)
        {
            return PullResult.Fail("offline: cannot reach GitHub");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return PullResult.Fail("timed out");
        }
    }

    private async Task<PullActionResult> GitHubWriteAsync(PullSource s, string repoPath, HttpMethod method, string path,
        Dictionary<string, string> body, bool parseCreated, CancellationToken ct)
    {
        var token = await GitHubTokenAsync(repoPath, ct).ConfigureAwait(false);
        if (token is null)
        {
            return PullActionResult.Fail("no GitHub login stored: push once from a terminal to sign in");
        }

        try
        {
            using var req = new HttpRequestMessage(method, ApiBase(s) + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return PullActionResult.Fail(GitHubPullParser.Friendly((int)resp.StatusCode, text));
            }

            return new PullActionResult(null, parseCreated ? GitHubPullParser.ParseOne(text) : null);
        }
        catch (HttpRequestException)
        {
            return PullActionResult.Fail("offline: cannot reach GitHub");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return PullActionResult.Fail("timed out");
        }
    }

    /// <summary>
    /// Asks git's credential helper for the stored github.com token, never interactively
    /// (GCM_INTERACTIVE=never, GIT_TERMINAL_PROMPT=0). Kept in memory only. A miss is retried on
    /// the next call, so signing in from a terminal later works without restarting logy.
    /// </summary>
    private async Task<string?> GitHubTokenAsync(string repoPath, CancellationToken ct)
    {
        if (_tokenLooked && _githubToken is not null)
        {
            return _githubToken;
        }

        _tokenLooked = true;
        try
        {
            var r = await _git.RunAsync(repoPath, ["credential", "fill"], stdin: "protocol=https\nhost=github.com\n\n",
                timeout: TimeSpan.FromSeconds(15), ct: ct, extraEnv: new() { ["GCM_INTERACTIVE"] = "never" }).ConfigureAwait(false);
            if (r.Ok)
            {
                foreach (var line in r.StdOut.Replace("\r", "").Split('\n'))
                {
                    if (line.StartsWith("password=", StringComparison.Ordinal) && line.Length > 9)
                    {
                        _githubToken = line[9..];
                    }
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return _githubToken;
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("logy");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return h;
    }

    // ---- CodeCommit ----

    private static async Task<PullResult> CodeCommitListAsync(PullSource s, CancellationToken ct)
    {
        List<PullRequest>? pulls = null;
        var r = await CodeCommitAsync(s, async client =>
        {
            var ids = new List<string>();
            string? next = null;
            do
            {
                var page = await client.ListPullRequestsAsync(new ListPullRequestsRequest
                {
                    RepositoryName = s.Name,
                    PullRequestStatus = PullRequestStatusEnum.OPEN,
                    NextToken = next,
                }, ct).ConfigureAwait(false);
                ids.AddRange(page.PullRequestIds ?? []);
                next = page.NextToken;
            }
            while (next is not null && ids.Count < MaxPulls);

            using var gate = new SemaphoreSlim(4);
            var tasks = ids.Take(MaxPulls).Select(async id =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var pr = (await client.GetPullRequestAsync(new GetPullRequestRequest { PullRequestId = id }, ct).ConfigureAwait(false)).PullRequest;
                    return Map(pr, s);
                }
                finally
                {
                    gate.Release();
                }
            });
            pulls = (await Task.WhenAll(tasks).ConfigureAwait(false)).OrderByDescending(p => p.Updated).ToList();
            return new PullActionResult(null);
        }).ConfigureAwait(false);
        return r.Ok ? new PullResult(pulls ?? [], null) : PullResult.Fail(r.Error!);
    }

    private static PullRequest Map(Amazon.CodeCommit.Model.PullRequest pr, PullSource s)
    {
        var target = pr.PullRequestTargets?.FirstOrDefault();
        return new PullRequest(
            pr.PullRequestId,
            pr.Title ?? "",
            ShortArn(pr.AuthorArn),
            StripHeads(target?.SourceReference),
            StripHeads(target?.DestinationReference),
            new DateTimeOffset(DateTime.SpecifyKind(pr.LastActivityDate ?? pr.CreationDate ?? DateTime.UtcNow, DateTimeKind.Utc)),
            s.CodeCommitConsoleUrl(pr.PullRequestId),
            false,
            pr.Description ?? "",
            pr.RevisionId,
            target?.SourceCommit);
    }

    /// <summary>Runs one CodeCommit operation with the URL's profile and region, mapping SDK errors to short text.</summary>
    private static async Task<PullActionResult> CodeCommitAsync(PullSource s, Func<AmazonCodeCommitClient, Task<PullActionResult>> op)
    {
        var profile = s.Profile ?? "default";
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
        {
            return PullActionResult.Fail($"{profile}: AWS profile not found");
        }

        try
        {
            using var client = new AmazonCodeCommitClient(credentials, RegionEndpoint.GetBySystemName(s.Region ?? "eu-west-1"));
            return await op(client).ConfigureAwait(false);
        }
        catch (AmazonServiceException ex)
        {
            return PullActionResult.Fail(CodeCommitFriendly(ex.ErrorCode, ex.Message, profile));
        }
        catch (AmazonClientException ex)
        {
            return PullActionResult.Fail(ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase)
                ? $"{profile}: credentials expired or missing"
                : "offline: cannot reach CodeCommit");
        }
        catch (HttpRequestException)
        {
            return PullActionResult.Fail("offline: cannot reach CodeCommit");
        }
    }

    /// <summary>CodeCommit error code → one short line for the panel's error bar.</summary>
    public static string CodeCommitFriendly(string? code, string? message, string profile)
    {
        // The SDK reports codes with or without the "Exception" suffix depending on the call.
        var c = (code ?? "").EndsWith("Exception", StringComparison.Ordinal) ? code![..^"Exception".Length] : code ?? "";
        return c switch
        {
            "ExpiredToken" or "UnrecognizedClient" or "InvalidClientTokenId" => $"{profile}: credentials expired or missing",
            "AccessDenied" => $"{profile}: no permission for this pull request action",
            "PullRequestCannotBeApprovedByAuthor" => "you can't approve your own pull request",
            "ManualMergeRequired" => "conflicts: merge in a terminal",
            "TipOfSourceReferenceIsDifferent" or "RevisionNotCurrent" => "the branch changed: refresh and try again",
            "SourceAndDestinationAreSame" => "source and target are the same branch",
            "ReferenceDoesNotExist" => "branch not found on CodeCommit: push it first",
            "PullRequestApprovalRulesNotSatisfied" => "approval rules not met",
            "PullRequestAlreadyClosed" => "the pull request is already closed",
            "TipsDivergenceExceeded" => "branches differ too much for CodeCommit to merge",
            _ => string.IsNullOrWhiteSpace(message) ? (c.Length > 0 ? c : "CodeCommit error") : message!,
        };
    }

    /// <summary>arn:aws:sts::123:assumed-role/Admin/lorettin → lorettin; arn:aws:iam::123:user/x → x.</summary>
    public static string ShortArn(string? arn)
    {
        if (string.IsNullOrEmpty(arn))
        {
            return "";
        }

        var slash = arn.LastIndexOf('/');
        return slash >= 0 ? arn[(slash + 1)..] : arn;
    }

    public static string StripHeads(string? reference) =>
        reference is null ? "" : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] : reference;
}
