using MyAiAssistant.Models;

namespace MyAiAssistant.Services.Messaging;

/// <summary>
/// Turns a conversation plus a new prompt into a stream of reply chunks.
///
/// The web chat socket only needs this much of the assistant, and keeping it this small is what
/// lets the socket, the protocol and the session handling be tested without a model, a network, or
/// an API key. <see cref="LlmChatResponder"/> is the production implementation.
/// </summary>
public interface IChatResponder
{
    IAsyncEnumerable<string> RespondAsync(
        IReadOnlyList<ChatMessage> history,
        string prompt,
        CancellationToken cancellationToken = default);
}
