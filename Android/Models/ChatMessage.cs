namespace FairyAI_Android.Models;

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Which side of the conversation this bubble belongs to. The chat template shows one or the other.</summary>
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => !IsUser;
}
