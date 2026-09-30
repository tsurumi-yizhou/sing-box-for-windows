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
        if (elapsed < TimeSpan.FromMinutes(1)) return Loc.Get("Updated just now", "刚刚更新");
        if (elapsed < TimeSpan.FromHours(1))
            return string.Format(Loc.Get("Updated {0} min ago", "{0} 分钟前更新"), (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1))
            return string.Format(Loc.Get("Updated {0} h ago", "{0} 小时前更新"), (int)elapsed.TotalHours);
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
