namespace SFW.Services;

/// <summary>
/// SFA/SFM RelativeDateTimeFormatter parity, shared by the profiles list and the
/// dashboard profile row so both describe a subscription's age the same way.
/// </summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset time)
    {
        var elapsed = DateTimeOffset.Now - time;
        if (elapsed < TimeSpan.FromMinutes(1)) return Loc.Get("RelativeTime.UpdatedJustNow");
        if (elapsed < TimeSpan.FromHours(1))
            return string.Format(Loc.Get("RelativeTime.UpdatedMinAgo"), (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1))
            return string.Format(Loc.Get("RelativeTime.UpdatedHAgo"), (int)elapsed.TotalHours);
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
