using FairyAI_Android.Services.Knowledge;
using FairyAI_Android.Services.Notes;
using FairyAI_Android.Services.Screen;
using FairyAI_Android.Services.Tools;
using FairyAI_Android.Services.Vision;

namespace FairyAI_Android.Services;

/// <summary>
/// Builds the tool registry the assistant works with. Everything registered here is a real
/// implementation; a capability that could not be executed does not get an entry.
/// </summary>
public static class ToolCatalog
{
    public static ToolRegistry Create(
        ScreenControl screen,
        ISearchService search,
        TranslateService translate,
        IKnowledgeSearch knowledge,
        NoteStore notes,
        ReminderService reminders,
        Func<string?, Task<VisionOutcome>> lookAtScreen,
        RAGService rag,
        Func<DateTime>? clock = null)
    {
        var registry = new ToolRegistry();

        registry.Register(new CurrentTimeTool());
        registry.Register(new CalculateTool());
        registry.Register(new WebSearchTool(search));
        registry.Register(new TranslateTool(translate));
        registry.Register(new KnowledgeListTool(knowledge));
        registry.Register(new KnowledgeSearchTool(knowledge));

        // Notes and reminders: the only tools that leave something behind on the phone, so every one
        // of them states its id in the answer — the model needs the id to change it later.
        registry.Register(new NoteSaveTool(notes));
        registry.Register(new NoteSearchTool(notes));
        registry.Register(new NoteDeleteTool(notes));

        registry.Register(new ReminderCreateTool(reminders, clock));
        registry.Register(new ReminderListTool(reminders, clock));
        registry.Register(new ReminderCancelTool(reminders));

        // Looking at the screen: the same gate as the other screen tools, plus a vision model.
        registry.Register(new DescribeScreenTool(lookAtScreen));

        // Questions against the imported documents. Retrieval happens here, so the model gets the
        // passages that matter instead of guessing which file to search.
        registry.Register(new AskDocumentsTool(question => rag.AskAsync(question ?? "")));

        // The phone's own screen, one tool per whitelisted capability. Each still needs its grant and
        // a fresh approval at the moment it runs.
        foreach (var capability in ScreenCapabilityCatalog.All)
            registry.Register(new PhoneScreenTool((action, package) => screen.RunAsync(action, package), capability));

        return registry;
    }
}
