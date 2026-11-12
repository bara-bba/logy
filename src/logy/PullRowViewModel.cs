using Logy.Core.Graph;
using Logy.Core.Pulls;

namespace Logy;

/// <summary>One row of a panel's pull request view.</summary>
public sealed class PullRowViewModel
{
    public PullRowViewModel(PullRequest pr, DateTimeOffset now)
    {
        Pull = pr;
        Number = "#" + pr.Id;
        Title = (pr.IsDraft ? "[draft] " : "") + pr.Title;
        Age = pr.Updated == DateTimeOffset.MinValue ? "" : RelativeTime.Format(pr.Updated.ToUnixTimeSeconds(), now);
        var branches = pr.SourceBranch.Length > 0 ? $"{pr.SourceBranch} → {pr.TargetBranch}" : "";
        Line2 = string.Join(" · ", new[] { pr.Author, branches }.Where(x => x.Length > 0));
        Tooltip = $"{pr.Title}\n{Line2}\nupdated {pr.Updated.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    public PullRequest Pull { get; }

    public string Number { get; }

    public string Title { get; }

    public string Age { get; }

    public string Line2 { get; }

    public string Tooltip { get; }
}
