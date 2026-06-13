namespace MediaBot.Configuration;

public class BotConfig
{
    public required string Token { get; set; }
    public List<long> AllowedChatIds { get; set; } = [];
    public string InviteCode { get; set; } = "";
    public string GuestInviteCode { get; set; } = "";
}

public class QBittorrentConfig
{
    public string BaseUrl { get; set; } = "http://qbittorrent:8080";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "adminadmin";
}

public class JellyfinConfig
{
    public string BaseUrl { get; set; } = "http://jellyfin:8096";
    public string ApiKey { get; set; } = "";
    public string PublicUrl { get; set; } = "";
}

public class JackettConfig
{
    public string BaseUrl { get; set; } = "http://jackett:9117";
    public string ApiKey { get; set; } = "";
}

public class SearchConfig
{
    // Results larger than this are hidden (0 = no limit)
    public double MaxFileSizeGb { get; set; } = 10.0;

    // Title fragments to exclude (case-insensitive), comma-separated
    public string ExcludeKeywords { get; set; } = "remux,bdremux";

    public long MaxFileSizeBytes => MaxFileSizeGb > 0
        ? (long)(MaxFileSizeGb * 1024 * 1024 * 1024)
        : long.MaxValue;

    public IReadOnlyList<string> ExcludeKeywordList =>
        ExcludeKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
