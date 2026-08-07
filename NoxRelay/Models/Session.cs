namespace NoxRelay.Models;

/// <summary>
/// Live in-memory session state. Lives only for the duration of the
/// connection — nothing here is persisted, matching the decision in the
/// project doc that session/turn state is not durable in V0.
/// </summary>
public class Session
{
    public required string Id { get; set; }
    public required Participant UserA { get; set; }
    public required Participant UserB { get; set; }
    public string CurrentTurn { get; set; } = "A"; // "A" or "B"
    public string Sentence  { get; set; } = string.Empty;
    public int CursorPosition { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Participant
{
    public required string UserId { get; set; }
    public required string ConnectionId { get; set; }
}