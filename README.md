# NovaWallet Ledger Service

A simplified wallet ledger service for **FirstBank NovaPay's NovaWallet** module — the
component that must never lose, duplicate, or miscount a customer's money.

Built with **ASP.NET Core 8**, combining **Clean Architecture** (Domain / Application /
Infrastructure / Api), **CQRS** (via MediatR commands and queries), and a **vertical
slice** organization inside the Application layer (each capability — CreateWallet,
CreditWallet, Transfer, GetBalance, GetStatement — is one folder containing its
command/query, validator, handler, and response, rather than being spread across
repository/service/controller layers).


---

## 1. Quick start

```bash
docker compose up --build
```

That is the only command needed. It starts Postgres, waits for it to be healthy, then
starts the API, which applies EF Core migrations automatically on boot.

- API: http://localhost:8080
- Swagger / OpenAPI UI: **http://localhost:8080/swagger**
- Liveness: http://localhost:8080/health/live
- Readiness (checks DB connectivity): http://localhost:8080/health/ready

### Getting a token (mock issuer)

There is no real identity provider in this exercise — see [§6](#6-authentication-the-mock-issuer).
Mint a bearer token for a given "customer":

```bash
curl -s -X POST http://localhost:8080/api/auth/token \
  -H "Content-Type: application/json" \
  -d '{"customerId":"11111111-1111-1111-1111-111111111111"}'
```

Use the returned `accessToken` as `Authorization: Bearer <token>` on every other call.
There are no role tiers — every authenticated customer can call every endpoint, scoped
to their own wallet (see [§5](#5-authorization-model)).

### End-to-end walkthrough

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/token \
  -H "Content-Type: application/json" \
  -d '{"customerId":"11111111-1111-1111-1111-111111111111"}' | jq -r .accessToken)

# 1. Create a wallet (starting balance zero, in NGN)
WALLET=$(curl -s -X POST http://localhost:8080/api/wallets/ \
  -H "Authorization: Bearer $TOKEN" | jq -r .walletId)

# 2. Credit it (simulates an inbound NIP transfer) — a customer credits their own wallet
curl -s -X POST http://localhost:8080/api/wallets/$WALLET/credit \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"amountKobo": 5000000, "externalReference": "NIP-DEMO-001"}'

# 3. Check balance (amounts are always integer kobo; 5,000,000 kobo = NGN 50,000.00)
curl -s http://localhost:8080/api/wallets/$WALLET/balance -H "Authorization: Bearer $TOKEN"

# 4. Transfer — Idempotency-Key is required
curl -s -X POST http://localhost:8080/api/wallets/$WALLET/transfer \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"destinationWalletId":"<some other wallet id>", "amountKobo": 150000, "description":"Rent contribution"}'

# 5. Statement (paginated, newest first)
curl -s "http://localhost:8080/api/wallets/$WALLET/statement?page=1&pageSize=20" -H "Authorization: Bearer $TOKEN"
```

---

## 2. Architecture

```
src/
  NovaWalletLedger.Domain/          # Money(kobo), Wallet aggregate, LedgerEntry,
                                     # AuditLogEntry, IdempotencyRecord, OutboxMessage.
                                     # Zero dependencies on anything else.
  NovaWalletLedger.Application/     # CQRS handlers, one vertical slice per capability:
    Features/Wallets/
      CreateWallet/  CreditWallet/  Transfer/  GetBalance/  GetStatement/
    Common/          # IApplicationDbContext, IUnitOfWork, IWalletLockService,
                      # IIdempotencyService, MediatR pipeline behaviors
  NovaWalletLedger.Infrastructure/  # EF Core + Npgsql, the locking/idempotency
                                     # implementations, JWT issuance, outbox dispatcher
  NovaWalletLedger.Api/             # Minimal API endpoints, JWT bearer auth, Swagger,
                                     # rate limiting, RFC 7807 exception handling
tests/
  NovaWalletLedger.UnitTests/        # Pure domain logic, no I/O
  NovaWalletLedger.IntegrationTests/ # Full API host + real Postgres via Testcontainers,
                                      # including the concurrency load test
```

**Dependency direction:** Api → Infrastructure → Application → Domain. The Application
layer only depends on abstractions (`IApplicationDbContext`, `IWalletLockService`, ...);
Infrastructure is the only project that knows about EF Core, Npgsql, or SQL.

### Why CQRS + vertical slices *inside* clean architecture, not instead of it

A pure "vertical slice" architecture (a la feature folders with no layering at all) is
attractive for its locality, but a financial ledger's core invariants — non-negative
balances, atomic transfers, an immutable audit trail — are exactly the kind of logic
that benefits from being pushed into a well-tested Domain layer with zero
infrastructure concerns. So: Domain stays a classic isolated core; everything above it
is organized as vertical slices (one folder per capability) so that adding, say, a
future `FreezeWallet` feature never means touching five different layer folders.

---

## 3. The money path: how correctness is enforced

### 3.1 Integers only

`Money` (`Domain/Common/Money.cs`) wraps a `long AmountKobo` and a currency code. There
is no `float`/`double` anywhere in the money path — `Money.FromNaira(decimal)` is the
only place a fractional Naira amount is ever converted, and it converts straight to
integer kobo. All arithmetic in `Money` and `Wallet` runs inside `checked { }` blocks so
an overflow throws instead of silently wrapping.

### 3.2 Never negative, concurrency-safe transfers

This is the hard constraint, and it is enforced with two layers:

1. **Pessimistic row locking.** Every mutation (`Credit`, `Debit` via `Transfer`) runs
   inside a DB transaction that first issues `SELECT ... FOR UPDATE` against the wallet
   row(s) (`WalletLockService`). No other transaction can read-modify-write that row
   until this one commits or rolls back — this is what actually prevents the
   double-spend, not application-level checks.
2. **Deterministic lock ordering for transfers.** `LockPairForUpdateAsync` locks both
   the source and destination wallet in a *single* `SELECT ... WHERE id = ANY(...)
   ORDER BY id FOR UPDATE` statement. Locking by ascending id — regardless of transfer
   direction — means two concurrent transfers between the same pair of wallets in
   opposite directions acquire their locks in the same relative order and can never
   deadlock.

The balance check itself lives on the `Wallet` aggregate (`Wallet.Debit`), which throws
`InsufficientFundsException` rather than ever allowing `BalanceKobo` to go negative;
there is also a DB-level `CHECK (balance_kobo >= 0)` constraint as a last-resort
backstop, and Postgres's hidden `xmin` column is wired up as an EF Core concurrency
token for defense-in-depth against any future code path that might read a wallet
outside the locking transaction.

**Tested by:** `ConcurrencyTests.Concurrent_transfers_that_would_overdraw_the_wallet_never_push_the_balance_negative`
— fires 50 concurrent transfer requests (₦300 each) against a wallet funded with only
₦10,000 (so only ~33 can legitimately succeed) through the real HTTP API against a
real Postgres instance, then asserts the ending balance is exactly
`starting − (successful_count × amount)`, never negative, and the destination wallet
was credited exactly once per success.

### 3.3 Idempotency

The transfer endpoint requires an `Idempotency-Key` header. Semantics:

| Situation | Result |
|---|---|
| New key | Processed normally |
| Same key + same payload, already completed | The *exact* original response is replayed (no reprocessing) |
| Same key + same payload, still in flight | `409 Conflict` ("processing") |
| Same key + different payload | `409 Conflict` (key reuse) |

Implementation: an `idempotency_records` row is inserted (claimed) via a **unique DB
constraint on `Key`**, inside the *same* transaction/lock as the transfer itself —
never as a separate cache layer — so the claim and the money movement always commit or
roll back together. Because a Postgres unique-violation aborts the whole transaction by
default, the claim attempt runs inside a **savepoint**
(`IUnitOfWork.ExecuteInSavepointAsync` / `IdempotencyService`); if two requests race to
claim a brand-new key, the loser's insert fails, rolls back to the savepoint (not the
whole transaction), and re-reads the winner's row to decide replay vs. conflict.

Business rejections (insufficient funds, daily limit) are also recorded against the
idempotency key and committed, so replaying a failed request returns the same status
code, title, and detail message instead of re-attempting it — verbatim from what was
stored, not re-derived (the one thing that deliberately does *not* match the original
response is `traceId`, since a replay is a distinct HTTP call and its own log lines
should carry their own correlation id). Genuine infrastructure failures (a DB timeout,
an unhandled exception) are *not* recorded — the whole transaction rolls back so a
legitimate retry with the same key can still succeed later.

**Tested by:** `Replaying_the_same_idempotency_key_does_not_double_process_the_transfer`,
`Reusing_an_idempotency_key_with_a_different_payload_is_rejected`, and — the harder
case — `Concurrently_replaying_the_same_idempotency_key_only_applies_the_transfer_once`
(20 concurrent requests, same key, asserts exactly one debit).

### 3.4 Daily outbound limit (₦500,000/day, resets at midnight WAT)

West Africa Time is a fixed `UTC+1` offset with **no DST rules**, so
`WestAfricaClock` (`Domain/Common`) hardcodes the offset rather than resolving an
IANA/Windows time zone name — this keeps the calculation correct and deterministic
across host OSes/containers regardless of whether tzdata is installed, and it is
provably correct for WAT specifically (it would be the wrong shortcut for a zone with
DST). The amount already debited "today" is computed as `SUM(debits WHERE
created_at_utc >= start_of_wat_day)` **inside the same locked transaction** as the
prospective debit, so it is consistent with any concurrent debit attempts against the
same wallet — it can never read a stale total.

### 3.5 Audit trail vs. statement — two separate, append-only tables

- `ledger_entries` — the customer-facing statement (`GET /wallets/{id}/statement`).
- `audit_log_entries` — a separate, append-only compliance/forensic trail. It records
  every balance mutation **and every rejected attempt** (insufficient funds, limit
  exceeded), with actor id, correlation id, and the idempotency key involved.

Nothing in this codebase ever issues an `UPDATE` or `DELETE` against either table —
corrections are always new, offsetting entries. For a production deployment, the
recommended next step is to `REVOKE UPDATE, DELETE` on `audit_log_entries` at the
database-role level so immutability is enforced by Postgres itself, not just by
application discipline; that hardening step (and the corresponding least-privilege DB
role split) was left out of this exercise's `docker-compose.yml` to keep the "one
command to run it" story simple, and is called out here deliberately rather than
silently skipped.

---

## 4. Kobo, BVN/NIN, NIP, USSD — what's modeled and what's deliberately out of scope

The brief describes a full super-app; this service is scoped to the ledger primitive
only. Explicitly out of scope, by design:

- **BVN/NIN-based tiered KYC** — `CustomerId` is an opaque `Guid`; a real system would
  resolve BVN/NIN to this id during onboarding, upstream of this service.
- **Real NIBSS NIP integration** — `POST /wallets/{id}/credit` *simulates* an inbound
  NIP transfer landing in the wallet. In a real deployment this would be triggered by
  NIBSS's webhook, not initiated by the customer; this service has no separate
  internal/system role tier, so it is scoped to the wallet owner's own token instead —
  a deliberate simplification for a single-tier demo, called out explicitly rather than
  left implicit (see [§5](#5-authorization-model)).
- **USSD (`*894#`) channel** — out of scope; this is a JSON API, USSD would be a
  separate gateway calling into it.
- **CBN/NDPA compliance program** — noted as an operating constraint, not implemented.
  Two touchpoints this design was written with in mind: (1) the audit trail's
  immutability requirement above maps to typical CBN consumer-protection
  record-keeping expectations, and (2) storing only an opaque `CustomerId` (no BVN/NIN,
  name, or phone number) in this service keeps it outside the strictest NDPA data
  classes — a real deployment would still need a documented data-processing
  justification and retention policy for `audit_log_entries`.

---

## 5. Authorization model

JWT claims: `sub` (customer id, a GUID) only — there are no role tiers. Every
capability is available to every authenticated customer, scoped by **wallet
ownership**, enforced per-handler (not via `[Authorize(Roles = ...)]`):

| Endpoint | Rule |
|---|---|
| `POST /wallets` | Any authenticated user — creates a wallet **for themselves** (`sub`). One wallet per customer. |
| `GET /wallets/{id}/balance`, `/statement` | Caller must own the wallet. |
| `POST /wallets/{id}/credit` | Caller must own the wallet — a customer credits their own wallet, simulating an inbound NIP transfer landing in it. |
| `POST /wallets/{id}/transfer` | Caller must own the **source** wallet. |

This is intentionally more than "check the JWT is present" — every handler
independently verifies the caller's `sub` against the wallet's `CustomerId` before
touching it, which is real claims-based authorization even without a role hierarchy,
per the task's framing that the point is "the middleware and claims handling." An
earlier version of this design gated `credit` behind a separate `admin`/`system` role
(modeling that, in production, an inbound NIP transfer is triggered by a webhook, not
the customer) — removed in favor of a single-tier model where a customer can exercise
every capability, at the cost of that one piece of production realism.

---

## 6. Authentication: the mock issuer

`POST /api/auth/token` issues a signed JWT for whatever `customerId` is asked for. It
is **intentionally unauthenticated and not representative of a production auth
flow** — a real deployment would federate to FirstBank's actual identity provider
(OIDC/JWKS). It exists purely so the API is runnable and testable end-to-end without
standing up a full auth server, per the task's own framing. The signing key is read
from `Jwt:SigningKey` config (overridden in `docker-compose.yml`) — **rotate/replace it
before this ever runs anywhere but a local demo.**

---

## 7. Errors: RFC 7807 Problem Details

Every error path — validation, domain rule violations, not-found, forbidden,
idempotency conflicts, and unhandled exceptions — is funneled through one
`IExceptionHandler` (`GlobalExceptionHandler`) into a consistent
`application/problem+json` body (`type`, `title`, `status`, `detail`, `traceId`, plus
context-specific extensions like `availableKobo` on an insufficient-funds error).
FluentValidation failures become a `ValidationProblemDetails` with per-field messages.

---

## 8. Stretch goals implemented

- **Rate limiting** on the transfer endpoint (`AddRateLimiter`, fixed window, 100
  requests / 10s / caller) — `429 Too Many Requests` on breach. Sized deliberately
  generously: a per-customer request-count limiter is meant to catch flooding/abuse,
  not to cap legitimate transaction frequency — the ₦500,000/day limit is the real
  backstop against abusive money movement. (An earlier, tighter limit of 20/10s was
  raised after it started rejecting the concurrency load test's own legitimate burst
  traffic with `429`s — see `AI_USAGE.md` §4.)
- **Outbox pattern**: a `TransferCompleted` event is written to `outbox_messages` in
  the *same transaction* as the transfer, guaranteeing at-least-once delivery without a
  distributed transaction across the DB and a broker. `OutboxDispatcherService`
  (a `BackgroundService`) polls and publishes — the reference implementation "publishes"
  via structured log (see container logs); swapping that one method for a real
  Kafka/SNS/RabbitMQ producer is the only change needed for production.
- **Structured logging with correlation IDs**: Serilog, with `HttpContext.TraceIdentifier`
  pushed onto the log context for every request and surfaced in every Problem Details
  response as `traceId`, so a support engineer can correlate a customer-reported error
  with the exact log lines across the whole request.
- **Health/readiness endpoints**: `/health/live` (process is up, no dependency checks —
  suitable for a container orchestrator's liveness probe) and `/health/ready` (checks
  DB connectivity — suitable for a readiness probe / load balancer target check).

---

## 9. How to test

```bash
# Domain unit tests — no external dependencies
dotnet test tests/NovaWalletLedger.UnitTests

# Full API + real Postgres via Testcontainers — requires a running Docker daemon
dotnet test tests/NovaWalletLedger.IntegrationTests
```

The integration suite includes `WalletApiTests` (happy paths, authorization,
idempotency, daily limit, pagination) and `ConcurrencyTests` (the load-based
double-spend and concurrent-idempotency-replay tests described in §3.2/§3.3).

> This solution was originally built without a Docker daemon available in the authoring
> sandbox, so verification happened in stages. First, with only a local Postgres install
> available: the actual test suite was pointed at it in place of Testcontainers, and
> **all 34 automated tests passed** — 19 unit tests and the full 15-test integration
> suite, including both `ConcurrencyTests` (50 concurrent transfers against one wallet;
> 20 concurrent requests sharing one Idempotency-Key). The happy-path/idempotency/
> authorization scenarios were also reproduced manually over raw HTTP as an independent
> check. That process caught and fixed three real bugs that static verification alone
> had missed: an EF Core/Postgres `xmin`-system-column interaction that 500'd the very
> first live request; a too-tight rate limit that rejected the concurrency test's own
> legitimate burst traffic; and a JWT configuration-timing bug where `Program.cs`
> captured its signing key too early relative to when `WebApplicationFactory` applies
> test configuration overrides, so every authenticated integration test failed with 401
> the first time the suite was actually run — see `AI_USAGE.md` §4 for the full account.
> Before Docker became available, `docker build`/`docker compose up` were also checked as
> closely as possible without it — the exact `dotnet publish -c Release` command the
> `Dockerfile` runs, followed by running the published DLL with the exact environment
> variables `docker-compose.yml` sets — which caught a real Dockerfile bug: `USER
> novawallet` was set before `COPY --from=build /app/publish .`, so the published files
> would have ended up root-owned while the process runs unprivileged. Fixed by copying
> first, then `chown`, then switching user.
>
> **`docker compose up --build` has since actually been run**, end to end, from a clean
> checkout, once Docker Desktop was installed: both containers built and started, the
> `db` service's healthcheck-gated startup ordering worked, migrations applied
> automatically, and the same full manual smoke test (Swagger, health checks, auth,
> wallet creation, credit, transfer, balance, statement) passed against the running
> containers with exact numbers. That closes the "single command" requirement's last
> remaining gap — the whole stack has now been verified at every layer described in this
> README, not just argued for.

---

## 10. Trade-offs & what would change for a real production deployment

- **Single Postgres instance, no read replica.** Fine for a ledger this size; a
  high-throughput NovaWallet would split statement reads to a replica.
- **Outbox publishes to a log, not a real broker.** The pattern (transactional write +
  polling dispatcher) is production-shaped; only the transport is a stand-in.
- **One wallet per customer.** The brief describes savings goals, Safe Lock, etc.
  (NovaSave) as separate sub-products; this ledger only models the core NovaWallet
  e-wallet balance, so "one wallet per customer" is the right scope boundary here —
  multi-wallet-per-customer would be the natural extension for NovaSave.
- **Mock JWT issuer.** Covered in §6 — swap for real OIDC before production.
- **No database-role hardening for audit-log immutability.** Covered in §3.5.
