# Implicit state-machine audit

> Implementation follow-up: the findings below are the original audit snapshot. See [remediation and remaining boundaries](#implementation-follow-up--2026-11-07) for the subsequent fixes.

Date: 2026-11-07. Audited repository revision: `fc6fbafc4ae25e2a95a798cb788bbf026d5fa3b2`.

## Scope and evidence

This is a source-level architectural audit of the Bare Bitcoin plugin, its frontend setup fragment, Node setup helper, persistence, background polling, authentication, development/release helpers, and repository workflows. The tracked BTCPay dependency is `submodules/btcpayserver` at `2d5a0d8077bb33af080e949031da33d84b80638d` (v2.4.4). Its directly connected setup, invoice creation, monitoring, and payment-recording paths were traced. This is **not** an audit of every unrelated subsystem in the vendored BTCPay application (for example, its independent wallet, user-registration, and payout machines).

All references below are repository-relative `path:line` references at those revisions. Ranges identify the complete relevant block. No application code, configuration, or tests were changed. No live provider calls, deployments, release scripts, or test executions were performed. Existing tests were inspected as evidence of intended behavior, not treated as evidence that new scenarios pass. “Confirmed” means a transition follows directly from the inspected implementation; it does not mean a production incident was reproduced. Likelihood is qualitative, based on the triggering conditions, not operational measurements.

Provider assumptions are limited to the checked-in OpenAPI document. In particular, `plugin/Resources/swagger/v1/openapi.json:548–568` lists UNSPECIFIED, UNPAID, PENDING, PAID, EXPIRED, and CANCELED; it does not define a complete transition contract, eventual-consistency guarantee, or actual received-amount field. Those uncertainties are called out rather than converted into business rules.

## Prioritized findings

| ID | Severity | Likelihood / condition | Classification | Finding |
| --- | --- | --- | --- | --- |
| F01 | High | Medium: slow requests or checkout cancellation | Confirmed | Cancellation does not reach HTTP or the signing queue; a canceled create can still create an invoice and then discard its identity. |
| F02 | High | Medium: malformed but successful provider response | Confirmed transition; provider occurrence unknown | Missing invoice field is treated as authoritative absence and removes monitoring. |
| F03 | High | Low–medium: shutdown during an in-flight write | Confirmed race | Registry disposal does not join in-flight flushes; it can report completion before durability and suppress a later failed-write retry. |
| F04 | High if amountless invoices are accepted | Unknown provider support | Conditional integration defect | Paid amount is inferred from the BOLT11 minimum, so an amountless payment is reported as zero. |
| F05 | Medium | High when configuring a wrong secret/account or read-only key | Confirmed | “Connection successful” does not establish receive capability or even account existence. |
| F06 | Medium | Medium during throttling | Confirmed | The final failed retry bypasses Retry-After deferral; 429s do not increase listener backoff. |
| F07 | Medium | Low–medium: damaged/unreadable state | Confirmed | A failed registry load is writable empty state and can overwrite the previous registry. |
| F08 | Medium | Medium when some requests stall | Confirmed latency coupling | One slow invoice blocks delivery of every completed result in the poll cycle. |
| F09 | Medium | Low: concurrent stale lookup and terminal cleanup | Confirmed possible ordering; provider behavior conditional | A stale unpaid result can resurrect terminal tracking and ownership can be reassigned after untracking. |
| F10 | Medium | Low: notification loss, restart, or multiple listeners | Conditional risk, partly mitigated by host | Queue insertion is treated as delivery without a durable consumer acknowledgement. |
| F11 | Low–medium | Medium: missing/unknown status or unreconciled legacy ID | Confirmed retention behavior | Several states have no automatic exit, age limit, or operator resolution path. |
| F12 | Low–medium | Medium during connection checks/provider slowness | Confirmed | Balance state has a global lock, per-instance cache, inconsistent TTL, and conflates unavailable with zero. |
| F13 | Low | Common when provider omits preimage; timing matters for timestamp | Confirmed | Paid metadata is synthesized rather than observed: lookup time becomes payment time and payment hash becomes preimage. |
| F14 | Low | Low: interrupted helper/release/CI execution | Confirmed gaps / conditional impact | Tooling has unbounded waits and partial-publication states without resumable transitions. |

### F01 — Cancellation stops local tracking, not the external operation

Execution: BTCPay creates a timeout token in `submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningLikePaymentHandler.cs:103–125` → `plugin/BareBitcoinLightningClient.cs:429–451` POST → `plugin/Services/BareBitcoinApiService.cs:122–148` waits on the per-key semaphore without cancellation → `:294–295` sends and reads without cancellation. Neither API method accepts a token. After a successful response, `plugin/BareBitcoinLightningClient.cs:471` finally passes the original token to tracking. If it is already canceled, `plugin/Services/BareBitcoinInvoiceService.cs:70` throws before insertion. The external invoice exists, but the caller receives cancellation and no invoice identity. A token already canceled at entry can still cause a POST.

GET and balance calls have the same transport gap (`plugin/BareBitcoinLightningClient.cs:163`; `plugin/Services/BareBitcoinBalanceService.cs:61`). Listener disposal waits five seconds but ignores the wait's boolean result, then disposes its token source (`plugin/Services/BareBitcoinListener.cs:278–295`); pending HTTP can outlive disposal and later encounter disposed cancellation state. A finite HttpClient timeout can eventually terminate a request, but does not implement the caller's deadline. A queue of signed operations has no total deadline.

Recommendation: carry cancellation through queue acquisition, send, and response reading; reject pre-canceled operations before side effects. Separately model `CreateOutcomeUnknown` because canceling a POST cannot prove the provider did not commit it. Use provider idempotency/reconciliation only if supported; otherwise retain enough local correlation to expose and resolve uncertain outcomes. Join polling before disposing its dependencies.

Proposed tests: pre-canceled create makes zero requests; cancel while queued behind a signed request and assert no subsequent POST; cancel during HTTP and assert the handler observes the token; let provider commit before cancellation and assert a deliberate reconciliation result rather than silent identity loss; hold GET past five seconds and verify disposal never frees objects still used by the worker. Existing cancellation tests at `BTCPayServer.Plugins.Tests/BareBitcoinLightningClientTests.cs:209,329,444` exercise thrown cancellation, not end-to-end token propagation.

### F02 — Invalid response becomes destructive absence

`plugin/BareBitcoinLightningClient.cs:165–176` returns the same `null` for HTTP 404 and HTTP 200 with a missing/empty `invoice`. `plugin/Services/BareBitcoinListener.cs:149–154` removes either from tracking. The host's direct poll also removes it from `_ListenedInvoices` at `submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:494–501`. Consequently, a transient `{}` response can stop same-process monitoring; subsequent cycles no longer query the ID. By contrast invalid JSON, malformed BOLT11, and mismatched payment hashes throw and remain retryable.

The transition is confirmed; an assertion that the provider actually emits incomplete responses would be speculative. Even authoritative 404 may need a policy if newly created invoices are eventually visible; the checked-in contract does not answer that question.

Recommendation: distinguish `Found`, `AuthoritativeNotFound`, and `InvalidResponse`; retain tracking for invalid payloads. Define a bounded confirmation policy for 404 only after establishing provider semantics. Test tracked invoice → `{}` → valid paid response and require eventual delivery; separately test confirmed 404. Current `BTCPayServer.Plugins.Tests/BareBitcoinLightningClientTests.cs:455` and `BTCPayServer.Plugins.Tests/BareBitcoinListenerTests.cs:92` individually encode the dangerous composition.

### F03 — Disposed does not mean all snapshots have finished writing

`plugin/Services/BareBitcoinInvoiceService.cs:201–205` clears `_dirty` before acquiring `_diskWriteLock` and performing the write (`:220–225`). If another `FlushAsync` is already suspended in `WriteAsync`, `DisposeAsync` sets `_disposed`, disposes the timer, and invokes a second flush (`:247–259`). That second flush sees `_dirty == false` and returns without acquiring the disk lock. With no previously recorded error, disposal then disposes both locks (`:269–270`) while the first write is still pending.

The timer uses an `async` callback converted to `async void` (`:55–59`); timer callback disposal does not represent an application-maintained task for its entire asynchronous continuation. Independently, an explicit concurrent `FlushAsync` also demonstrates the race. If its write later fails, `MarkDirtyAndRescheduleAsync` cannot acquire the disposed tracking semaphore (`:273–304`), and `_disposed` prohibits scheduling. If the process exits after disposal returns, the pending write need not finish. Mutations also have no closing-state guard, allowing a track operation during shutdown after the final snapshot.

Recommendation: explicit `Running → Closing → Closed`, a single tracked flush worker, rejection/draining of mutations during Closing, and await all accepted snapshot writes before releasing synchronization primitives. Preserve error state until shutdown's final durability decision. Avoid an untracked async timer callback.

Proposed test: block the first writer after it has captured a dirty snapshot; call DisposeAsync; assert disposal remains incomplete until release. Repeat with the first write failing and verify retry or a surfaced final durability failure. Also race TrackInvoice against Closing. `BTCPayServer.Plugins.Tests/BareBitcoinInvoiceServiceTests.cs:486–511` releases the writer and awaits both tasks before checking disk; it never asserts disposal waits and only covers a successful write.

### F04 — Paid amountless invoices have no actual received amount

Creation explicitly accepts an amountless BOLT11 for `LightMoney.Zero` (`plugin/BareBitcoinLightningClient.cs:531–536`), and BTCPay requests this for top-ups (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningLikePaymentHandler.cs:93,107–112`). Lookup sets `AmountReceived = bolt11.MinimumAmount` when paid (`plugin/BareBitcoinLightningClient.cs:190–205`), which is zero for amountless invoices. The host uses this value for payment recording (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:613–634`), not an independently fetched settlement amount.

This is a conditional end-to-end defect: the repository does not establish whether Bare Bitcoin accepts zero-amount creation in production. If it does, a real payment amount cannot be recovered from this response mapping. It is not correct to infer that all fixed-amount payments have this problem.

Recommendation: explicitly reject unsupported amountless creation until actual settled amount can be obtained from a supported provider source. Model `PaidAmountUnknown` as distinct from zero. Test a top-up paid with a known positive amount and require that exact amount or an explicit unsupported result; never silently record zero. Existing amount parsing tests (`BTCPayServer.Plugins.Tests/BareBitcoinInvoiceLifecycleTests.cs:315–334`) exercise unpaid amounts only.

### F05 — Configuration accepted without proving it can receive

The connection handler requires key presence, account/store identity, and a valid store binding (`plugin/BareBitcoinLightningConnectionStringHandler.cs:61–110`), then only calls GetBalance (`:133–146`). Balance uses API-key-only authentication and returns zero if the selected account is absent (`plugin/Services/BareBitcoinBalanceService.cs:61–79`). It never checks the private key. `GetInfo` throws NotSupported (`plugin/BareBitcoinLightningClient.cs:603–607`), which BTCPay deliberately accepts as empty node information (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningLikePaymentHandler.cs:164–173`); UI test success is then shown (`submodules/btcpayserver/BTCPayServer/Controllers/UIStoresController.LightningLike.cs:196–209`). A valid read key plus invalid Base64 secret can therefore pass connection testing and later fail CreateHmac (`plugin/Services/BareBitcoinApiService.cs:88`). A valid Base64 wrong secret or insufficient receive permission also remains untested. On failed balance initialization the handler returns null with `error` still null (`:122,137–140`).

Recommendation: distinguish `Bound`, `ReadVerified`, `AccountVerified`, and `ReceiveCapabilityUnknown/Verified`; validate secret syntax locally and account existence explicitly. Do not create a live invoice merely to test capability without an intentional test workflow. Surface what was verified and preserve a useful failure reason. Test wrong-account/read-success, malformed secret/read-success, read-only credentials, and failed lookup error propagation.

### F06 — Backoff exists only while another retry is available

The 429 branch is inside a catch filter requiring `attempt < _maxRetries` (`plugin/BareBitcoinLightningClient.cs:121–154`). A 429 with Retry-After on the last attempt bypasses both sleeping and persistent deferral. The next listener cycle queries again after its ordinary two-second delay because all 429s are explicitly excluded from error counting (`plugin/Services/BareBitcoinListener.cs:135–138,196–227`). This also applies to `maxRetries = 0` through the public constructor. Long Retry-After values are capped at one hour (`plugin/BareBitcoinLightningClient.cs:128–136`); this is an intentional policy, not full compliance with arbitrary server delays.

Backoff is keyed by invoice and client instance. A shared account/key quota is not coordinated across invoice IDs, ListInvoices, listeners, or newly constructed clients. Whether the quota is account-wide is unknown. Concurrent success can remove a deferral set by another request (`:113`), and expiry-removal is not conditional on the dictionary value still matching the one inspected (`:91–105`).

Recommendation: record server deferral independently of retry budget; use an atomic not-before maximum, a monotonic/testable clock, and scope cooldown to the provider's documented quota. Test 500,500,500,429 with Retry-After 120 seconds; next poll must not send. Test zero retries and concurrent success/429. Tests at `BTCPayServer.Plugins.Tests/BareBitcoinLightningClientTests.cs:550,594` cover deferral and successful retries but not the exhausted-budget transition.

### F07 — Load failure can turn recovery evidence into an empty replacement

Parse and read exceptions log and leave persistence enabled (`plugin/Services/BareBitcoinInvoiceService.cs:437–444`). A later TrackInvoice marks the mostly empty registry dirty; flushing replaces the original file (`plugin/Services/FileDiskWriter.cs:29–31`). Thus a transient read failure can discard readable older data once writes work again. Invalid JSON is not preserved for investigation. Unknown schema takes a safer path, disables persistence, and preserves the file (`plugin/Services/BareBitcoinInvoiceService.cs:389–397`), but LastFlushException remains null and new creations still succeed in memory (`:309–314`). That degraded mode has no runtime re-enable transition.

Recommendation: distinguish Missing, Loaded, Corrupt, Unreadable, and UnsupportedSchema. Preserve/quarantine original bytes and require successful recovery or an explicit reset before replacing unreadable state. Expose persistence health separately from last flush error. Test a writer whose initial Read throws and later Write succeeds, ensuring old state is not replaced; test malformed JSON byte preservation. Existing `BTCPayServer.Plugins.Tests/BareBitcoinInvoiceServiceTests.cs:97` verifies empty startup, not preservation after a new mutation.

### F08 — Completed payment observations wait for the slowest request

`plugin/Services/BareBitcoinListener.cs:119–144` collects all results before `:147–194` delivers any. A single slow GET, or repeated Retry-After sleeps up to 60 seconds each, blocks already-observed paid invoices for the whole cycle. With multiple batches, delay compounds. The bounded notification channel limits queued notifications but not the results bag, which can contain every tracked invoice. The host removes invoices after its expiration deadline (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:685–705`), so sufficiently delayed delivery may arrive after the host has stopped accepting that ID. Actual lost-payment timing depends on deadlines and provider latency.

Recommendation: stream completed lookups into a single delivery worker while preserving bounded concurrency and backpressure; enforce per-operation deadlines; distinguish paused quota from failed observation. Test one immediately paid invoice alongside a gated slow request and assert delivery before releasing the slow one. Existing parallelism/partial-failure tests (`BTCPayServer.Plugins.Tests/BareBitcoinListenerTests.cs:444,529`) do not assert this property.

### F09 — Registry membership is not terminal-state enforcement

Two lookups can run concurrently for the same ID. For example, host reconciliation and a listener both call GetInvoice (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:119,497,537` versus `plugin/Services/BareBitcoinListener.cs:128`). One observes expired and untracks (`plugin/BareBitcoinLightningClient.cs:228–230`); an older unpaid response then tracks it again (`:213–217`). A paid delivery also untracks and deletes ownership (`plugin/Services/BareBitcoinInvoiceService.cs:111–115`), permitting a subsequent claim by a different scope. No terminal status, observation generation, or tombstone is stored.

Scopes correctly isolate enumeration and removal, and simultaneous conflicting ownership claims are serialized (`plugin/Services/BareBitcoinInvoiceService.cs:70–98,133–164`). They do **not** authorize an arbitrary direct lookup: FetchInvoice contacts the provider before checking any ownership, TrackInvoice can refuse a conflicting assignment but returns no rejection result, and GetInvoice still returns the provider invoice. A signed store binding authenticates store identity, not ownership of every provider ID accessible to shared credentials. This is not a demonstrated web authorization bypass: exploitability requires an exposed caller accepting a foreign identifier, and that route was not established.

Recommendation: define which source is authoritative for ownership and terminality; use compare-and-apply observation generations or per-ID serialization, and retain terminal ownership long enough for retry/replay policy. Keep direct provider lookup separate from ownership mutation. Do not prohibit legitimate late settlement without a provider rule. Tests: delayed unpaid result after terminal cleanup; untrack then foreign claim; foreign direct lookup with shared provider credentials; concurrent terminal observations.

### F10 — Queue write, consumption, and durable payment recording are distinct

The plugin writes to its channel and then removes registry membership (`plugin/Services/BareBitcoinListener.cs:164–186`). Write success proves only enqueueing. Its delivered set is memory-only, normally removed immediately after untracking; on persistent untrack errors it eventually evicts old entries (`:166–186`). A restart, listener replacement, or another client instance can replay or lose a queued notification. `_listenerLock` only serializes listener creation within one client (`plugin/BareBitcoinLightningClient.cs:571–600`).

Important mitigation: BTCPay independently loads monitored invoices at startup (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:80–119`), polls every known invoice when reconnecting (`:535–537,579–590`), and identifies recorded payments by payment hash (`:624–643`). A recording exception exits the listening operation and permits reconnection. Therefore channel loss alone is **not proof of permanently lost or double-booked payments**. Recovery still depends on host monitoring retaining the ID, provider lookup remaining possible, and appropriate ownership/credentials. The plugin's list API is a tracking view, not payment history, because delivered invoices disappear.

Recommendation: document at-least-once host reconciliation as the contract and test it end to end before introducing a separate outbox. If stronger standalone guarantees are needed, model ObservedPaid, Queued, ConsumerRecorded, and CleanupPending with durable acknowledgement/reconciliation. Tests: crash after enqueue but before host DB commit; DB failure after dequeue; restart with stale registry snapshot; two listener instances for the same scope; delivered-set eviction during persistent cleanup failure. Assert a single durable payment and eventual cleanup, not merely one WaitInvoice result.

### F11 — Unknown, quarantined, and abandoned scopes can persist indefinitely

Unknown/missing/PENDING/UNSPECIFIED provider status all become Unpaid (`plugin/BareBitcoinLightningClient.cs:179–185`), regardless of local BOLT11 expiry. A consistent future/invalid status can therefore remain tracked and polled indefinitely. Refusing to infer unpaid → expired solely from local time is reasonable because settlement can race expiry, but unknown needs its own observable state and escalation policy. Legacy IDs that the host no longer monitors never reach reclamation. A legacy 404 returns before claim/cleanup, leaving its quarantine entry forever. Old account/store scopes also have no inventory/retirement API. Unsupported schema remains memory-only until service restart after repair.

Recommendation: operator-visible counts/ages and a deliberate reconciliation/quarantine resolution mechanism; no blind TTL deletion of potentially paid invoices. Test repeated unknown status past expiry, unclaimable legacy 404, store deletion/account change, and schema repair followed by restart. Legacy and unsupported-schema tests already establish intentional quarantine/fail-safe behavior (`BTCPayServer.Plugins.Tests/BareBitcoinInvoiceServiceTests.cs:108–237`); the gap is lifecycle completion and visibility.

### F12 — Cache and availability states are inconsistent

`plugin/Services/BareBitcoinBalanceService.cs:23` uses one static semaphore for every instance/account while its cache is instance-local (`:24–25`). A slow balance request for store A blocks store B, and recreating clients defeats cache reuse. The host factory creates clients rather than caching them (`submodules/btcpayserver/BTCPayServer/Services/LightningClientFactoryService.cs:26–46`), and the plugin synchronously fetches balance on every construction. The documented five-second `_cacheTimeout` (`:26–27`) is unused; the actual fresh interval is one minute (`:51–53`). Missing accounts return uncached zero, indistinguishable in the result from a real zero balance; exceptions preserve stale cache internally but do not return it. The public service's accountId parameter is not part of the cache key, although the current client constructs a service for one fixed account, so cross-account cached results require an additional caller that reuses the service.

Recommendation: instance/account-scoped cache and synchronization, explicit unavailable/missing-account results, one TTL definition, and async validation outside synchronous client construction. Use monotonic freshness. Tests: two accounts with one blocked request; same service called for two account IDs; provider account disappearance; requests at 6 and 61 seconds; clock rollback. The inspected test files contain no dedicated balance-service test suite.

### F13 — Lookup metadata masquerades as settlement evidence

`plugin/BareBitcoinLightningClient.cs:192–196` assigns current time as PaidAt and substitutes payment hash when preimage is absent. A payment hash is not its preimage. The host validates the preimage against the hash (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:662–682`) and rejects the fallback, mitigating use as cryptographic proof, but a different API consumer could trust the field. Repeated lookups also produce different PaidAt values; an offline payment is dated when queried rather than when settled. The checked-in lookup schema has neither field, so accurate values require another supported source or an explicit “unknown”.

Recommendation: leave unavailable proof/time absent and distinguish observation time from settlement time. Test repeat paid lookup under an advancing clock and missing preimage; require stable known settlement data or null. Existing `BTCPayServer.Plugins.Tests/BareBitcoinInvoiceLifecycleTests.cs:358` deliberately asserts the false preimage fallback, so correcting this requires changing the expected contract.

## State-machine inventory and transition tables

The machines below overlap: invoice status, ownership, durability, delivery, and host recording are independent dimensions. A single enum combining every dimension would obscure rather than enforce the invariants. Tables describe transitions implemented today, including questionable ones; findings above identify which need restrictions.

### M01 — Invoice creation and external commit

State is represented by local variables, awaited tasks, returned BOLT11, and registry membership (`plugin/BareBitcoinLightningClient.cs:421–554`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Requested | Rounded duration outside 60–86400 seconds | Rejected | Throw before POST. |
| Requested | Valid duration | Awaiting signing / HTTP | Build account, amount, descriptions and lifetime. |
| Awaiting HTTP | Provider accepts; response lost or timeout | Outcome unknown | Provider may have created a payable invoice; no local identity. |
| Awaiting HTTP | Response present | Validating | Parse JSON/BOLT11, verify signature/hash/network. |
| Validating | Amount mismatch, already expired, deadline differs by >30s | Rejected locally, possibly created remotely | Throw; no tracking or remote cancellation. |
| Validating | Valid | Tracking pending | Use complete BOLT11 as identifier. |
| Tracking pending | Track succeeds or throws IOException | Returned unpaid | Caller gets invoice; IO failure is logged. |
| Tracking pending | Cancellation or other exception | Failed locally, created remotely | No successful return; F01. |
| Returned unpaid | Caller/host persists prompt | Host-known | Host can reconcile independently of plugin registry. |

No automatic POST retries are present, avoiding an immediate duplicate-creation loop. There is no idempotency key, cancellation implementation, or reconciliation state for unknown outcome. The 30-second tolerance is explicit; whether it is acceptable past the host monitoring deadline is a policy assumption, not established here. Validate response delay at 29/31 seconds and failures after provider commit. A durable creation saga is justified only if provider correlation support makes it recoverable; otherwise expose limitations explicitly.

### M02 — Provider invoice observation and terminal mapping

Implemented by `plugin/BareBitcoinLightningClient.cs:158–241`; host recording consumes the result.

| Observation / previous state | Trigger / guard | Mapped state | Side effects |
| --- | --- | --- | --- |
| Any / no local observation | 404 | Missing | Return null; listener removes ID. |
| Any | HTTP success without invoice | Missing | Same destructive transition; F02. |
| Any | Invalid JSON/BOLT11/signature/hash binding | Observation failed | Throw; no successful tracking change. |
| Any | UNPAID | Unpaid | Track/claim ownership. |
| Any | PENDING, UNSPECIFIED, missing or unknown status | Unpaid | Same track path; intermediate/unknown state is lost. |
| Any | PAID | Paid | Synthesize metadata, claim legacy if applicable, retain existing membership until delivery. |
| Any | EXPIRED or CANCELED | Expired | Claim legacy then untrack. |
| Paid/expired previously observed | Later unpaid observation | Unpaid again | Allowed, because no status history exists; F09. |

A paid result for a nonlegacy, already untracked ID is returned but not re-added. ListInvoices only visits tracked IDs, filters PendingOnly after fetching, propagates authentication errors, and skips many per-invoice parse/HTTP errors (`:298–362`). A null lookup is **not** untracked by ListInvoices itself; only the listener/host does that. Thus transition policy differs by entrypoint. Tests should exercise the same response sequence through direct lookup, list, listener, and host reconciliation, not only each in isolation.

### M03 — Ownership registry and legacy migration

State: `_trackedInvoiceRegistry`, `_invoiceOwners`, `_unassignedLegacyInvoices` (`plugin/Services/BareBitcoinInvoiceService.cs:24–26`). Scope hashes endpoint/network/account/store, intentionally excluding credentials (`plugin/Services/BareBitcoinInvoiceScope.cs:14–35`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Absent | Track(scope,id), no foreign owner | Owned(scope) | Add both indexes; dirty. |
| Quarantined | Track or TryClaimLegacy; claim serialized | Owned(scope) | Remove quarantine; add indexes; dirty. |
| Owned(A) | Track(B), A != B | Owned(A) | Warn, no mutation; no failure result. |
| Owned(A) | Track(A) | Owned(A) | Deduplicate, normally no dirty change. |
| Owned(A) | Untrack(B), A != B | Owned(A) | No-op. |
| Owned(A) | Untrack(A) | Absent | Remove owner and empty scope; dirty. |
| Quarantined | Untrack(any) | Quarantined | No-op. |
| Legacy array / schema 2 | Startup | Quarantined | Retain IDs; schedule schema-3 rewrite. |
| Schema 3 duplicate owners | Startup, sorted scope order | First lexicographic owner | Drop later ownership and rewrite. |
| Owned old connection | Credential rotation, same scope | Owned same scope | Stable scope permits reuse. |
| Owned old connection | Account/store/network change | Owned old scope | No automatic transfer or retirement. |

Mutations are atomically locked within one process. There is no database ownership constraint or cross-process lock. Lexicographic conflict repair is deterministic but not evidence that the selected store is correct; corrupted duplicate ownership warrants quarantine/operator reconciliation. Legacy claim assumes the caller is the owning store; the registry itself proves only first-claim-wins. Tests should verify account changes and invalid persisted duplicates never silently assign a payment to an arbitrary store.

### M04 — Persistence load, snapshot, write, and shutdown

State: `_dirty`, snapshot/written versions, disabled reason, failure count, last exception, disposed flag, two semaphores and timer (`plugin/Services/BareBitcoinInvoiceService.cs:27–40`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Loading | No file | Clean empty | No initial write. |
| Loading | Valid schema 3 | Clean loaded / dirty canonicalized | Restore ownership; optionally repair duplicates. |
| Loading | Legacy array/schema 2 | Dirty migrated | Schedule rewrite. |
| Loading | Unsupported schema | Persistence disabled | Preserve file, accept memory-only mutations. |
| Loading | JSON/read error | Writable empty or partially loaded | Log; F07. |
| Clean | First mutation | Dirty scheduled | One-shot timer in one second. |
| Dirty | Flush obtains tracking lock | Snapshot in flight | Serialize, clear dirty, increment snapshot version. |
| Snapshot in flight | Version <= written version | Superseded | Skip stale disk write. |
| Snapshot in flight | Successful serialized write | Clean or newer-dirty | Advance written version; reset error/backoff. |
| Serializing/writing | Failure | Dirty retry scheduled | Record error, exponential 1–30s delay unless disposing. |
| Running | Dispose | Closing (implicit) | Set flag, dispose timer, final flush and one retry. |
| Closing | Flush returned | Disposed | Dispose locks, even if older write is pending; F03. |

Monotonic snapshot versioning correctly prevents an older completed snapshot overwriting a newer one. Mutations during I/O can schedule a subsequent snapshot. Atomic rename protects readers from partial JSON but is not an explicit fsync durability guarantee (`plugin/Services/FileDiskWriter.cs:23–31`). A leftover `.tmp` is ignored on load; two processes sharing the path also share the temp name, without mutual exclusion. Multi-process deployment support is not asserted; if required, this design needs database or file-lock coordination. Test crash before/after rename and concurrent instances only for supported deployment modes. Disk writes have no cancellation/deadline, so shutdown can also hang if its own final write never finishes.

### M05 — Polling worker and adaptive delay

State: polling task, cancellation source, error counter, delay, results bag (`plugin/Services/BareBitcoinListener.cs:94–252`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Constructed | StartPolling called | Fetching registry | Starts immediately, not via hosted-service StartAsync. |
| Fetching registry | Snapshot acquired | Polling IDs | At most configured 1–100 concurrent lookups. |
| Polling IDs | Per-ID success | Accumulating | Store result until all requests finish. |
| Polling IDs | 429 | Deferred/skipped | Not counted as failure. |
| Polling IDs | Other error | Failed observation | Count and log; keep tracking. |
| All requests completed | Results available | Processing results | Sequential delivery/cleanup. |
| Processing | >=50% failures | Backoff delay | 4,8,16,30 seconds, capped. |
| Processing | Lower error rate or empty snapshot | Normal delay | Reset to two seconds. |
| Any outer operation | General exception | Error delay | Five seconds then retry. |
| Work/delay | Cancellation | Exited | No channel completion in worker finally. |
| Processing | ChannelClosedException | Exited | No disposed flag set by worker. |

A stopped/faulted task is not represented in IsDisposed. Listen can reuse a non-disposed but stopped listener, and WaitInvoice can wait on an uncompleted empty channel. Normal production cancellation is initiated by Dispose; spontaneous outer cancellation/worker faults are a conditional liveness risk. The worker should publish terminal health and complete its channel in finally; test independent worker failure and assert callers receive an error/replacement rather than waiting forever. F08 covers batch starvation. No independent background job scheduler, webhook receiver, or distributed lease exists in this plugin.

### M06 — Notification delivery and listener lifetime

State: bounded channel, delivered dictionary/FIFO, `_isDisposed`, `_currentListener` (`plugin/Services/BareBitcoinListener.cs:25–59,146–193,278–329`; `plugin/BareBitcoinLightningClient.cs:571–600`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Paid observed, not delivered | Channel has space | Enqueued | Write invoice; remember delivered ID. |
| Paid observed | Channel full | Waiting for space | Whole result-processing loop pauses; cancellation supported. |
| Enqueued / remembered | Untrack success | Untracked, dedup entry forgotten | No consumer acknowledgement required. |
| Enqueued / remembered | Untrack IOException | Cleanup pending | Keep dedup entry and retry next cycle. |
| Cleanup pending | Capacity reached | Possibly forgotten | Evict oldest ~10%; future duplicate delivery possible. |
| Enqueued | WaitInvoice | Consumed | Remove from channel, return to host; no DB acknowledgement. |
| Listener absent/disposed | Listen under client lock | New listener | Spawn new polling worker. |
| Listener not disposed | Listen | Same listener | Assumes one consumer; SingleReader is configured. |
| Active | Dispose | Canceling → disposed | Wait at most 5s, complete writer, dispose token source. |

Dispose is not concurrency-idempotent: the plain flag is checked before cancellation and only set at the end; simultaneous calls can both enter, and `_cts.Cancel()` is outside the try block. Multiple concurrent WaitInvoice callers violate the intended single-reader usage without enforcement. The inspected host uses one reader per session, reducing likelihood. Prefer an interlocked lifetime transition and async join. Test simultaneous Dispose calls, Listen during disposal, multiple client instances, full channel at shutdown, and unexpected polling exit. Internal before/after-write callbacks are test seams; their exceptions can interrupt transitions but are not wired in production.

### M07 — Retry and provider cooldown

State: attempt counter and client-local `ConcurrentDictionary<invoiceId,notBefore>` (`plugin/BareBitcoinLightningClient.cs:82–155,279–290,309–314`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Ready | Future notBefore exists | Deferred | Throw synthetic 429 without HTTP. |
| Deferred | Time reached | Ready | Remove entry. |
| Ready | Fetch success/null | Complete | Clear backoff. |
| Requesting | 401/403 | Failed authentication | No inner retry. |
| Requesting | Network/5xx/429 and attempts remain | Retry wait | Exponential 200ms base, capped 30s; no jitter. |
| Requesting | Positive Retry-After <=60s, attempts remain | Retry wait | Honor delay within this operation. |
| Requesting | Retry-After >60s, attempts remain | Deferred | Store at most one hour; throw 429. |
| Requesting | Retry budget exhausted | Failed | Propagate; final Retry-After not stored. |
| Retry wait | Cancellation | Canceled | No further retry. |
| Listed registry snapshot | Backoff ID not tracked | Forgotten | ListInvoices prunes; listener does not perform this sweep. |

Transport timeouts throw cancellation-family exceptions rather than matching the HttpRequestException retry filter; listener catches them as per-ID errors unless shutdown was requested, whereas ListInvoices can fail as a whole. No account-wide circuit breaker or credential-repair state exists. Repeated 401/403 becomes capped 30-second listener polling, not a disabled connection. F01/F06 describe enforcement gaps. Test exact retry count, timeout behavior across entrypoints, concurrent cooldown mutation, and clock changes.

### M08 — HMAC request ordering and auth-state eviction

State is static per public key: nonce, refcount, request semaphore, LastUsed, Removed; pruning has its own interlocked flag (`plugin/Services/BareBitcoinApiService.cs:25–28,52–56,122–242`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| No auth state | Signed request | Retained | Create/get state; increment references under lock. |
| Retained | Semaphore busy | Queued | Wait without cancellation. |
| Retained/queued | Semaphore acquired | Signing/sending | Nonce=max(last+1, current milliseconds); hold until response. |
| Signing/sending | Success or exception | Idle/other waiters | Finally release semaphore, decrement reference, update last-used. |
| Idle | >1024 states; idle >1h; refcount zero; semaphore free | Removed | Mark under lock; remove dictionary entry; dispose semaphore. |
| Removed observed by retainer | Race with prune | Retry retention | No use of disposed state. |
| Any | Simple-auth request | Independent request | Bypass nonce/semaphore entirely. |
| Process restart / evicted key reused | Next signed request | Fresh nonce history | Previous high-water mark lost. |

In-process signed-request ordering is deliberately sound, and tests exercise same-key serialization (`BTCPayServer.Plugins.Tests/BareBitcoinApiServiceTests.cs:84,128`). The 1024 threshold is not a hard memory bound; recently used states remain. Monotonicity across processes, restarts, or clock rollback is not guaranteed. If provider requires a globally increasing nonce per key, sharing a key across processes or rolling the clock back can cause rejection until time catches up; the local spec does not establish that exact requirement. Document key-sharing assumptions; persist/synchronize nonce only if required. Test idle cleanup while requests wait, exception release, clock rollback/restart, and separate keys proceeding concurrently. The queue's cancellation gap is F01.

### M09 — Balance freshness and availability

State: cached object, last successful check, global semaphore (`plugin/Services/BareBitcoinBalanceService.cs:21–109`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Empty/stale/fresh | GetBalance | Waiting for global lock | Caller cancellation applies only while acquiring lock. |
| Fresh | Age <1 minute | Fresh | Return cached object, no request. |
| Empty/stale | Lock acquired | Fetching | API-key-only account-list request. |
| Fetching | Matching account | Fresh | Convert availableBtc; absent field defaults to zero; timestamp now. |
| Fetching | No accounts / account absent | Unavailable reported as zero | Return new zero; do not update cache or time. |
| Fetching | HTTP/parse failure | Failed with old cache retained | Throw and release lock. |
| Fresh | Time elapsed | Stale | Implicit evaluation on next call. |

There is no stale-while-error result, explicit invalid-account state, invalidation on received payment, or cache health indication. F12 covers enforcement and tests.

### M10 — Store binding, setup UI, and configuration validation

Frontend state lives in DOM form text, template data attributes, and Bootstrap accordion classes (`plugin/Views/Shared/BareBitcoin/LNPaymentMethodSetupTab.cshtml:3–46,49–75`). Backend state lives in persisted connection strings and the server data-protection key ring.

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Non-BTC view | Razor CryptoCode guard | No plugin UI | Return. |
| BTC document loading | DOMContentLoaded | Setup fragment installed | Clone accordion; register submit listener. |
| Unbound/stale/foreign Bare Bitcoin string | Form submit, type matches | Bound to current rendered store | Remove all old binding fields; append signed store ID/token. |
| Other provider string | Submit | Unchanged | No injection. |
| Missing/mismatched token | Handler Create | Rejected | No client. |
| Valid token + HTTP store mismatch | Handler Create | Rejected | Prevent copied binding during authenticated setup. |
| Valid token + matching/no HTTP store context | Handler Create | Constructed/read-checking | Background contexts rely on persisted token. |
| Read check succeeds | Handler returns client | Read-accepted | Receive capability still unknown; F05. |
| Save validated config | Host save | Persisted connection | Host updates store DB. |
| Key ring lost/revoked or legacy binding absent | Future reconstruction | Rejected until reconfigured | Fail closed; web setup must mint a new token. |

Store comparison and token verification are correctly centralized in the handler (`plugin/BareBitcoinLightningConnectionStringHandler.cs:85–110`; `plugin/Services/BareBitcoinStoreBinding.cs:23–49`; `plugin/Services/BareBitcoinStoreContext.cs:20–24`). No token expiration/revocation field exists beyond data-protection behavior. Binding is to store ID, not account or credentials; account changes preserve the proof of store identity but change invoice scope. Server checks remain essential because direct form submission can bypass JS. DOM missing `CustomNodeSupport` causes an exception before binding registration (`:16`); direct `form.submit()` also bypasses submit events. These are compatibility risks, not observed host failures. Test ordinary click, Enter/requestSubmit, no-JS/direct POST, duplicate binding fields, current-store mismatch, and restored DB without the original key ring. Accordion open/close carries no payment authority and needs no domain enum.

### M11 — Host monitoring and payment recording boundary

Source: `submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:70–125,279–286,489–695`.

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Persisted monitored invoice | Startup/event queue | Host listening map | Resolve current store config, add invoice, start direct poll. |
| Known ID | Poll returns null/expired | Removed | Stop monitoring that ID. |
| Known ID | Poll returns unpaid | RetryLater | Retain map entry. |
| Listener absent/completed | Periodic CheckConnection and nonempty map | Listening | Construct client/session and poll all known IDs. |
| Known ID | Paid with unavailable amount | RetryLater | Warn, retain map entry. |
| Known ID | Paid with amount | Recording | Build settled PaymentData keyed by payment hash. |
| Recording | Payment already recorded / invoice missing | AlreadyRecorded | Remove map entry. |
| Recording | PaymentService accepts | RecordedNow | Store payment, optionally update proof, publish event, remove map entry. |
| Listening/recording | Exception | Listening operation ends/faults | Disposal and later connection check permit retry. |
| Known ID | Monitoring expiration | Removed | Stop session if map empty. |

Plugin delivery cleanup is earlier than host Recording. Startup/reconnection polls mitigate F10; periodic connection checking is **not** a periodic full payment poll of every healthy session (`:279–286` versus `:535–537`). Recovery is bounded by host monitoring policy. Startup uses the current store configuration (`:101–105`), so changing provider account/credentials can make old invoices unrecoverable; the persisted plugin scope alone does not retain old credentials. Propose integration tests for account rotation, missing binding after upgrade, expired monitoring during slow delivery, and DB failure between payment insertion and event publication. Independent host invoice/order state machines outside this boundary were not audited.

### M12 — Logging suppression

State: `(level,template) → IsFirstCall, WindowStart, SuppressedCount`, per-state lock, eviction flag (`plugin/Services/LogThrottle.cs:23–31,63–145`).

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Absent | Enabled log call | New state | Allocate by level/template. |
| New/expired window | Log call | Active window | Emit old suppressed count, log new event, reset count/time. |
| Active window | Repeated template | Active suppressed | Increment count; values/exception differences are ignored. |
| Any | Logger level disabled | Unchanged | No tracking. |
| Expired | Above capacity threshold, eviction wins | Removed | Emit summary once for removed entry. |
| Active suppressed | No subsequent call | Retained | No timer/Dispose flush of summary. |

Current callers use static templates, so the documented non-hard capacity is not a practical growing-key defect here. Suppression is per template, not invoice, so different invoice failures collapse into one summary. A logging caller can retain an entry reference before eviction, then update that detached state after removal while another caller creates a new state, weakening strict suppression under concurrency. This is low-impact diagnostic behavior, not payment corruption. Recommend a removed/generation guard only if strict bounds/suppression are required; test retained reference versus concurrent eviction and quiet final suppression windows. Existing tests cover basic concurrent logging/eviction, not a proven production incident.

### M13 — Interactive setup helper

Source: `barebitcoin-lightning-connection-setup.js:8–18,26–138`.

| From | Trigger / guard | To | Side effects |
| --- | --- | --- | --- |
| Started | Prompt callbacks | Public key → secret captured | Close each readline interface after answer. |
| Keys captured | Empty key | Failed exit | Error and exit 1. |
| Keys captured | Both nonempty | HTTPS pending | API-key-only account listing. |
| HTTPS pending | Request error | Failed exit | Reject, top-level catch. |
| HTTPS pending | Response end | Validating response | Buffer full body then check 200/JSON/accounts. |
| Response valid | One account | Selected | Auto-select ID. |
| Response valid | Multiple accounts | Await selection | parseInt plus range checks. |
| Selected | Valid index | Configuration printed | Secret included; string remains store-unbound by design. |

No explicit timeout, response-stream error/aborted handler, or readline close/EOF rejection exists. A stalled response or terminal EOF can leave an unresolved operation; process termination may be the only recovery. `parseInt("1junk",10)` accepts selection 1, a permissive input policy rather than a major state defect. Recommendation: settle promises exactly once on end/error/abort/close, define deadline and cancel behavior, and validate full numeric input if desired. Test a socket that sends headers then stalls, abort after headers, EOF during each prompt, malformed accounts, and the required web-binding next step. The secret is not validated by this script (F05).

### M14 — Release/configuration helpers and repository automation

These are operational state machines, not payment runtime logic. Sources: `pluginpacker.sh:77–226`, `ConfigBuilder/Program.cs:4–58`, `.github/workflows/plugin-builder-compat.yml`, `.github/workflows/codex-pull-request-code-review.yml`, `.github/workflows/gemini-pull-request-code-review.yml`.

| Machine / from | Trigger / guard | To | Side effects / recovery |
| --- | --- | --- | --- |
| Release / arguments | SemVer, master, clean checkout, absent local tag, source layout valid | Preflight passed | No mutation yet (`pluginpacker.sh:114–151`). |
| Release / preflight | Version rewrite | Modified | EXIT trap restores project until committed (`:158–175`). |
| Release / modified | Tests and optional package pass | Ready to commit | Package artifacts written; failure restores project, not every artifact (`:175–201`). |
| Release / ready | git commit succeeds | Committed | Disable project rollback (`:203–205`). |
| Release / committed | git tag succeeds | Tagged locally | No rollback for failure here (`:206`). |
| Release / tagged | Push master, then tag | Published refs or partial publication | Separate operations, no atomicity (`:208–210`). |
| Release / tagged | --no-push | Local candidate | Instructions printed; publication deliberately deferred. |
| ConfigBuilder / scanning | Candidate DLL exists | Selected list | Missing plugin binaries skipped; per-plugin exceptions logged (`ConfigBuilder/Program.cs:6–36`). |
| ConfigBuilder / list ready | Host project found | Writing config → done | Whole appsettings.dev.json replacement, no merge/atomic temp file (`:38–58`). |
| CI build / triggered | Checkout/setup/build succeed | Testing → success/failure | Fresh runner; rerun restarts job, no release publication. |
| Review / PR event | Same-repo non-Dependabot guard | Running | Per-PR concurrency cancels older run. |
| Review / running | Model output exists | Posting | Find marker comment then update or create. |
| Review / newer push | Cancellation | Canceled / possible external side effect already committed | No head-SHA guard around comment write. |
| Gemini / failure | failure() | Failure notice | Old failure notice is not cleared after later success. |

F14 details: a push failure after the release commit/tag leaves a valid partial release; rerunning the script then fails its local-tag guard (`pluginpacker.sh:133–136`). A master push can succeed while tag push fails. Recovery must inspect local/remote refs and resume, not blindly delete a tag or recommit. Recommend explicit resumable phases and atomic ref push if supported. Test each command failure with stubbed git/dotnet in a disposable repo; assert restore before commit, retained evidence after commit, and clear recovery instructions. No release was executed for this audit.

ConfigBuilder has no preservation/rollback of pre-existing settings and can write a partial file on interruption. Whether replacing all settings is intended is not specified; do not assume unrelated settings should be discarded. Test pre-existing settings and interrupted write; prefer atomic replacement and explicit merge/replace policy.

Review-comment lookup is not a durable idempotency key: it searches only the returned comment page and matches text without author/head identity (`.github/workflows/codex-pull-request-code-review.yml:102–127`; `.github/workflows/gemini-pull-request-code-review.yml:135–161`). An API success followed by an interrupted response/retry or an old in-flight posting request can produce duplicate/stale feedback. Recommend bot-owned marker plus head SHA, pagination, and a current-head guard. Test canceled older review finishing after a newer one and a matching comment outside the first page. These are workflow correctness risks, not runtime plugin vulnerabilities.

### M15 — Unsupported and dormant capability paths

`plugin/BareBitcoinLightningClient.cs:365–370,407–418,603–663` explicitly rejects payment sending/history, cancellation, deposit address, channel and peer operations. These have only `Called → Unsupported`, no pending/success runtime states. The receive-only restriction is documented; lack of sending/refunds is not a defect. CancelInvoice likewise does not cancel a provider invoice; BTCPay catches unsupported cancellation when replacing partial-payment prompts (`submodules/btcpayserver/BTCPayServer/Payments/Lightning/LightningListener.cs:322–329,347–354`). Old invoices may remain payable until provider expiry; whether this needs stronger handling depends on the product's replacement-payment policy.

| From | Trigger | To | Side effects |
| --- | --- | --- | --- |
| Unsupported capability | Pay/ListPayments/GetPayment/etc. | Exception | No external request or mutation. |
| Optional node-info probe | GetInfo | NotSupported | Host treats as no public address. |
| Optional cancellation | CancelInvoice | NotImplemented | Host may continue replacing the prompt. |
| Dormant ToInvoice helper | PAID/EXPIRED/PENDING/unknown payload | Mapped invoice | Different schema, no signature validation/tracking, no AmountReceived. |
| Dormant ToLightningPayment helper | RECEIVE / missing initiation fields | Null | Filter. |
| Dormant ToLightningPayment helper | FAILURE/PENDING/SUCCESS/other | Failed/Pending/Complete/Unknown | Pure projection, not actual outgoing-payment implementation. |

The conversion helpers (`plugin/BareBitcoinLightningClient.cs:244–269,373–404`) have no repository call sites beyond their declarations. Their alternate schemas must not be mistaken for active integration paths. If revived, route them through shared validated mappings and test proof/amount/status consistency; otherwise remove them in a separate cleanup. `plugin/BareBitcoinSwaggerProvider.cs:18–24` is a one-shot read/parse operation with no persisted state or retry/cache; its failures propagate. Interfaces, csproj files, static SVG, and view imports introduce no additional domain state machines.

## Cross-cutting enforcement and test priorities

### Invariants already enforced

- Store identity is server-authenticated; web context mismatches fail closed.
- Registry enumeration/removal is scoped and mutations use a single in-process lock.
- Legacy ownership conflicts cannot replace an existing live owner.
- Created/queried BOLT11 signatures and recognizable identifier payment hashes are checked before tracking changes.
- Older persistence snapshots cannot overwrite a newer written version within one service instance.
- Signed requests are serialized per public key while active references prevent eviction of busy auth state.
- Channel backpressure waits instead of dropping notifications, and per-ID poll errors generally preserve other work.
- Unknown future registry schema is preserved instead of overwritten.

### Invariants absent or inconsistent

- Cancellation/deadlines end at the HTTP/signing boundary rather than encompassing the full operation.
- “Missing,” “invalid response,” “unknown status,” and “expired” are not distinct observations throughout the call chain.
- Terminality and ownership lifetime are not persisted; observation ordering is not enforced.
- Enqueued, consumed, and durably recorded are conflated by plugin cleanup.
- Dirty=false means “snapshot captured,” not “durable”; disposed=true means neither joined nor durable.
- Connection accepted means read lookup completed, not account/receive verified.
- Retry-After enforcement depends on remaining attempts and which entrypoint is polling.
- Balance freshness, unavailable state, and account identity are not represented consistently.

### Recommended implementation sequence (future work only)

1. Add deterministic integration tests for F01–F03, then fix end-to-end cancellation, invalid-response handling, and flush/lifetime joining.
2. Resolve amountless support with the provider contract; either supply actual settled amount or reject the capability explicitly. Correct connection validation and retry exhaustion handling.
3. Test the host/plugin crash and payment-recording boundary before deciding whether a durable outbox is warranted. Preserve the host's idempotent payment identity.
4. Add observation/ownership version checks and persistence health/recovery states. Keep unknown provider outcomes visible instead of inventing terminal rules.
5. Address cache isolation, streaming delivery, diagnostics, and operational helper resumability.

Use controlled TaskCompletionSource barriers, injectable clocks, and fake HTTP/disk/provider outcomes to test ordering. Real sleeps are unnecessary for most scenarios. The most valuable tests assert externally meaningful invariants—no POST after pre-cancellation, no cleanup on invalid data, disposal waits for durability, exact paid amount, and one durable host payment—rather than mirroring private flags. Extend the existing suites in a later change; none were modified or run during this audit.

## Implementation follow-up — 2026-11-07

The original findings and line references above describe the audited baseline, not the implementation after remediation. The follow-up changes are on `codex/state-machine-audit`, rebased onto master `2b93ac5`. Upstream introduced provider settlement fields, amountless support, verified preimages, and a shared listener hub after the original audit; those implementations are preserved. No provider account was contacted, no release or review comment was published, and the vendored BTCPay source was not changed.

| Finding | Resolution | Remaining boundary |
| --- | --- | --- |
| F01 | Cancellation flows through signing queues, HTTP send and body reads; pre-canceled creation sends nothing. Once a successful create response has been validated, tracking does not discard its identity merely because the caller canceled. Interrupted POSTs explicitly report unknown outcome and a trace. Listener cancellation resources outlive the worker. | Cancellation cannot roll back a remotely committed POST. Reconciliation is manual because no provider idempotency/recovery contract was established. |
| F02 | Missing/empty invoice in a successful response throws an observation error instead of returning null; tracked IDs survive and can later settle. | 404 retains its existing authoritative-not-found interpretation. Eventual-consistency semantics remain unspecified. |
| F03 | The write lock now covers snapshot capture through write completion. Concurrent DisposeAsync calls share one task, accepted writes are joined, and closing rejects mutations. An in-flight failure is included in the final retry decision. | Shutdown cannot force an indefinitely hung storage device to complete. Persistent failure is exposed and logged; it is not reported as durable success. |
| F04 | Preserves upstream settlement validation and amountless support: received amounts are taken from the documented provider field; missing/invalid settlement stays monitored without zero credit or aborting the host sweep. | A paid invoice cannot be credited until a usable amount is available. |
| F05 | Secret syntax and public-key syntax are validated locally. BTCPay's IExtendedLightningClient.Validate performs account/read checks asynchronously; GetInfo also validates read access before its unsupported-node-info result. Client construction no longer performs network I/O. Missing account/balance is an error. Setup copy states the limits of verification. | A syntactically valid secret or Receive permission cannot be proven with the documented read endpoint. No live test invoice is created silently. The hook was confirmed in both v2.3.7 and v2.4.4 source. |
| F06 | Positive Retry-After is retained even on the final attempt, including zero retry budget. Updates keep the later deadline; removal compares the observed value, successful concurrent responses do not clear a newer cooldown, and each retry rechecks cooldown. Listener 429s contribute to adaptive backoff. | Cooldown remains per invoice/client; no undocumented account-wide quota is assumed. |
| F07 | Corrupt/read-failed/conflicting-owner registries disable persistence and preserve the original. FileDiskWriter no longer treats File.Exists's masked access errors as a missing file. Writes use unique same-directory temporary files, flush file data, then rename. | Filesystem/platform rename metadata durability and distributed writers are not promised. One process per registry is documented. |
| F08 | Bounded concurrent producers stream completed observations to the single delivery worker; a slow request no longer blocks already completed paid results. Producer cancellation/join occurs when consumption stops. | Bounded-channel backpressure still intentionally limits work if the host stops consuming. |
| F09 | Schema 4 retains terminal invoice ownership in retiredOwners. Stale unpaid tracking cannot reactivate retired IDs, including after restart, and known foreign direct lookups are rejected before HTTP and before return. Conflicting persisted owners are preserved for explicit recovery instead of choosing lexicographically. | Unassigned legacy/unknown-ID ownership still relies on the host's authoritative store reconciliation. No proof of provider-ID ownership can be invented from a store binding. Terminal ownership is retained without an unsafe automatic expiry. |
| F10 | Preserves upstream scoped fan-out and bounded recent-payment replay before cleanup. Concurrent Dispose is idempotent, worker exit completes the channel, and only one WaitInvoice consumer is accepted. Listen recreates a completed worker. | Hub enqueue/replay is not a durable host DB acknowledgement. Host startup/reconnection reconciliation and payment-hash idempotency remain required; exactly-once delivery is not claimed. The earlier consume-before-cleanup approach was superseded to preserve the newer multi-listener protocol. |
| F11 | Unknown/unspecified status fails visibly rather than becoming unpaid. GetHealth exposes active/quarantined/retired counts and persistence errors. README documents restoration, ownership reconciliation, key recovery, and intentionally retained states. | Unknown outcomes, legacy quarantine, and abandoned scopes are not blindly expired or deleted. Their safe resolution requires authoritative evidence, so manual recovery remains intentional. |
| F12 | Cache locking is per service instead of global, cache entries are account-keyed, freshness uses one five-second monotonic lifetime, and missing account/balance is distinct from zero. Async validation removes the construction-time balance request. | No cross-process/shared account cache is introduced. |
| F13 | Preserves upstream validation of provider settlement timestamps and cryptographic preimages; missing/invalid evidence is not fabricated. Incompatible unused conversion helpers were removed. | The host can use observation time if no validated settlement time is available. |
| F14 | Setup helper handles EOF/abort/response errors and request deadline. ConfigBuilder merges existing JSON using atomic replacement and rejects malformed input. Release publication uses atomic pushes with guarded --resume. Review comments paginate, require bot authorship, reject an already-stale head, and identify their commit; matching failure notices are cleaned after success. | GitHub does not offer a transaction between head check and comment creation; commit-specific comments prevent a late old run overwriting a newer head's review. Atomic push requires server support and does not fall back to partial publication. |

Additional changes: log-throttle callers recheck dictionary identity under the state lock after concurrent eviction; UI binding setup no longer fails merely because the optional accordion container is absent. Receive-only unsupported operations remain unsupported. There is no attempt to implement refunds or provider cancellation without a documented API.

### Regression coverage and verification

`BTCPayServer.Plugins.Tests/StateMachineRegressionTests.cs` adds controlled transport/writer barriers and outcome tests for queued/in-flight/pre-cancellation, unknown POST outcomes, invalid responses, uncreditable amountless settlement, retry-budget exhaustion, retired ownership across restart, direct lookup isolation, failed-load preservation, shutdown joins and retries, streaming delivery, account validation, and account-keyed/monotonic balance caching. Existing tests were updated only where the intentional contract changed (unknown status, schema 4, corrupt ownership handling, and rate-limit backoff). The existing shutdown test now asserts that disposal cannot complete before the blocked write is released.

`tests/setup-helper.test.cjs` verifies stalled, aborted, and successful responses. `tests/review-workflows.test.cjs` executes the Gemini workflow script (the Codex review workflow was removed upstream) with fake GitHub methods to verify stale-head suppression and bot-owned paginated comment matching. `tests/tooling-regression.py` runs ConfigBuilder against disposable settings and exercises release resumption with a mock Git executable. These checks are included in the compatibility workflow.

Validation results are recorded after the final run below. Compiler/package warnings inherited from the host dependency graph remain outside these state-machine fixes; no dependency upgrades were bundled into this change.

Pre-rebase validation (superseded by the final PR validation below): `dotnet build BTCPayServer.Plugins.Tests/BTCPayServer.Plugins.Tests.csproj -m:1` succeeded; `dotnet test --project BTCPayServer.Plugins.Tests/BTCPayServer.Plugins.Tests.csproj --no-build --minimum-expected-tests 1` passed **226/226** tests. `node --test tests/*.test.cjs` passed **7/7** tests. `python3 tests/tooling-regression.py` passed **2/2** integration checks after building ConfigBuilder. Workflow YAML parsed successfully with Ruby's YAML parser; `bash -n pluginpacker.sh`, Node syntax checking, and `git diff --check` passed. The primary checkout and BTCPay submodule have no modifications. No live-provider or host-database integration test was run; host acknowledgement/reconciliation behavior is a documented integration boundary, not a newly tested exactly-once guarantee.

Final PR validation after rebasing onto master `2b93ac5` and its BTCPay submodule `5d0745c`: .NET build succeeded and **266/266 tests passed**. Node checks passed **5/5** (the removed Codex workflow is no longer tested); tooling integration checks passed **2/2**. Workflow YAML parsing, shell syntax, and whitespace checks passed. Upstream amountless settlement, payment-time/preimage validation, scoped listener fan-out, and the submodule version are preserved. No live-provider/host-database test or deployment was performed.
