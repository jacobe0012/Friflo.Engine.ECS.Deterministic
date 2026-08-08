using System;
using System.IO;
using System.Reflection;

namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Server startup bootstrap. When Klotho is split across referenced assemblies,
    /// type registration (commands, messages, components, JIT warmups) runs through
    /// [ModuleInitializer], which only fires once each assembly is loaded. A
    /// referenced-but-untouched assembly is loaded lazily, so its registrations would
    /// be missing when the first command/message arrives. This loads every Klotho and
    /// game assembly found in the deploy directory, then runs warmups (which also
    /// executes the module initializers of everything loaded) before any factory is
    /// constructed.
    ///
    /// Call once from the main thread at startup. Assumes a multi-file, untrimmed deploy
    /// (the dedicated-server default): the directory scan relies on each assembly existing
    /// as a separate .dll on disk, so PublishSingleFile / PublishTrimmed / NativeAOT would
    /// need a different discovery strategy.
    /// </summary>
    public static class KlothoServerBootstrap
    {
        /// <summary>
        /// Loads Klotho/game assemblies and runs JIT warmups. Call once at process start,
        /// before constructing any transport/simulation/factory.
        /// </summary>
        /// <param name="gameAssemblyPrefixes">
        /// Assembly name prefixes for the game project (e.g. "Brawler"). Klotho framework
        /// assemblies (xpTURN.Klotho.*, LiteNetLib) are always included.
        /// </param>
        public static void Initialize(params string[] gameAssemblyPrefixes)
        {
            ForceLoadAssemblies(gameAssemblyPrefixes);
            // Triggers the [ModuleInitializer] of every loaded assembly, then warms up.
            WarmupRegistry.RunAll();
        }

        /// <summary>
        /// Loads every matching Klotho/game assembly from the application base directory
        /// into the AppDomain. Scanning the deploy directory (rather than walking metadata
        /// references) ensures registration-only assemblies are not missed: the C# compiler
        /// drops references to assemblies whose types are never used directly.
        ///
        /// Note: this only loads the assemblies; their [ModuleInitializer] registrations are
        /// triggered by <see cref="WarmupRegistry.RunAll"/> (call <see cref="Initialize"/>
        /// instead unless you have a reason to separate the two). Assumes a single-folder
        /// deploy, which is the dedicated-server layout.
        /// </summary>
        public static void ForceLoadAssemblies(params string[] gameAssemblyPrefixes)
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var dllPath in Directory.EnumerateFiles(baseDir, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dllPath);
                if (!IsTargetAssembly(name, gameAssemblyPrefixes))
                    continue;

                try
                {
                    Assembly.Load(new AssemblyName(name));
                }
                catch (Exception ex)
                {
                    // A failure here silently drops that assembly's registrations, which
                    // surfaces much later as an "unknown command/message type". Make it loud.
                    Console.Error.WriteLine(
                        $"[KlothoServerBootstrap] Failed to load '{name}': {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static bool IsTargetAssembly(string name, string[] gameAssemblyPrefixes)
        {
            if (name == null)
                return false;
            if (name.StartsWith("xpTURN.Klotho", StringComparison.Ordinal) || name == "LiteNetLib")
                return true;
            if (gameAssemblyPrefixes != null)
            {
                for (int i = 0; i < gameAssemblyPrefixes.Length; i++)
                {
                    if (name.StartsWith(gameAssemblyPrefixes[i], StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }
    }
}
