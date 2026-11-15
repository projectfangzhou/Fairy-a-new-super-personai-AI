"""Restores two blocks of Chinese that were double-encoded (UTF-8 read as GBK, then saved again).

Both blocks are unrecoverable by re-encoding because bytes were lost in the original corruption, so the
text below is reconstructed from the code's own structure and comments. Line numbers are 1-based and
inclusive; BOM and CRLF are preserved exactly.
"""
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent

TOOLFRAMEWORK = """/// <summary>
/// Android Capability Registry — declares all AI capabilities.
/// </summary>
public class CapabilityRegistry
{
    public static string BuildSystemPrompt(AppConfig config)
    {
        return @"你是仙灵，一个功能强大的个人 AI 助手。你拥有以下能力，根据用户需求自主选择工具：

## 核心能力
1. 屏幕自动化：截屏、点击、输入、按键
2. 语音合成：TTS 语音克隆
3. 知识库：文档分析、笔记管理
4. 联网搜索：Web 搜索
5. 系统工具：时间、计算、应用打开
6. 翻译：多语言翻译
7. 提醒：日程管理

## 人格设定
" + config.Personality.SystemPrompt;
    }

    public static string GetCapabilitiesJson(ToolRegistry tools)
    {
        return JsonSerializer.Serialize(new
        {
            name = "仙灵",
            version = "2.0.0",
            tools = tools.GetAll().Select(t => new { t.Name, t.Description })
        });
    }
}"""

IMPORTANT_INFO = """/// <summary>Important info extraction service — regex-based date/address extraction.</summary>
public class ImportantInfoService
{
    private readonly KnowledgeBaseService _kb;
    private static readonly string LogPath = Path.Combine(FileSystem.AppDataDirectory, "fairy.log");

    public ImportantInfoService(KnowledgeBaseService kb) => _kb = kb;

    /// <summary>Extract and save important info (dates, addresses, phone numbers).</summary>
    public async Task<string> ExtractAndSaveAsync(string text)
    {
        var extracted = new List<string>();

        // Extract dates
        var datePattern = @"(\\d{4}[-/年]\\d{1,2}[-/月]\\d{1,2}[日号]?)|(\\d{1,2}[-/月]\\d{1,2}[日号]?)|((今天|明天|后天|昨天|前天|周[一二三四五六日天]|星期[一二三四五六日天])\\s*(上午|下午|晚上|中午)?)";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, datePattern))
            extracted.Add($"日期: {m.Value}");

        // Extract phone numbers
        var phonePattern = @"(1[3-9]\\d{9})|(\\d{3,4}-\\d{7,8})";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, phonePattern))
            extracted.Add($"电话: {m.Value}");

        // Extract addresses
        var addrPattern = @"([\\u4e00-\\u9fa5]{2,}(?:省|市|区|县|路|街|道|巷|号|楼|室))";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, addrPattern))
            extracted.Add($"地址: {m.Value}");

        if (extracted.Count > 0)
        {
            var content = string.Join("\\n", extracted);
            await _kb.SaveImportantInfoAsync("auto_extracted", content);
            Log($"Extracted {extracted.Count} items");
            return content;
        }

        return "No important info found";
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] [INFO] {msg}\\n"); } catch { }
    }
}"""

TARGETS = [
    ("Services/ToolFramework.cs", 166, 194, TOOLFRAMEWORK, "CapabilityRegistry"),
    ("Services/AndroidRealServices.cs", 245, 288, IMPORTANT_INFO, "ImportantInfoService"),
]


def apply(relative, first, last, block, marker):
    path = ROOT / relative
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    body = raw[3:] if bom else raw
    text = body.decode("utf-8", "strict")

    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.split(newline)

    assert lines[first - 1].lstrip().startswith("///"), f"{relative}:{first} is not the doc comment"
    assert lines[last - 1] == "}", f"{relative}:{last} is not the class terminator"
    assert marker in "\n".join(lines[first - 1:last]), f"{relative}: marker {marker} missing in range"

    replacement = block.split("\n")
    if newline != "\n":
        replacement = block.split("\n")

    updated = lines[:first - 1] + replacement + lines[last:]
    new_text = newline.join(updated)

    # Nothing double-encoded may survive in this file.
    import re
    run = re.compile(r"[\u4e00-\u9fff\uff00-\uffef\u3000-\u303f]{3,}")
    for match in run.finditer(new_text):
        try:
            recovered = match.group(0).encode("gbk").decode("utf-8")
        except (UnicodeEncodeError, UnicodeDecodeError):
            continue
        raise SystemExit(f"{relative}: still double-encoded: {recovered[:40]}")

    candidate = (b"\xef\xbb\xbf" if bom else b"") + new_text.encode("utf-8")

    # The same corruption also turned em dashes into 鈥 followed by a lost byte.
    em_dash_before = candidate.count("鈥".encode("utf-8") + b"?")
    candidate = candidate.replace("鈥".encode("utf-8") + b"?", "—".encode("utf-8"))

    path.write_bytes(candidate)
    print(f"{relative}: replaced lines {first}-{last}, bom={bom}, "
          f"newline={'CRLF' if newline == chr(13) + chr(10) else 'LF'}, em-dashes restored={em_dash_before}")


for target in TARGETS:
    apply(*target)
