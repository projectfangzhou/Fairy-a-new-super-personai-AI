namespace MyAiAssistant.Services;

/// <summary>A phone that holds a token for the summary endpoint.</summary>
public sealed record PairedDeviceView(string DeviceName);

/// <summary>
/// What the control centre shows about sync with a paired phone: whether the door is open, which
/// devices hold a key to it, and why the last attempt failed (if it did).
///
/// The three switches are separate on purpose — reading task titles, answering approvals from the
/// phone, and exchanging the user's own notes are three different permissions.
/// </summary>
public sealed record SummarySyncView(
    bool Enabled,
    bool PairControlEnabled,
    bool NotesSyncEnabled,
    int PairedDeviceCount,
    IReadOnlyList<PairedDeviceView> Devices,
    string? LastError);

public sealed record SummarySyncActionResult(bool Ok, string Message);

/// <summary>
/// The sync settings as the dashboard may change them. An interface so the API can be tested
/// without touching the real configuration file or the real token store.
/// </summary>
public interface ISummarySyncControl
{
    SummarySyncView Read();

    SummarySyncActionResult Apply(bool enabled);

    /// <summary>
    /// A separate switch: letting the phone answer approvals is a bigger permission than letting it
    /// read summaries, so the two are never toggled together.
    /// </summary>
    SummarySyncActionResult ApplyPairControl(bool enabled);

    /// <summary>
    /// Note sync — a third switch. Notes are the user's own writing, so sharing them is not implied
    /// by sharing task summaries.
    /// </summary>
    SummarySyncActionResult ApplyNotes(bool enabled);

    SummarySyncActionResult Revoke(string deviceName);
}
