using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Adapts a Friflo <see cref="EntityStore"/> to Klotho's <see cref="ISimulation"/>, so game code
/// written against Friflo (<c>EntityStore</c> / <c>SystemRoot</c> / <c>Query</c>) can run under
/// Klotho's rollback and lockstep engine.
/// </summary>
/// <remarks>
/// <b>Determinism contract.</b> The store passed here is the <i>simulation</i> store and must be
/// kept separate from any presentation store. Within it:
/// <list type="bullet">
///   <item>Components and scripts may contain only value types — plus <see cref="Entity"/>, which
///         contributes just its <c>(Id, Revision)</c>. Any other reference field is rejected: its
///         bytes are an address that differs per run and per peer.</item>
///   <item>Use fixed-point (<c>FP64</c>) rather than <c>float</c>/<c>double</c> for any value that
///         crosses a tick boundary; IEEE results vary across platforms.</item>
///   <item>No parallel job execution (<c>JobExecution.Parallel</c>) — completion order is not
///         reproducible.</item>
///   <item>Keep simulation state <i>in the store</i>. A counter held in a system field is
///         invisible to the snapshot, so a rollback rewinds the entities but not the counter and
///         the replay takes different branches.</item>
///   <item>Create the store with the default <see cref="PidType.UsePidAsId"/>;
///         <see cref="PidType.RandomPids"/> seeds ids from a random source.</item>
/// </list>
/// Indexed components (<c>IIndexedComponent</c>), relations (<c>IRelation</c>), the parent/child
/// hierarchy (<c>AddChild</c>) and <see cref="Script"/>s are all captured and restored.
///
/// <b>Snapshot ring.</b> Following the Klotho contract, a snapshot saved at
/// <see cref="SaveSnapshot"/> is keyed by the current <see cref="CurrentTick"/> and holds the
/// state <i>before</i> executing that tick. <see cref="Rollback"/> restores exactly that state.
/// </remarks>
public sealed class FrifloSimulation : ISimulation
{
    private readonly EntityStore        store;
    private readonly SystemRoot         systems;
    private readonly FrifloStateHasher  hasher;
    private readonly Action<EntityStore, List<ICommand>, int> applyCommands;

    /// <summary>Ring of snapshots, indexed by <c>tick % capacity</c>.</summary>
    private readonly FrifloStoreSnapshot[] ring;
    /// <summary>Tick each ring slot holds, or -1 when the slot is empty.</summary>
    private readonly int[]                 ringTicks;

    /// <inheritdoc />
    public int CurrentTick { get; private set; }

    /// <summary>Number of past ticks that can be rolled back to.</summary>
    public int RollbackCapacity => ring.Length;

    /// <summary>The simulation store. Do not mutate outside <see cref="Tick"/>.</summary>
    public EntityStore Store => store;

    /// <inheritdoc />
    public event Action<int> OnPlayerJoinedNotification;

    /// <summary>
    /// Create a simulation over <paramref name="store"/>.
    /// </summary>
    /// <param name="store">The simulation store. Must satisfy the determinism contract above.</param>
    /// <param name="systems">Systems executed once per tick, in registration order.</param>
    /// <param name="applyCommands">
    /// Applies the commands of a tick to the store before the systems run. Receives the store,
    /// the commands (already in deterministic order) and the tick being executed.
    /// </param>
    /// <param name="rollbackCapacity">Number of ticks retained for rollback.</param>
    public FrifloSimulation(
        EntityStore                                 store,
        SystemRoot                                  systems,
        Action<EntityStore, List<ICommand>, int>    applyCommands   = null,
        int                                         rollbackCapacity = 32)
    {
        this.store         = store   ?? throw new ArgumentNullException(nameof(store));
        this.systems       = systems ?? throw new ArgumentNullException(nameof(systems));
        this.applyCommands = applyCommands;
        if (rollbackCapacity < 1) {
            throw new ArgumentOutOfRangeException(nameof(rollbackCapacity), rollbackCapacity, "must be >= 1");
        }
        hasher    = new FrifloStateHasher(store);
        ring      = new FrifloStoreSnapshot[rollbackCapacity];
        ringTicks = new int[rollbackCapacity];
        for (int i = 0; i < rollbackCapacity; i++) {
            ring[i]      = new FrifloStoreSnapshot(store);
            ringTicks[i] = -1;
        }
    }

    /// <inheritdoc />
    public void Initialize() { }

    /// <inheritdoc />
    /// <remarks>
    /// Systems must not take their timing from <see cref="UpdateTick"/>: it carries
    /// <c>float</c> fields, and a float tick counter loses integer precision past 2^24 ticks.
    /// Use a fixed-point delta constant and read the tick number from
    /// <see cref="CurrentTick"/> instead. The values passed here exist only to satisfy the
    /// Friflo signature.
    /// </remarks>
    public void Tick(List<ICommand> commands)
    {
        applyCommands?.Invoke(store, commands, CurrentTick);
        systems.Update(default);
        CurrentTick++;
    }

    /// <inheritdoc />
    /// <remarks>Keyed by <see cref="CurrentTick"/>: the state before executing that tick.</remarks>
    public void SaveSnapshot()
    {
        int slot = Slot(CurrentTick);
        ring[slot].Capture();
        ringTicks[slot] = CurrentTick;
    }

    /// <inheritdoc />
    public void Rollback(int targetTick)
    {
        int slot = Slot(targetTick);
        if (ringTicks[slot] != targetTick) {
            throw new InvalidOperationException(
                $"no snapshot for tick {targetTick}. nearest: {GetNearestRollbackTick(targetTick)}");
        }
        ring[slot].Restore();
        CurrentTick = targetTick;

        // Snapshots for ticks after the rollback target belong to the abandoned branch.
        // Dropping them keeps GetNearestRollbackTick from offering a state that no longer
        // matches this timeline.
        for (int i = 0; i < ringTicks.Length; i++) {
            if (ringTicks[i] > targetTick) {
                ringTicks[i] = -1;
            }
        }
    }

    /// <inheritdoc />
    public int GetNearestRollbackTick(int targetTick)
    {
        int best = -1;
        for (int i = 0; i < ringTicks.Length; i++) {
            int tick = ringTicks[i];
            if (tick >= 0 && tick <= targetTick && tick > best) {
                best = tick;
            }
        }
        return best;
    }

    /// <inheritdoc />
    public long GetStateHash() => hasher.ComputeHash();

    /// <inheritdoc />
    public void Reset()
    {
        for (int i = 0; i < ringTicks.Length; i++) {
            ringTicks[i] = -1;
        }
        CurrentTick = 0;
    }

    /// <inheritdoc />
    public byte[] SerializeFullState()
    {
        var serializer = new FrifloStateSerializer(store);
        return serializer.Serialize(CurrentTick);
    }

    /// <inheritdoc />
    public void RestoreFromFullState(byte[] stateData)
    {
        var serializer = new FrifloStateSerializer(store);
        CurrentTick    = serializer.Deserialize(stateData);
        // History from the previous timeline must not be offered after a restore.
        for (int i = 0; i < ringTicks.Length; i++) {
            ringTicks[i] = -1;
        }
    }

    /// <inheritdoc />
    public (byte[] data, long hash) SerializeFullStateWithHash() => (SerializeFullState(), GetStateHash());

    /// <inheritdoc />
    /// <remarks>
    /// No-op: this bridge does not buffer simulation events. Friflo raises its own component and
    /// entity events synchronously, and the determinism contract keeps view state out of the
    /// simulation store.
    /// </remarks>
    public void EmitSyncEvents() { }

    /// <inheritdoc />
    public void OnPlayerJoined(int playerId, int tick)
    {
        // Contract: implementations MUST raise the notification.
        OnPlayerJoinedNotification?.Invoke(playerId);
    }

    private int Slot(int tick)
    {
        int slot = tick % ring.Length;
        return slot < 0 ? slot + ring.Length : slot;
    }
}
