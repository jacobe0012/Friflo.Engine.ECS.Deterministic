# Deterministic Math

Every number in a Klotho simulation is **fixed-point**, not floating-point. `float`/`double` results differ across CPUs, compilers, and platforms — the moment two peers disagree on the last bit of a multiply, their states diverge and the netcode falls into a rollback storm. Klotho sidesteps this entirely: simulation state is built on **`FP64`**, a 32.32 fixed-point number whose every operation (including `Sqrt` and trigonometry) is bit-exact on any platform. On top of `FP64` sit fixed-point vectors, quaternions, matrices, geometric primitives, and a seeded RNG.

> Audience: game developers writing simulation logic (systems, components, gameplay math).
> Goal: use `FP64` and the `FP*` types correctly, know what's available, and never accidentally introduce a `float` into deterministic code.
>
> Related: [ECS.md](ECS.md) (components hold `FP64` state) · [Specification.md](Specification.md) §8 (formal tables) · [PhysicsWorld.md](PhysicsWorld.md) / [Navigation.md](Navigation.md) (built on this) · [SynchronizationDesign.md](SynchronizationDesign.md) (why determinism matters)

---

## 1. Why Fixed-Point

`FP64` stores a number as a 64-bit signed integer (`long _rawValue`) split into a **32.32** layout: the upper 32 bits are the integer part, the lower 32 bits are the fraction.

| Property | Value |
| ---- | ---- |
| Format | 32 integer bits · 32 fractional bits |
| Scale factor (`ONE`) | `1L << 32` = 4,294,967,296 |
| Precision (`Epsilon`) | `2⁻³²` ≈ 2.33 × 10⁻¹⁰ |
| Range | ±2,147,483,647.999… (int32 integer part) |
| Storage | one `long` — value type, zero GC |

Because it's pure integer arithmetic underneath, the same inputs produce the same bits everywhere. That bit-exactness is the foundation the whole sync model stands on (see [SynchronizationDesign.md](SynchronizationDesign.md)).

---

## 2. File Layout

```text
com.xpturn.klotho/Runtime/Deterministic/
├── Math/
│   ├── FP64.cs            # core type: constants, conversions, operators
│   ├── FP64.Math.cs       # Abs/Min/Max/Clamp/Sqrt/Pow/Exp/Log/Lerp/...
│   ├── FP64.Trig.cs       # Sin/Cos (LUT) + SinCordic/CosCordic + Atan2/Asin/Acos/Tan
│   ├── FP64.Internal.cs   # SafeMultiply / SafeDivide (overflow-safe, zero-GC)
│   ├── FPVector2.cs / FPVector3.cs / FPVector4.cs
│   ├── FPQuaternion.cs
│   └── FPMatrix2x2.cs / FPMatrix3x3.cs / FPMatrix4x4.cs
├── Geometry/
│   ├── FPBounds2.cs / FPBounds3.cs   # AABBs
│   ├── FPRay2.cs / FPRay3.cs         # rays
│   ├── FPPlane.cs / FPSphere.cs / FPCapsule.cs   # primitives
│   └── FPContact.cs / FPMeshData.cs / ShapeType.cs
└── Random/
    └── DeterministicRandom.cs        # Xorshift128+ seeded RNG
```

Engine-conversion extensions (`ToVector3()` / `ToFPVector3()`, etc.) live in the per-engine adapter files (`FP*.Unity.cs` / `FP*.Godot.cs`) — see [§10](#10-engine-conversions).

---

## 3. `FP64` Essentials

### Constants

`FP64.Zero` · `One` · `Half` · `MinValue` · `MaxValue` · `Pi` · `TwoPi` · `HalfPi` · `Deg2Rad` · `Rad2Deg` · `Epsilon`. Also `FP64.ONE` / `HALF` / `FRACTIONAL_BITS` raw `const`s for low-level work.

### Constructing & converting

```csharp
FP64 a = FP64.FromInt(5);          // exact: 5
FP64 b = FP64.FromDouble(0.5);     // authoring-time literal
FP64 c = FP64.FromFloat(1.25f);    // authoring-time literal
FP64 d = FP64.FromRaw(rawLong);    // reconstruct from RawValue
FP64 e = 3;                        // implicit int → FP64

int    i  = a.ToInt();             // truncates (>> 32)
float  f  = c.ToFloat();           // for the view layer only
double g  = b.ToDouble();
long   rw = a.RawValue;            // exact wire/hash representation
```

> **The one rule that matters:** `FromFloat`/`FromDouble`/`ToFloat` are **boundary** conversions — use them when authoring constants, reading config, or handing values to the view/render layer. **Never let a `float` participate in simulation arithmetic.** Convert a `float` literal to `FP64` once, then do all math in `FP64`. A `float` multiply inside a system is the classic determinism bug.

### Operators

Full operator set: `+ - * / %`, unary `-`, and comparisons `== != < > <= >=`. Multiply and divide route through overflow-safe paths (`SafeMultiply`/`SafeDivide`) that fall back to Hi/Lo decomposition with **no allocation**. `FP64` implements `IEquatable<FP64>` and `IComparable<FP64>`.

```csharp
FP64 speed = FP64.FromInt(5);
FP64 dt    = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);
FP64 step  = speed * dt;
```

---

## 4. Math Functions (`FP64.Math.cs`)

All deterministic, all zero-GC:

| Group | Functions |
| ---- | ---- |
| Rounding | `Abs` · `Floor` · `Ceiling` · `Round` |
| Range | `Min` · `Max` · `Clamp` · `Clamp01` |
| Interpolation | `Lerp` · `LerpUnclamped` · `InverseLerp` · `SmoothStep` · `MoveTowards` · `SmoothDamp` |
| Powers/roots | `Sqrt` · `RSqrt` · `Rcp` · `Pow` · `Cbrt` |
| Exp/log | `Exp` · `Exp2` · `Log` · `Log2` · `Ln` |
| Modular | `Fmod` · `Remainder` · `Repeat` · `PingPong` |
| Angles | `DeltaAngle` · `MoveTowardsAngle` |

`Sqrt` is a 2-pass binary-restoring root in 64-bit integer arithmetic — exact and GC-free.

---

## 5. Trigonometry (`FP64.Trig.cs`)

```csharp
FP64 s  = FP64.Sin(angleRad);
FP64 c  = FP64.Cos(angleRad);
FP64 a  = FP64.Atan2(dy, dx);
FP64 t  = FP64.Tan(angleRad);
FP64 r  = degrees * FP64.Deg2Rad;     // degrees → radians
```

- **`Sin` / `Cos` use a lookup table** by default — 1572 entries at 0.001 rad spacing over `[0, π/2]`, with quadrant expansion and linear interpolation. Fast and deterministic.
- **`SinCordic` / `CosCordic`** are CORDIC alternatives (32 iterations, gain K ≈ 1.6467) when you want the table-free path. **Pick one approach and use it consistently** — every peer must compute the same function the same way.
- `Atan2` is CORDIC vectoring with full quadrant + degenerate-case handling; `Asin` / `Acos` / `Atan` / `Tan` are composed from these.

---

## 6. Vectors — `FPVector2` / `FPVector3` / `FPVector4`

Plain structs of `FP64` fields (`x, y, z, w`). Example surface for `FPVector3`:

```csharp
public struct FPVector3 { public FP64 x, y, z; }
```

- **Constants** — `Zero` `One` `Up` `Down` `Left` `Right` `Forward` `Back` (2D variants drop the Z axes).
- **Properties** — `magnitude` (overflow-safe, scaled by max component), `sqrMagnitude`, `normalized`.
- **Operators** — `+ - * /` (scalar on either side), unary `-`, `== !=`.
- **Static ops** — `Dot` · `Cross` · `Distance` · `SqrDistance` · `Lerp` · `LerpUnclamped` · `Slerp` · `MoveTowards` · `RotateTowards` · `Reflect` · `Project` · `ProjectOnPlane` · `Angle` · `SignedAngle` · `ClampMagnitude` · `Min` · `Max` · `Scale` · `OrthoNormalize` · `SmoothDamp`.
- **Swizzles** — `ToXY()` / `ToXZ()` project to `FPVector2`.

```csharp
FPVector3 dir  = (target - transform.Position).normalized;
FP64      dist = FPVector3.Distance(a, b);
FPVector3 mid  = FPVector3.Lerp(a, b, FP64.Half);
```

---

## 7. Quaternions — `FPQuaternion`

```csharp
FPQuaternion q = FPQuaternion.Euler(0, yawDegrees, 0);
FPQuaternion r = FPQuaternion.AngleAxis(angle, FPVector3.Up);
FPVector3    v = q * FPVector3.Forward;          // rotate a vector
FPQuaternion s = FPQuaternion.Slerp(a, b, FP64.Half);
```

- **Construction** — `Euler(x,y,z)` / `Euler(FPVector3)` · `AngleAxis` · `LookRotation(forward[, up])` · `FromToRotation`.
- **Properties** — `eulerAngles` · `normalized` · `conjugate` · `magnitude` / `sqrMagnitude`.
- **Ops** — `*` (compose; rotate a vector) · `Dot` · `Angle` · `Inverse` · `Normalize` · `Lerp` / `LerpUnclamped` · `Slerp` / `SlerpUnclamped` · `RotateTowards`.

---

## 8. Matrices & Geometry

- **`FPMatrix2x2` / `FPMatrix3x3` / `FPMatrix4x4`** — transform matrices with transpose, inverse, and vector transforms.
- **Geometry primitives** (`Deterministic/Geometry/`) — `FPBounds2` / `FPBounds3` (AABBs), `FPRay2` / `FPRay3` (rays), `FPPlane`, `FPSphere`, `FPCapsule`, plus `FPContact` / `FPMeshData` / `ShapeType`. These back the physics and navigation modules ([PhysicsWorld.md](PhysicsWorld.md), [Navigation.md](Navigation.md)).
- **`FPHash`** — FNV-1a deterministic hashing over `FP64`/primitive values; this is what `Frame.CalculateHash()` folds component fields through ([ECS.md §7](ECS.md)).
- **`FPAnimationCurve`** — deterministic animation curves evaluated from baked keyframes (a fixed-point stand-in for `AnimationCurve`).

---

## 9. Deterministic Random — `DeterministicRandom`

A seeded **Xorshift128+** generator (state seeded via SplitMix64). It's a `struct`, so it lives in components or systems with no allocation, and its state serializes for rollback.

```csharp
var rng = new DeterministicRandom(seed);                 // from an int seed
var rng2 = DeterministicRandom.FromSeed(worldSeed, featureKey, index);  // independent stream per feature
rng.SetSeed(seed);                                       // reseed
rng.SetFullState(s0, s1);                                // restore exact state
```

Distribution methods (note the **actual** names — fixed-point uses `Fixed`, not `FP`):

| Method | Returns |
| ---- | ---- |
| `NextInt()` | `int` in `[0, 2³¹)` |
| `NextInt(min, max)` | `int` in `[min, max)` |
| `NextIntInclusive(min, max)` | `int` in `[min, max]` |
| `NextFixed()` | `FP64` in `[0, 1)` |
| `NextFixed(min, max)` / `NextFixedInclusive(...)` | `FP64` in range |
| `NextBool()` | `bool` |
| `NextChance(percent)` | `bool` at the given integer percentage |
| `NextWeighted(int[] weights)` | weighted index |
| `NextInsideUnitCircle()` / `NextInsideUnitSphere()` | `FPVector2` / `FPVector3` |
| `NextDirection2D()` / `NextDirection3D()` | unit `FPVector2` / `FPVector3` |
| `NextRotation()` | random `FPQuaternion` |
| `Shuffle<T>(T[] array)` | Fisher-Yates in place |

> **Determinism for RNG:** seed from a value every peer agrees on — Klotho injects the shared seed via the singleton `RandomSeedComponent` ([ECS.md §9](ECS.md)), restored on late-join / reconnect / spectator / replay. For independent, reproducible streams (e.g. loot vs. crit vs. spawn) derive each with `FromSeed(worldSeed, featureKey)` so consuming one stream never shifts another. Never use `System.Random`, `UnityEngine.Random`, or `GD.Randf` in simulation code.

---

## 10. Engine Conversions

Adapter extension methods bridge `FP*` to the host engine's float types **at the view boundary only**. The method names are identical on both engines; the target type differs:

| Klotho | Unity (`FP*.Unity.cs`) | Godot (`FP*.Godot.cs`) |
| ---- | ---- | ---- |
| `FPVector3` | `ToVector3()` ↔ `ToFPVector3()` (`UnityEngine.Vector3`) | `ToVector3()` ↔ `ToFPVector3()` (`Godot.Vector3`) |
| `FPRay3` | `ToRay()` → `UnityEngine.Ray` | `ToRayQuery()` → `PhysicsRayQueryParameters3D` |
| `FPPlane` | `ToPlane()` / `ToFPPlane()` | `ToPlane()` / `ToFPPlane()` (`D = −distance`) |
| `FPBounds3` | `ToBounds()` / `ToFPBounds3()` | `ToAabb()` / `ToFPBounds3()` |

Use these to position views and read input — never to round-trip simulation state through a `float`.

---

## 11. Determinism Rules (must-read)

1. **No `float`/`double` in simulation arithmetic.** Convert literals/config to `FP64` once at the boundary; do all math in `FP64`.
2. **All simulation state is `FP64` / integer / bool** — components store `FP64`, never `float` ([ECS.md §10](ECS.md)).
3. **Pick one trig path** (LUT `Sin`/`Cos` *or* CORDIC) and use it consistently across all peers.
4. **RNG is seeded and shared** — `DeterministicRandom` only, seeded from `RandomSeedComponent` or `FromSeed`; never an ambient RNG.
5. **Hash/serialize via `RawValue`** — the codec writes `FP64` as its raw `long`, so the wire/hash representation is exact (the source generator handles this for you — see [Serialization.md](Serialization.md)).

---

## 12. Worked Example — deterministic homing movement

```csharp
public class HomingSystem : ISystem
{
    static readonly FP64 ArriveDist = FP64.FromDouble(0.1);

    public void Update(ref Frame frame)
    {
        FP64 dt = FP64.FromInt(frame.DeltaTimeMs) / FP64.FromInt(1000);  // ms → seconds, in FP64

        var f = frame.Filter<TransformComponent, HomingComponent>();
        while (f.Next(out var e))
        {
            ref var t = ref frame.Get<TransformComponent>(e);
            ref readonly var h = ref frame.GetReadOnly<HomingComponent>(e);

            FPVector3 toTarget = h.Target - t.Position;
            FP64 dist = toTarget.magnitude;
            if (dist <= ArriveDist) continue;

            FPVector3 dir  = toTarget.normalized;
            FP64 step      = FP64.Min(h.Speed * dt, dist);   // don't overshoot
            t.Position    += dir * step;
            t.Rotation     = FP64.Atan2(dir.x, dir.z) * FP64.Rad2Deg;  // face travel direction
        }
    }
}
```

Every value is `FP64`/`FPVector3`; the only `int` is the engine-provided `DeltaTimeMs`, converted to `FP64` immediately. No `float` touches the computation, so every peer's `t.Position` and `t.Rotation` come out bit-identical — and the frame hash matches.
