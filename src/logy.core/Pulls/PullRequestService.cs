using System.Net;
using System.Net.Http.Headers;
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

/// <summary>
/// Reads open pull requests. Read-only: logy never creates, merges or comments.
/// GitHub uses the REST API with the token Git Credential Manager already stores for github.com
/// (no login prompt; anonymous if there is none). CodeCommit uses the AWS SDK with the profile
/// named in the codecommit:: URL, so the same ~/.aws/config credentials as git-remote-codecommit.
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
        source.Kind == RemoteKind.GitHub ? GitHubAsync(source, repoPath, ct) : CodeCommitAsync(source, ct);

    // ---- GitHub ----

    private async Task<PullResult> GitHubAsync(PullSource s, string repoPath, CancellationToken ct)
    {
        var token = await GitHubTokenAsync(repoPath, ct).ConfigureAwait(false);
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(s.Owner)}/{Uri.EscapeDataString(s.Name)}/pulls?state=open&per_page={MaxPulls}&sort=updated&direction=desc";
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

    /// <summary>
    /// Asks git's credential helper for the stored github.com token, never interactively
    /// (GCM_INTERACTIVE=never, GIT_TERMINAL_PROMPT=0). Looked up once per run; kept in memory only.
    /// </summary>
    private async Task<string?> GitHubTokenAsync(string repoPath, CancellationToken ct)
    {
        if (_tokenLooked)
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

    private static async Task<PullResult> CodeCommitAsync(PullSource s, CancellationToken ct)
    {
        var profile = s.Profile ?? "default";
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
        {
            return PullResult.Fail($"{profile}: AWS profile not found");
        }

        var region = RegionEndpoint.GetBySystemName(s.Region ?? "eu-west-1");
        try
        {
            using var client = new AmazonCodeCommitClient(credentials, region);
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
                    var target = pr.PullRequestTargets?.FirstOrDefault();
                    return new PullRequest(
                        pr.PullRequestId,
                        pr.Title ?? "",
                        ShortArn(pr.AuthorArn),
                        StripHeads(target?.SourceReference),
                        StripHeads(target?.DestinationReference),
                        new DateTimeOffset(DateTime.SpecifyKind(pr.LastActivityDate ?? pr.CreationDate ?? DateTime.UtcNow, DateTimeKind.Utc)),
                        s.CodeCommitConsoleUrl(pr.PullRequestId));
                }
                finally
                {
                    gate.Release();
                }
            });
            var pulls = (await Task.WhenAll(tasks).ConfigureAwait(false)).OrderByDescending(p => p.Updated).ToList();
            return new PullResult(pulls, null);
        }
        catch (AmazonServiceException ex) when (ex.ErrorCode is "ExpiredTokenException" or "ExpiredToken" or "UnrecognizedClientException" or "InvalidClientTokenId")
        {
            return PullResult.Fail($"{profile}: credentials expired or missing");
        }
        catch (AmazonServiceException ex) when (ex.ErrorCode is "AccessDeniedException")
        {
            return PullResult.Fail($"{profile}: no permission to list pull requests");
        }
        catch (AmazonServiceException ex)
        {
            return PullResult.Fail(ex.Message.Length > 0 ? ex.Message : ex.ErrorCode ?? "CodeCommit error");
        }
        catch (AmazonClientException ex)
        {
            return PullResult.Fail(ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase)
                ? $"{profile}: credentials expired or missing"
                : "offline: cannot reach CodeCommit");
        }
        catch (HttpRequestException)
        {
            return PullResult.Fail("offline: cannot reach CodeCommit");
        }
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
