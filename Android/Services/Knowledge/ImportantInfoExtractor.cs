using System.Text.RegularExpressions;

namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// Pulls dates, phone numbers and addresses out of a piece of text.
///
/// Pure and separate from the service that saves the results, because the patterns are the part worth
/// asserting on. Chinese date words (今天/明天/星期…) are part of the date pattern: screen text and
/// chat answers mostly say "明天下午" rather than "2026-11-15". The original copy of this file had lost
/// bytes, so the patterns were rewritten from what the code was clearly trying to do.
/// </summary>
public static class ImportantInfoExtractor
{
    private const string DatePattern =
        @"(\d{4}[-/年]\d{1,2}[-/月]\d{1,2}[日号]?)|(\d{1,2}[-/月]\d{1,2}[日号]?)|" +
        @"((今天|明天|后天|昨天|前天|周[一二三四五六日天]|星期[一二三四五六日天])\s*(上午|下午|晚上|中午)?)";

    private const string PhonePattern = @"(1[3-9]\d{9})|(\d{3,4}-\d{7,8})";

    private const string AddressPattern = @"([\u4e00-\u9fa5]{2,}(?:省|市|区|县|路|街|道|巷|号|楼|室))";

    /// <summary>Every match, labelled, in the order it appears. Duplicates are dropped.</summary>
    public static List<string> Extract(string? text)
    {
        var extracted = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return extracted;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Collect(string pattern, string label)
        {
            foreach (Match match in Regex.Matches(text, pattern))
            {
                var value = match.Value.Trim();
                if (value.Length == 0) continue;

                var entry = $"{label}: {value}";
                if (seen.Add(entry)) extracted.Add(entry);
            }
        }

        Collect(DatePattern, "日期");
        Collect(PhonePattern, "电话");
        Collect(AddressPattern, "地址");

        return extracted;
    }
}
