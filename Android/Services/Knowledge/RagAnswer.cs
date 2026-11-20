namespace FairyAI_Android.Services.Knowledge;

/// <summary>
/// What a question against the knowledge base produced: the answer, which documents it rests on, and
/// which retriever found them. An answer that cannot name its sources is a guess wearing a knowledge
/// base's clothes, so both are part of the result rather than an afterthought.
/// </summary>
public sealed record RagAnswer(
    bool Ok,
    string Text,
    IReadOnlyList<string> Sources,
    string Retrieval,
    string? Error)
{
    public static RagAnswer Fail(string message) => new(false, message, Array.Empty<string>(), "没有命中", message);
}
