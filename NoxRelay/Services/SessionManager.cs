using System.Collections.Concurrent;
using NoxRelay.Models;

namespace NoxRelay.Services;

/// <summary>
/// Singleton holding all live session and matchmaking state. Registered via
/// AddSingleton — the Hub itself is instantiated per call and cannot hold
/// state, so everything real lives here. No ORM, no SQL, pure in-memory,
/// per the project doc's decision to keep the hot path free of persistence.
/// </summary>
public class SessionManager
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _connectionToSessionId = new();
    private QueuedUser? _waitingUser;
    private readonly object _matchLock = new();
    private readonly ILogger<SessionManager> _logger;

    public SessionManager(ILogger<SessionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to match the given user with whoever is currently waiting.
    /// If nobody is waiting, this user becomes the new waiting user and
    /// null is returned (caller should not emit "matched" yet).
    /// </summary>
    public Session? TryMatch(string userId, string connectionId)
    {
        lock (_matchLock)
        {
            if (_waitingUser is null || _waitingUser.UserId == userId)
            {
                // Nobody waiting yet, or the same user reconnecting/re-queueing
                // — replace the waiting slot rather than matching with self.
                _waitingUser = new QueuedUser {UserId =  userId, ConnectionId =  connectionId};
                _logger.LogInformation(
                    "User {UserId} ({ConnectionId}) is now waiting in queue",
                    userId, connectionId);
                return null;
            }

            var partner = _waitingUser;
            _waitingUser = null;

            var session = new Session
            {
                Id = Guid.NewGuid().ToString("N"),
                UserA = new Participant { UserId = partner.UserId, ConnectionId = partner.ConnectionId },
                UserB = new Participant { UserId = userId, ConnectionId = connectionId }
            };
            
            _sessions[session.Id] =  session;
            _connectionToSessionId[partner.ConnectionId] = session.Id;
            _connectionToSessionId[connectionId] = session.Id;
            
            _logger.LogInformation(
                "Match created: session {SessionId} — userA={UserAId} userB={UserBId}",
                session.Id, session.UserA.UserId, session.UserB.UserId);

            return session;
        }
    }

    /// <summary>
    /// Removes a user from the waiting slot if they disconnect or leave
    /// before being matched.
    /// </summary>
    public void CancelWaiting(string connectionId)
    {
        lock (_matchLock)
        {
            if (_waitingUser?.ConnectionId == connectionId)
            {
                _logger.LogInformation(
                    "Waiting user {ConnectionId} left the queue before matching", connectionId);
                _waitingUser = null;
            }
        }
    }

    public Session? GetSessionByConnectionId(string connectionId)
    {
        if (_connectionToSessionId.TryGetValue(connectionId, out var sessionId) &&
            _sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }
        
        return null;
    }

    /// <summary>
    /// Server rejects any keystroke not coming from the socket holding the
    /// current turn — this is the entire "no race conditions" guarantee
    /// from the project doc, no OT/CRDT needed since only one side writes
    /// at a time by design.
    /// </summary>
    public bool TryApplyKeystroke(Session session, string connectionId, string diff)
    {
        var isUserA = session.UserA.ConnectionId == connectionId;
        var isUserB = session.UserB.ConnectionId == connectionId;
        var callerSide = isUserA ? "A" : isUserB ? "B" : null;

        if (callerSide is null)
        {
            _logger.LogWarning(
                "Rejected keystroke: connection {ConnectionId} is not a participant of session {SessionId}",
                connectionId, session.Id);
            return false;
        }

        if (callerSide != session.CurrentTurn)
        {
            _logger.LogWarning(
                "Rejected keystroke: connection {ConnectionId} (side {Side}) tried to write out of turn in session {SessionId} (current turn: {CurrentTurn})",
                connectionId, callerSide, session.Id, session.CurrentTurn);
            return false;
        }
        
        session.Sentence += diff;
        session.CursorPosition = session.Sentence.Length;
        
        _logger.LogDebug(
            "Keystroke applied in session {SessionId} by side {Side}: diff={Diff} sentenceLength={Length}",
            session.Id, callerSide, diff, session.Sentence.Length);
        return true;
    }

    public void PassTurn(Session session)
    {
        var previousTurn = session.CurrentTurn;
        session.CurrentTurn = session.CurrentTurn == "A" ? "B" : "A";

        _logger.LogInformation(
            "Turn passed in session {SessionId}: {Previous} -> {Current}",
            session.Id, previousTurn, session.CurrentTurn);
    }


    public void RemoveSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        _connectionToSessionId.TryRemove(session.UserA.ConnectionId, out _);
        _connectionToSessionId.TryRemove(session.UserB.ConnectionId, out _);
        
        _logger.LogInformation(
            "Session {SessionId} removed (final sentence: \"{Sentence}\")",
            sessionId, session.Sentence);
    }

    public Participant? GetPartner(Session session, string connectionId)
    {
        if (session.UserA.ConnectionId == connectionId) return session.UserB;
        return session.UserB.ConnectionId == connectionId ? session.UserA : null;
    }
}