using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class NodeRegionHelper
{
    private static readonly (string[] Keywords, string Label)[] RegionRules =
    [
        (["香港", "HK", "Hong Kong", "HongKong", "🇭🇰"], "香港"),
        (["台湾", "台灣", "TW", "Taiwan", "🇹🇼"], "台湾"),
        (["澳门", "澳門", "MO", "Macao", "Macau", "🇲🇴"], "澳门"),
        (["日本", "JP", "Japan", "东京", "東京", "大阪", "🇯🇵"], "日本"),
        (["韩国", "韓國", "KR", "Korea", "首尔", "首爾", "🇰🇷"], "韩国"),
        (["新加坡", "SG", "Singapore", "🇸🇬"], "新加坡"),
        (["美国", "美國", "US", "USA", "United States", "洛杉矶", "洛杉磯", "纽约", "紐約", "🇺🇸"], "美国"),
        (["加拿大", "CA", "Canada", "🇨🇦"], "加拿大"),
        (["英国", "英國", "UK", "Britain", "London", "伦敦", "倫敦", "🇬🇧"], "英国"),
        (["德国", "德國", "DE", "Germany", "法兰克福", "法蘭克福", "🇩🇪"], "德国"),
        (["法国", "法國", "FR", "France", "巴黎", "🇫🇷"], "法国"),
        (["荷兰", "荷蘭", "NL", "Netherlands", "阿姆斯特丹", "🇳🇱"], "荷兰"),
        (["俄罗斯", "俄羅斯", "RU", "Russia", "莫斯科", "🇷🇺"], "俄罗斯"),
        (["土耳其", "TR", "Turkey", "伊斯坦布尔", "🇹🇷"], "土耳其"),
        (["印度", "IN", "India", "🇮🇳"], "印度"),
        (["澳大利亚", "AU", "Australia", "悉尼", "墨尔本", "🇦🇺"], "澳大利亚"),
        (["泰国", "TH", "Thailand", "曼谷", "🇹🇭"], "泰国"),
        (["越南", "VN", "Vietnam", "🇻🇳"], "越南"),
        (["马来西亚", "MY", "Malaysia", "吉隆坡", "🇲🇾"], "马来西亚"),
        (["印尼", "印度尼西亚", "ID", "Indonesia", "🇮🇩"], "印尼"),
        (["菲律宾", "PH", "Philippines", "🇵🇭"], "菲律宾"),
        (["中国大陆", "中国", "CN", "China", "北京", "上海", "广州", "廣州", "深圳", "🇨🇳"], "中国")
    ];

    private static readonly (string[] Tokens, string Label)[] DomainRules =
    [
        ([".hk", "-hk.", "-hk-", "_hk_", "hongkong"], "香港"),
        ([".tw", "-tw.", "-tw-", "_tw_", "taiwan"], "台湾"),
        ([".mo", "macau", "macao"], "澳门"),
        ([".jp", "-jp.", "-jp-", "_jp_", ".japan"], "日本"),
        ([".kr", "-kr.", "-kr-", "_kr_", "korea"], "韩国"),
        ([".sg", "-sg.", "-sg-", "_sg_", "singapore"], "新加坡"),
        ([".us", "-us.", "-us-", "_us_", "unitedstates", "america"], "美国"),
        ([".ca", "-ca.", "canada"], "加拿大"),
        ([".uk", ".gb", "-uk.", "britain", "london"], "英国"),
        ([".de", "-de.", "germany", "frankfurt"], "德国"),
        ([".fr", "-fr.", "france", "paris"], "法国"),
        ([".nl", "-nl.", "netherlands", "amsterdam"], "荷兰"),
        ([".ru", "-ru.", "russia", "moscow"], "俄罗斯"),
        ([".tr", "-tr.", "turkey", "istanbul"], "土耳其"),
        ([".in", "-in.", "india"], "印度"),
        ([".au", "-au.", "australia", "sydney"], "澳大利亚"),
        ([".th", "-th.", "thailand", "bangkok"], "泰国"),
        ([".vn", "-vn.", "vietnam"], "越南"),
        ([".my", "-my.", "malaysia"], "马来西亚"),
        ([".id", "-id.", "indonesia"], "印尼"),
        ([".ph", "-ph.", "philippines"], "菲律宾"),
        ([".cn", "-cn.", "china", "beijing", "shanghai", "guangzhou", "shenzhen"], "中国")
    ];

    public static string Resolve(VmessProfile profile)
    {
        var source = $"{profile.Name} {profile.Remark} {profile.SubscriptionName} {profile.Address} {profile.Host} {profile.Sni}".Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return "-";
        }

        foreach (var (keywords, label) in RegionRules)
        {
            foreach (var keyword in keywords)
            {
                if (ContainsToken(source, keyword))
                {
                    return label;
                }
            }
        }

        var domainSource = source.ToLowerInvariant();
        foreach (var (tokens, label) in DomainRules)
        {
            foreach (var token in tokens)
            {
                if (domainSource.Contains(token, StringComparison.Ordinal))
                {
                    return label;
                }
            }
        }

        return "-";
    }

    private static bool ContainsToken(string source, string keyword)
    {
        if (keyword.Length is 2 or 3 && keyword.All(char.IsAsciiLetterUpper))
        {
            return source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || source.Contains($"[{keyword}]", StringComparison.OrdinalIgnoreCase)
                || source.Contains($"({keyword})", StringComparison.OrdinalIgnoreCase)
                || source.Contains($" {keyword} ", StringComparison.OrdinalIgnoreCase)
                || source.Contains($"-{keyword}-", StringComparison.OrdinalIgnoreCase)
                || source.Contains($"_{keyword}_", StringComparison.OrdinalIgnoreCase);
        }

        return source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
