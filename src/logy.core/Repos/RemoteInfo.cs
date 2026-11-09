namespace Logy.Core.Repos;

public enum RemoteKind
{
    None,
    GitHub,
    CodeCommit,
    Other,
}

/// <summary>Where a repo's origin lives. <see cref="Profile"/> is the AWS profile of a codecommit:: URL.</summary>
public sealed record RemoteInfo(RemoteKind Kind, string? Url, string? Profile)
{
    public static readonly RemoteInfo None = new(RemoteKind.None, null, null);

    /// <summary>Short label for the panel header.</summary>
    public string Chip => Kind switch
    {
        RemoteKind.GitHub => "GH",
        RemoteKind.CodeCommit => "CC",
        RemoteKind.Other => "GIT",
        _ => "",
    };

    public bool HasRemote => Kind != RemoteKind.None;

    public static RemoteInfo Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return None;
        }

        url = url.Trim();
        if (url.StartsWith("codecommit:", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteInfo(RemoteKind.CodeCommit, url, CodeCommitProfile(url));
        }

        if (url.Contains("git-codecommit.", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteInfo(RemoteKind.CodeCommit, url, null);
        }

        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteInfo(RemoteKind.GitHub, url, null);
        }

        return new RemoteInfo(RemoteKind.Other, url, null);
    }

    /// <summary>
    /// codecommit::eu-west-1://admin-barbara@repo and codecommit://admin-barbara@repo both carry
    /// the profile between "//" and "@". No "@" means the default profile.
    /// </summary>
    private static string? CodeCommitProfile(string url)
    {
        var slashes = url.IndexOf("//", StringComparison.Ordinal);
        var at = url.IndexOf('@', slashes < 0 ? 0 : slashes);
        if (slashes < 0 || at < 0)
        {
            return null;
        }

        var profile = url[(slashes + 2)..at];
        return profile.Length > 0 ? profile : null;
    }
}
