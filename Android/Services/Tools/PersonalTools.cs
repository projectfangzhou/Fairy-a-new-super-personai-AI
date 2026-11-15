using System.Text.Json.Nodes;
using FairyAI_Android.Services.Knowledge;
using FairyAI_Android.Services.Notes;
using FairyAI_Android.Services.Reminders;

namespace FairyAI_Android.Services.Tools;

/// <summary>Writes a note. The only path by which the model may create something that persists.</summary>
public sealed class NoteSaveTool : ITool
{
    private readonly NoteStore _notes;

    public NoteSaveTool(NoteStore notes) => _notes = notes ?? throw new ArgumentNullException(nameof(notes));

    public string Name => "note_save";

    public string Description => "把一段内容存成笔记，返回笔记编号";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "title", Type = "string", Description = "标题，可以留空由内容推出来", Required = false },
        new() { Name = "body", Type = "string", Description = "笔记正文" },
        new() { Name = "tags", Type = "string", Description = "标签，用逗号分隔，可留空", Required = false },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var title = args?["title"]?.GetValue<string>();
        var body = args?["body"]?.GetValue<string>();
        var tags = SplitTags(args?["tags"]?.GetValue<string>());

        var result = _notes.Save(new NoteDraft(title ?? "", body ?? "", tags));
        return Task.FromResult(result.Ok && result.Note is not null
            ? $"{result.Message}（编号 {result.Note.Id}）"
            : "没能保存：" + result.Message);
    }

    internal static List<string> SplitTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        return raw.Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => tag.Length > 0)
            .ToList();
    }
}

/// <summary>Searches notes by keyword and answers with the matching lines, not whole notes.</summary>
public sealed class NoteSearchTool : ITool
{
    private const int Limit = 5;

    private readonly NoteStore _notes;

    public NoteSearchTool(NoteStore notes) => _notes = notes ?? throw new ArgumentNullException(nameof(notes));

    public string Name => "note_search";

    public string Description => "按关键词搜索笔记，返回最相关的几条（带编号）";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "query", Type = "string", Description = "关键词，多个词用空格分开", Required = false },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var query = args?["query"]?.GetValue<string>();
        var hits = _notes.Search(query, Limit);

        if (hits.Count == 0)
        {
            return Task.FromResult(_notes.Count == 0
                ? "还没有任何笔记"
                : $"没有找到包含「{query}」的笔记（共有 {_notes.Count} 条）");
        }

        var lines = hits.Select(hit =>
            $"- [{hit.Note.Id}] {hit.Note.Title}：{hit.Snippet}");

        return Task.FromResult($"找到 {hits.Count} 条：\n" + string.Join("\n", lines));
    }
}

/// <summary>Removes a note by its id, which the model only gets from a list or search result.</summary>
public sealed class NoteDeleteTool : ITool
{
    private readonly NoteStore _notes;

    public NoteDeleteTool(NoteStore notes) => _notes = notes ?? throw new ArgumentNullException(nameof(notes));

    public string Name => "note_delete";

    public string Description => "按编号删除一条笔记";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "id", Type = "string", Description = "笔记编号，例如 n1a2b3c4d5e6" },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var id = args?["id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult("没有给出笔记编号");

        var note = _notes.Get(id);
        if (note is null) return Task.FromResult($"没有编号为 {id} 的笔记");

        return Task.FromResult(_notes.Delete(id)
            ? $"已删除《{note.Title}》"
            : "删除没有生效：" + (_notes.LastError ?? "未知原因"));
    }
}

/// <summary>
/// Creates a reminder. Goes through <see cref="ReminderService"/> rather than the store, so the
/// platform alarm is scheduled in the same step — a reminder that only exists in the file would not
/// wake the phone up.
/// </summary>
public sealed class ReminderCreateTool : ITool
{
    private readonly ReminderService _reminders;
    private readonly Func<DateTime> _clock;

    public ReminderCreateTool(ReminderService reminders, Func<DateTime>? clock = null)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? (() => DateTime.Now);
    }

    public string Name => "reminder_create";

    public string Description => "设定一条提醒；时间可以说 +30m / +2h / +1d，或者本地时间 2026-11-20 09:30 / 09:30";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "title", Type = "string", Description = "提醒内容" },
        new() { Name = "due", Type = "string", Description = "什么时候提醒：+30m、+2h、+1d 或 2026-11-20 09:30" },
        new() { Name = "repeat", Type = "string", Description = "none（默认）/ daily / weekly / monthly", Required = false },
        new() { Name = "note", Type = "string", Description = "备注，可留空", Required = false },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var title = args?["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult("没有给出提醒内容");

        var dueText = args?["due"]?.GetValue<string>();
        var repeatText = args?["repeat"]?.GetValue<string>();

        var repeat = ReminderSchedule.ParseRepeat(repeatText);
        if (repeat is null) return Task.FromResult($"看不懂的重复方式「{repeatText}」，可用 none / daily / weekly / monthly");

        var now = _clock();
        var due = ReminderSchedule.ParseDue(dueText, now);
        if (due is null)
            return Task.FromResult($"看不懂的时间「{dueText}」，可以用 +30m、+2h、+1d 或 2026-11-20 09:30");

        var (ok, message, reminder) = _reminders.Add(new ReminderDraft(title, due.Value, repeat.Value)
        {
            Note = args?["note"]?.GetValue<string>() ?? "",
        });

        return Task.FromResult(ok && reminder is not null ? $"{message}（编号 {reminder.Id}）" : "没能设定：" + message);
    }
}

/// <summary>Lists what is still scheduled, with ids so a later call can cancel one.</summary>
public sealed class ReminderListTool : ITool
{
    private const int Limit = 20;

    private readonly ReminderService _reminders;
    private readonly Func<DateTime> _clock;

    public ReminderListTool(ReminderService reminders, Func<DateTime>? clock = null)
    {
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _clock = clock ?? (() => DateTime.Now);
    }

    public string Name => "reminder_list";

    public string Description => "列出还没到时间的提醒";

    public List<ToolParameter> Parameters => new();

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var now = _clock();
        var pending = _reminders.All().Where(item => item.Enabled).Take(Limit).ToList();

        if (pending.Count == 0) return Task.FromResult("现在没有待提醒的事项");

        var lines = pending.Select(item =>
            $"- [{item.Id}] {item.Title} · {ReminderSchedule.DescribeWhen(item.DueAt, now)} · {ReminderSchedule.DescribeRepeat(item.Repeat)}");

        return Task.FromResult($"共 {pending.Count} 条：\n" + string.Join("\n", lines));
    }
}

/// <summary>Cancels one reminder.</summary>
public sealed class ReminderCancelTool : ITool
{
    private readonly ReminderService _reminders;

    public ReminderCancelTool(ReminderService reminders) =>
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));

    public string Name => "reminder_cancel";

    public string Description => "按编号取消一条提醒";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "id", Type = "string", Description = "提醒编号，例如 r1a2b3c4d5e6" },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var id = args?["id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult("没有给出提醒编号");

        var reminder = _reminders.All().FirstOrDefault(item => item.Id == id);
        if (reminder is null) return Task.FromResult($"没有编号为 {id} 的提醒");

        return Task.FromResult(_reminders.Cancel(id) ? $"已取消提醒「{reminder.Title}」" : "取消失败");
    }
}

/// <summary>
/// Answers from the imported documents. This is what makes the knowledge base more than a folder:
/// the model gets back the passage that matched, with the document name to cite.
/// </summary>
public sealed class KnowledgeSearchTool : ITool
{
    private readonly IKnowledgeSearch _knowledge;

    public KnowledgeSearchTool(IKnowledgeSearch knowledge) =>
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));

    public string Name => "knowledge_search";

    public string Description => "在已导入知识库的文档里按关键词查找，返回命中的段落";

    public List<ToolParameter> Parameters => new()
    {
        new() { Name = "query", Type = "string", Description = "关键词" },
    };

    public Task<string> ExecuteAsync(JsonObject args)
    {
        var query = args?["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult("没有给出查询词");

        var hits = _knowledge.Search(query, 3);
        if (hits.Count == 0)
        {
            return Task.FromResult(_knowledge.Documents.Count == 0
                ? "知识库里还没有文档"
                : $"知识库里没有和「{query}」相关的内容");
        }

        var lines = hits.Select(hit => $"《{hit.Document}》：{hit.Passage}");
        return Task.FromResult(string.Join("\n\n", lines));
    }
}
