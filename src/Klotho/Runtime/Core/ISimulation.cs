using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Deterministic simulation interface.
    /// Must guarantee the same output for the same input.
    /// </summary>
    public interface ISimulation
    {
        /// <summary>
        /// Current simulation tick.
        /// </summary>
        int CurrentTick { get; }

        /// <summary>
        /// Initialize the simulation.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Execute a single simulation tick.
        /// </summary>
        /// <param name="commands">Commands to execute in this tick.</param>
        void Tick(List<ICommand> commands);

        /// <summary>
        /// Roll back to a specific tick (used when prediction fails).
        /// </summary>
        void Rollback(int targetTick);

        /// <summary>
        /// Per-tick snapshot save trigger. The engine calls this every tick
        /// while a rollback window is configured (plus at restore/boundary points); save the
        /// current state keyed by this simulation's own <see cref="CurrentTick"/> — call timing
        /// varies (before/after Tick, after restore) but the ring invariant is single:
        /// "key K = state before executing tick K". May be a no-op (e.g. a simulation whose
        /// <see cref="Rollback"/> restores correctly without snapshots); the only obligation is
        /// on <see cref="GetNearestRollbackTick"/>: return only ticks <see cref="Rollback"/> can
        /// actually restore on the current timeline.
        /// </summary>
        void SaveSnapshot();

        /// <summary>
        /// Returns the nearest tick at or below <paramref name="targetTick"/> that this simulation
        /// can <see cref="Rollback"/> to from its own internal snapshot history, or -1 when none.
        /// Simulations own their rollback history — the engine triggers saves via
        /// <see cref="SaveSnapshot"/> and resolves rollback targets through this query; the
        /// storage itself is simulation-owned. After
        /// <see cref="RestoreFromFullState"/> or <see cref="Reset"/>, history from the previous
        /// timeline must not be exposed here (clear it or bound the query by the current tick) —
        /// the engine does not clear non-ECS history.
        /// </summary>
        int GetNearestRollbackTick(int targetTick);

        /// <summary>
        /// Returns the hash of the current state (for sync validation).
        /// </summary>
        long GetStateHash();

        /// <summary>
        /// Reset the simulation.
        /// </summary>
        void Reset();

        void RestoreFromFullState(byte[] stateData);

        byte[] SerializeFullState();

        (byte[] data, long hash) SerializeFullStateWithHash();

        void EmitSyncEvents();

        /// <summary>
        /// Called when a new player is added via Late Join.
        /// Automatically invoked when a PlayerJoinCommand is detected inside Tick(commands).
        /// Implementations MUST raise OnPlayerJoinedNotification (contract).
        /// </summary>
        void OnPlayerJoined(int playerId, int tick);

        /// <summary>
        /// Raised inside ISimulation.OnPlayerJoined to signal the engine that a player joined.
        /// Carries only the joined playerId — sim entity count is internal to the simulation.
        /// </summary>
        event Action<int> OnPlayerJoinedNotification;

        /// <summary>
        /// Whether the current (possibly restored) state is a terminal "match ended" state.
        /// Must be deterministic and monotonic within a timeline (once ended, later ticks stay ended; a
        /// rollback to a pre-end snapshot may report false again). The engine reads this — NOT the
        /// event-driven OnMatchEnded latch — to gate Pause-grace StopCommand injection and as the
        /// fire-forward backstop precondition at resync/restore boundaries. GC-free, called per
        /// tick. Default false for simulations with no match-end concept.
        /// </summary>
        bool IsMatchEndedState => false;

        /// <summary>
        /// The active match-end payload when <see cref="IsMatchEndedState"/> is true.
        /// Called only by the resync/restore backstop (rare) to fire OnMatchEnded once for a match-end
        /// that was lost from the event buffer. May return a reused instance — callers must not retain it
        /// past the OnMatchEnded dispatch. Returns null when there is no active match end.
        /// </summary>
        IMatchEndEvent GetActiveMatchEnd() => null;
    }

}
