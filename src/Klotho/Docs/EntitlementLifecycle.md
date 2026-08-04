# Entitlement (Trusted Player Data) Lifecycle

> **What this is**: a guided tour of how one piece of *trusted per-player data* — what a player owns (characters, skills, consumables), how much they paid, their rank, whatever your game needs — travels through the Klotho engine, told through the reference sample code.
>
> **Companion**: [LobbyIntegrationGuide.md §9](LobbyIntegrationGuide.md) is the *how-to* (which hooks to implement, how to wire them). This doc is the *how-it-works* — follow one blob from birth to disposal.

## The one idea to hold onto

The engine treats an entitlement as an **opaque `byte[]`**. It never looks inside. Your game decides what the bytes mean and how to pack them; the engine only **carries, stores, propagates, and hands them back**.

Two rules make the whole thing trustworthy:

1. **The source is never the client.** The bytes come from a lobby you trust (SD) or from a lobby *signature* every peer can verify (P2P). A player can't invent their own entitlement.
2. **Once handed over, the bytes are frozen.** Whoever produced the `byte[]` must not touch it again — the engine and its caches share the same array by reference, and that's only safe because nobody mutates it.

---

## At a glance — the six stages

```
[origin]    born during join validation
   SD : lobby redeemTicket response   ─┐
   P2P: lobby-signed ticket payload   ─┤→ IdentityValidationOutcome.Accept(account, name, ENTITLEMENT)
                                       │        (opaque byte[], frozen from here on)
[store]     kept per-player, on the authority/peer side ◄┘   never on the roster wire
   │
[preserve]  rides the player's lifetime — survives disconnect, survives the GameStart
   │        roster rebuild, released only when the player is evicted
   │
[propagate] SD : the tick-0 seed effect ships in the Initial FullState; the raw bytes are
   │              also sent to clients so they can READ them (UI) — no re-verification
   │         P2P: the signed ticket is sent to every peer → each re-verifies the signature
   │              and re-derives byte-identical bytes for itself
   │
[read]      engine.GetPlayerEntitlement(playerId)  → three consumers:
   │           ② tick-0 loadout seed   ·   config guard (clamp)   ·   ③ in-match gate (SD)
   │
[dispose]   player evicted → per-player state gone → the byte[] is garbage-collected
```

The rest of this doc walks these six stages with the actual sample code. The samples pack the owned-set as a small bitmask struct — that's the running example throughout:

```csharp
// Samples/IdentityP2pRef/Runtime/DemoEntitlementData.cs
// A [KlothoSerializableStruct] must be unmanaged → fixed-width bitmasks. 12 bytes total.
[KlothoSerializableStruct]
public partial struct DemoEntitlementData
{
    public int OwnedClassMask;      // character class 0..3        → bit
    public int OwnedSkillMask;      // (classIdx*2 + slot), 8 bits → bit
    public int OwnedConsumableMask; // (consumableId - 100)        → bit
}
```

---

## 1. Origin — where the bytes come from

The entitlement is minted from the **verified account**, not from anything the client says. In the samples that's a pure function `ForAccount(account)`:

```csharp
// Samples/IdentitySdRef/Runtime/DemoEntitlement.cs
public static byte[] ForAccount(string account)
{
    if (IsRestricted(account))            // a "guest" account owns less
        return Encode(new DemoEntitlementData {
            OwnedClassMask      = AllClassesMask, // all 4 classes...
            OwnedSkillMask      = 0b01010101,     // ...but only skill slot 0 of each
            OwnedConsumableMask = 0,              // ...and no consumable
        });

    return Encode(new DemoEntitlementData {       // everyone else: the full loadout
        OwnedClassMask      = AllClassesMask,
        OwnedSkillMask      = FullSkillMask,
        OwnedConsumableMask = 1,
    });
}
```

**Why it's a pure function of the account:** a reconnect, a late-join, or (in P2P) each peer re-running this must produce the *exact same bytes*. Purity guarantees that. (If it depended on wall-clock, RNG, or dictionary order, peers would disagree and the match would desync — see the rules at the end.)

Now the bytes enter the engine. Both modes funnel through the same door — `IdentityValidationOutcome.Accept(...)`:

| | SD (dedicated server) | P2P (host / guest) |
|---|---|---|
| Where they come from | the lobby's `redeemTicket` reply | the lobby **signs them into the ticket** |
| How they enter | the validator returns `Accept(account, name, entitlement)` | the validator extracts them after verifying the signature; each guest re-extracts from the propagated ticket |
| Why we trust them | the server is the trust anchor — an online lobby lookup is fine | only the lobby *signature* is trusted (not the host relay) — same signed ticket ⇒ same bytes on every peer |

```csharp
// SD: the redeem reply carries the bytes; the validator hands them to the core.
// Samples/IdentitySdRef/Runtime/SdRedeemIdentityValidator.cs
handle.SetResult(IdentityValidationOutcome.Accept(r.Account, r.DisplayName, r.Entitlement));
```

```csharp
// P2P: the lobby signs the SAME bytes into the ticket payload, so one signature
// covers both identity and entitlement. Samples/.../BrawlerDevIdentity.Server.cs
var ticket = new LobbyTicket(account, displayName, sessionId, nowMs, expiresAt, nonce,
    entitlement: DemoEntitlement.ForAccount(account));
```

> **The frozen-bytes contract.** After you pass a `byte[]` to `Accept(...)`, don't mutate or reuse it. Unlike the `string` fields next to it, a `byte[]` *can* be changed — so this is a promise you make, not something the language enforces. The engine shares the reference downstream; the promise is what keeps that safe.

---

## 2. Store — how the engine keeps them

The bytes are held **per player, on the authority/peer side**, and deliberately kept **off the wire**:

```csharp
// The ONLY way to read an entitlement. It is NOT a field on IPlayerInfo,
// so it is never serialized into RosterEntry and never leaks to other clients.
byte[] GetPlayerEntitlement(int playerId);   // com.xpturn.klotho/Runtime/Core/ILockstepEngine.cs
```

Who stores what:

- **SD server** — the bytes from the redeem reply (or `null` if no validator ran).
- **P2P host** — the bytes extracted from the joining peer's verified ticket.
- **P2P guest** — the bytes it **re-derived itself** from the propagated ticket. A guest keeps the derived entitlement, *not* the ticket — it re-verifies each time rather than hoarding the credential.

Keeping it off `IPlayerInfo` is the whole point: a large or private owned-set never rides the roster to other players.

---

## 3. Preserve — it lives as long as the player does

There's no separate bookkeeping. The entitlement is attached to the player and shares the player's fate:

- **Across a disconnect** — kept, so a reconnecting player gets the same data back. Released only when the player is *evicted*.
- **Across the GameStart roster rebuild** — when the match starts, the roster is rebuilt from the player-id set, which doesn't itself carry entitlements. The engine deliberately **retains** them across that rebuild (by reference — safe because they're frozen). Skip this and a late-join *after* GameStart would carry an **empty** entitlement → a missing seed → desync.
- **P2P host's own entitlement** — the host derives it through the **same signature-only path** the guests use, *not* the full enforcing validator. If it used the enforcing path (which can bail out early on expiry/nonce), only the host's bytes would be empty and its tick-0 seed would disagree with everyone else's → desync.
- **Reconnect** — nothing special: the entitlement is preserved on the player just like `Account`/`DisplayName`. On SD the seed is restored by full-state resync; on P2P the ticket is re-bundled in the reconnect accept so the peer re-derives it.

---

## 4. Propagate — this is where SD and P2P differ most

| | SD (dedicated) | P2P (host) |
|---|---|---|
| **What travels** | the tick-0 seed **effect** (baked into the Initial FullState) **plus** the verified **bytes themselves** (so clients can read them, e.g. for UI) | the **original signed ticket** (not the bytes) goes to every peer |
| **How** | seed effect via the Initial FullState; the raw bytes ride the identity channel per-player | rides the identity channel — roster sync, join/late-join notifications, late-join/reconnect accepts |
| **Verification on arrival** | none — it's server-trusted | each peer runs `IPropagatedTicketVerifier.ReverifyPropagatedTicket` — **signature only** (expiry/nonce/session were already checked at join) |
| **When it's on** | always (server authority) | **only if an entitlement hook is configured** — otherwise nothing changes (no regression) |

> **Why P2P works this way:** peers don't trust the host to relay honest data, so trusted data must survive *lobby signature + delivery to every peer + independent per-peer re-verification*. Forgery can't get through. A genuine **omission** (a missing seed) shows up as a tick-0 desync — which safely aborts that match instead of letting peers silently diverge.

---

## 5. Read & use — one accessor, three consumers

Everyone reads through the same call, then decodes. The decoder is lenient on purpose:

```csharp
// null / empty / corrupt bytes → "all owned" → no gating.
// This is what makes the whole feature opt-in: no lobby wired ⇒ entitlement is null ⇒ nothing is gated.
var ent = DemoEntitlement.Decode(engine.GetPlayerEntitlement(playerId));
```

Who gets what back from `GetPlayerEntitlement`:

| Caller | Returns | Note |
|---|---|---|
| **SD server** | the stored redeem bytes | the authoritative copy |
| **SD client** | the propagated bytes — **read-only** (for UI) | `null` until they arrive, or if no validator ran. The client must **never** re-seed simulation from these — the seed is authored server-side (below) |
| **P2P peer** | the bytes it re-derived | every peer verified and derived its own |

There are exactly three places entitlements have an effect. Here's each, with its real code.

### ② Start-of-match loadout seed

Runs inside `OnInitializeWorld` (before `SaveSnapshot(0)`), pushing each player's owned-set *into* the deterministic simulation state as a `LoadoutSeedComponent`:

```csharp
// Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerSimSetup.cs
public static void SeedOneLoadout(ref Frame frame, IKlothoEngine engine, int pid)
{
    if (HasLoadoutSeed(ref frame, pid)) return;              // idempotent → rollback-safe

    var ent = DemoEntitlement.Decode(engine.GetPlayerEntitlement(pid)); // null → all owned

    var seed = frame.CreateEntity();
    frame.Add(seed, new LoadoutSeedComponent {
        PlayerId            = pid,
        OwnedSkillMask      = ent.OwnedSkillMask,
        OwnedConsumableMask = ent.OwnedConsumableMask,
    });
}
```

Two details that keep it deterministic and reusable:

- It iterates the authoritative `SessionParticipantComponent` slots — **not** a local `maxPlayers` guess — so every peer seeds the exact same set of entities.
- The **same** `SeedOneLoadout` handles a late-joiner via `OnPlayerJoinedWorld`. The `HasLoadoutSeed` guard makes re-entry harmless.

On SD this runs on the server, and the result ships to clients inside the Initial FullState — `OnInitializeWorld` doesn't run on an SD client at all, so a client never re-seeds.

### Config guard — clamp an illegal selection

Cross-checks a client's character pick against the owned classes. Own it → keep it; don't → clamp to a server-decided default:

```csharp
// Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerPlayerConfigEntitlementGuard.cs
public PlayerConfigVerdict Check(int playerId, byte[] entitlement, PlayerConfigBase selection)
{
    if (!(selection is BrawlerPlayerConfig cfg)) return PlayerConfigVerdict.Pass();

    int classMask = DemoEntitlement.Decode(entitlement).OwnedClassMask;
    int cls = cfg.SelectedCharacterClass;

    if ((uint)cls < 32u && (classMask & (1 << cls)) != 0)
        return PlayerConfigVerdict.Pass();          // owned → keep the pick

    if (classMask == 0) return PlayerConfigVerdict.Pass();   // owns nothing → nothing to clamp to
    return PlayerConfigVerdict.Clamp(                        // unowned → lowest owned bit,
        new BrawlerPlayerConfig { SelectedCharacterClass = LowestSetBit(classMask) }); // deterministic
}
```

"Lowest owned bit" is a deterministic *first owned class* — so SD's single server decision and P2P's per-peer decisions all land on the same answer.

### ③ In-match gate — SD only

Drops an unowned command *before it reaches an authoritative tick*. The sample only gates consumable use; everything else passes:

```csharp
// Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerReliableCommandEntitlementGate.cs
public ReliableCommandVerdict Check(int playerId, byte[] entitlement, ICommand command)
{
    if (!(command is UseConsumableCommand use)) return ReliableCommandVerdict.Accept(); // e.g. spawn

    int mask = DemoEntitlement.Decode(entitlement).OwnedConsumableMask;
    int bit  = use.ConsumableId - DemoEntitlement.ConsumableId;   // id 100 → bit 0

    return ((uint)bit < 32u && (mask & (1 << bit)) != 0)
        ? ReliableCommandVerdict.Accept()
        : ReliableCommandVerdict.Drop();            // unowned → the use simply never happens
}
```

The player just sees "nothing happened." On the client side, `SendUseConsumableCommand` issues on the reliable channel and resolves its handle once the effect shows up in the frame — if the server dropped it, that never comes.

**Why P2P has no gate:** it doesn't need one. The owned-set is *already in the simulation state* (from the seed), so an unowned action no-ops identically on every peer without anyone having to police commands.

---

## 6. Dispose — nothing to clean up

When a player is evicted, its per-player state is dropped and the `byte[]` is garbage-collected — no explicit clear. Because the bytes were frozen, any shared reference (like the seed retained across the GameStart rebuild) stays valid right up until then.

---

## Rules to keep in mind

1. **Never on the roster wire.** The entitlement (and, in P2P, the ticket) live outside `IPlayerInfo`, so they never end up in `RosterEntry`. `GetPlayerEntitlement` is the only door.
2. **Frozen once published.** The `byte[]` you hand the core is immutable thereafter. That's what makes caches and the seed path safe to share it by reference.
3. **P2P must be bit-identical everywhere.** The seed, the clamp, and the in-match no-op all have to produce the same result on every peer. No RNG, no floats, no culture-sensitive or dictionary-order logic — any of those desyncs tick-0.
4. **Fully opt-in.** No hook configured → `GetPlayerEntitlement` returns `null` → guard/gate skipped, P2P propagation off, `PlayerConfig` behaves exactly as before.
5. **SD client bytes are read-only.** A client holds the propagated bytes for display (UI), but the *seed* is authored server-side and delivered in the Initial FullState. Client code must treat `GetPlayerEntitlement` as read-only and never re-seed simulation from it.

---

## Why the core boundary is `byte[]`

The core never parses the payload — three reasons:

1. The entitlement is **lobby-authored** and may come from a non-Unity backend, so tying it to C# code generation would be wrong.
2. In P2P the signature is verified over the **original bytes** (verify-over-wire), so the core must not re-serialize them.
3. A byte-identical blob makes cross-peer determinism automatic.

The *game* picks the payload format. The samples use the fixed-width `DemoEntitlementData` bitmask because it's compact (12 bytes, fits the P2P pre-auth budget) and trivially deterministic. The codec lives entirely in the game assembly:

```csharp
// Samples/IdentityP2pRef/Runtime/DemoEntitlement.cs — the byte[] ↔ struct boundary.
public static byte[] Encode(in DemoEntitlementData d) { /* generated Serialize */ }

public static DemoEntitlementData Decode(byte[] entitlement)
{
    if (entitlement == null || entitlement.Length == 0) return Full; // "all owned"
    try { /* generated Deserialize */ }
    catch { return Full; }   // lenient: a corrupt blob does not gate
}
```

The core boundary stays `byte[]` no matter what you choose — everything in stages 1–6 is unchanged by the encoding.

---

## The full picture — SD wired end-to-end

Three projects, one blob:

**① The lobby produces it** — [`Samples/DevLobbyServer`](../Samples/DevLobbyServer/Program.cs). A plain console process (not a game server). On a redeem request it calls `DemoEntitlement.ForAccount(account)` and packs the bytes into the reply:

```csharp
// Samples/IdentitySdRef/Runtime/DevLobbyCore.cs
var result = RedeemResult.Accept(p.Account, p.DisplayName, DemoEntitlement.ForAccount(p.Account));
```

**② The dedicated server stores it and registers the consumers** — [`Samples/Brawler/Server/Program.cs`](../Samples/Brawler/Server/Program.cs):

```csharp
roomManagerConfig.PlayerConfigEntitlementGuard  = new BrawlerPlayerConfigEntitlementGuard();
roomManagerConfig.ReliableCommandEntitlementGate = new BrawlerReliableCommandEntitlementGate();

if (lobbyEnabled)  // --lobby host:port
    roomManagerConfig.IdentityValidator = SdDevIdentity.CreateValidator(/* ..., redeemClient, ... */);
```

The guard and gate are *always* registered but stay inert until a lobby fills in the bytes — with no `--lobby`, `GetPlayerEntitlement` is `null` and nothing gates.

**③ The simulation reads it** — [`Samples/Brawler/Assets/Brawler`](../Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerSimSetup.cs), the three consumers shown in §5.

One last connection: the in-editor P2P host path ([`BrawlerDevIdentity.Server.cs`](../Samples/Brawler/Assets/Brawler/Scripts/Manager/BrawlerDevIdentity.Server.cs)) signs those **same** `ForAccount` bytes into the ticket. One entitlement format, both modes.

> **Want to see it running?** For a step-by-step play test — start the dev lobby, run the dedicated server with `--lobby`, enable the lobby on the client, and watch a `"guest"` account get gated — see [LobbyIntegrationGuide §9.8](LobbyIntegrationGuide.md).
