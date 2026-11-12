using System.Text.Json;
using Logy.Core.Repos;

namespace Logy.Core.Pulls;

/// <summary>One open pull request, as shown in a panel's pull request view.</summary>
public sealed record PullRequest(
    string Id,
    string Title,
    string Author,
    string SourceBranch,
    string TargetBranch,
    DateTimeOffset Updated,
    string WebUrl,
    bool IsDraft = false,
    string Description = "",
    string? RevisionId = null,
    string? SourceCommit = null);

/// <summary>How a pull request is merged. GitHub offers Merge/Squash/Rebase, CodeCommit FastForward/Squash/Merge.</summary>
public enum MergeMethod
{
    Merge,
    Squash,
    Rebase,
    FastForward,
}

public static class MergeMethods
{
    /// <summary>The methods a host supports, default first.</summary>
    public static IReadOnlyList<MergeMethod> For(RemoteKind kind) => kind == RemoteKind.GitHub
        ? [MergeMethod.Merge, MergeMethod.Squash, MergeMethod.Rebase]
        : [MergeMethod.FastForward, MergeMethod.Squash, MergeMethod.Merge];

    public static string Label(MergeMethod m) => m switch
    {
        MergeMethod.Merge => "merge commit",
        MergeMethod.Squash => "squash",
        MergeMethod.Rebase => "rebase",
        _ => "fast-forward",
    };

    /// <summary>GitHub's <c>merge_method</c> value.</summary>
    public static string GitHubName(MergeMethod m) => m switch
    {
        MergeMethod.Squash => "squash",
        MergeMethod.Rebase => "rebase",
        _ => "merge",
    };
}

/// <summary>Where a repo's pull requests live: GitHub owner/name, or CodeCommit region/name/profile.</summary>
public sealed record PullSource(RemoteKind Kind, string Owner, string Name, string? Region, string? Profile)
{
    /// <summary>
    /// https://github.com/o/r(.git), git@github.com:o/r.git, codecommit::region://profile@repo,
    /// codecommit://profile@repo, https://git-codecommit.region.amazonaws.com/v1/repos/repo. Else null.
    /// </summary>
    public static PullSource? From(RemoteInfo remote)
    {
        var url = remote.Url?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (remote.Kind == RemoteKind.GitHub)
        {
            var i = url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
            var path = url[(i + "github.com".Length)..].TrimStart(':', '/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }

            var parts = path.Trim('/').Split('/');
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? new PullSource(RemoteKind.GitHub, parts[0], parts[1], null, null)
                : null;
        }

        if (remote.Kind != RemoteKind.CodeCommit)
        {
            return null;
        }

        if (url.StartsWith("codecommit:", StringComparison.OrdinalIgnoreCase))
        {
            // codecommit::eu-west-1://profile@repo  or  codecommit://profile@repo
            var rest = url["codecommit:".Length..];
            string? region = null;
            if (rest.StartsWith(':'))
            {
                var end = rest.IndexOf("://", StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                region = rest[1..end];
                rest = rest[end..];
            }

            rest = rest.TrimStart(':', '/');
            var at = rest.IndexOf('@');
            var name = at >= 0 ? rest[(at + 1)..] : rest;
            return name.Length > 0 ? new PullSource(RemoteKind.CodeCommit, "", name, region, remote.Profile) : null;
        }

        // https://git-codecommit.<region>.amazonaws.com/v1/repos/<name>
        var host = url.IndexOf("git-codecommit.", StringComparison.OrdinalIgnoreCase);
        var dot = url.IndexOf('.', host + "git-codecommit.".Length);
        var repos = url.IndexOf("/v1/repos/", StringComparison.OrdinalIgnoreCase);
        if (host < 0 || dot < 0 || repos < 0)
        {
            return null;
        }

        var reg = url[(host + "git-codecommit.".Length)..dot];
        var repo = url[(repos + "/v1/repos/".Length)..].Trim('/');
        return repo.Length > 0 ? new PullSource(RemoteKind.CodeCommit, "", repo, reg, null) : null;
    }

    public string CodeCommitConsoleUrl(string id) =>
        $"https://{Region}.console.aws.amazon.com/codesuite/codecommit/repositories/{Uri.EscapeDataString(Name)}/pull-requests/{Uri.EscapeDataString(id)}/details?region={Region}";
}

/// <summary>Parses the GitHub REST <c>GET /repos/{o}/{r}/pulls</c> response.</summary>
public static class GitHubPullParser
{
    public static List<PullRequest> Parse(string json)
    {
        var list = new List<PullRequest>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            list.Add(FromElement(pr));
        }

        return list;
    }

    /// <summary>One pull request object, e.g. the response of <c>POST /repos/{o}/{r}/pulls</c>.</summary>
    public static PullRequest? ParseOne(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? FromElement(doc.RootElement) : null;
    }

    private static PullRequest FromElement(JsonElement pr)
    {
        static string S(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var number = pr.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32().ToString() : "";
        var author = pr.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? S(u, "login") : "";
        var head = pr.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.Object ? S(h, "ref") : "";
        var headSha = h.ValueKind == JsonValueKind.Object ? S(h, "sha") : "";
        var baseRef = pr.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object ? S(b, "ref") : "";
        var updated = DateTimeOffset.TryParse(S(pr, "updated_at"), out var t) ? t : DateTimeOffset.MinValue;
        var draft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        return new PullRequest(number, S(pr, "title"), author, head, baseRef, updated, S(pr, "html_url"), draft,
            S(pr, "body"), null, headSha.Length > 0 ? headSha : null);
    }

    /// <summary>
    /// Short message for a failed GitHub call: the JSON "message" plus any "errors[].message",
    /// mapped to the common cases (own PR, duplicate PR, nothing to merge, conflicts).
    /// </summary>
    public static string Friendly(int status, string body)
    {
        var text = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    parts.Add(m.GetString() ?? "");
                }

                if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in errs.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(em.GetString() ?? "");
                        }
                        else if (e.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(e.GetString() ?? "");
                        }
                    }
                }

                text = string.Join(": ", parts.Where(p => p.Length > 0));
            }
        }
        catch (JsonException)
        {
        }

        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("approve your own"))
        {
            return "you can't approve your own pull request";
        }

        if (Has("pull request already exists"))
        {
            return "a pull request for this branch already exists";
        }

        if (Has("No commits between"))
        {
            return "no changes between the branches";
        }

        if (Has("not mergeable") || status == 405)
        {
            return text.Length > 0 && !Has("not mergeable") ? text : "not mergeable: resolve conflicts in a terminal";
        }

        if (Has("Head branch was modified") || status == 409)
        {
            return "the branch changed: refresh and try again";
        }

        if (status is 401 or 403)
        {
            return "GitHub: authentication failed or no permission";
        }

        if (status == 404)
        {
            return "not found on GitHub (no access?)";
        }

        return text.Length > 0 ? text : $"GitHub returned {status}";
    }
}
