# Lobby ↔ Dedicated Server ↔ Client Integration Guide (Mockup)

> **Level**: Conceptual / mockup. The goal is to show the flow, the responsibility boundaries, and the integration points — the actual crypto/transport library is the game's choice. The code here is **illustrative of the integration shape**, not a production implementation.
>
> **Reference**: the formal spec is [Specification.md §9.6 Player Identity Handoff](Specification.md). **Trusted player data (entitlements)** — inventory / owned characters / loadout that rides the *same* verified channel — is covered here in §9.
>
> **Working reference implementations** (the mockups here map to real, tested code):
> - Lobby server — [`Samples/DevLobbyServer/`](../Samples/DevLobbyServer) (ticket issue + SD redeem + match-result intake §4-F)
> - P2P validator — [`Samples/IdentityP2pRef/`](../Samples/IdentityP2pRef) (offline signature + propagated-ticket re-verify §9)
> - SD validator — [`Samples/IdentitySdRef/`](../Samples/IdentitySdRef) (online redeem)
> - Entitlement codec / payload — [`DemoEntitlement`](../Samples/IdentitySdRef/Runtime/DemoEntitlement.cs) (shared by SD + [P2P](../Samples/IdentityP2pRef/Runtime/DemoEntitlement.cs)) over a [`DemoEntitlementData`](../Samples/IdentityP2pRef/Runtime/DemoEntitlementData.cs) `[KlothoSerializableStruct]` bitmask (§9)
> - Entitlement guard / gate — Brawler [`BrawlerPlayerConfigEntitlementGuard`](../Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerPlayerConfigEntitlementGuard.cs) (① clamp) / [`BrawlerReliableCommandEntitlementGate`](../Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerReliableCommandEntitlementGate.cs) (③ drop) + in-match [`UseConsumableCommand`](../Samples/Brawler/Assets/Brawler/Scripts/ECS/Commands/UseConsumableCommand.cs) demo (§9)
> - Identity keys — [Samples/DevIdentityKeys.md](Samples/DevIdentityKeys.md) (generate / rotate the signing key pair)
>
> **No-library principle**: This guide names no specific crypto/network library. `Sign()` / `Verify()` (signing) and `PostJson()` (lobby calls) are **seams the game fills in**. The only thing the core mandates is the **contract (interface)**.

---

## 1. What Gets Integrated

The Klotho core intentionally **does not contain a lobby / matchmaking / authentication** (3-layer separation, [Specification.md §Network Layer Separation](Specification.md)). All the core provides is the **carriage · validation hook · propagation** of the credential that crosses this boundary; the actual signing, redeem, and crypto live in the **Game Service Layer** (the lobby server + the hooks the game implements).

```
┌────────────────────────────────────────────────┐
│         Lobby / Auth Server (outside core)     │
│  · login auth → account confirmed              │
│  · matchmaking → session (server/room) assign  │
│  · issue ticket (sign)    ─────────────┐       │
│  · (SD) redeem verify API ◄────────┐   │       │
└─────────────────────────────────── │ ──│───────┘
            ▲ (1) login              │   │ (2) ticket + endpoint
            │                        │   ▼
       ┌────┴──────┐          ┌──────┴─────────────────┐
       │  Client   │ (3) join │   Klotho session       │
       │(has ticket)─────────►│   carries ticket in    │
       └───────────┘          │   handshake            │
                              └──────────┬─────────────┘
                                         │ (4) call validation hook
                    ┌────────────────────┴────────────────────┐
                    ▼ SD (dedicated)                          ▼ P2P
        dedicated server verifies online        each host verifies the lobby
        via lobby redeem → account              signature offline → account
                    │                                         │
                    └──────────────┬──────────────────────────┘
                                   ▼
              (5) propagate verified Account/DisplayName to every peer via roster
```

Responsibilities of the three parties:

| Party | Responsibility | Touchpoint with Klotho |
|---|---|---|
| **Lobby server** (game-built) | Login auth, match assignment, **ticket issue (sign)**, (SD) **redeem verify** | None — never talks to the core directly. Called by client/dedicated |
| **Client** (game-built) | Present the lobby-issued ticket at join | `IPlayerIdentityProvider.GetTicket()` |
| **Dedicated server / P2P host** (game-built) | Validate the join ticket → confirm & propagate authoritative account/name | `IPlayerIdentityValidator.BeginValidate(...)` |

---

## 2. End-to-End Sequence (SD / Dedicated)

```
Client               Lobby Server         Dedicated Server      (other clients)
   │                     │                    │                        │
   │── login(authToken) ►│                    │                        │
   │◄ account confirmed ─│                    │                        │
   │                     │                    │                        │
   │── requestMatch ────►│  matchmaking       │                        │
   │                     │  session/server    │                        │
   │◄ {ticket, endpoint, roomId, mode:"SD"} ─ signed ticket            │
   │                     │                    │                        │
   │════════════ Connect(endpoint) + carry ticket in handshake ═══════►│
   │                     │                    │ (4) validation hook:   │
   │                     │◄ redeemTicket ─────│   local 1st (sig/exp)  │
   │                     │  nonce consume/ban │   → lobby redeem       │
   │                     │─ ok{account,name} ►│                        │
   │                     │                    │ reserve slot + roster  │
   │◄══ join accepted + propagate authoritative account/DisplayName ══►│ (5)
   │                     │                    │                        │
   (on failure: send JoinReject(reason), then disconnect — no slot consumed)
```

P2P has no redeem round-trip at (4); the **host verifies the lobby signature offline** with the lobby public key (§5). The rest of the flow is identical.

---

## 3. Ticket Structure (Concept)

Ticket = `signed payload`. The lobby fills the payload and signs it with the lobby private key. **The core treats the ticket as opaque bytes** — it never parses it, so the game is free to add fields in its own encoding with zero core changes. Only the minimal set needed for validation and replay defense is part of the contract.

```
LobbyTicket (logical structure; serialization/encoding is the game's choice)
├─ account      : stable account id (invariant across sessions)
├─ displayName  : display name
├─ sessionId    : this match/session identifier (binds ticket ↔ session)
├─ issuedAt     : issue time
├─ expiresAt    : expiry time (replay / late-join defense)
├─ nonce        : one-time value (replay defense)
├─ entitlement  : (optional, §9) opaque trusted player-data blob — owned characters/loadout, etc.
└─ signature    : signature over the fields above (lobby key)
```

- **Signing scheme**: asymmetric signing recommended — only the lobby *public* key is distributed to clients/servers, so there is no shared-secret leak risk (a symmetric key embedded in every client invites forgery). *The exact algorithm/library is the game's choice.*
- **Carriage encoding**: the handshake carries it as a single `string` (e.g. base64url). The inner serialization (JSON/binary/etc.) is the game's choice.
- **Verified extra fields** (MMR/level, etc.): add them as game fields *outside* the minimal set (the core is opaque, so zero changes). Mutable/unverified game data (character/team/cosmetics) goes through the separate `PlayerConfig` channel — keep it out of identity.
- **Entitlement** (trusted player data, §9): one *signed* blob covered by the same signature — no extra key. In **P2P** it rides the ticket payload (each peer verifies offline); in **SD** the lobby returns it from `redeemTicket` instead (the server is the trust anchor). Keep it small (P2P shares the `MaxPreAuthMessageBytes` 4096 B pre-auth budget). This is the trusted counterpart to the untrusted `PlayerConfig` — see §9.

---

## 4-A. Lobby Server (Mockup)

An **external service** that never talks to the core directly. It only needs two endpoints. Transport (gRPC/REST/other) and serialization are the game's choice.

### issueTicket — on login / match success (lobby → client)

```
POST /lobby/issueTicket
  req:  { authToken, matchId }
  res:  { ticket, endpoint:"host:port", roomId, mode:"SD"|"P2P" }   // roomId: SD multi-room assignment (§4-D)
```

```csharp
// Mockup — the game fills in the real auth/matchmaking/signing
IssueTicketResponse IssueTicket(IssueTicketRequest req)
{
    var account = Authenticate(req.AuthToken);          // ← game's auth
    var match   = AssignMatch(account, req.MatchId);    // ← game's matchmaking

    var payload = new TicketPayload {
        Account     = account.Id,
        DisplayName = account.DisplayName,
        SessionId   = match.SessionId,        // the match/session this ticket binds to
        IssuedAt    = Now(),
        ExpiresAt   = Now() + TicketTtl,      // keep short — especially for P2P
        Nonce       = NewNonce(),             // one-time
        Entitlement = (match.Mode == "P2P")   // (§9) P2P: sign the trusted blob INTO the ticket (offline verify).
                    ? EncodeEntitlement(account.OwnedForMatch(match)) // small: loadout, not full inventory
                    : null,                   // SD: omit here — returned from redeemTicket instead
    };
    var ticket = Encode(payload, Sign(payload, LobbyPrivateKey));   // ← signing seam

    return new IssueTicketResponse {
        Ticket   = ticket,                    // opaque string the client carries in the handshake
        Endpoint = match.ServerEndpoint,      // dedicated address (SD) / host address (P2P)
        RoomId   = match.RoomId,              // SD multi-room: the assigned room (client routes to it) — §4-D
        Mode     = match.Mode,                // "SD" | "P2P"
    };
}
```

### redeemTicket — dedicated server delegating join verification (dedicated → lobby, **SD only**)

```
POST /lobby/redeemTicket
  req:  { ticket, sessionId, serverId, roomId }   // roomId: the room the peer was routed to (§4-D)
  res:  { ok:true, account, displayName, entitlement? } | { ok:false, reason }   // entitlement: opaque trusted blob (§9)
```

```csharp
// Mockup — only the SD dedicated server calls this. P2P never does (offline verify).
RedeemResponse RedeemTicket(RedeemRequest req)
{
    var payload = Decode(req.Ticket);
    if (!Verify(payload, payload.Signature, LobbyPublicKey))   // ← verify seam
        return RedeemResponse.Fail("bad-signature");
    if (payload.ExpiresAt <= Now())
        return RedeemResponse.Fail("expired");

    // Match binding: the ticket's own sessionId is authoritative. The lobby checks that this match is
    // assigned to the redeeming (serverId, roomId) — blocks ticket reuse across matches/servers/rooms (§4-D).
    if (!IsMatchAssignedTo(payload.SessionId, req.ServerId, req.RoomId))
        return RedeemResponse.Fail("session-mismatch");   // wire 8 — also covers wrong room

    // nonce consume — idempotency window: a repeat within the window returns the cached result
    // (recovers a player who passed validation but dropped before slot reservation). Beyond it: replay reject.
    if (!ConsumeNonceIdempotent(payload.Nonce, out var cached))
        return RedeemResponse.Fail("replay");
    if (IsBanned(payload.Account))                              // real-time ban applied here
        return RedeemResponse.Fail("banned");

    // (§9) attach the account's trusted player data in the SAME redeem snapshot (atomic with account/name).
    // Deterministic per account so a reconnect/late-join re-redeem recovers the identical blob.
    var entitlement = EncodeEntitlement(LookupOwnedForMatch(payload.Account, payload.SessionId));
    return RedeemResponse.Ok(payload.Account, payload.DisplayName, entitlement);
}
```

> **Mockup note**: an **idempotency window** instead of strict single-use — a player who passed validation but dropped right before slot reservation must be recoverable on rejoin. Keep the window short, and pair it with "account active in this match" tracking.

---

## 4-B. Client (Mockup)

The client just **holds the ticket and presents it at join**. Carriage, propagation, and UI updates are handled by the core.

### (1) Ticket provider — `IPlayerIdentityProvider`

```csharp
// Game-implemented. The core calls GetTicket() once and puts it in the handshake (opaque).
public sealed class LobbyTicketProvider : IPlayerIdentityProvider
{
    private readonly string _ticket;     // the string kept from the issueTicket response
    public LobbyTicketProvider(string ticket) => _ticket = ticket;
    public string GetTicket() => _ticket;   // null/"" → behaves as "no lobby"
}
```

### (2) Register on the builder → join

```csharp
// after the lobby response: { ticket, endpoint, roomId, mode }
var setup = new KlothoFlowSetupBuilder()
    .WithHandshake(transport, endpoint)              // existing join flow
    .WithLobbyIdentity(new LobbyTicketProvider(ticket))   // ← register the ticket provider
    .Build();

// On Connect the core carries GetTicket()'s result as PlayerJoinMessage.Ticket
await session.Connect(...);
```

### (3) Receive authoritative identity / handle rejection

Once the join is accepted, every peer's authoritative `Account`/`DisplayName` is propagated via the roster and exposed through `IPlayerInfo`. The UI reads that (no local fabrication).

```csharp
foreach (var p in session.Players)
    ShowPlayerRow(p.PlayerId, p.DisplayName, p.Account);   // authoritative values

// On validation failure the core receives a JoinReject → disconnects. Branch on the reason code.
session.OnJoinFailed += reason => {
    // JoinFailReason enum (client-local). See the mapping table in §6.
    ShowError(reason);   // e.g. IdentityExpired → "Your ticket expired. Please re-queue."
};
```

> **No lobby (LAN/dev)**: skip `WithLobbyIdentity` and the ticket is carried empty and the validation hook is not invoked — **identical to current behavior**. If you just want a nickname shown, the client may present an unverified `ClaimedDisplayName` (ignored when a validator is present, so it is spoof-proof in production).

---

## 4-C. Dedicated Server / P2P Host (Mockup)

Where the authority validates the join ticket. Implement `IPlayerIdentityValidator` and register it on the builder. The core invokes the hook **before slot reservation** (a reject consumes no slot), so validation is a one-shot join event.

### Core contract

```csharp
public interface IPlayerIdentityValidator
{
    // Called on the network loop. Returns a pollable handle.
    IIdentityValidation BeginValidate(in IdentityValidationRequest request);
}

public interface IIdentityValidation : IDisposable
{
    bool IsComplete { get; }              // completion (always true for synchronous P2P)
    IdentityValidationOutcome Outcome { get; }   // result once complete
}
// Result: IdentityValidationOutcome.Accept(account, displayName) / Reject(wireCode)
```

`IdentityValidationRequest` carries all the context a validator needs: `Ticket`, `ClaimedDisplayName`, `SessionMagic` (session binding), `PeerId`, `DeviceId`, `IsLateJoin`, `IsHostSelf`, `RoomId` (the room the peer was routed to — SD multi-room; `-1` when not room-scoped, see §4-D).

### SD (dedicated) — online redeem (asynchronous)

A redeem is a network round-trip, so to **not block the loop** it returns an incomplete handle and completes it from a background thread (the core polls `IsComplete` each tick).

```csharp
public sealed class SdRedeemValidator : IPlayerIdentityValidator
{
    public IIdentityValidation BeginValidate(in IdentityValidationRequest req)
    {
        // Do not retain the `in` struct — copy by value (strings are immutable → safe on a bg thread)
        var ticket = req.Ticket;
        var sessionMagic = req.SessionMagic;
        var roomId = req.RoomId;   // routed room (SD multi-room) — carried into redeem for cross-check (§4-D)

        if (string.IsNullOrEmpty(ticket))
            return Completed(Reject(10));   // IdentityRequired

        var handle = new PendingValidation();
        RunBackground(async () => {
            // local 1st pass: signature/expiry (reject obvious forgeries early) — signing lib is a seam
            var payload = Decode(ticket);
            if (!Verify(payload, payload.Signature, LobbyPublicKey)) { handle.Complete(Reject(6)); return; }
            if (payload.ExpiresAt <= Now())                          { handle.Complete(Reject(7)); return; }

            // authority: lobby redeem (nonce consume / ban / match binding) — transport is a seam
            var res = await PostJson("/lobby/redeemTicket",
                new { ticket, sessionId = MapSession(sessionMagic), serverId = ServerId, roomId });

            if (!res.ok)                  { handle.Complete(Reject(MapReason(res.reason))); return; } // out-of-range codes clamp to 9
            if (IsBadAccount(res.account)) { handle.Complete(Reject(6)); return; }  // empty / >62 UTF-8 B → identity collision
            handle.Complete(Accept(res.account, res.displayName));
        });
        return handle;   // incomplete → the core polls it
    }
}
```

`PendingValidation` writes the outcome first, then sets `IsComplete` with a release write (the threading contract). `Dispose` cancels an in-flight redeem (peer dropped before the nonce was consumed) — idempotently.

### P2P (host) — offline signature verify (synchronous)

No network call. Returns an already-complete handle.

```csharp
public sealed class P2pSignatureValidator : IPlayerIdentityValidator
{
    private readonly HashSet<string> _seenNonces = new();   // session-scoped nonce

    public IIdentityValidation BeginValidate(in IdentityValidationRequest req)
    {
        if (string.IsNullOrEmpty(req.Ticket)) return Completed(Reject(10));

        // verify-over-wire: split the wire + base64-decode to RAW bytes, verify the signature over those
        // bytes, and parse the payload ONLY after verify passes (never deserialize an unverified ticket).
        var (rawPayload, signature) = DecodeWire(req.Ticket);
        if (!Verify(rawPayload, signature, LobbyPublicKey))  return Completed(Reject(6));  // signature/pubkey seam
        var p = ParsePayload(rawPayload);                                                  // parse after verify
        if (p.SessionId != ExpectedSessionId(req))           return Completed(Reject(8));  // session mismatch
        if (p.ExpiresAt <= Now())                            return Completed(Reject(7));  // expired
        if (IsBadAccount(p.Account))                         return Completed(Reject(6));  // empty / >62 UTF-8 B → identity collision
        if (!_seenNonces.Add(p.Nonce))                       return Completed(Reject(9));  // in-session replay (session-scoped; prune expired in a long session)

        return Completed(Accept(p.Account, p.DisplayName));
    }
}
```

> **Semi-trust (identity-only default) → full zero-trust (with entitlements)**: with *only* identity configured, the P2P host is the single verifier and guests trust the host-relayed `Account`/`DisplayName` — the original ticket is **not** propagated (only the 2 authoritative strings are), keeping it lightweight. This remains the default. **When the entitlement hook is configured (§9)**, the core turns on original-ticket propagation: each peer's *original signed ticket* rides the roster / join notifications / late-join & reconnect accepts, and every guest **re-verifies it independently** via `IPropagatedTicketVerifier` (signature-only). That closes the semi-trust gap — a cheating host can no longer forge identity **or** entitlement — and identity is strengthened to full zero-trust *as a side effect* of enabling entitlements. Identity-only games see no propagation and no regression.

### Builder registration

```csharp
// dedicated (SD)
new KlothoFlowSetupBuilder()
    .WithHandshake(transport, listenEndpoint)
    .WithIdentityValidator(new SdRedeemValidator())
    .Build();

// P2P host
new KlothoFlowSetupBuilder()
    .WithHandshake(transport, listenEndpoint)
    .WithIdentityValidator(new P2pSignatureValidator())
    .Build();
```

---

## 4-D. roomId Trust (SD Multi-Room)

On a multi-room dedicated server (one server hosting several independent matches), the client must say **which room** it is joining. That room id travels an **untrusted path**, so it is treated as a routing hint — the *authority* is the lobby's assignment ledger, not the client's claim.

**Flow**

```
(1) lobby assigns the match → picks a (serverId, roomId), records  sessionId → (serverId, roomId),
    and returns roomId in the issueTicket response
(2) client routes:  RoomHandshakeMessage{ roomId }   ← FIRST message, pre-join, NO access control
(3) the routed room reaches the validator as  IdentityValidationRequest.RoomId
(4) the SD validator carries it into the redeem:  redeemTicket{ ..., roomId }
(5) lobby cross-checks the routed roomId against the room bound to sessionId
        match    → accept
        mismatch → Reject(8), no slot consumed (the hook runs before slot reservation)
```

**Why roomId is not trusted on its own**: routing (`RoomHandshake`) happens **before** validation (`PlayerJoin` + ticket), and the router applies no access control — a client can route to any in-range room id (an unknown one is even lazily created). This is the same client-asserted problem as a spoofed display name. The defense has the same shape as cross-match defense, narrowed to room granularity: trust flows from the **signed** `sessionId` bound to a `(serverId, roomId)`.

**Tamper outcomes**

| Client behavior | Result |
|---|---|
| routes to the assigned roomId | normal join |
| routes to a different room (same server) with its own ticket | redeem (server, room) mismatch → **Reject(8)**, no slot consumed |
| routes to an in-range room the server hasn't created yet | room is lazily created, but the binding mismatch still rejects at redeem |
| routes to an out-of-range roomId | router rejects (RoomNotFound) before redeem |
| no lobby (validator off) | no defense — any room (LAN/dev intent) |

**Granularity**: the binding is **per match** — `sessionId` (= matchId) binds the whole match's players to one `(server, room)`, so the 2nd..Nth player of the same match share the room (one room serves one match). Single-room SD binds to room `0`; P2P is not multi-room and passes a `-1` sentinel the validator ignores (the `RoomId` field is append-only opt-in — a validator that does not read it is unaffected).

> **Mockup note**: the lobby's cross-check is authoritative on its own. A local fast-reject in the validator (rejecting before the redeem round-trip) would need the *expected* room in the ticket/redeem result — not carried today, so it is deferred. The redeem cross-check already closes the hole.

---

## 4-E. Per-Room Match Config & Reservation (SD Multi-Room)

A multi-room server can host each room on a **different stage** (map / level) with its own **per-match config** (game mode, rules, difficulty). The room's config is resolved at creation time by an `IMatchConfigSource` the server supplies, then carried to every peer on the existing config channel — so a joiner builds the same stage without any client-side lookup.

**Config source**

- `MatchConfigContext` = `{ RoomId, StageId, MatchConfigData }` — the stage selector (`StageId`, a scalar) plus an opaque, game-defined per-match payload (`MatchConfigData`, a `byte[]`).
- `IMatchConfigSource.TryResolve(roomId, out cfg)` runs synchronously on the room-creation thread (no blocking I/O); returning `false` **refuses the room** — the client is turned away with room-not-found instead of a room being created blind.
- Lobbyless: `StaticMatchConfigSource` maps room ids → (stage, payload) from a fixed table. Lobby-backed: a source populated by reservations (below).

**Lobby reservation — reserve before connect**

```
(1) lobby assigns the match → picks (serverId, roomId) AND the room's match config (stage + payload)
(2) lobby pushes the reservation to the assigned server, and WAITS for the server to confirm
(3) only on confirm does the lobby mint the ticket + return the endpoint/roomId to the client
(4) client connects → server creates the room from the reserved config → propagates to all peers
```

**Why reserve first**: the client only ever receives an endpoint for a room the server has already set up, and a room the lobby never reserved is refused — this stops an unauthenticated peer from claiming a room ahead of the players the lobby placed there (narrower companion to the `sessionId → (server, room)` binding in §4-D). Only the assigned server can confirm its own reservation; a reconnecting server has its active reservations re-pushed; and because the ticket is minted only after the reservation commits, a reservation that is rolled back leaves no usable ticket behind.

**No lobby**: the server's own `StaticMatchConfigSource` decides the room→stage mapping (or none is wired). With no source at all, rooms are created open exactly as before — see §7.

> **Mockup note**: the reserve-push channel lives in the reference SD lobby sample, not the core. `StageId` 0 with empty `MatchConfigData` is the single-stage default, so a game that does not use stages is unaffected.

---

## 4-F. Match Result Reporting (SD Multi-Room)

§4-E carried the match *config* lobby → server. This section is its mirror: at match end the dedicated server reports the **verified result** back to the lobby, so the game backend (persistence / leaderboard / rewards) hears the outcome from the server authority — never from a client.

**What travels**

```
MatchResult (dedi → lobby)                        MatchResultAck (lobby → dedi)
├─ matchInstanceId : result idempotency key (lobby-minted — below)
├─ roomId / stageId
├─ terminationKind : NormalEnd | Aborted
├─ roster[]        : per-player { PlayerId, Account, DisplayName } — the verified identities (§2),
│                    leavers included (a player who left before the end still appears)
└─ payload         : NormalEnd → game-authored opaque result blob (winner / stats / acquisitions)
                     Aborted   → abort notification { abortReason, culpritPlayerId }
```

- **The game authors the result; the pipe stays opaque.** The game's match-end event (an `IMatchEndEvent`) additionally implements `IMatchResultProvider`, exposing `MatchResultData` (`byte[]`) assembled from **verified** simulation state. The core and the wire never parse it — the game defines the schema on both ends (see [GameDevAPI §3.7](./GameDevAPI.md#37-match-result--imatchresultprovider)). An event that does not implement the interface reports nothing (opt-in, no regression).
- **Identity rides OUTSIDE the game blob.** The blob stays pure deterministic stats keyed by `PlayerId`; the roster side-channel carries the verified `Account` / `DisplayName`, and the backend joins the two by `PlayerId`. This keeps the deterministic payload identity-free.
- **Abort notifications.** A server-authoritative abort (state divergence) and the **abandoned** case (every peer left mid-match with no end ever requested) are reported as `Aborted` with an `abortReason`. ⚠️ `culpritPlayerId = -1` means *"unknown who"* for a divergence but *"no single culprit — the whole roster is responsible"* for abandoned; gate leaver-penalty / match-void policy on the reason, never on `-1` alone. A match that never started (drain before Playing) is **not** reported — the lobby's reserve TTL reclaims it.

**Match instance key — why `matchId` cannot key the result**

The rendezvous `matchId` is what clients share to meet in a room, so it repeats across matches (re-queue on the same id) — keying results by it would discard a real second match as a duplicate. The result needs a key that is unique per match *instance*: the lobby mints one when it binds the room slot at reservation (`{matchId}#{token}` — the one transition that happens exactly once per match), pushes it with the reservation (§4-E), and the server keys the result by it. To join a backend record on the rendezvous key, take the substring before the last `#`.

**Delivery — at-least-once + idempotent intake**

```
(1) match ends / aborts → the server captures result + roster snapshot on the room's own thread
(2) append to a local crash-backstop journal (flush), then send MatchResult
(3) no ack → resend on a timer until a give-up bound (reference: ~3 s / 120 s);
    a reconnect re-flushes every un-acked result
(4) lobby: de-dup by matchInstanceId → hand off to the game backend (IMatchResultSink.Submit)
(5) ack ONLY after the handoff returns cleanly → the server stops resending
    (a sink failure withholds the ack → server retries; a duplicate is acked idempotently)
```

Results are flushed **ahead of** the periodic room report, so the lobby processes a result before the room's reclaim signal. The journal keeps a durable copy for offline re-collection if the lobby stays unreachable past the give-up bound.

**No lobby**: no reservation table → no match key → nothing is emitted (no regression).

> **Mockup note**: like the reserve-push channel (§4-E), the result channel lives in the reference SD lobby sample (`SdRoomReporter` on the server, `IMatchResultSink` on the lobby), not the core. The core's surface is the `IMatchResultProvider` read plus the `RoomManagerConfig.OnRoomCreated` / `OnRoomDraining` per-room subscription hooks the reporter wires into.

---

## 5. SD vs P2P at a Glance

| Aspect | SD (dedicated) | P2P (host) |
|---|---|---|
| Verifier | dedicated server | host verifies at join; **with entitlements (§9)** every guest re-verifies the propagated ticket (else semi-trust: guests trust the host) |
| Method | lobby redeem (online) + local 1st-pass signature | signature only (offline) |
| Trust anchor | server + lobby | lobby signature |
| Real-time ban | yes (redeem) | no — relies on expiry (keep `expiresAt` short) |
| Replay defense | nonce idempotency window + (server, room) binding | `expiresAt` + session-scoped nonce |
| account source | redeem response | ticket payload (verified) |
| entitlement source (§9) | redeem response (server queries backend) | ticket payload (each peer verifies + re-verifies) |
| entitlement clamp/gate (§9) | server single decision (authoritative rewrite/drop) | per-peer deterministic (same signed bytes → same verdict) |
| Klotho hook | async handle (polled) | sync handle (immediately complete) |

---

## 6. Reject Reason Codes

On failure the authority returns `Reject(wireCode)` → the core sends `JoinReject` then disconnects. The **wire byte** and the client-local `JoinFailReason` enum are **distinct numbering schemes**, mapped by `JoinFailReason.FromJoinReject`. An out-of-range code (buggy redeem) is clamped to `9` (IdentityRejected).

| Meaning | Wire byte | `JoinFailReason` enum |
|---|---|---|
| (room reasons: RoomFull, etc.) | 1~5 | 7~11 |
| IdentityInvalid (bad signature / format / empty-or-oversize account) | 6 | 12 |
| IdentityExpired | 7 | 13 |
| IdentitySessionMismatch (cross-match / wrong room) | 8 | 14 |
| IdentityRejected (redeem deny / ban / consumed nonce) | 9 | 15 |
| IdentityRequired (validator present, no ticket) | 10 | 16 |
| IdentityValidationFailed (transport fault / redeem timeout) | 11 | 17 |

---

## 7. No Lobby (opt-in OFF) — No Regression

With neither provider nor validator registered:

| Aspect | Behavior |
|---|---|
| `PlayerJoinMessage.Ticket` | empty string (not carried) |
| Validation hook | not invoked |
| `Account` | `""` (never interchanged with DeviceId — reconnect fingerprint ≠ account) |
| `DisplayName` | client-claimed value (if any, unverified) → otherwise a fabricated fallback (`"Host"`/`Player{id}`) |
| Reject | does not occur |
| Overall | **identical to current code** (one micro-exception below) |

LAN / prototype / samples keep working as-is; only production turns on the provider (client) + validator (server/host).

> **Micro-exception**: the pre-auth message-size guard (`PlayerJoinMessage.MaxPreAuthMessageBytes`, 4096 B) applies **unconditionally** — an oversized *first* handshake message is rejected (wire 6, `IdentityInvalid`) even with no lobby. The bound is far above any legitimate join message (DeviceId + claimed name), so it is not observable in practice; it is a pre-auth memory-amplification hardening, not a behavioral change.

---

## 8. Integration Checklist

**Lobby server team**
- [ ] `issueTicket` — after login auth + match assignment, issue a signed ticket + endpoint + mode
- [ ] `redeemTicket` (SD) — verify signature/expiry/match-binding + idempotent nonce consume + ban check
- [ ] SD multi-room: return the assigned `roomId` from `issueTicket`; cross-check the redeem `(serverId, roomId)` against the binding → wrong room = Reject(8) (§4-D)
- [ ] If asymmetric signing, **distribute the public key to clients/dedicated servers**

**Client team**
- [ ] Implement `IPlayerIdentityProvider` (return the stored ticket)
- [ ] Register `WithLobbyIdentity(provider)` then Connect to the endpoint
- [ ] UI shows `IPlayerInfo.DisplayName`/`Account` (authoritative); remove local fabrication
- [ ] Branch on `OnJoinFailed` reason codes (expired → re-queue, etc.)

**Dedicated / host team**
- [ ] Implement `IPlayerIdentityValidator` — SD = async redeem, P2P = sync signature
- [ ] Register `WithIdentityValidator(validator)`
- [ ] SD: incomplete handle + background completion + `Dispose` cancel (idempotent)
- [ ] Reject with `Reject(wireCode 6~11)`, accept with `Accept(account, displayName)`
- [ ] SD multi-room: carry `IdentityValidationRequest.RoomId` into the redeem so the lobby can cross-check the routed room (§4-D)

**Common**
- [ ] Keep identity (verified, invariant) and game data (PlayerConfig, unverified, mutable) **separate**
- [ ] Authoritative fields that affect the simulation (team/loadout) must not be injected out-of-band — use a deterministic path (GameStart seed / ReliableCommand)

**Entitlements (trusted player data — §9, optional)**
- [ ] Lobby: attach the account's owned set — P2P into the ticket payload (`issueTicket`), SD into the `redeemTicket` response — deterministic per account (reconnect re-redeem recovers the identical blob); keep it small
- [ ] Server/host: implement `IPlayerConfigEntitlementGuard` (clamp start-of-match selection) and/or `IReliableCommandEntitlementGate` (drop unowned in-match commands); read the seed via `IKlothoEngine.GetPlayerEntitlement` at tick-0
- [ ] SD: register on `RoomManagerConfig.PlayerConfigEntitlementGuard` / `.ReliableCommandEntitlementGate`. P2P: `WithPlayerConfigEntitlementGuard(...)` (this also turns on original-ticket propagation + per-peer re-verify; the validator must also implement `IPropagatedTicketVerifier`)
- [ ] P2P only: the guard/seed/no-op logic must be a **deterministic pure function** (same signed bytes → byte-identical verdict on every peer) — no RNG / float / culture / dictionary-order, else tick-0 desync
- [ ] Leave the hooks unset for identity-only / LAN / prototype → `PlayerConfig` behaves exactly as today (no regression)

**Match results (server → lobby — §4-F, optional)**
- [ ] Game: implement `IMatchResultProvider` on the match-end event; assemble the blob from verified state at fire time (keyed by `PlayerId`, no identity inside)
- [ ] Lobby: implement `IMatchResultSink.Submit` — fast / non-blocking, throw = not accepted (ack withheld → server retries); de-dup by `matchInstanceId`
- [ ] Backend: join the game blob to the roster identities by `PlayerId`; branch abort policy on `abortReason`, never on `culpritPlayerId == -1` alone

---

## 9. Trusted Player Data (Entitlements)

> **Level**: same conceptual/mockup framing as above — the flow and integration seams, not a production implementation.

§1–§8 established a **trusted identity** (which account this is). Entitlements answer the next question — **what that account owns** (inventory / owned characters / loadout / unlocks) — over the *same* verified channel. The key distinction:

| | `PlayerConfig` (existing) | **Entitlement** (this section) |
|---|---|---|
| Author | client | account authority (lobby / backend) |
| Trust | **untrusted intent** (client-written) | **trusted** (verified / signed) |
| Example | *selected* character, cosmetic preference | *owned* characters, equipped loadout, unlocked stats |
| Put owned/inventory data here? | **No** — a client can forge it | Yes |

Rule of thumb: a client *selecting* something goes through `PlayerConfig`; the authority confirming the client *owns* it is the entitlement. The core treats the entitlement as an **opaque `byte[]`** — it never parses it; the game encodes/decodes it (a small owned-id list is enough — keep the *full* inventory in the backend, put only what this match needs on the wire). The reference samples standardize the payload as a fixed-width bitmask over a [`DemoEntitlementData`](../Samples/IdentityP2pRef/Runtime/DemoEntitlementData.cs) `[KlothoSerializableStruct]` (producer serializes, guard/gate/seed only deserialize — verify-over-wire preserved); the encoding remains the game's choice and the core boundary stays `byte[]`.

### 9.1 Entitlement Flow (End-to-End)

Entitlement rides the *same* verified channel as identity (§1–§8) — no new transport. Its journey is **origin → store → (propagate) → read**, diverging by mode only at *propagate* and *read-locality*:

```
                       ┌───────────────── ORIGIN (at join validation, §4-C) ──────────────────┐
   Lobby / Auth        │  SD : redeemTicket response      → RedeemResult.Entitlement          │
   (owns / signs)  ────┤  P2P: signed INTO the ticket payload (§3) → LobbyTicket.Entitlement  │
                       └───────────────────────────────┬──────────────────────────────────────┘
                                                       ▼
              IdentityValidationOutcome.Accept(account, displayName, ENTITLEMENT)   ← opaque byte[]
                                                       ▼
                       STORE — server/peer-side PlayerInfo.Entitlement
                       (NOT on IPlayerInfo → never serialized onto the roster wire)
                                                       ▼
        ┌─────────────────────── PROPAGATE (mode-divergent) ────────────────────────┐
        │ SD : tick-0 seed EFFECT ships (Initial FullState); the raw bytes are      │
        │       ALSO propagated to clients for READ (UI, GetPlayerEntitlement       │
        │       non-null) — read-only, NOT a seed source                            │
        │ P2P: the original SIGNED ticket is propagated to every peer; each guest   │
        │       re-verifies signature-only → re-derives byte-identical entitlement  │
        └───────────────────────────────────┬───────────────────────────────────────┘
                                            ▼
                       READ — IKlothoEngine.GetPlayerEntitlement(playerId)
        ┌──────────────── ② start-of-match ────────────────┐         ┌──── ③ in-match ────┐
        ▼ config guard (clamp selection)   ▼ tick-0 seed              ▼ reliable-cmd gate
   IPlayerConfigEntitlement-           OnInitializeWorld (before   IReliableCommandEntitlement-
   Guard.Check(selection) →            SaveSnapshot(0)) → seed     Gate.Check(command) →
   Pass / Clamp / Reject               loadout deterministically   Accept / Drop
```

(The ① view/cosmetic path — a `PlayerConfig` field with no ownership stake — needs no entitlement hook, so it is not shown here; see §9.4.)

**SD (dedicated)** — the entitlement rides the redeem; its tick-0 *effect* is baked into the Initial FullState (the seed source), and the raw bytes are **also** propagated to clients as **read-only** data (e.g. for UI — `GetPlayerEntitlement` is non-null there, but the client never re-seeds from it). Each hop names its endpoints — `A ──msg──► B` is a network message **from A to B**; an indented `→` is the receiver's local step:

```
origin  (during join validation, §2)
   Dedicated Server ──redeemTicket──►  Lobby
   Lobby ──ok{ account, name, +ENTITLEMENT }──►  Dedicated Server
        → server stores it (PlayerInfo.Entitlement — server-only, never on the roster wire)

start-of-match
   Client ──PlayerConfig(selection)──►  Server
        → Server runs ② Guard.Check  →  Pass / Clamp / Reject
   Server  (local, tick-0):  GetPlayerEntitlement → seed loadout → SaveSnapshot(0)
   Server ──Initial FullState──►  every client
        → carries the seed EFFECT (drives the sim); the raw bytes ride the roster /
          join notifications separately for READ-only use (GetPlayerEntitlement, UI)

in-match
   Client ──UseXxx (reliable command)──►  Server
        → Server runs ③ Gate.Check  →  owned: accept,  unowned: Drop
   Server ──accepted command (tick-assigned)──►  every client
```

**P2P (host)** — the entitlement rides the *signed* ticket. There is exactly **one** propagation message; after it, **every peer runs the same steps locally**, so there is no per-message routing to track (same `──►` / `→` convention as above):

```
origin  (during join validation, §3 — offline; no lobby round-trip)
   Client ──Connect + signed ticket (entitlement signed inside)──►  Host
        → Host verifies the signature and extracts the entitlement  (host self: signature-only)
   Host ──ORIGINAL signed ticket──►  every peer

then EVERY peer runs these locally — deterministic (identical signed bytes → identical result):
   → ReverifyPropagatedTicket (signature-only): re-derive the entitlement bytes locally
   → ② tick-0: GetPlayerEntitlement → seed loadout
   → ② Guard.Check(selection)  →  Pass / Clamp / Reject
   → ③ in-match UseXxx: no server gate — each peer checks ownership at apply,
        deterministic + idempotent  →  exactly-once-observed
```

> The full data lifecycle (origin → store → preserve → propagate → read → dispose, with the invariants at each step) is out of scope here — see the companion reference [Entitlement (Trusted Player Data) Lifecycle](EntitlementLifecycle.md). This guide covers the **integration** view (hooks, wiring, mode differences).

---

### 9.2 Where it comes from (per mode)

Already carried by the identity channel (§3) — no new transport:

- **SD**: the lobby returns it from `redeemTicket` (§4-A) → the validator hands it to the core via `IdentityValidationOutcome.Accept(account, displayName, entitlement)`. The dedicated server is the trust anchor, so an online lookup is fine.
- **P2P**: the lobby signs it **into the ticket payload** (§3) → the validator extracts it on accept, and (crucially) the *original signed ticket* is propagated so every guest re-verifies it independently (§4-C). Host relay is never trusted.

### 9.3 Core seams the game fills in

| Hook | When | Verdict | Notes |
|---|---|---|---|
| `IPlayerConfigEntitlementGuard.Check(playerId, entitlement, selection)` | client config arrives (post-join) | `Pass` / `Clamp(replacement)` / `Reject(wireCode)` | cross-check a *selection* against the owned set; recommended default is **clamp-to-default**, not reject |
| `IReliableCommandEntitlementGate.Check(playerId, entitlement, command)` | in-match reliable command | `Accept` / `Drop` | an unowned action simply does not happen (no “default action” to substitute) |
| `IPropagatedTicketVerifier.ReverifyPropagatedTicket(ticket)` | P2P guest, each propagated ticket | `IdentityValidationOutcome` | **signature-only** (do NOT re-check expiry/nonce/session) — the join-time gate already did; the reference P2P validator implements this alongside `IPlayerIdentityValidator` |
| `IKlothoEngine.GetPlayerEntitlement(playerId)` | tick-0 world init | `byte[]` (opaque) | read it in `OnInitializeWorld` (before `SaveSnapshot(0)`) to seed the sim deterministically |

### 9.4 Which channel carries a field (simulation-impact branch)

The *only* thing that matters is whether the field affects the deterministic simulation:

| Field kind | Channel | Determinism |
|---|---|---|
| ① view / metadata only (rank badge, cosmetic) | authority-authored `PlayerConfig` | non-deterministic OK |
| ② sim-affecting, fixed at start (loadout/stats from owned character) | **tick-0 seed** — read `GetPlayerEntitlement` in `OnInitializeWorld` | must be deterministic |
| ③ sim-affecting, changes mid-match (item consume/grant) | **`IReliableCommand`** (authority-issued → tick-assigned; delivered **exactly-once / idempotent**) | must be deterministic |

> **Never** inject a sim-affecting field via an out-of-band `PlayerConfig` broadcast — that desyncs. ② and ③ are the deterministic paths.

### 9.5 Where SD and P2P diverge

| | SD | P2P |
|---|---|---|
| Clamp/gate decision (② start, ③ in-match) | **server single decision** — authoritative rewrite / drop, then broadcast | **each peer** decides locally against its own verified entitlement — must be a **deterministic pure function** (identical bytes → identical verdict), else tick-0 desync |
| ② seed source | server applies at tick-0 → clients adopt it via the **Initial FullState** (the seed source; `OnInitializeWorld` does not run on the SD client). The raw bytes are separately propagated so client `GetPlayerEntitlement` is non-null for **read-only** use (UI), never a seed source | each peer seeds **locally** from the propagated signed bytes (no host authorship, no re-derivation) |
| late-join / reconnect | accept unchanged — seed covered by full-state resync (server full-state is trusted) | original signed ticket is **also** bundled in the accepts (host-authored full-state is untrusted) so the joiner re-verifies |

### 9.6 Wiring

```csharp
// SD (dedicated) — on the RoomManager config, alongside the identity validator
roomManagerConfig.IdentityValidator             = new SdRedeemValidator();      // §4-C (redeem returns entitlement)
roomManagerConfig.PlayerConfigEntitlementGuard  = new MyEntitlementGuard();     // ② clamp start-of-match selection
roomManagerConfig.ReliableCommandEntitlementGate= new MyReliableCommandGate();  // ③ drop unowned in-match commands

// P2P (host & guest) — on the flow builder
new KlothoFlowSetupBuilder()
    .WithHandshake(transport, endpoint)
    .WithIdentityValidator(new P2pSignatureValidator())      // also implements IPropagatedTicketVerifier
    .WithPlayerConfigEntitlementGuard(new MyEntitlementGuard())  // ← also ENABLES original-ticket propagation + per-peer re-verify
    .Build();
```

> P2P: `WithPlayerConfigEntitlementGuard` is the single switch — it wires the guard **and** turns on propagation (fail-closed: refused with a log if the validator is not also an `IPropagatedTicketVerifier`, so host-relayed entitlement is never trusted blindly).

### 9.7 No entitlements (opt-in OFF) — no regression

Leave every hook unset and `GetPlayerEntitlement` returns `null`: the guard/gate are skipped, P2P propagation stays off (identity-only semi-trust preserved, §4-C), and `PlayerConfig` behaves exactly as before. Entitlements are a strict superset you turn on per game.

### 9.8 Play test — watch it work end-to-end

The fastest way to understand the whole flow is to run it and watch a restricted account get gated. The reference samples ([`DevLobbyServer`](../Samples/DevLobbyServer/Program.cs), [`Brawler/Server`](../Samples/Brawler/Server/Program.cs), [`Brawler/Assets/Brawler`](../Samples/Brawler/Assets/Brawler/Scripts/ECS/BrawlerSimSetup.cs)) use a deliberately simple demo rule:

> An account whose name contains **`"guest"`** is *restricted* — it owns every class but only **skill slot 0** of each and **no consumable**. Every other account (including a blank one, which becomes a generated `dev-NNNN` id) owns the **full loadout**.

So the whole feature is observable by giving one client a `"guest"` account and comparing:

| | Full account | `"guest"` account |
|---|---|---|
| Pick any of the 4 classes | ✅ works | ✅ works — *all classes are owned in the demo, so the config-guard clamp (②) never fires here; it just passes* |
| Skill slot 0 | ✅ fires | ✅ fires |
| **Skill slot 1** | ✅ fires | ❌ no-ops — the **tick-0 seed** (②) never granted it |
| **Consumable use** | ✅ happens | ❌ dropped — SD by the **in-match gate** (③); P2P no-ops from the seeded owned-set |

> This is the base run flow from [Brawler.I.HowToRun.md](Samples/Brawler.I.HowToRun.md) with the lobby turned **on**. Do the common setup there first (open the scene, confirm the `.bytes` assets are bound). The lifecycle of the bytes you are watching is traced end-to-end in [EntitlementLifecycle.md](EntitlementLifecycle.md).

#### Option A — Server-Driven (dedicated server + dev lobby)

SD needs two processes running *before* the Unity client connects: the **dev lobby** (mints the entitlement) and the **dedicated server** (redeems it, then stores + gates).

**1. Start the dev lobby** — [`Samples/DevLobbyServer`](../Samples/DevLobbyServer/Program.cs). Plain console app, default port `9999`:

```bash
cd Samples/DevLobbyServer
dotnet run -- 9999 Information
# → "[DevLobby] listening on 9999 — match 'sdsample-dev-match' → server 'sdsample-dev-server'"
```

**2. Start the dedicated server with `--lobby`** — [`Samples/Brawler/Server`](../Samples/Brawler/Server/Program.cs). The `--lobby host:port` flag is what turns the validator + entitlement on (without it the server runs ticketless and nothing gates):

```bash
cd Samples/Brawler/Server
./build.sh                                                    # once (dotnet build -c Debug)

# single-room:  <port> <botCount> [logLevel]  + --lobby host:port
dotnet run --project BrawlerDedicatedServer.csproj -- 7777 0 Information --lobby localhost:9999
# → "identity validator active — dev lobby localhost:9999 ..."
# → "room reporter active — advertising 127.0.0.1:7777 ... to lobby localhost:9999"
```

The server registers its capacity with the lobby (`--advertise` defaults to loopback, fine for same-machine testing). Keep the dev defaults consistent: `sessionconfig.json` `MaxPlayers = 2` matches the lobby's seeded room capacity, so an assignment the server would reject never happens.

**3. Configure the Unity client** — `BrawlerGameController` Inspector:

- `_simulationConfig` (`SimulationConfig.asset`): **`Mode = ServerDriven`**
- `BrawlerLobbySettings` (the `_brawlerLobby` block):
  - `_lobbyEnabled = true` ← the runtime toggle
  - `_account = "guest"` on one client, blank (or anything else) on another
  - `_matchId = sdsample-dev-match` (default), `_lobbyAddress = localhost`, `_lobbyPort = 9999`
- `BrawlerSettings`: `_roomId = -1` (single-room). `_hostAddress`/`_port` here are ignored for the join — with the lobby on, the client fetches a ticket and the **lobby-assigned** endpoint wins ([`JoinGameAsync`](../Samples/Brawler/Assets/Brawler/Scripts/Manager/BrawlerGameController.cs#L519) → `TryFetchLobbyAsync`).

**4. Play.** Press Play → **Guest** → **Join Room** → **Ready**. On join the client fetches a signed ticket from the lobby, the server redeems it (minting the entitlement from the account), and the match starts once `MinPlayers` are ready. Now try skill slot 1 and the consumable on each client — the `"guest"` one is gated, the other isn't.

#### Option B — P2P (no servers, in-process lobby stub)

P2P needs **no** DevLobbyServer and **no** dedicated server — the lobby is an in-process signed-ticket stub, and every peer re-verifies for itself.

- `_simulationConfig`: **`Mode = P2P`**
- On **both** the host and the guest peer, set `_brawlerLobby._lobbyEnabled = true`, and set `_account = "guest"` on the peer you want restricted.
- Run host + guest as in [Brawler.I.HowToRun §I-2](Samples/Brawler.I.HowToRun.md) (editor hosts, a standalone build joins, or vice-versa).

Because the owned-set is baked into the deterministic tick-0 seed, the restriction shows up **identically on every peer** — the guest's slot-1 skill and consumable simply no-op in the simulation, with no server involved. (P2P has no reliable-command gate; it doesn't need one.)

#### Turning it off

Set `_lobbyEnabled = false` (or, for SD, just omit `--lobby` on the server). No ticket is fetched, `GetPlayerEntitlement` returns `null`, and every client plays with the full loadout — exactly the pre-entitlement behaviour (§9.7).
