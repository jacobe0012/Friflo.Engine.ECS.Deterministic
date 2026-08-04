using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// The game side of a networked Friflo simulation: register systems, seed the world, supply input.
/// </summary>
/// <remarks>
/// This is the Friflo counterpart to Klotho's <c>ISimulationCallbacks</c>, not an implementation of
/// it. That interface types <c>RegisterSystems</c> against the concrete <c>EcsSimulation</c>, which
/// a Friflo-backed simulation cannot supply. Implementing it would mean leaving that member
/// permanently unimplementable — and it is never called on this path anyway, since only
/// <c>KlothoSession.Create</c> and <c>RoomManager</c> invoke it and
/// <see cref="FrifloKlothoSession"/> replaces both.
/// <para>
/// Everything here runs on every peer and must be deterministic: same inputs, same result, on every
/// machine and on every rollback re-simulation. See <see cref="FrifloSimulation"/> for the full
/// contract — in particular, simulation state belongs in the store, never in a field of a system or
/// of the callbacks object, because a rollback restores the store and nothing else.
/// </para>
/// </remarks>
public interface IFrifloSimulationCallbacks
{
    /// <summary>
    /// Add the systems that run once per tick, in the order they should execute.
    /// </summary>
    /// <remarks>
    /// Execution order is part of the simulation's definition: two peers that register the same
    /// systems in different orders will diverge. Do not register parallel jobs
    /// (<c>JobExecution.Parallel</c>) — completion order is not reproducible.
    /// </remarks>
    void RegisterSystems(SystemRoot systems, EntityStore store);

    /// <summary>
    /// Create the starting entities, before tick 0 is snapshotted.
    /// </summary>
    /// <param name="store">The simulation store.</param>
    /// <param name="randomSeed">
    /// The seed every peer agreed on during the handshake. Any randomness in world setup must come
    /// from this rather than from a clock or an unseeded generator, or peers start out different.
    /// </param>
    void OnInitializeWorld(EntityStore store, int randomSeed);

    /// <summary>
    /// Called once per tick, before it executes, to collect this player's input.
    /// </summary>
    /// <remarks>
    /// Send through <paramref name="sink"/>; the framework stamps the player id and the target tick
    /// (local input is scheduled <c>InputDelayTicks</c> ahead). Sending nothing is fine — the engine
    /// fills in an empty command. This is the one place where reading real, non-deterministic input
    /// (a keyboard, a mouse) is correct: what crosses into the simulation is the resulting command,
    /// which every peer then executes identically.
    /// </remarks>
    void OnPollInput(int playerId, int tick, IFrifloCommandSink sink);
}

/// <summary>Accepts the commands produced by <see cref="IFrifloSimulationCallbacks.OnPollInput"/>.</summary>
public interface IFrifloCommandSink
{
    /// <summary>
    /// Submit a command for the local player. Its <c>PlayerId</c> and <c>Tick</c> are assigned by
    /// the framework, so any values already on the command are overwritten.
    /// </summary>
    void Send(ICommand command);
}
