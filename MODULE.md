# ServerOperationFlow

> **Module group — Networking.** An optional layer on top of `PFound.NetworkLayer`. Grouped by purpose —
> see the catalog `Assets/PFound/README.md` and each module's **Dependencies** for exact edges.

## Purpose

Models each networked, **server-authoritative** operation as a task object with a **compiler-forced
lifecycle**. Every operation — spend currency, accept an invite, claim a reward — fills the SAME ordered,
half-sealed shape, which deletes a recurring bug class: forgetting to validate before sending, applying an
unverified result, missing cancellation, or handling errors inconsistently. The valuable core is the
**client-prediction + server-authority** split behind one uniform lifecycle. It is transport-backed by
NetworkLayer's request/reply and is game-agnostic: everything game-specific is injected via generics and
seams. It is NOT part of NetworkLayer core (the transport stays neutral).

## The lifecycle (sealed order; a subclass only fills the hooks)

A concrete flow subclasses `ServerOperationFlow<TRequest, TResponse, TResult>`. `RunAsync` drives:

1. **Entry** — admitted or refused by the opt-in run policy.
2. **Local pre-check (client prediction)** — `PreCheck()`; a failing result rejects instantly and **no
   request is sent**.
3. **Build request** — `BuildRequest()`.
4. **Send + await (server authoritative)** — over the injected transport; the client does not decide success.
5. **Interpret response** — `Interpret(response)` maps the response to the game result.
6. **On success — SYNCHRONOUS apply** — `ApplySuccess(result)` (no awaits), then the injected success sink
   and analytics fire.
7. **Optional async, cancellable post-effects** — `RunPostEffectsAsync(result, cancellation)`: navigation,
   animation, and **hand-chaining other operations**.
8. **Uniform failure handling** — one path for both the pre-check reject and the server failure: the
   injected failure presenter maps the concrete result's typed code to a user-facing message (localization/UI
   stays game-side) and analytics records the outcome.

## Assemblies

| Assembly | Location | Notes |
|---|---|---|
| `PFound.ServerOperationFlow.Core` | `Core/Runtime/` | Engine-free lifecycle over the transport seam. `noEngineReferences`, no `PFound.*` deps. mono/csc-testable. |
| `PFound.ServerOperationFlow.Core.Tests` | `Core/Tests/` | Standalone mono/csc runner (`Program.cs` + `TestKit`). |
| `PFound.ServerOperationFlow` | `Runtime/` | Thin adapter binding the seam to NetworkLayer's `ClientPeer.CallAsync`. Depends on `PFound.NetworkLayer`. |
| `PFound.ServerOperationFlow.Tests` | `Tests/` | Unity EditMode glue tests (adapter over the loopback transport). |

Namespaces: `PFound.ServerOperationFlow.Core` (lifecycle + seams), `PFound.ServerOperationFlow` (adapter).

## Dependencies

- **Core:** none (engine-free; async via `System.Threading.Tasks`).
- **Runtime adapter:** `PFound.NetworkLayer` only.
- No hard dependency on any signal bus, UI, or localization — all injected as seams.

## Key types

| Type | Description |
|---|---|
| `ServerOperationFlow<TRequest,TResponse,TResult>` | Abstract base with the sealed `RunAsync` lifecycle; subclass fills `PreCheck` / `BuildRequest` / `Interpret` / `ApplySuccess` / (optional) `RunPostEffectsAsync`. |
| `ServerOperationContext<TRequest,TResponse,TResult>` | Bundles the injected seams. Transport + failure presenter are required; success sink, analytics, and gate default to no-ops. |
| `ServerOperationRun<TResult>` | The report of one run: `Disposition` (`Completed` / `DuplicateSuppressed`), `Accepted`, and `Result` (read only when `Accepted`). |
| `IServerOperationTransport<TRequest,TResponse>` | The send/await seam. Core-side, constraint-free. |
| `IServerOperationResult` | The game result contract the lifecycle branches on: `IsSuccess`, `ErrorMessage`. The status/result CODE is deliberately game-specific and NOT on this interface — it lives on the concrete result type. |
| `ServerOperationResult<TCode>` | A ready-made result generic over the game's own code enum (`TCode : struct, Enum`): `Success(code)` / `Failure(code, message)`, exposing `TCode ResultCode`. So a game gets `ServerOperationResult<OpResult>` with a typed `OpResult ResultCode`. |
| `IServerOperationSuccessSink<TResult>` | Success emitter (Signaling-free; injected only). |
| `IServerOperationFailurePresenter<TResult>` | Maps a failed result to a user-facing message and shows it (game-side). |
| `IServerOperationAnalytics` | Optional telemetry, fired on every terminal outcome. |
| `ServerOperationGate` | The opt-in run policy: single-flight duplicate-guard + loading-indicator hook. `Disabled` = no gate. |
| `IServerOperationLoadingIndicator` | The busy-indicator seam the gate drives (`Show` / `Hide`). |
| `ServerOperationHost<TResult>` | The ambient host (`Current`) a flow resolves its context from — mirrors `NetworkClient.Current`, generic over the game's single result type (so the failure presenter + success sink are held with their real type, no cast). Holds the transport factory + typed failure presenter + success sink + shared gate/analytics + an ambient `Cancellation` token; the flow's parameterless base ctor calls `CreateContext<TRequest,TResponse>()`. Lets a flow be `new`ed with only the game's params. |
| `IServerOperationTransportFactory` | The game-supplied transport seam the host uses to build a context's transport per flow (the request/response pair). The failure presenter + success sink are set directly on the typed host — no channels indirection, no `object` bridge. |
| `ClientPeerServerOperationTransport<TRequest,TResponse>` | **Adapter.** Binds the seam to `ClientPeer.CallAsync`; carries the `RequestMessage` / `ReplyMessage` constraints so the Core never does. The engine-free ambient transport factory (whose Core-side signature is constraint-free) builds it reflectively from the flow's concrete envelope types, which satisfy the constraints at runtime. |

## Use-case examples

### 0. Wire contract discipline (the request/reply/DTO shape the examples assume)

The `TRequest` / `TResponse` types cross the client↔server boundary, so they use a **fixed, stable**
shape — explicit MessagePack keys, immutable value DTOs, and enum-declared opcodes. Do NOT rely on
contractless member-order serialization for anything that crosses the wire.

```csharp
using MessagePack;

// (W3) Banded two-level opcodes: a tiny CENTRAL domain enum (high byte, one entry per subsystem) plus a
// per-domain op enum (low byte) with LOCAL values, folded to (domain << 8) | op. Different domain ⇒
// different high byte ⇒ cross-subsystem collisions are structurally impossible. Only the REQUEST carries
// an opcode — the reply is decoded by CORRELATION (the outstanding call already knows the reply type) and
// is NOT enrolled, so each operation costs exactly one op value (no reply half).
public enum NetDomain : byte { Wallet = 1 }   // central; value = opcode HIGH byte (0x01)
public enum WalletOp  : byte { Spend = 1, Grant = 2 }   // one op per operation; replies have no opcode

// (W2) Content = an immutable readonly struct DTO with explicit [Key]s + IEquatable value-equality.
[MessagePackObject]
public readonly struct SpendCoins : IEquatable<SpendCoins>
{
    [Key(0)] public readonly int Amount;
    [SerializationConstructor] public SpendCoins(int amount) { Amount = amount; }
    public bool Equals(SpendCoins other) => Amount == other.Amount;
    public override int GetHashCode() => Amount;
}

// (W1) The poolable RequestMessage/ReplyMessage ENVELOPE stays a plain class (NOT [MessagePackObject]) —
// it just carries the stable content DTO. Wire stability lives in the DTO (which owns the game data that
// actually evolves); the envelope is a thin, framework-owned shape (its only members are the base Status
// and the DTO field), so leaving it contractless costs nothing. This also keeps NetworkLayer's mono/csc
// core build MessagePack-free — the base message types carry no MessagePack attributes.
public sealed class SpendCoinsRequest : RequestMessage
{
    public SpendCoins Content;
    public override void Clear() { Content = default; }
}

public sealed class SpendCoinsReply : ReplyMessage
{
    public int Balance;                          // authoritative server value
    public override void Clear() { base.Clear(); Balance = 0; }
}

// Enrolment (once, shared by both peers) — the request/reply PAIR overload: the request takes the opcode,
// the reply is registered for pooling by TYPE only (opcode-less, decoded by correlation):
catalog.ForDomain(NetDomain.Wallet).Enroll<SpendCoinsRequest, SpendCoinsReply>(WalletOp.Spend);
```

> `DuplicateKey` is a `string`, so fold the content DTO into it (e.g. `DuplicateKey => $"Grant:{_content}"`).
> An immutable value DTO with a value-based `ToString()` gives a stable per-value key, so two runs with
> value-equal content dedup correctly (see example 5); the DTO's `IEquatable`/value-equality is what makes
> that projection well-defined. You can hand-write this DTO+envelope shape as above, or let Phase 2's
> **source generator** emit it from a compact `[RemoteProcedure]` declaration (`[Request(n)]`/`[Reply(n)]` fields
> → `[Key(n)]` DTOs, envelopes, the pair `Register`, and a uniform `Execute`); see NetworkLayer's MODULE.md
> "Message codegen".
>
> **Your operations live in `Assets/GameSpecific/Networking/`** — the canonical, copyable real-game
> reference. Exactly two folders: `Data/` holds every shared DTO struct, `Operations/` holds every
> operation (each a `[RemoteProcedure]` spec; `GetPlayerDataOperation.cs` is a `playerId in → PlayerData out` **query**,
> `JoinAllianceOperation.cs` a terser operation, `SpendCoinsOperation.cs` additionally keeps a `ServerOperationFlow` lifecycle as
> the advanced example). Add one via right-click → **Create → PFound → Server Operation** inside an assembly
> that references `PFound.NetworkLayer` + `PFound.ServerOperationFlow.Core` + `MessagePack.Annotations.dll`.
>
> **Uniform call vs advanced lifecycle.** Every operation is a `[RemoteProcedure]` partial, and the primary
> way to call ANY of them is the generated `var value = await <Op>.Execute(args)`, which returns the reply
> DTO by value. A **query** (`GetPlayerDataOperation`) is exactly that — nothing else. A **mutation** that must predict
> local state then apply the authoritative result can opt into a `ServerOperationFlow` subclass (validate → send →
> apply) on top of the same generated messages; that lifecycle is the advanced path, not the default.
>
> **A reply is ALWAYS a DTO struct, never a bare primitive** — wrap a lone value in a single-field
> `[MessagePackObject]` DTO (`SpendResult`, `JoinResult`) so it keeps a wire `[Key]` and can grow
> append-only. (Requests may still carry primitive fields.)
>
> **Where things go:** opcodes → the one central `NetOpcodes.cs` (`NetDomain` + one op enum per domain);
> every DTO struct → `Data/` (e.g. `Data/PlayerData.cs`, reused by any operation); every operation →
> `Operations/`.
>
> **Zero-copy read:** each generated envelope exposes `public ref readonly TContent View => ref Content;`, so
> a consumer reads the immutable content DTO without copying the struct (`ref readonly var r = ref reply.View;`)
> — matters for large multi-field DTOs, harmless for small ones. `Content` stays a settable field for
> `BuildRequest`/pooling.
>
> **Wire versioning (`[Reserved]`):** wire indices are append-only — never reuse a retired index (an old
> peer's bytes would be read as the new field). Mark retired numbers at the type level with `[Reserved(n)]`
> (or `[Reserved(n, m, …)]`) so the source records the history; a type-level marker retires that number
> across BOTH the request and reply key spaces. The analyzer enforces it.

### 1. A minimal task (pre-check + request + success-apply)

```csharp
// TRequest / TResponse are your NetworkLayer wire messages (see example 0); TResult is your game result.
// The ready-made result is generic over YOUR code enum: ServerOperationResult<TCode>. Declare that enum once —
// its members ARE the result codes, so ResultCode reads as a named value, not a bare number.
public enum ResultCode { None = 0, InsufficientFunds = 1, ServerRejected = 2 }

public sealed class SpendCoinsOperationFlow
    : ServerOperationFlow<SpendCoinsRequest, SpendCoinsReply, ServerOperationResult<ResultCode>>
{
    readonly int _amount;
    readonly Wallet _wallet;
    int _serverBalance;

    public SpendCoinsOperationFlow(
        ServerOperationContext<SpendCoinsRequest, SpendCoinsReply, ServerOperationResult<ResultCode>> context,
        Wallet wallet, int amount) : base(context) { _wallet = wallet; _amount = amount; }

    protected override ServerOperationResult<ResultCode> PreCheck()          // step 2 — instant local reject
        => _wallet.Balance >= _amount
            ? ServerOperationResult<ResultCode>.Success()
            : ServerOperationResult<ResultCode>.Failure(ResultCode.InsufficientFunds);

    protected override SpendCoinsRequest BuildRequest()          // step 3
        => new SpendCoinsRequest { Amount = _amount };

    protected override ServerOperationResult<ResultCode> Interpret(SpendCoinsReply reply)   // step 5
    {
        _serverBalance = reply.Balance;
        return reply.Status == ReplyStatus.Ok
            ? ServerOperationResult<ResultCode>.Success()
            : ServerOperationResult<ResultCode>.Failure(ResultCode.ServerRejected);
    }

    protected override void ApplySuccess(ServerOperationResult<ResultCode> result)          // step 6 — synchronous
        => _wallet.Balance = _serverBalance;                    // authoritative value from the server
}

// Wiring + running:
var transport = new ClientPeerServerOperationTransport<SpendCoinsRequest, SpendCoinsReply>(clientPeer);
var context   = new ServerOperationContext<SpendCoinsRequest, SpendCoinsReply, ServerOperationResult<ResultCode>>(
    transport, myFailurePresenter);

ServerOperationRun<ServerOperationResult<ResultCode>> run = await new SpendCoinsOperationFlow(context, wallet, 50).RunAsync();
if (run.Accepted && run.Result.IsSuccess) { /* UI already consistent — ApplySuccess ran */ }
```

### 2. The pre-check gate — instant local rejection, no round-trip

`PreCheck()` runs **before any network I/O**. Returning a failing result short-circuits: no request is
built or sent, and the failure funnels straight to the injected `FailurePresenter`. Use it for anything the
client can already know is impossible (not enough currency, item not owned, cooldown active) so the player
gets an instant response instead of a round-trip.

```csharp
protected override ServerOperationResult<ResultCode> PreCheck()
    => _wallet.Balance >= _amount
        ? ServerOperationResult<ResultCode>.Success()
        : ServerOperationResult<ResultCode>.Failure(ResultCode.InsufficientFunds);   // no request ever sent
```

A pre-check **pass does not imply success** — the server is still authoritative. Only the mapped response
(step 5) decides.

### 3. Sync-apply vs async-post-effects — what goes where and why

- **`ApplySuccess` (step 6) is synchronous — no awaits.** Put the authoritative state mutation here so
  local state is consistent the instant the run returns (balances, inventory, entitlement flags). This is
  also where you'd raise an in-process signal via the injected success sink.
- **`RunPostEffectsAsync` (step 7) is async and cancellable.** Put everything that can be interrupted here:
  navigation, animations, a follow-up screen, or chaining another operation. Cancellation (below) stops
  this phase; it never rolls back the synchronous apply.

```csharp
protected override void ApplySuccess(ServerOperationResult<ResultCode> result)          // consistent immediately
    => _inventory.Add(_itemId, 1);

protected override async Task RunPostEffectsAsync(                            // interruptible follow-up
    ServerOperationResult<ResultCode> result, CancellationToken cancellation)
{
    await _ui.PlayGrantAnimationAsync(_itemId, cancellation);
    cancellation.ThrowIfCancellationRequested();
    await _ui.ShowInventoryAsync(cancellation);
}
```

### 4. Chaining tasks manually in post-effects

Multi-step flows are hand-chained by design (no saga engine). Chain inside `RunPostEffectsAsync` and check
cancellation between steps, so cancelling the outer run also stops the chained one.

```csharp
protected override async Task RunPostEffectsAsync(
    ServerOperationResult<ResultCode> result, CancellationToken cancellation)
{
    cancellation.ThrowIfCancellationRequested();
    await new ClaimBonusOperation(_bonusContext, _bonusId).RunAsync(cancellation);   // chained lifecycle
    await new RefreshRosterOperation(_rosterContext).RunAsync(cancellation);
}
```

### 5. The opt-in run policy — single-flight + loading indicator (and opting out)

Create one `ServerOperationGate` and inject it via the context. It gives two behaviours: a duplicate-guard
(a second run of the same key while one is in flight is a no-op) and a loading-indicator hook (shown before
the send, hidden on any terminal). The **dedup key defaults to the operation type** — a double-tap on a
button fires one request. Override `DuplicateKey` to fold in request parameters for **per-target** guarding.

```csharp
// One gate shared by the operations that should guard against each other.
var gate = ServerOperationGate.WithLoading(mySpinner);   // or ServerOperationGate.DedupOnly() for no spinner

var context = new ServerOperationContext<AcceptInviteRequest, AcceptInviteReply, ServerOperationResult<ResultCode>>(
    transport, failurePresenter) { Gate = gate };

// Default key = operation type: any two AcceptInvite runs guard each other.
await new AcceptInviteOperationFlow(context, inviteId).RunAsync();

// Per-target key: guard per invitation instead, so different invites can run concurrently.
public sealed class AcceptInviteOperationFlow : ServerOperationFlow<AcceptInviteRequest, AcceptInviteReply, ServerOperationResult<ResultCode>>
{
    readonly string _inviteId;
    protected override string DuplicateKey => "AcceptInvite:" + _inviteId;   // per-target dedup
    // ...
}

var run = await op.RunAsync();
if (run.Disposition == ServerOperationDisposition.DuplicateSuppressed) { /* already in flight; ignored */ }
```

**Opting out (no gate):** supply no gate at all. The context defaults to `ServerOperationGate.Disabled`, so
every run is admitted and nothing is shown — the plain, un-gated behaviour.

```csharp
var context = new ServerOperationContext<PingRequest, PingReply, ServerOperationResult<ResultCode>>(transport, presenter);
// Gate stays Disabled → concurrent runs are NOT suppressed, no loading indicator.
```

## Setup / wiring

Pure library — everything is `new X()`; no MonoBehaviour or singleton. Build a `ClientPeer` (see
`PFound.NetworkLayer`), wrap it in `ClientPeerServerOperationTransport<TRequest,TResponse>`, gather the
seams into a `ServerOperationContext`, and construct + `await RunAsync()` your operations. Both peers must
share the same opcode→type catalog for the request/reply to decode.

## Testing

- **Core (engine-free):** standalone mono/csc runner with a stub transport —
  ```
  csc -nologo -warn:0 -out:/tmp/pf_serverop.exe \
      Assets/PFound/ServerOperation/Core/Runtime/*.cs \
      Assets/PFound/ServerOperation/Core/Tests/*.cs && mono /tmp/pf_serverop.exe
  ```
  Covers pre-check short-circuit, ordered happy path, server-failure funnelling, cancellation of
  post-effects (incl. a chained task), manual chaining, single-flight (overlap → one send; release → rerun;
  opt-out → two sends; per-target keys), and the loading hook (show before send, hide on both success and
  failure; no show on a pre-check reject).
- **Unity (EditMode):** `Tests/` drives the adapter over the in-memory loopback transport — a manual wire
  endpoint correlates the client's request frame and returns a crafted reply, so `send → await → interpret →
  apply` runs end-to-end through `ClientPeer.CallAsync` without a server peer.

## Limitations / Known Gaps

- **Optimistic apply + reconcile/rollback is a deferred optional mode.** The prediction model here is the
  **pre-check gate only** (reject impossible ops locally). Optimistically applying a result before the
  server confirms — then reconciling/rolling back on mismatch — is an advanced mode that is intentionally
  NOT implemented.
- **Codegen shipped (NetworkLayer).** Request/response DTOs can be hand-written typed NetworkLayer messages
  bound to opcodes via `MessageCatalog`, or emitted by NetworkLayer's Roslyn source generator from a compact
  `[RemoteProcedure]` declaration (DTOs + explicit keys + pooling envelopes + the pair `Register` + `Execute`), with a companion
  analyzer for wire-versioning/opcode rules. See NetworkLayer's MODULE.md "Message codegen".
- **Cancellation is ambient + gameloop-owned; covers entry + post-effects, not the in-flight network hop.**
  A flow observes the host's ambient `Cancellation` token when its caller passes none (an explicit token to
  `RunAsync` still wins). A cancel throws *before* the flow enters the single-flight gate — so a session end /
  scene teardown / app pause aborts not-yet-started flows — and stops the cancellable post-effect phase. But the
  underlying `ClientPeer.CallAsync` is deadline-bounded, so a request already in flight faults on its deadline
  rather than being cancelled mid-flight.
- **Single-threaded, pump-driven transport.** Inherited from NetworkLayer: no delivery happens without a
  per-frame `ClientPeer.Update()`.
