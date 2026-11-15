using FairyAI_Android.Services.Notes;

namespace FairyAI_Android.Services;

/// <summary>
/// The notes the user keeps on the phone, for the UI and for the model's <c>note_*</c> tools.
/// Thin on purpose: validation and persistence live in <see cref="NoteStore"/> so they can be
/// exercised without a device.
/// </summary>
public sealed class NoteService
{
    private readonly NoteStore _store;

    public NoteService() : this(Path.Combine(FileSystem.AppDataDirectory, "notes.json"))
    {
    }

    public NoteService(string filePath) => _store = new NoteStore(filePath);

    public string FilePath => _store.FilePath;

    /// <summary>The store itself, for the assistant's note tools — same instance, same in-memory list.</summary>
    public NoteStore Store => _store;

    /// <summary>Set when the file on disk could not be read; the store then started empty.</summary>
    public string? LoadError => _store.LoadError;

    public string? LastError => _store.LastError;

    public int Count => _store.Count;

    public IReadOnlyList<Note> All() => _store.All();

    public Note? Get(string? id) => _store.Get(id);

    public NoteSaveResult Save(string title, string body, IReadOnlyList<string>? tags = null) =>
        _store.Save(new NoteDraft(title, body, tags));

    public bool Delete(string? id) => _store.Delete(id);

    public bool SetPinned(string? id, bool pinned) => _store.SetPinned(id, pinned);

    public IReadOnlyList<NoteHit> Search(string? query, int limit = 10) => _store.Search(query, limit);
}
