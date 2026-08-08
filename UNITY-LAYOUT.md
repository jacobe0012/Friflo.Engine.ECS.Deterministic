# Unity layout branch (`feat/unity-layout`)

This branch exists so the repository can be dropped **inside a Unity project's `Assets/`** as a
git submodule. It is `feat/klotho-integration` plus the mechanical changes that make Unity's
importer behave.

Nothing about the engine, the bridge or the determinism contract differs between the two
branches. If you are not consuming this from Unity, use `feat/klotho-integration`.

## What Unity compiles

Unity compiles **every** `.cs` it finds under `Assets/`, with no notion of a `.csproj`. A
directory whose name ends in `~` is skipped entirely. So the three assemblies Unity needs keep
their names, and everything else — test projects, sample apps, the AOT smoke harness, the
dedicated-server host, tooling — is suffixed:

| Reaches Unity | Suffixed with `~` (Unity ignores) |
|---|---|
| `src/ECS/` | `src/Tests~/` `src/Tests-internal~/` `src/Tests-NativeAOT~/` `src/Tests-Unity~/` |
| `src/Klotho/Runtime/` | `src/apps~/` `src/CodeGen~/` `src/Hub~/` `src/ECS.Boost~/` |
| `src/Klotho/Bridge/` | `src/Klotho/{Tests~,AotSmoke~,UdpMatch~,Server~,Tools~}/` |
| `src/Klotho/Analyzers/` (source generator DLL) | `src/Klotho/Runtime/Plugins~/` (needs Microsoft.Extensions.Logging) |
| | `src/Klotho/Runtime/ECS/DataAsset/Json~/` (Newtonsoft-coupled) |

`dotnet build Engine.sln` still builds all of it — `Engine.sln` and the `ProjectReference`
paths were updated to match. The rename is invisible to the .NET side.

**When adding a directory that Unity must not import, rename it AND update the three places
that name it**: `Engine.sln`, any `ProjectReference` pointing into it, and — for
`Klotho.Runtime` — the `Exclude` globs in `Klotho.Runtime.csproj`. `Klotho.Runtime` sets
`EnableDefaultCompileItems=false` and globs its own sources, so a directory renamed without
updating its `Exclude` gets pulled straight back into the .NET build. That is exactly how the
`Plugins~` rename first broke this build.

## Assembly definitions

Three `.asmdef` files, all with `noEngineReferences: true` (no UnityEngine dependency) and all
referencing each other **by name**:

```
Friflo.Engine.ECS          src/ECS/
Klotho.Runtime             src/Klotho/Runtime/
Klotho.Friflo.Bridge       src/Klotho/Bridge/   -> references the two above
```

Reference these by name from the consuming project too, never by `GUID:` — see below.

## `.meta` files are not committed

`.gitignore` excludes `src/ECS/**/*.meta` and `src/Klotho/**/*.meta`. Unity generates them next
to every imported file; committing them would make every submodule pull conflict with whatever
the local editor generated.

**The consequence is that guids are not stable across clones.** Any `.asmdef` in the consuming
project that references these assemblies must use the **name** form (`"Friflo.Engine.ECS"`),
not the `"GUID:..."` form. A GUID reference will resolve on the machine that created it and
silently break on a fresh clone.

## External dependencies the consuming Unity project must provide

Neither assembly bundles its NuGet dependencies. Install via NuGetForUnity (or equivalent):

| Assembly | Needs |
|---|---|
| `Friflo.Engine.ECS` | `Friflo.Json.Fliox`, `Friflo.Json.Burst`, `System.Runtime.CompilerServices.Unsafe` |
| `Klotho.Runtime` | `Newtonsoft.Json` (`com.unity.nuget.newtonsoft-json` works) |

## Language version

`src/ECS` uses file-scoped namespaces (C# 10) in 237 files; `src/Klotho/Bridge` in 13. Unity
6000.0 defaults to C# 9, so the consuming project must raise the language version — e.g. via
`xpTURN.Polyfill`'s "Apply Additional Compiler Arguments -langversion", or an
`Assets/csc.rsp` carrying `-langversion:preview`. Without it these assemblies do not compile
in Unity.

## Source generator

`src/Klotho/Analyzers/KlothoGenerator.dll` is a netstandard2.0 Roslyn incremental generator.
To make Unity run it, select the DLL in the Project window and tick the **RoslynAnalyzer**
asset label. Unity will not execute it otherwise.
