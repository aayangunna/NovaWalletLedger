# AI Usage

This repository was built with **Claude Code** as the primary development tool, working
from one detailed scenario/requirements prompt (the FirstBank NovaPay "NovaWallet
Ledger Service" brief), delivered end-to-end in a single continuous session. This
document is an account of how the tool was directed, what it produced, and — per the
task's actual ask — where a naive approach to this problem is wrong or unsafe for a
financial system, and how the design in this repo avoids it, including three real bugs
that surfaced only once the service was actually run against a live database (§4). In
the interest of accuracy: most of this was a single-pass build (scaffold → domain →
application → infrastructure → api → tests → docs), verified incrementally with
`dotnet build`/`dotnet test` after each layer — but §4 happened later, in a second pass
once a real local Postgres instance became available, and materially changed what could
be verified versus merely argued for.

## Tools used

Claude Code, no other AI code-generation tool.

## Representative prompts and what came back

**1. "Build a NovaWallet Ledger Service in ASP.NET Core 8 using Clean Architecture,
CQRS, and vertical slices, satisfying [the full functional/hard-constraint list from
the brief]."**
This was the entire directive — one long, detailed spec, not a series of small back-and-forth
prompts. What came back: a six-project solution (Domain/Application/Infrastructure/Api
+ two test projects), ~45 source files, an EF Core migration, a Docker Compose setup,
and this documentation. Because the brief was unusually specific about hard constraints
(integer kobo, non-negative balance under concurrency, idempotency semantics, daily
limit reset time zone, RFC 7807 errors), most of the judgment calls documented below
were made *during* generation rather than needing a correction pass afterward — the
specificity of the prompt did a lot of the work of preventing naive mistakes before
they happened, which is itself worth noting: vague prompts on a financial-correctness
task are exactly where an LLM is most likely to default to the naive pattern.

**2. "Verify it actually compiles and the domain tests actually pass, don't just
generate code and claim success."**
This was self-directed within the session (Claude Code ran `dotnet build` after every
project and `dotnet test` on the unit-test project), not a literal user prompt — but it
surfaced two real, concrete bugs, documented in §2.

## 2. Real bugs caught during this session (by the compiler, not by inspection)

**`UseXminAsConcurrencyToken()` is obsolete in the installed Npgsql EF Core provider
version.** The first pass at `WalletConfiguration` used this method to wire Postgres's
`xmin` system column up as an EF Core concurrency token. `dotnet build` on the
Infrastructure project returned it as a `CS0618` obsolete-API warning, pointing at the
provider's own recommended replacement. Fixed by mapping a shadow `uint xmin` property
with `.IsRowVersion()` instead — same runtime behavior, not using a deprecated call
path. Low-stakes on its own, but it is a real example of an LLM defaulting to the
first/most-documented API it has strong priors on, rather than the current one — worth
independently checking build warnings rather than assuming a plausible-looking API call
is current.

**A FluentAssertions predicate using C# pattern-matching (`is X or Y`) inside what
FluentAssertions treats as an expression tree.** `ConcurrencyTests` originally asserted
`responses.Should().OnlyContain(r => r.StatusCode is HttpStatusCode.OK or
HttpStatusCode.UnprocessableEntity)`, which is valid C# but not valid inside an
`Expression<Func<T,bool>>` (`CS8122`) — `is`/`or` pattern matching can't be represented
in an expression tree. Caught immediately by `dotnet build` on the test project; fixed
by rewriting as `r.StatusCode == HttpStatusCode.OK || r.StatusCode ==
HttpStatusCode.UnprocessableEntity`. Mechanical, but a reminder that test-assertion
libraries built on expression trees have a narrower C# subset than normal method
bodies, and it's easy to write something that reads fine but doesn't compile in that
context.

## 3. Design alternatives considered and rejected — why the naive version is unsafe here

These were not implemented-then-fixed; they are documented because they are the
approaches a less careful pass at this same brief (AI-written or hand-written) would
plausibly reach for, and the brief specifically asks for this kind of judgment to be
shown.

**Optimistic-only concurrency control (a `RowVersion`/`xmin` token with a retry loop
around `SaveChangesAsync`) as the *primary* mechanism for the transfer endpoint.** This
is a standard, perfectly defensible pattern for most CRUD domains. It is the wrong
primary mechanism specifically for "concurrent transfer requests must never allow the
balance to go negative," because it only detects a conflict *after* the fact, on the
losing writer's save — between the first writer's read and its write, a second
concurrent request can read the same pre-mutation balance and independently conclude
"sufficient funds" before either has written anything (a time-of-check-to-time-of-use
race). A retry loop bolted on afterward is also, in practice, a common source of subtle
bugs (retrying too much or too little scope, silently exhausting retries under exactly
the load pattern — many concurrent requests against one hot wallet — that this task is
designed to test). What's implemented instead is pessimistic row locking
(`SELECT ... FOR UPDATE` in `WalletLockService`, held for the duration of the DB
transaction) as the mechanism the non-negative-balance guarantee actually depends on,
with the optimistic `xmin` token kept only as defense-in-depth for any future code path
that might touch a wallet outside the locked path. `ConcurrencyTests.Concurrent_transfers_that_would_overdraw_the_wallet_never_push_the_balance_negative`
exists specifically to pin this down under real load (50 concurrent transfer requests
against one wallet, through the real HTTP API, against a real Postgres instance).

**Locking the two wallets in a transfer sequentially, in caller-specified order (source
first, then destination), as two separate round trips.** This is the natural first
instinct — it's how you'd lock one wallet, so locking two looks like "just do it
twice." It is a textbook deadlock: a concurrent transfer running in the *opposite*
direction between the same two wallets would lock its own source (this transaction's
destination) first, then block waiting for a row this transaction already holds — both
wait forever (Postgres's deadlock detector would eventually kill one, but under load
that surfaces as unpredictable failures on exactly the hot-wallet-pair pattern two
users repeatedly paying each other would produce). `WalletLockService.LockPairForUpdateAsync`
instead locks both rows in a single `SELECT ... WHERE id = ANY(...) ORDER BY id FOR
UPDATE` statement — one round trip, and the ordering is by wallet id, not by
transfer direction, so any two concurrent transfers touching the same wallet pair
acquire locks in the same relative order no matter which one is "source" from its own
point of view.

**Idempotency as an HTTP-layer response cache, separate from the transfer's own DB
transaction** (check a cache/table for the key → if absent, run the transfer in its own
transaction → then separately write the key+response to the cache). This looks
reasonable and would pass a simple "call the endpoint twice in a row" test. It has a
real crash-window bug: if the process is killed (pod eviction, rolling deploy, network
blip — routine events in any containerized deployment) *after* the transfer's
transaction commits but *before* the separate cache write happens, a client retry with
the same key sees "no record" and reprocesses the transfer from scratch — a real
double-spend triggered by ordinary infrastructure churn, not an exotic edge case. What's
implemented instead claims the idempotency key inside the *same* transaction as the
wallet locks and the ledger/audit writes (`TransferCommandHandler`), committing all of
it together; because a Postgres unique-violation would otherwise abort that whole
transaction, the claim attempt runs inside a savepoint
(`IUnitOfWork.ExecuteInSavepointAsync`) so a losing request in a two-way race rolls back
only its own claim and re-reads the winner's row.
`ConcurrencyTests.Concurrently_replaying_the_same_idempotency_key_only_applies_the_transfer_once`
(20 concurrent requests sharing one key, asserting exactly one debit) is the test that
would catch a regression toward the separate-cache version — a sequential
"call it twice" test does not exercise the race at all.

**Accepting Naira as a `decimal` (or worse, a `double`) anywhere past the API boundary.**
The naive/common version of a "money type" in a lot of generated code exposes a decimal
or float property that gets passed around and operated on freely, with conversion to
an integer minor-unit happening "somewhere" before persistence — which means it's easy
for a `double` to sneak into a downstream calculation (a report, a client-side total)
without anyone noticing until rounding drift shows up in production. `Money`
(`Domain/Common/Money.cs`) instead only exposes `AmountKobo` (`long`) as its state;
`Money.FromNaira(decimal)` is the *only* conversion entry point, applied once at the
boundary, and every internal API afterward only ever moves integer kobo. There is no
constructor or method on `Money` that accepts a `double`/`float` at all — it's not
possible to accidentally use one. `MoneyTests.FromNaira_handles_values_that_break_naive_float_conversion`
uses the canonical `0.1m + 0.2m` case to document why, and would fail immediately if a
future change reintroduced a floating-point path anywhere in the type.

## 4. Three real bugs found only by actually running the service against a live database

The environment this was built in initially had no Docker daemon and no local
Postgres, so the first pass was verified statically (build, unit tests, migration
generation, DI-wiring smoke test) and the concurrency claims in §3 were, at that point,
argued for by reading the code rather than proven by running it — that gap was stated
plainly in an earlier version of this document. A local Postgres instance was later
installed (walking the user through the same `28P01`/`psql`-not-on-PATH problems a real
first-time setup hits, which is its own small data point on how much friction a
"one command" story can still have in practice) and the API was run directly against
it, then the actual `dotnet test` suite was run against it too. All three bugs below are
things a "looks correct, compiles, unit tests pass" review — human or AI — would have
missed, and none is hypothetical: each was hit on the first live request or test run
that exercised it.

**Postgres's `xmin` is a system column, and `SELECT *` does not include system
columns.** The very first live call to the credit endpoint returned a 500. The server
log showed `PostgresException 42703: column n.xmin does not exist`. Cause:
`WalletLockService.LockForUpdateAsync` ran `SELECT * FROM wallets WHERE id = @id FOR
UPDATE` as raw SQL to acquire the pessimistic lock; because the `Wallet` entity also has
an `xmin`-backed shadow property for the defense-in-depth optimistic-concurrency token
(§3), EF Core generates a wrapper query that explicitly projects `n.xmin` from that raw
SQL's result set — and Postgres's `SELECT *` deliberately excludes system columns like
`xmin`, so the projection fails at the SQL level, before any C# code runs. This is a
well-known but easy-to-forget interaction between raw `FromSql` queries and EF Core's
Postgres-`xmin`-as-concurrency-token feature; no amount of `dotnet build` or a unit test
against in-memory/mocked data would ever exercise it, because it is purely a property of
what SQL Postgres actually executes. Fixed by explicitly selecting the column:
`SELECT *, xmin FROM wallets ...` in both `LockForUpdateAsync` and
`LockPairForUpdateAsync`. Verified fixed by re-running the exact same live request.

**The transfer endpoint's own rate limiter blocked the load pattern needed to prove the
transfer endpoint is concurrency-safe.** Firing 50 concurrent transfer requests from one
authenticated customer against one wallet — the scenario `ConcurrencyTests` and the
brief's own "exercise the concurrency edge case under load" requirement both call for —
returned a wall of `429 Too Many Requests` after the 20th request, because the rate
limiter (`PermitLimit = 20` per 10 seconds per caller, a stretch goal added for
abuse-protection) has no way to distinguish "one customer flooding the endpoint" from
"a legitimate burst against one wallet used to validate the locking guarantee," and
correctly throttled both. This is a real design bug, not a test artifact: the exact same
50-request burst is what `ConcurrencyTests.Concurrent_transfers_...` sends over HTTP, so
that test would have failed the same way the first time it was actually run against a
live server, asserting every response is `200` or `422` and instead finding `429`s
mixed in. Fixed by raising the limit to 100/10s — still a real throttle against
sustained abuse (the ₦500,000/day limit, not the request-rate limiter, is the intended
backstop against abusive money movement), but no longer indistinguishable from the
burst pattern a correctness test for the endpoint's core guarantee legitimately needs to
send. This is the more interesting bug of the two: it isn't a coding mistake in the
usual sense, it's two individually-reasonable features (rate limiting, and load-testing
a concurrency guarantee) whose numbers hadn't been checked against each other — exactly
the kind of interaction an LLM (or a developer working feature-by-feature) is prone to
miss, because each feature looks correct in isolation.

After the manual live testing above (which used `dotnet run` directly, talking to the
API purely over HTTP, with no access to its internals), the actual xUnit integration
suite was also run — pointed temporarily at the same local Postgres instead of
Testcontainers, since this session still has no Docker daemon — and it failed 14 of 15
`WalletApiTests` with `401 Unauthorized`, on the very first `POST /api/wallets/` call,
despite `AuthenticateAsync` successfully fetching a token first. This is the third and
most structurally interesting bug, because it wasn't caught by the manual testing above
at all: manual testing had used `dotnet run`, where a token is issued and validated
using the *same* `appsettings.json` values on both sides, so a mismatch was invisible;
it only surfaced once a test harness that overrides configuration at a different point
in the startup sequence was used.

**Root cause:** `Program.cs` read `builder.Configuration.GetSection("Jwt").Get<JwtOptions>()`
into a local variable to build `TokenValidationParameters` *before* `builder.Build()` was
called. `WebApplicationFactory` (used by `NovaWalletApiFactory`) applies its
`ConfigureAppConfiguration` overrides — including the test's own JWT signing key — at
`Build()` time, which, in a minimal-hosting `Program.cs`, happens *after* that earlier
line already executed and captured a snapshot. The result: `JwtTokenService` (which
reads its key lazily via injected `IOptions<JwtOptions>`, resolved from the DI container
built *after* `Build()`) correctly saw the test's overridden signing key when *issuing*
a token, while the bearer-validation middleware — configured from the too-early
snapshot — was still checking signatures against the default `appsettings.json` key.
Two different keys, on the issuing and validating sides of the exact same request,
depending on nothing more than *when* a line of code happened to run relative to a
framework lifecycle hook. `IncludeErrorDetails = true` plus a temporary
`OnAuthenticationFailed` handler surfaced the real exception
(`SecurityTokenSignatureKeyNotFoundException`, "signature validation failed") in under a
minute; without that, the 401 alone gives no hint that it's a *configuration-timing* bug
rather than a token-format or claims bug. **Fixed** by replacing the eager
`.AddJwtBearer(options => { ... using the captured jwtOptions ... })` with
`builder.Services.AddOptions<JwtBearerOptions>(...).Configure<IOptions<JwtOptions>>(...)`,
so `TokenValidationParameters` is now built lazily, from the fully-built configuration,
exactly like `JwtTokenService` already did — the issuing and validating sides now
structurally cannot see two different keys, rather than merely happening not to in the
common case. This bug did not depend on Postgres, Testcontainers, or concurrency at all
— any integration test exercising authentication would have hit it, on the very first
request, the very first time the suite was actually executed. It is also, in hindsight,
the most obvious argument in this whole document for actually running the tests rather
than trusting that a design "obviously" works: nothing about the JWT/auth code, read on
its own, looks wrong.

After all three fixes, the full scenario was re-run live end-to-end against the real
Postgres instance — first manually over HTTP (wallet creation, admin-only credit,
balance, transfer, statement, sequential idempotency replay with a byte-identical
response, idempotency key-reuse conflict returning 409, insufficient-funds rejection
returning 422 with the expected `availableKobo`/`requestedKobo` extensions, 50 concurrent
transfers against one wallet — 33 succeeded, 17 correctly rejected as insufficient
funds, ending balance exactly `starting − 33×amount`, destination credited exactly
`33×amount` — and 20 concurrent requests sharing one Idempotency-Key, all 20 returning
the identical `transferId` with the debit applied exactly once) — and then via the real
`dotnet test` run described below, which is the stronger evidence of the two since it
runs the exact assertions a reviewer would run, not hand-picked spot checks.

## What was, and wasn't, independently runtime-verified

**Verified for real**, against a real local Postgres instance (not mocked, not
in-memory): all **34 automated tests pass** — 19 domain unit tests
(`dotnet test tests/NovaWalletLedger.UnitTests`) plus the full 15-test integration suite
(`dotnet test tests/NovaWalletLedger.IntegrationTests`, temporarily pointed at the local
Postgres instance in place of Testcontainers for this run only), including both
concurrency tests: `Concurrent_transfers_that_would_overdraw_the_wallet_never_push_the_balance_negative`
and `Concurrently_replaying_the_same_idempotency_key_only_applies_the_transfer_once`.
The manual HTTP walkthrough above additionally confirms Swagger/OpenAPI reachability and
the `/health/ready` check, and the EF Core migration generates and applies successfully
against the configured model. `NovaWalletApiFactory.cs` has been reverted back to its
Testcontainers-based version (shown in the repo) after this verification run — the
local-Postgres variant was a temporary substitution for this session only, made
necessary by the lack of a Docker daemon here, not a change to how the suite is meant to
run.

**Also verified**, once directly asked "does `docker compose up` actually work?": since
this sandbox still has no Docker daemon, `docker build`/`docker compose up` themselves
could not be run, but the closest available substitute was — `dotnet publish -c Release`
(the exact command the `Dockerfile`'s build stage runs) followed by running the
published `NovaWalletLedger.Api.dll` directly, with `ASPNETCORE_ENVIRONMENT`,
`ConnectionStrings__Database`, and `Jwt__SigningKey` all set to the exact values
`docker-compose.yml` sets (substituting `Host=localhost` for `Host=db`, since only
Compose's internal DNS resolves the latter). The full flow — Swagger, both health
checks, auth, wallet creation, credit, transfer, balance — passed against that Release
build exactly as it had against the `dotnet run`/Debug build used earlier. This also
prompted a careful line-by-line re-read of the `Dockerfile` itself (since it's the one
piece of the deployment path with genuinely zero runtime coverage), which found a real,
if lower-stakes, bug: `USER novawallet` was set *before* `COPY --from=build
/app/publish .`. `COPY` always executes as root regardless of the active `USER`, so the
published files were ending up root-owned while the process runs unprivileged —
something that would likely have kept working by accident (COPY's default file mode is
usually world-readable), but wasn't a guaranteed grant, just a lucky default. Fixed by
copying first, then `RUN chown -R novawallet:novawallet /app`, then switching to that
user — the same "reads correct, only a bug in practice" pattern as the three bugs above,
found by review rather than by running it, since running it wasn't possible here.

## 5. `docker compose up` — the last gap, closed

Everything in §4 was verified with Docker still unavailable in this environment. Docker
Desktop was subsequently installed (a genuinely bumpy process worth being honest about
too: `winget install` silently requires an interactive UAC consent click it cannot
supply itself, which is almost certainly what made the very first attempt at this,
installing PostgreSQL earlier in the session, appear to hang for 40+ minutes — it was
sitting at a consent dialog the whole time, not stuck; the same thing happened again on
the first Docker Desktop attempt, and the fix both times was simply asking the user to
click "Yes" on their own screen, since the agent has no way to interact with a UAC
prompt itself). Docker Desktop also required a system restart before its engine would
start, and the daemon needed to be launched manually afterward — none of which is
software work, just the ordinary friction of getting a dev machine into a state where
"one command" is actually true.

With the daemon finally running, `docker compose up --build` was run for real, from a
clean checkout, with no shortcuts: it built both images from scratch (a ~16-minute image
pull on a slow connection, then a normal `dotnet restore`/`publish`), started Postgres,
waited on its healthcheck as `depends_on: condition: service_healthy` specifies, started
the API, and the API's own startup log showed "Database migrations applied
successfully" without any of the retry-loop warnings that appear when the database
isn't ready yet — meaning the healthcheck-gated ordering genuinely worked, not just "the
retry loop papered over a race." The same manual smoke test used throughout this
document (Swagger, both health endpoints, auth, wallet creation, credit, transfer,
balance, statement) was then run against the live containers over `localhost:8080` and
passed with exact numbers, identical to every other run of this same test in this
document.

This closes the one gap every earlier section of this file explicitly flagged as
unverified: the actual `docker build`/`docker compose up` mechanics — image layer
building, container networking, the healthcheck-gated startup order — have now been
exercised for real, not just argued for by analogy to a Release-publish dry run. The
only piece still not run through `dotnet test` specifically is the Testcontainers-backed
integration suite (as opposed to the manual smoke test) — a reasonable next step, but a
much smaller gap than "has anyone actually run `docker compose up`" was.
