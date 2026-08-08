# PhysicsDeterminismProbe

Cross-runtime determinism guard for the deterministic FP64 math + `FPPhysicsWorld`
physics layer.

It compiles the **same** `Deterministic/{Math,Physics,Geometry}` sources that ship in
the package and runs them under two runtimes, then diffs raw-`long` output:

- **CoreCLR** (dedicated server runtime) — `net8.0`, via `dotnet`
- **Unity Mono** (client scripting backend) — `net472`, via the editor's MonoBleedingEdge `mono`

If the two outputs differ, an FP64 primitive or a physics step is **not** bit-identical
across the server and client runtimes — a determinism bug.

## What it checks

- **Part A — primitive sweeps**: `Sqrt`/`Mul`/`Div`/`Sin`/`Cos`/`Tan`/`Atan`/`Atan2`/`Asin`/`Acos`,
  4M operands each (full raw range + contact-like small range), folded into FNV-1a.
- **Part B — box-on-ground step**: a dynamic box falling onto a static ground (both the
  `_contacts` static-body path and the `_staticContacts`/BVH static-collider path that the
  real `RegisterGroundCollider` uses), 24 ticks, per-tick raw position/velocity/rotation.
  Parameters match `P2pSample` (gravity (0,-10,0), dt 0.025s, box halfExt 0.5 @ y=1,
  ground box 10x0.2x10 @ y=-0.1).

As of 2026-05-29 every line is **bit-identical** between CoreCLR and Unity 6.3 Mono —
i.e. the physics layer is cross-runtime deterministic. (The divergence that motivated this
probe was outside the physics step: the SD client never registered the static ground collider.)

## Run

```sh
cd Tools/PhysicsDeterminismProbe
dotnet build -f net8.0  -c Release
dotnet build -f net472  -c Release   # needs System.Memory (restored automatically)

MONO="/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge/bin/mono"
dotnet bin/Release/net8.0/PhysicsDeterminismProbe.dll out_coreclr.txt
"$MONO"     bin/Release/net472/PhysicsDeterminismProbe.exe out_mono.txt

diff <(grep -v '^#' out_coreclr.txt) <(grep -v '^#' out_mono.txt) \
  && echo "IDENTICAL (deterministic)" || echo "DIVERGENCE — investigate"
```

(Adjust the `MONO` path to the Unity version in use — any installed editor's
`Contents/Resources/Scripting/MonoBleedingEdge/bin/mono`.)

## Caveats

- **Serialization is stubbed** (`Stubs.cs`): the probe never serializes (it only calls
  `FPPhysicsWorld.Step`), so the real `xpTURN.Klotho.Serialization` is replaced with
  no-op stand-ins to avoid pulling the ECS/Core closure. This probe does **not** verify
  snapshot serialize/deserialize.
- `HashCodePolyfill.cs` provides `System.HashCode` for `net472` (used only by
  `GetHashCode()`, never on the measured path).
- Sources are glob-linked from the package, so a `Deterministic/*` API change can break
  the build — that is expected; update the probe alongside.
- Not wired into CI. To promote to a real regression test, swap the stubs for the real
  Serialization and add it to a CI step (or the existing `Tools/DeterminismVerification`).
