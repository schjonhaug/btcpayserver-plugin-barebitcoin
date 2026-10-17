# Bare Bitcoin Plugin for BTCPay Server

A receive-only Lightning plugin that creates and tracks BOLT11 invoices through the Bare Bitcoin API (`https://api.bb.no`, documented at https://barebitcoin.no/developers/llms-full.txt).

## Build and test

```sh
git submodule update --init --recursive
dotnet test BTCPayServer.Plugins.Tests
```

## Invariants

These matter most, both when changing code and when reviewing it. Prioritize correctness and security issues that can cause lost or misattributed payments, cross-store data access, credential disclosure, or state corruption.

- Tracked invoice reads, writes, polling, cleanup, and listing must remain isolated by the owning store connection scope: the Bare Bitcoin account and the BTCPay store together.
- A listener must never enumerate, query, deliver, or untrack invoice IDs belonging to another scope.
- Payment notifications must be delivered reliably before tracking state is removed; persistence failures must not silently lose retry state.
- `GetInvoice` returning `null` makes both the plugin listener and BTCPay core stop tracking the invoice, and an exception from it aborts BTCPay's polling sweep for the whole store connection. A paid invoice that cannot be credited yet must stay tracked without doing either.
- Provider data is validated before it reaches BTCPay: amounts, payment times, and preimages that cannot be verified are not reported as fact.
- Private API keys, authentication material, and raw account identifiers must not be logged or added to persisted tracking keys.
- Invoice persistence must remain restart-safe, concurrency-safe, bounded, and compatible with the documented legacy-state migration.
- Cancellation must propagate without being converted into retryable failures, and network/authentication failures must not corrupt tracked state.
- The plugin is receive-only. Do not suggest or introduce outgoing payment or refund behavior without an explicit product decision.

## Tests

Changes to invoice ownership, listener delivery, persistence, concurrency, retry/backoff, authentication, or connection-string handling need focused regression tests. Missing cross-store isolation tests are a significant coverage gap.

## Reviewing

Review only changes introduced by the pull request and their immediate context. Do not report pre-existing issues unless the change makes them newly reachable or materially worse.
