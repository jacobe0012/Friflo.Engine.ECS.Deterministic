using System;
using System.Collections.Generic;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// Validates determinism and rollback correctness without networking: every tick runs forward,
/// then rolls back <c>checkDistance</c> ticks, re-simulates the same inputs, and compares the
/// resulting state hashes. A mismatch means the simulation is not reproducible.
/// </summary>
/// <remarks>
/// This mirrors the logic of Klotho's own <c>SyncTestRunner</c> but talks only to
/// <see cref="ISimulation"/>. Klotho's version casts to its built-in <c>EcsSimulation</c>
/// (<c>simulation as EcsSimulation ?? throw</c>) and so cannot drive a Friflo-backed
/// simulation. Keeping this separate also means neither Klotho nor Friflo sources are patched,
/// so a Klotho upgrade does not conflict.
/// </remarks>
public sealed class FrifloSyncTestRunner : ISyncTestRunner
{
    private ISimulation      simulation;
    private int              checkDistance;
    private int              ringCapacity;

    /// <summary>Forward-pass hash per tick, indexed by <c>tick % ringCapacity</c>.</summary>
    private long[]           hashRing;
    /// <summary>Inputs per tick, replayed during re-simulation.</summary>
    private List<ICommand>[] inputRing;

    private int totalChecks;
    private int failedChecks;
    private int consecutiveFails;

    /// <summary>Tick from which validation may resume after history was invalidated.</summary>
    private int resumeFromTick = -1;

    /// <summary>Reused so re-simulation does not allocate per tick.</summary>
    private readonly List<ICommand> resimCommands = new();

    /// <inheritdoc />
    public float SuccessRate => totalChecks > 0 ? (float)(totalChecks - failedChecks) / totalChecks : 1.0f;
    /// <inheritdoc />
    public int   TotalChecks  => totalChecks;
    /// <inheritdoc />
    public int   FailedChecks => failedChecks;

    /// <inheritdoc />
    public event Action<SyncTestFailure> OnSyncError;
    /// <inheritdoc />
    public event Action                  OnSyncTestDisabled;

    /// <inheritdoc />
    public int ConsecutiveFailLimit { get; set; } = 3;

    /// <inheritdoc />
    public void Initialize(ISimulation simulation, int checkDistance = 5)
    {
        this.simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        if (checkDistance < 1) {
            throw new ArgumentException($"checkDistance({checkDistance}) must be >= 1");
        }
        if (simulation is FrifloSimulation friflo && checkDistance >= friflo.RollbackCapacity) {
            throw new ArgumentException(
                $"checkDistance({checkDistance}) must be < RollbackCapacity({friflo.RollbackCapacity})");
        }
        this.checkDistance = checkDistance;
        ringCapacity       = checkDistance + 2;  // headroom so the oldest slot is not overwritten mid-check

        hashRing  = new long[ringCapacity];
        inputRing = new List<ICommand>[ringCapacity];
        for (int i = 0; i < ringCapacity; i++) {
            inputRing[i] = new List<ICommand>();
        }
        totalChecks = failedChecks = consecutiveFails = 0;
    }

    /// <inheritdoc />
    public void NotifyExternalRollback(int resumeFrom)
    {
        // An external rollback invalidates the recorded hashes and inputs: they describe a
        // branch the simulation is no longer on. Skip checks until the window refills.
        resumeFromTick = resumeFrom + checkDistance;
        for (int i = 0; i < ringCapacity; i++) {
            hashRing[i] = 0;
            inputRing[i].Clear();
        }
    }

    /// <inheritdoc />
    public SyncTestResult RunTick(int tick, IReadOnlyList<ICommand> commands)
    {
        int ringIdx = Index(tick);

        // 1. Snapshot the pre-tick state, so this tick can be rolled back to later.
        simulation.SaveSnapshot();

        // 2. Record the inputs for replay.
        var tickInputs = inputRing[ringIdx];
        tickInputs.Clear();
        for (int i = 0; i < commands.Count; i++) {
            tickInputs.Add(commands[i]);
        }

        // 3. Forward execution.
        simulation.Tick(tickInputs);

        long forwardHash = simulation.GetStateHash();
        hashRing[ringIdx] = forwardHash;

        // 4. Not enough history yet to roll back the full check distance.
        if (tick < checkDistance || (resumeFromTick >= 0 && tick < resumeFromTick)) {
            return new SyncTestResult { Status = SyncTestStatus.Skip, Tick = tick };
        }
        if (resumeFromTick >= 0 && tick >= resumeFromTick) {
            resumeFromTick = -1;
        }

        // 5. Roll back and re-simulate the same inputs.
        int rollbackTo = tick - checkDistance;
        simulation.Rollback(rollbackTo);

        for (int resimTick = rollbackTo; resimTick <= tick; resimTick++) {
            // Re-save during replay: the ring must describe the branch now being executed.
            simulation.SaveSnapshot();

            resimCommands.Clear();
            var saved = inputRing[Index(resimTick)];
            for (int i = 0; i < saved.Count; i++) {
                resimCommands.Add(saved[i]);
            }
            simulation.Tick(resimCommands);
        }

        // 6. Compare.
        long rollbackHash = simulation.GetStateHash();
        totalChecks++;

        if (forwardHash == rollbackHash) {
            consecutiveFails = 0;
            return new SyncTestResult {
                Status          = SyncTestStatus.Pass,
                Tick            = tick,
                RollbackFromTick = tick,
                RollbackToTick  = rollbackTo,
                ExpectedHash    = forwardHash,
                ActualHash      = rollbackHash,
            };
        }

        failedChecks++;
        consecutiveFails++;

        // The re-simulated branch diverged, so the ring slots in [rollbackTo, tick] now describe
        // that branch. Skip ahead until forward saves have refilled the window, otherwise the
        // next check would roll back into polluted history and report a second, phantom failure.
        resumeFromTick = tick + checkDistance + 1;

        OnSyncError?.Invoke(new SyncTestFailure {
            Tick             = tick,
            RollbackDistance = checkDistance,
            ExpectedHash     = forwardHash,
            ActualHash       = rollbackHash,
            EntityCount      = (simulation as FrifloSimulation)?.Store.Count ?? 0,
        });
        if (consecutiveFails >= ConsecutiveFailLimit) {
            OnSyncTestDisabled?.Invoke();
        }
        return new SyncTestResult {
            Status           = SyncTestStatus.Fail,
            Tick             = tick,
            RollbackFromTick = tick,
            RollbackToTick   = rollbackTo,
            ExpectedHash     = forwardHash,
            ActualHash       = rollbackHash,
        };
    }

    private int Index(int tick)
    {
        int index = tick % ringCapacity;
        return index < 0 ? index + ringCapacity : index;
    }
}
