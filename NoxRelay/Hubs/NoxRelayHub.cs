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
    private readonly ILogger<NoxRelayHub> _logger;
    
    public NoxRelayHub(SessionManager sessionManager, ILogger<NoxRelayHub> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }
    
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Connection established: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public async Task JoinQueue(string userId)
    {
        _logger.LogInformation(
            "JoinQueue received from {ConnectionId} (userId={UserId})",
            Context.ConnectionId, userId);
        
        var session = _sessionManager.TryMatch(userId, Context.ConnectionId);
        if (session is null)
        {
            return; // now waiting; "matched" fires when a partner joins
        }

        await Groups.AddToGroupAsync(session.UserA.ConnectionId, session.Id);
        await Groups.AddToGroupAsync(session.UserB.ConnectionId, session.Id);
        
        _logger.LogInformation(
            "Emitting 'matched' to session {SessionId} — userA={UserAConn} userB={UserBConn}",
            session.Id, session.UserA.ConnectionId, session.UserB.ConnectionId);
        
        // Sent per-participant rather than to the whole group, since each
        // side needs to know which of "A"/"B" it is — that information is
        // only known server-side (by ConnectionId) and can't be inferred
        // by the client from a shared broadcast.
        await Clients.Client(session.UserA.ConnectionId).SendAsync("matched", new
        {
            sessionId = session.Id,
            currentTurn = session.CurrentTurn,
            yourSide = "A"
        });

        await Clients.Client(session.UserB.ConnectionId).SendAsync("matched", new
        {
            sessionId = session.Id,
            currentTurn = session.CurrentTurn,
            yourSide = "B"
        });
    }

    public async Task Keystroke(string diff)
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null)
        {
            _logger.LogWarning(
                "Keystroke from {ConnectionId} but no active session found for this connection",
                Context.ConnectionId);
            return;
        }

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
        if (session is null)
        {
            _logger.LogWarning(
                "PassTurn from {ConnectionId} but no active session found for this connection",
                Context.ConnectionId);
            return;
        }
        
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
        
        _logger.LogInformation(
            "LeaveSession requested by {ConnectionId} for session {SessionId}",
            Context.ConnectionId, session.Id);
        
        await Groups.RemoveFromGroupAsync(session.UserA.ConnectionId, session.Id);
        await Groups.RemoveFromGroupAsync(session.UserB.ConnectionId, session.Id);
        _sessionManager.RemoveSession(session.Id);
    }

    public async Task CompleteSentence()
    {
        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is null)
        {
            _logger.LogWarning(
                "CompleteSentence from {ConnectionId} but no active session found",
                Context.ConnectionId);
            return;
        }
        
        _logger.LogInformation(
            "Sentence completed in session {SessionId}: \"{Sentence}\"",
            session.Id, session.Sentence);
        
        await Clients.Group(session.Id).SendAsync("sentence_completed", new
        {
            finalSentence = session.Sentence
        });
        
        _sessionManager.RemoveSession(session.Id);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            _logger.LogWarning(exception,
                "Connection {ConnectionId} disconnected with exception", Context.ConnectionId);
        }
        else
        {
            _logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);
        }

        _sessionManager.CancelWaiting(Context.ConnectionId);

        var session = _sessionManager.GetSessionByConnectionId(Context.ConnectionId);
        if (session is not null)
        {
            var partner = _sessionManager.GetPartner(session, Context.ConnectionId);
            if (partner is not null)
            {
                _logger.LogInformation(
                    "Notifying partner {PartnerConnectionId} of disconnection in session {SessionId}",
                    partner.ConnectionId, session.Id);
                await Clients.Client(partner.ConnectionId).SendAsync("partner_disconnected");
            }
            _sessionManager.RemoveSession(session.Id);
        }

        await base.OnDisconnectedAsync(exception);
    }
}