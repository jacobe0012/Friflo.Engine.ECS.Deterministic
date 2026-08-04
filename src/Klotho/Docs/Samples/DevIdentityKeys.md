# Dev Identity Keys — Generation & Rotation Guide

How to generate and rotate the **dev Ed25519 key pair** used to sign sample identity (lobby ticket)
material in the SD and P2P samples.

> ⚠️ These are **dev-only throwaway keys**. The private seed is a public, hard-coded value in this
> repository and must never be used in production. See the last section for production key handling.

---

## 1. Model — Ed25519 key pair

- **Private key (seed)**: 32 bytes. Used to **sign** (issue) tickets → held only by the issuer (lobby).
- **Public key**: 32 bytes. Used to **verify** signatures → held by the game server / host (validator) and clients.
- The public key is **deterministically derived** from the private seed (RFC 8032). The **seed is the source
  of truth**; the public key is a function of it.
  - Derivation: [`PureEd25519Backend.DerivePublicKey(byte[] seed)`](../../Samples/IdentityP2pRef/Runtime/PureEd25519Backend.cs#L37)

**Invariant**: within a model, `publicKey == DerivePublicKey(seed)` must hold. If you change the seed you
**must** update the matching public key as well (all locations in §2).

---

## 2. Key locations (update all on rotation)

SD and P2P currently share the **same dev pair** (seed `0x01..0x20`). They are independent per model — you
may rotate one model alone (but always the seed↔public pair together within that model).

| Model | Key | File | Identifier |
|---|---|---|---|
| **SD** | private seed | [Samples/DevLobbyServer/SdDevLobby.cs](../../Samples/DevLobbyServer/SdDevLobby.cs) | `s_seed` |
| **SD** | public key | [Samples/IdentitySdRef/Runtime/SdDevIdentity.cs](../../Samples/IdentitySdRef/Runtime/SdDevIdentity.cs) | `s_publicKey` |
| **P2P (Unity)** | private seed | [Samples/P2pSample/Assets/P2pSample/Scripts/View/P2pDevIdentity.Server.cs](../../Samples/P2pSample/Assets/P2pSample/Scripts/View/P2pDevIdentity.Server.cs) | `Seed` |
| **P2P (Unity)** | public key | [Samples/P2pSample/Assets/P2pSample/Scripts/View/P2pDevIdentity.Client.cs](../../Samples/P2pSample/Assets/P2pSample/Scripts/View/P2pDevIdentity.Client.cs) | `PublicKey` |
| **P2P (Godot)** | private seed | [Samples/GodotP2pSample/P2pDevIdentity.Server.cs](../../Samples/GodotP2pSample/P2pDevIdentity.Server.cs) | `Seed` |
| **P2P (Godot)** | public key | [Samples/GodotP2pSample/P2pDevIdentity.Client.cs](../../Samples/GodotP2pSample/P2pDevIdentity.Client.cs) | `PublicKey` |

> **Interop**: every node that plays together must use the same pair.
> - SD: lobby (seed), game server (public), and client share one pair.
> - P2P: host (self-mint seed) and guests (verify public) share one pair. To play **Unity P2P against
>   Godot P2P**, keep both engines' keys identical.

---

## 3. Generate a new key

Generate a random 32-byte seed and derive the public key. Use the project's own `PureEd25519Backend` so the
result matches runtime derivation exactly.

Run in any context that references `PureEd25519Backend` (e.g. a temporary entry point in
`Samples/DevLobbyServer`, or a scratch console referencing IdentityP2pRef):

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using xpTURN.Klotho.Samples.Identity; // PureEd25519Backend

byte[] seed = RandomNumberGenerator.GetBytes(32);          // new private key
byte[] pub  = PureEd25519Backend.DerivePublicKey(seed);    // deterministically derived public key

static string ToInit(byte[] b)
{
    var sb = new StringBuilder();
    for (int i = 0; i < b.Length; i++)
    {
        if (i % 16 == 0) sb.Append("\n            ");
        sb.Append($"0x{b[i]:x2}, ");
    }
    return sb.ToString();
}

Console.WriteLine("// --- private seed (issuer/lobby only) ---");
Console.WriteLine(ToInit(seed));
Console.WriteLine("\n// --- public key (verify) ---");
Console.WriteLine(ToInit(pub));
```

The output matches the existing literal format (16 per row, lowercase hex), so you can paste it directly.

> Alternative: `openssl genpkey -algorithm ed25519` also works, but extracting the raw 32-byte seed/public
> key from PEM is fiddly and requires separately verifying derivation — the snippet above is preferred.

---

## 4. Rotation procedure

1. **Generate**: run the §3 snippet to get the new `seed` + `pub` literals.
2. **Update**: paste into the target model's locations (§2 table).
   - SD only: `SdDevLobby.s_seed` + `SdDevIdentity.s_publicKey`.
   - P2P only: `P2pDevIdentity.Server.cs:Seed` + `Client.cs:PublicKey` (both Unity and Godot — identical
     values if cross-engine play is intended).
   - Always change the **seed↔public pair together** (the invariant).
3. **Build check**:
   - `dotnet build Samples/DevLobbyServer` and the game servers (`-p:DefineConstants=KLOTHO_DEV_LOBBY`) → 0/0.
   - For Unity samples, reimport in the editor, then Play.
4. **e2e check**: DevLobbyServer + game server + client → `redeem ok` join (SD), or P2P host/guest join — confirm
   signing/verification passes with the new pair.

> On a mismatch: SD fails redeem with `IdentityInvalid(6)` (signature verify failure); P2P fails at host-side
> verification (join rejected).

---

## 5. Production notes (vs. the dev model)

- **Never embed the private key in client or game-server binaries.** The dev samples hard-code the seed for
  convenience (P2P self-mints; SD isolates it in `SdDevLobby`), but in production only the **lobby backend**
  holds the private key, and clients/servers hold only the public key + an issued ticket.
- **Key rotation (production)**: a public key hard-coded in clients will reject valid tickets after rotation
  (stale key). Use a **key-version tag** (a keyId in the ticket → the server verifies with that version's
  public key) or a public-key update channel.
- The dev pair is a throwaway with no trust boundary, so source replacement is sufficient — no rotation
  infrastructure needed.
