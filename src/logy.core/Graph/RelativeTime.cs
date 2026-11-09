namespace Logy.Core.Graph;

public static class RelativeTime
{
    /// <summary>"now", "5m", "3h", "2d", "4w", or the local date yyyy-MM-dd from 52 weeks on.</summary>
    public static string Format(long unixTime, DateTimeOffset now)
    {
        var then = DateTimeOffset.FromUnixTimeSeconds(unixTime);
        var age = now - then;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays}d";
        }

        if (age < TimeSpan.FromDays(7 * 52))
        {
            return $"{(int)(age.TotalDays / 7)}w";
        }

        return then.ToLocalTime().ToString("yyyy-MM-dd");
    }
}
