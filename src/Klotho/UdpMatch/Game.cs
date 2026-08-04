using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using xpTURN.Klotho.Core;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Serialization;

namespace Klotho.Friflo.UdpMatch;

/// <summary>
/// The simulation every process in the match runs. Identical on the server and on each client, by
/// virtue of being the same code compiled once.
/// </summary>
/// <remarks>
/// Deliberately the same shape as the in-process tests: components, a parent/child hierarchy and a
/// <see cref="Script"/>, so the world that has to cross the socket spans all three of the ways
/// state is stored. The point of running it over real UDP is not to test new simulation logic but
/// to put the existing logic through a real socket, a real serializer round trip and two
/// independent clocks.
/// </remarks>
internal static class Game
{
    /// <summary>Type id for <see cref="NudgeCommand"/>. Every process must agree on it.</summary>
    internal const int NudgeCommandTypeId = 200;

    /// <summary>Wall-clock ms per simulation tick.</summary>
    /// <remarks>
    /// Slower than the in-process tests use. Those drive every peer from one loop, so a short tick
    /// only makes them finish sooner; here each process runs on its own clock and has to actually
    /// keep up with a real socket.
    /// </remarks>
    internal const int TickIntervalMs = 20;

    /// <summary>Nudges the sending player's avatar.</summary>
    internal sealed class NudgeCommand : CommandBase
    {
        public int dx;
        public int dy;

        public override int  CommandTypeId     => NudgeCommandTypeId;
        public override bool IsContinuousInput => true;

        public override int GetSerializedSize() => base.GetSerializedSize() + 8;

        protected override void SerializeData(ref SpanWriter writer)
        {
            writer.WriteInt32(dx);
            writer.WriteInt32(dy);
        }

        protected override void DeserializeData(ref SpanReader reader)
        {
            dx = reader.ReadInt32();
            dy = reader.ReadInt32();
        }
    }

    /// <summary>An avatar owned by one player.</summary>
    internal struct Avatar : IComponent
    {
        public int  playerId;
        public FP64 x, y;
    }

    /// <summary>An avatar's equipment, parented to it. Exists to put a hierarchy in the world.</summary>
    internal struct Held : IComponent
    {
        public int slot;
    }

    /// <summary>State attached to an avatar as a <see cref="Script"/> rather than a component.</summary>
    internal sealed class Loadout : Script
    {
        public int    charges;
        public Entity held;
    }

    /// <summary>Applies a constant drift, so state keeps changing even with no input.</summary>
    internal sealed class DriftSystem : QuerySystem<Avatar>
    {
        protected override void OnUpdate()
        {
            Query.ForEachEntity((ref Avatar avatar, Entity _) => {
                avatar.x += FP64.FromInt(1);
            });
        }
    }

    /// <summary>Game side of the simulation, shared by the server and every client.</summary>
    internal sealed class Callbacks : IFrifloSimulationCallbacks, IFrifloCommandApplier
    {
        private readonly int playerCount;

        internal Callbacks(int playerCount) => this.playerCount = playerCount;

        /// <inheritdoc />
        public void RegisterSystems(SystemRoot systems, EntityStore store) => systems.Add(new DriftSystem());

        /// <inheritdoc />
        public void OnInitializeWorld(EntityStore store, int randomSeed)
        {
            // Server only; each client receives this world as a full state over the socket. Fixed
            // creation order, since entity ids are assigned in sequence and the hash covers them.
            //
            // Two children per avatar, not one: an IdArray of count 1 keeps the id inline and never
            // touches the pooled allocator, so a one-child world would round-trip even with a
            // broken child pool.
            for (int playerId = 1; playerId <= playerCount; playerId++) {
                var avatar = store.CreateEntity(new Avatar { playerId = playerId, x = FP64.Zero, y = FP64.Zero });
                var held   = store.CreateEntity(new Held { slot = playerId });
                var spare  = store.CreateEntity(new Held { slot = playerId + 100 });
                avatar.AddChild(held);
                avatar.AddChild(spare);
                avatar.AddScript(new Loadout { charges = playerId * 3, held = held });
            }
        }

        /// <inheritdoc />
        public void OnPollInput(int playerId, int tick, IFrifloCommandSink sink)
        {
            // A pure function of (playerId, tick), so re-simulating a tick reproduces its command,
            // and deliberately not a pattern the "repeat the last continuous input" predictor can
            // guess - a client predicting its opponent has to be wrong sometimes.
            sink.Send(new NudgeCommand {
                dx = (tick * 7 + playerId * 13) % 5,
                dy = (tick * 3 + playerId * 11) % 4,
            });
        }

        /// <inheritdoc />
        public void ApplyCommands(EntityStore store, List<ICommand> commands, int tick)
        {
            for (int i = 0; i < commands.Count; i++) {
                if (commands[i] is not NudgeCommand nudge) {
                    continue;   // EmptyCommand substituted for input that never arrived, or a system command
                }
                int owner = nudge.PlayerId;
                store.Query<Avatar>().ForEachEntity((ref Avatar avatar, Entity _) => {
                    if (avatar.playerId == owner) {
                        avatar.x += FP64.FromInt(nudge.dx);
                        avatar.y += FP64.FromInt(nudge.dy);
                    }
                });
            }
        }
    }

    /// <summary>Register the game's command types. Identical in every process, by construction.</summary>
    internal static void RegisterCommands(CommandFactory factory) =>
        factory.RegisterCommand<NudgeCommand>(NudgeCommandTypeId);

    internal static SimulationConfig NewSimulationConfig() => new() {
        Mode              = NetworkMode.ServerDriven,
        TickIntervalMs    = TickIntervalMs,
        InputDelayTicks   = 2,
        MaxRollbackTicks  = 30,
        SyncCheckInterval = 10,
    };

    internal static SessionConfig NewSessionConfig(int playerCount) => new() {
        MaxPlayers          = playerCount,
        MinPlayers          = playerCount,
        CountdownDurationMs = 1,
    };
}
