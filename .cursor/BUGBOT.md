# Bare Bitcoin Plugin Review Rules

Review only changes introduced by the pull request and their immediate context. Do not report pre-existing issues unless the change makes them newly reachable or materially worse.

Prioritize correctness and security issues that can cause lost or misattributed payments, cross-store data access, credential disclosure, or state corruption.

Repository invariants:

- Tracked invoice reads, writes, polling, cleanup, and listing must remain isolated by the owning Bare Bitcoin account scope.
- A listener must never enumerate, query, deliver, or untrack invoice IDs belonging to another scope.
- Payment notifications must be delivered reliably before tracking state is removed; persistence failures must not silently lose retry state.
- Private API keys, authentication material, and raw account identifiers must not be logged or added to persisted tracking keys.
- Invoice persistence must remain restart-safe, concurrency-safe, bounded, and compatible with the documented legacy-state migration.
- Cancellation must propagate without being converted into retryable failures, and network/authentication failures must not corrupt tracked state.
- The plugin is receive-only. Do not suggest or introduce outgoing payment or refund behavior without an explicit product decision.

Require focused regression tests for changes to invoice ownership, listener delivery, persistence, concurrency, retry/backoff, authentication, or connection-string handling. Treat missing cross-account isolation tests as a significant coverage gap.
