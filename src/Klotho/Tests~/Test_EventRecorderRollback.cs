using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Klotho.Friflo;
using NUnit.Framework;
using xpTURN.Klotho.Core;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>
/// Friflo's EventRecorder is supported with deterministic per-tick semantics: the bridge clears
/// abandoned/previous-tick events before commands and systems execute a tick.
/// </summary>
public static class Test_EventRecorderRollback
{
    private struct Trigger : IComponent { public int value; }
    private struct Observed : IComponent { public int count; }

    private sealed class ObserveEventsSystem : BaseSystem
    {
        private EntityStore store;

        protected override void OnAddStore(EntityStore entityStore) => store = entityStore;

        protected internal override void OnUpdateGroup()
        {
            int count = store.EventRecorder.ComponentEvents.Length;
            store.Query<Observed>().ForEachEntity((ref Observed observed, Entity _) => observed.count += count);
        }
    }

    [Test]
    public static void Tick_DiscardsEventsFromTheAbandonedRollbackBranch()
    {
        var store = new EntityStore();
        store.EventRecorder.Enabled = true;
        var entity = store.CreateEntity();
        var simulation = new FrifloSimulation(store, new SystemRoot(store));
        simulation.SaveSnapshot();

        entity.AddComponent(new Trigger { value = 1 });
        Assert.AreEqual(1, store.EventRecorder.ComponentEvents.Length,
            "precondition: the abandoned branch must leave a recorded event");

        simulation.Rollback(0);
        simulation.Tick(new List<ICommand>());

        Assert.AreEqual(0, store.EventRecorder.ComponentEvents.Length,
            "an event from the abandoned branch survived into the corrected tick");
    }

    [Test]
    public static void Tick_ClearsBeforeCommandsSoSystemsCanObserveCurrentTickEvents()
    {
        var store    = new EntityStore();
        var observed = store.CreateEntity(new Observed());
        var target   = store.CreateEntity();
        store.EventRecorder.Enabled = true;
        store.EventRecorder.ClearEvents();

        var systems = new SystemRoot(store) { new ObserveEventsSystem() };
        var simulation = new FrifloSimulation(
            store,
            systems,
            applyCommands: (s, _, _) => target.AddComponent(new Trigger { value = 7 }));

        simulation.Tick(new List<ICommand>());

        Assert.AreEqual(1, observed.GetComponent<Observed>().count,
            "clearing after command application would erase the current tick's events before systems see them");
        Assert.AreEqual(1, store.EventRecorder.ComponentEvents.Length);
    }
}
