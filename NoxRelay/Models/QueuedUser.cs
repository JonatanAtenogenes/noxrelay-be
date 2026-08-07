namespace NoxRelay.Models;

public class QueuedUser
{
    public required  string UserId { get; set; }
    public required string ConnectionId { get; set; }
}