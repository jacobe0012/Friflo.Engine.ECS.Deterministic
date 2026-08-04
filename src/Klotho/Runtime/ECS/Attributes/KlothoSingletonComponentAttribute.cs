using System;

namespace xpTURN.Klotho.ECS
{
    /// <summary>
    /// Marks a component type as singleton — exactly one entity in the frame may carry it.
    /// Frame.Add throws if a second entity with this component is created.
    /// Read via Frame.GetSingleton&lt;T&gt;() / GetReadOnlySingleton&lt;T&gt;() / TryGetSingleton&lt;T&gt;().
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class KlothoSingletonComponentAttribute : Attribute { }
}
