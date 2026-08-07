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

        if (callerSide is null || callerSide != session.CurrentTurn)
        {
            return false; // not their turn, or not a participant of this session
        }
        
        session.Sentence += diff;
        session.CursorPosition = session.Sentence.Length;
        return true;
    }

    public void PassTurn(Session session)
    {
        session.CurrentTurn = session.CurrentTurn == "A" ? "B" : "A";
    }

    public void RemoveSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        _connectionToSessionId.TryRemove(session.UserA.ConnectionId, out _);
        _connectionToSessionId.TryRemove(session.UserB.ConnectionId, out _);
    }

    public Participant? GetPartner(Session session, string connectionId)
    {
        if (session.UserA.ConnectionId == connectionId) return session.UserB;
        return session.UserB.ConnectionId == connectionId ? session.UserA : null;
    }
}