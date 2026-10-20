# Unity layout branch (`feat/unity-layout`)

This branch exists so the repository can be dropped **inside a Unity project's `Assets/`** as a
git submodule. It builds on `feat/klotho-integration` with the mechanical changes that make
Unity's importer behave, plus the deterministic-state and connection fixes described below.
The .NET projects remain buildable from this same tree; the branches are no longer identical
in their bridge implementation.

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
| | `src/Klotho/Runtime/ThirdParty/LiteNetLib.v2.1.4~` (see below) |

### LiteNetLib comes from the consuming project

`Klotho.Runtime` vendors LiteNetLib 2.1.4, and a Unity project that does any networking very
likely already has `com.revenantx.litenetlib`. Compiling both puts every LiteNetLib type in two
assemblies, and the errors land on *the game's* files rather than on either copy — a `CS0433`
for `NetPeer` / `DisconnectInfo` / `INetEventListener` / `DeliveryMethod` at each use site.

So the vendored copy is suffixed and `Klotho.Runtime.asmdef` references the `LiteNetLib` assembly
instead. **The consuming project must supply `com.revenantx.litenetlib` 2.1.4.**

This is safe rather than a judgement call: the two copies were diffed and are byte-identical
across all 25 files — the vendored one is plain upstream 2.1.4 with no local changes. Only the
packaging files (`asmdef` / `csproj` / `package.json`) differ, and those exist solely on the UPM
side.

The `.csproj` deliberately does *not* exclude the suffixed directory: on the .NET side there is
no package to collide with, so the server build keeps compiling the vendored sources.

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
| `Friflo.Engine.ECS` | `Friflo.Json.Fliox` 1.0.4, `Friflo.Json.Fliox.Annotation` 1.0.4, `Friflo.Json.Burst` 1.0.4, `System.Runtime.CompilerServices.Unsafe` 6.0.0 |
| `Klotho.Runtime` | `Newtonsoft.Json` (`com.unity.nuget.newtonsoft-json` works) |

`System.ComponentModel.Annotations` is deliberately *not* on that list. `DataEntity.pid` carries a
`[Key]` from `System.ComponentModel.DataAnnotations`, which netstandard2.1 does not include —
and installing the real NuGet package does not fix it, because Unity resolves that assembly name
to its own facade (which forwards to mscorlib and has no `KeyAttribute`) and keeps doing so even
with `isOverridable`. Both were tried; both left the same two errors. The attribute is therefore
compiled out under Unity: it only marks a primary key for a Fliox *database*, which nothing here
hosts. `[Serialize("id")]`, which does affect the wire format, stays unconditional.

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

## Deterministic state and NativeAOT registration

FullState is now **version 5**. Script fields declared privately on base classes are included in
rollback, hashing and FullState; older payloads are rejected before mutating the receiving store.
Do not mix this build with v4 peers. Components may also hold readonly Entity fields, and a null
Entity round-trips as `default(Entity)`, including its equality semantics.

For NativeAOT, register each component, relation and script through its existing, most-specific
`NativeAOT.Register...` method. These entry points preserve their own field metadata; script
registration also preserves private base-class fields. **Nested value types need explicit metadata
registration**, because retaining an outer field does not retain the members of its field type:

```csharp
aot.RegisterComponent<TargetState>();
aot.RegisterScript<Cooldown>();
aot.RegisterDeterministicType<FPVector3>(); // if a field contains an FPVector3
aot.RegisterDeterministicType<FP64>();      // including the FP64 fields inside that vector
aot.CreateSchema();
```

`RegisterDeterministicType<T>()` adds metadata only, not another component to the schema. Register
every non-primitive, non-enum nested value type before `CreateSchema()`; Entity itself is a special
case and needs no extra registration. Missing registration fails immediately with the field path
and required call, instead of producing a partial hash or losing state. JIT builds discover fields
normally and do not require these extra calls. These registrations apply to field-based layouts:
scripts and components containing Entity references. Plain unmanaged components still use their raw
value bytes. This does not relax the ban on other managed fields.

The native smoke program is `src/Klotho/AotSmoke~/Klotho.AotSmoke.csproj`. Publish and run its native
executable: a JIT/Mono smoke pass does not verify trimming or the AOT path. NativeAOT checks are not
an IL2CPP Player certification.

## Server-driven client initialization

`FrifloKlothoClient.Connect()` completes the existing `KlothoConnection` handshake **before**
constructing its engine and snapshot ring, so both use the server's authoritative configuration.
Late join and reconnect also use the configuration carried by the completed connection result.

While the handshake is pending, `Store`, `Phase`, `State`, `LocalPlayerId`, `LastVerifiedTick`,
`Update()` and `Stop()` are usable. Wait for `Phase == SessionPhase.Synchronized` before accessing
`Engine`, `Simulation`, `NetworkService` or calling `SetReady()`; those operations throw a clear
`InvalidOperationException` if called too early. `HasWorld` still becomes true only once the
server's FullState has been applied, not merely when the configuration handshake completes.

## Completed-handshake receive ownership

A completed `KlothoConnection` result now retains packets arriving before its session is created.
This supports both construction inside `onCompleted` and construction after the handshake's
`Update()` returns, including late join and cold-start reconnect. The adopting SD/P2P service
replays pending receives in order on its first `Update()`, after engine initialization and world
seeding; it does not execute them from `InitializeFromConnection()`.

The handoff copies the valid packet bytes (transport buffers may be reused), is single-use, and
is bounded to 1024 packets / 4 MiB. Overflow or a disconnect during handoff fails explicitly rather
than proceeding with missing input. Normal delivery takes over after pending packets have drained.

`KlothoConnection.Dispose()` detaches the handshake handlers but does not discard a successful
result's pending receives. Adopt each result once, or call `ConnectionResult.Dispose()` if abandoning
it. Disposing an already-adopted result does not cancel the service's receives. Neither operation
closes the caller-owned transport; disconnect that transport explicitly when tearing down a peer.
