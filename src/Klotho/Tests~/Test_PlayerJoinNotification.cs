using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using NUnit.Framework;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

public static class Test_PlayerJoinNotification
{
    private struct JoinNotificationMarker : IComponent { }

    private sealed class ObserveSystem : QuerySystem<JoinNotificationMarker>
    {
        internal Action Observe;
        protected override void OnUpdate() => Observe();
    }

    [Test]
    public static void Tick_NotifiesPlayerJoinAfterCommandsAndBeforeSystems()
    {
        var store = new EntityStore();
        store.CreateEntity(new JoinNotificationMarker());
        var trace = new List<string>();
        var systems = new SystemRoot(store);
        systems.Add(new ObserveSystem { Observe = () => trace.Add("systems") });
        var simulation = new FrifloSimulation(store, systems,
            (_, _, _) => trace.Add("commands"));
        simulation.OnPlayerJoinedNotification += id => trace.Add($"joined:{id}:{simulation.CurrentTick}");

        simulation.Tick(new List<ICommand> { new PlayerJoinCommand { JoinedPlayerId = 7 } });

        CollectionAssert.AreEqual(new[] { "commands", "joined:7:0", "systems" }, trace,
            "the engine must learn about the new player before this tick's systems execute");
        Assert.AreEqual(1, simulation.CurrentTick);
    }
}
