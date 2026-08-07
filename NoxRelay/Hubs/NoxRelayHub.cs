using Microsoft.AspNetCore.SignalR;
using NoxRelay.Services;

namespace NoxRelay.Hubs;

/// <summary>
/// Instantiated per call by SignalR — cannot hold state as fields. All real
/// state lives in the injected SessionManager singleton. The sessionId is
/// used directly as a SignalR Group, so we never track socket lists by hand.
/// </summary>
public class NoxRelayHub : Hub
{
    private readonly SessionManager _sessionManager;
    
    public NoxRelayHub(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public async Task JoinQueue(string userId)
    {
        var session = _sessionManager.TryMatch(userId, Context.ConnectionId);
        if (session is null)
        {
            return; // now waiting; "matched" fires when a partner joins
        }

        await Groups.AddToGroupAsync(session.UserA.ConnectionId, session.Id);
        await Groups.AddToGroupAsync(session.UserB.ConnectionId, session.Id);

        await Clients.Group(session.Id).SendAsync("matched", new
        {
            sessionId = session.Id,
            currentTurn = session.CurrentTurn,
        });
    }

    public async Task Keystroke(string diff)
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null) return;

        var applied = _sessionManager.TryApplyKeystroke(session, Context.ConnectionId, diff);
        if (!applied) return; // not this connection's turn — silently rejected

        await Clients.OthersInGroup(session.Id).SendAsync("remote_keystroke", new
        {
            diff,
            fullSentence = session.Sentence
        });
    }

    public async Task PassTurn()
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null) return;
        
        _sessionManager.PassTurn(session);

        await Clients.Group(session.Id).SendAsync("turn_changed", new
        {
            currentTurn = session.CurrentTurn,
        });
    }

    public async Task LeaveSession()
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null) return;
        
        await Groups.RemoveFromGroupAsync(session.UserA.ConnectionId, session.Id);
        await Groups.RemoveFromGroupAsync(session.UserB.ConnectionId, session.Id);
        _sessionManager.RemoveSession(session.Id);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _sessionManager.CancelWaiting(Context.ConnectionId);
        
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null)
        {
            var partner = _sessionManager.GetPartner(session, Context.ConnectionId);
            if (partner is not null)
            {
                await Clients.Client(partner.ConnectionId).SendAsync("partner_disconnected");
            }
            // V0: session dies here outright. Reconnection with grace period
            // is explicitly out of scope per the project doc.
            _sessionManager.RemoveSession(session.Id);
        }
        await base.OnDisconnectedAsync(exception);
    }
}