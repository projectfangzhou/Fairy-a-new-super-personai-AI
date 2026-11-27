using System.Runtime.CompilerServices;
using MyAiAssistant.Models;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// The real assistant: delegates to <see cref="ILlmService"/>, which already handles provider
/// selection, fallback to the backup model, and streaming.
///
/// The service is injected rather than constructed here so that the socket's tests do not have to
/// compile (or configure) the whole LLM stack.
/// </summary>
public sealed class LlmChatResponder : IChatResponder
{
    private readonly ILlmService _llm;

    public LlmChatResponder(ILlmService llm) => _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    public async IAsyncEnumerable<string> RespondAsync(
        IReadOnlyList<ChatMessage> history,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _llm
                           .StreamChatWithFallbackAsync(history, prompt, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}
