using System;
using System.Globalization;
using System.IO;
using System.Text;
using xpTURN.Klotho.Deterministic.Math;
using xpTURN.Klotho.Deterministic.Physics;

// Cross-runtime numeric isolation probe, Part A.
// Same source compiled by Roslyn; executed under CoreCLR (net8.0, `dotnet`) and
// Unity Mono (net472, `mono`). Folds every primitive result into an FNV-1a ulong;
// diff the two runtimes' outputs. A differing fold => that primitive is NOT
// cross-runtime bit-identical (a non-determinism root-cause class).

const ulong FNV_OFFSET = 14695981039346656037UL;
const ulong FNV_PRIME = 1099511628211UL;

string outPath = args.Length > 0 ? args[0] : "probe_out.txt";
var sb = new StringBuilder();

string rt =
#if NETFRAMEWORK
    "mono/net472";
#else
    "coreclr/net8";
#endif
sb.Append("# runtime=").Append(rt).Append(" version=").Append(Environment.Version.ToString()).Append('\n');

static ulong Fold(ulong h, long v) { h ^= (ulong)v; h *= FNV_PRIME; return h; }
static long NextRaw(ref ulong s) { s = s * 6364136223846793005UL + 1442695040888963407UL; return (long)s; }

const int SWEEP = 4_000_000;

// Sqrt / Mul / Div sweep over the full raw range.
{
    ulong st = 0x1234_5678_9abc_def0UL;
    ulong hSqrt = FNV_OFFSET, hMul = FNV_OFFSET, hDiv = FNV_OFFSET;
    // Track first divergence-prone samples for pinpointing (printed as a tail).
    for (int i = 0; i < SWEEP; i++)
    {
        long ra = NextRaw(ref st);
        long rb = NextRaw(ref st);
        FP64 a = FP64.FromRaw(ra);
        FP64 b = FP64.FromRaw(rb);

        FP64 sa = FP64.FromRaw(ra < 0 ? -ra : ra);
        hSqrt = Fold(hSqrt, FP64.Sqrt(sa).RawValue);
        hMul = Fold(hMul, (a * b).RawValue);
        if (rb != 0) hDiv = Fold(hDiv, (a / b).RawValue);
    }
    sb.Append("sweep,sqrt,").Append(hSqrt.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,mul,").Append(hMul.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,div,").Append(hDiv.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

// Sin / Cos sweep.
{
    ulong st = 0x0fed_cba9_8765_4321UL;
    ulong hSin = FNV_OFFSET, hCos = FNV_OFFSET;
    long span = FP64.TwoPi.RawValue * 2;
    for (int i = 0; i < SWEEP; i++)
    {
        long ra = NextRaw(ref st);
        FP64 ang = FP64.FromRaw(ra % span);
        hSin = Fold(hSin, FP64.Sin(ang).RawValue);
        hCos = Fold(hCos, FP64.Cos(ang).RawValue);
    }
    sb.Append("sweep,sin,").Append(hSin.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,cos,").Append(hCos.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

// Inverse trig / tan / atan2 sweep (airtight primitive coverage).
{
    ulong st = 0x55aa_55aa_1234_abcdUL;
    ulong hTan = FNV_OFFSET, hAtan2 = FNV_OFFSET, hAsin = FNV_OFFSET, hAcos = FNV_OFFSET, hAtan = FNV_OFFSET;
    long span = FP64.TwoPi.RawValue * 2;
    long unit = FP64.One.RawValue; // [-1,1] domain for asin/acos
    for (int i = 0; i < SWEEP; i++)
    {
        long ra = NextRaw(ref st);
        long rb = NextRaw(ref st);
        FP64 ang = FP64.FromRaw(ra % span);
        hTan = Fold(hTan, FP64.Tan(ang).RawValue);
        hAtan = Fold(hAtan, FP64.Atan(FP64.FromRaw(ra)).RawValue);
        hAtan2 = Fold(hAtan2, FP64.Atan2(FP64.FromRaw(ra), FP64.FromRaw(rb)).RawValue);
        // asin/acos domain [-1,1]
        long d = ra % (2 * unit + 1) - unit;
        FP64 dom = FP64.FromRaw(d);
        hAsin = Fold(hAsin, FP64.Asin(dom).RawValue);
        hAcos = Fold(hAcos, FP64.Acos(dom).RawValue);
    }
    sb.Append("sweep,tan,").Append(hTan.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,atan,").Append(hAtan.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,atan2,").Append(hAtan2.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,asin,").Append(hAsin.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,acos,").Append(hAcos.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

// Targeted small-magnitude sweep (contact-like operands: |v| < a few).
{
    ulong st = 0xabcd_0123_4567_89efUL;
    ulong hSqrtS = FNV_OFFSET, hDivS = FNV_OFFSET;
    long lo = FP64.FromInt(-4).RawValue, range = FP64.FromInt(8).RawValue;
    for (int i = 0; i < SWEEP; i++)
    {
        long ra = NextRaw(ref st);
        long rb = NextRaw(ref st);
        long va = lo + (long)((ulong)ra % (ulong)range);
        long vb = lo + (long)((ulong)rb % (ulong)range);
        FP64 a = FP64.FromRaw(va < 0 ? -va : va);
        FP64 b = FP64.FromRaw(vb);
        hSqrtS = Fold(hSqrtS, FP64.Sqrt(a).RawValue);
        if (vb != 0) hDivS = Fold(hDivS, (FP64.FromRaw(va) / b).RawValue);
    }
    sb.Append("sweep,sqrt_small,").Append(hSqrtS.ToString(CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("sweep,div_small,").Append(hDivS.ToString(CultureInfo.InvariantCulture)).Append('\n');
}

// ---- Part B: box-on-ground via FPStaticCollider + static BVH (the actual non-determinism
//      bug path: _staticPairs -> _staticContacts -> MergeStaticContacts -> ResolveContact).
//      Exercises explicit-layout FPCollider union + contact-only Sqrt/Divide in the real path. ----
static void RunStep(StringBuilder o, string tag, bool useStaticCollider)
{
    var world = new FPPhysicsWorld(FP64.FromInt(10));
    var bodies = new FPPhysicsBody[useStaticCollider ? 1 : 2];

    // dynamic box — matches P2pSample exactly: halfExt 0.5, spawn y=1, mass 1,
    // friction 0 (CreateDynamic default), restitution 0.
    bodies[0].id = 2;
    bodies[0].rigidBody = FPRigidBody.CreateDynamic(FP64.One);
    bodies[0].collider = FPCollider.FromBox(new FPBoxShape(
        new FPVector3(FP64.Half, FP64.Half, FP64.Half), FPVector3.Zero));
    bodies[0].position = new FPVector3(FP64.Zero, FP64.One, FP64.Zero);
    bodies[0].rotation = FPQuaternion.Identity;

    if (useStaticCollider)
    {
        // ground static collider — matches P2pSample: 10x0.2x10 box at Y=-0.1 (top y=0),
        // friction 0.5, restitution 0, via FromFloat (exact operand match).
        var ground = new FPStaticCollider
        {
            id = 1,
            friction = FP64.FromFloat(0.5f),
            restitution = FP64.Zero,
            collider = FPCollider.FromBox(new FPBoxShape(
                new FPVector3(FP64.FromInt(5), FP64.FromFloat(0.1f), FP64.FromInt(5)),
                new FPVector3(FP64.Zero, FP64.FromFloat(-0.1f), FP64.Zero))),
        };
        world.LoadStaticColliders(new[] { ground }, 1);
        world.RebuildStaticBVH(bodies, 1);
    }
    else
    {
        bodies[1].id = 1;
        bodies[1].rigidBody = FPRigidBody.CreateStatic();
        bodies[1].rigidBody.friction = FP64.FromFloat(0.5f);
        bodies[1].collider = FPCollider.FromBox(new FPBoxShape(
            new FPVector3(FP64.FromInt(5), FP64.FromFloat(0.1f), FP64.FromInt(5)), FPVector3.Zero));
        bodies[1].position = new FPVector3(FP64.Zero, FP64.FromFloat(-0.1f), FP64.Zero);
        bodies[1].rotation = FPQuaternion.Identity;
    }

    FP64 dt = FP64.FromInt(25) / FP64.FromInt(1000); // 0.025s, matches PhysicsSystem
    var gravity = new FPVector3(FP64.Zero, FP64.FromInt(-10), FP64.Zero);
    int count = useStaticCollider ? 1 : 2;

    o.Append("# ").Append(tag).Append(",tick,posx,posy,posz,velx,vely,velz,rotx,roty,rotz,rotw\n");
    for (int tick = 0; tick < 24; tick++)
    {
        world.Step(bodies, count, dt, gravity, null, null, null);
        ref FPPhysicsBody b = ref bodies[0];
        o.Append(tag).Append(',').Append(tick).Append(',')
          .Append(b.position.x.RawValue).Append(',')
          .Append(b.position.y.RawValue).Append(',')
          .Append(b.position.z.RawValue).Append(',')
          .Append(b.rigidBody.velocity.x.RawValue).Append(',')
          .Append(b.rigidBody.velocity.y.RawValue).Append(',')
          .Append(b.rigidBody.velocity.z.RawValue).Append(',')
          .Append(b.rotation.x.RawValue).Append(',')
          .Append(b.rotation.y.RawValue).Append(',')
          .Append(b.rotation.z.RawValue).Append(',')
          .Append(b.rotation.w.RawValue).Append('\n');
    }
}

RunStep(sb, "step_staticcollider", useStaticCollider: true);
RunStep(sb, "step_staticbody", useStaticCollider: false);

File.WriteAllText(outPath, sb.ToString());
Console.Write(sb.ToString());
return 0;
