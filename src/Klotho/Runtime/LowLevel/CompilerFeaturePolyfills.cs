// Polyfills for compiler-recognised attributes that live in the BCL from .NET 6 onwards but are
// absent from the netstandard2.1 surface Unity compiles against.
//
// These types are matched by the C# compiler *by name*, not by identity: declaring them in this
// assembly is the documented way to use the feature on an older target. Runtime behaviour is
// unaffected — nothing reflects over them.
//
// Only KLogHandlers/KLoggerExtensions need these, for the interpolated-string log handlers that
// let `log.Info($"...")` skip formatting entirely when the level is disabled. Without the
// polyfills those files do not compile under Unity, and dropping them would mean formatting every
// log message whether or not anyone reads it.
//
// Guarded so that a target which already provides them (net8.0, when this project is built by
// dotnet) does not get a duplicate definition.
#if NETSTANDARD2_1 || NETSTANDARD2_0 || UNITY_2018_1_OR_NEWER

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    /// <summary>Marks a type as an interpolated string handler.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    internal sealed class InterpolatedStringHandlerAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method to run automatically when the assembly loads.
    /// </summary>
    /// <remarks>
    /// KlothoGenerator emits these on the registration methods it generates, so this polyfill is
    /// what makes the generated code compile at all under Unity — the command / message / component
    /// type registrations happen in module initializers.
    /// <para>
    /// It must be <c>public</c>, unlike the two handler attributes: the generated code lives in the
    /// same assembly today, but the attribute is part of the contract the generator emits against.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ModuleInitializerAttribute : Attribute
    {
    }

    /// <summary>
    /// Names the parameters to pass to the handler's constructor, ahead of literalLength and
    /// formattedCount.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        public InterpolatedStringHandlerArgumentAttribute(string argument)
        {
            Arguments = new[] { argument };
        }

        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments)
        {
            Arguments = arguments;
        }

        public string[] Arguments { get; }
    }
}

#endif
