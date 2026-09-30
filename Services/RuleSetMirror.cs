using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFW.Services;

/// <summary>
/// Remote rule-sets are downloaded by the core over a direct connection at
/// service start (the proxy/TUN is not up yet), resolved through the config's
/// own DNS module — not the system resolver. Configs whose final DNS answers
/// GitHub with unusable IPs (e.g. CNNIC 1.2.4.8 returning stale/poisoned
/// answers for raw.githubusercontent.com) make every start fail. Remote
/// rule-set URLs pointing at raw.githubusercontent.com are therefore rewritten
/// to the jsDelivr GitHub mirror (byte-identical content over HTTPS) for the
/// start only — the stored profile is untouched and downloaded rule-sets are
/// persisted by the core cache (cache.db).
/// </summary>
public static class RuleSetMirror
{
    private const string GithubRawPrefix = "https://raw.githubusercontent.com/";
    private const string MirrorPrefix = "https://cdn.jsdelivr.net/gh";

    public static string RewriteGithubRuleSetUrls(string configContent, Action<string> log)
    {
        if (!configContent.Contains(GithubRawPrefix, StringComparison.Ordinal)) return configContent;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(configContent);
        }
        catch (JsonException)
        {
            return configContent;
        }

        if (root?["route"]?["rule_set"] is not JsonArray ruleSets) return configContent;

        var rewritten = 0;
        foreach (var node in ruleSets)
        {
            if (node is not JsonObject ruleSet) continue;
            if (ruleSet["type"]?.GetValue<string>() != "remote") continue;
            var rewrittenUrl = Rewrite(ruleSet["download_url"]?.GetValue<string>());
            if (rewrittenUrl is null) continue;
            ruleSet["download_url"] = rewrittenUrl;
            rewritten++;
        }
        if (rewritten == 0) return configContent;

        log(string.Format(Loc.Get(
            "{0} rule-set URL(s) rewritten to the jsDelivr mirror for this start.",
            "本次启动已将 {0} 个规则集 URL 改写至 jsDelivr 镜像。"), rewritten));
        return root!.ToJsonString();

        static string? Rewrite(string? url)
        {
            if (url is null || !url.StartsWith(GithubRawPrefix, StringComparison.Ordinal)) return null;
            // https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
            // → {mirror}/{owner}/{repo}@{branch}/{path}
            var parts = url[GithubRawPrefix.Length..].Split('/', 4);
            return parts.Length < 4 ? null : $"{MirrorPrefix}/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}";
        }
    }
}
