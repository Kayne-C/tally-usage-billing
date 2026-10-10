# Tally

[English](README.md) · [Türkçe](README.tr.md)

A usage-based billing engine for .NET on PostgreSQL: it ingests metered usage events, prices them with tiered/volume/package/flat models, and issues immutable invoices, including correct **adjustments for late-arriving usage**.

The point of the project is one property that is easy to claim and hard to keep:

> Whatever the batching, ordering, duplication, retries and billing-run interleaving, the sum of everything billed for a (customer, meter, month) equals the price of the final total usage, to the cent.

```
POST /events  [{ "customerId":"acme", "eventId":"7f3a", "meter":"api.calls", "quantity":1500, "eventTime":"2026-01-15T10:00:00Z" }]
POST /customers/acme/invoices?asOf=2026-02-01T00:00:00Z
```

## What is in it

| Capability | How |
|---|---|
| Idempotent ingestion | `(customerId, eventId)` is unique; a redelivered event is recognized and counted zero times |
| Exactly-once aggregation | Rollups are updated in the same statement that inserts the *new* events, never for duplicates |
| Pricing models | Flat, Graduated (with per-tier flat fee), Volume, Package; included free units; configurable currency decimals |
| Versioned plans | Plans are immutable versions; a customer stays on the version they subscribed to |
| Late usage | Billed as an `adjustment` line equal to `price(total now) − price(total already billed)` |
| Proration | First-month base fee prorated by remaining days |
| Immutable invoices | Database triggers reject UPDATE/DELETE; per-customer gapless numbering |
| Preview | The same code path as billing, without writing |
| Operations | REST API, bearer key (server refuses to start without one), Docker, CI |

## Design in one page

* **Prices are a function of the cumulative total, not of an event.** A graduated price depends on where the *total* lies in the tiers, so billing a month in pieces would double-count or under-count unless every piece is priced as a difference of cumulative prices. Each rollup remembers `billed_quantity` and `billed_amount`; a billing run emits `Cost(quantity) − billed_amount` and stores the new pair. Adjustments are the same mechanism run again ([ADR 1](docs/adr/en/0001-cumulative-pricing.md)).
* **Rounding happens once, on the cumulative price,** never per event, so rounding error cannot accumulate across batches.
* **Ingestion is one SQL statement:** insert-or-ignore events, aggregate the rows that were actually inserted, upsert rollups, in a fixed key order ([ADR 2](docs/adr/en/0002-ingestion.md)).
* **A billing run locks the customer and the rollups it bills** (`FOR UPDATE`), so an event racing a run is either in this invoice or in the next adjustment, never lost and never in both ([ADR 3](docs/adr/en/0003-invoices.md)).

## Evidence

One Windows machine, PostgreSQL 17 in Docker on the same machine, load generator in the same process. Relative, not absolute.

| Run | Result |
|---|---|
| 50 tests | pass in CI |
| 1,000,000 events, 2,000 customers, 8 writers, batches of 1,000 | ~255,000 events/s, batch p50 28 ms, p99 96 ms |
| Redelivery of 100,000 of those events | 100% recognized as duplicates, 0.1 s |
| 2,000 invoices, 3 months and 3 meters each (12 lines) | ~1,470 invoices/s |

Reproduce: `dotnet run -c Release --project bench/Tally.Bench -- --events 1000000 --customers 2000`.

### How it is tested

* **Pricing (24):** every model and boundary, rounding direction, currency decimals, proration, plan validation; graduated pricing is compared with an independent **unit-by-unit oracle** on 300 random plans, and checked to be monotone on 200 more.
* **Ingestion (9):** redelivery, duplicates inside a batch, unknown customers and pre-subscription events, UTC month boundaries, rejected invalid batches leave nothing behind, 50 concurrent retries of one batch, and an 8-writer run of 6,000 events (up to 3 deliveries each, random batch sizes) whose rollups must equal an in-memory oracle and a `SUM()` over the raw events.
* **Billing (12):** tiered invoice, no double billing, open month untouched, late-usage adjustment equals the difference of cumulative prices, gapless numbering, proration, previews write nothing, unpriced meters reported, immutability enforced by the database, plan versions pinned, ten simultaneous runs make one invoice.
* **Consistency test:** 6 customers, 9,000 distinct events over 3 months delivered up to twice in random order and batch sizes by 8 writers **while 4 billing loops run continuously**. At the end, for every (customer, meter, month), the sum of all invoice lines equals the price of the oracle total, quantities add up, there is exactly one `usage` line, base fees are billed exactly once per month, numbering is gapless and no rollup is left unbilled.
* **API (4):** auth, validation errors, an end-to-end customer flow.

### Bug found by the consistency test

Ingestion locked rollup rows in `(meter, period)` order while a billing run locked the same customer's rollups in `(period, meter)` order: PostgreSQL reported `deadlock detected` under concurrent load. Both now use the same `(meter, period)` order (and the invoice is presented sorted by period afterwards). The test ran clean three times in a row after the fix.

## Run it

```bash
docker compose up --build
KEY="Authorization: Bearer change-me-0123456789"
curl -X PUT localhost:8080/plans -H "$KEY" -H "Content-Type: application/json" -d '{"id":"pro","baseFee":50,"meters":[{"meter":"api.calls","model":"Graduated","tiers":[{"upTo":1000,"unitPrice":0.01},{"upTo":null,"unitPrice":0.005}]}]}'
curl -X PUT localhost:8080/customers/acme -H "$KEY" -H "Content-Type: application/json" -d '{"planId":"pro","startDate":"2026-01-01"}'
curl -X POST localhost:8080/events -H "$KEY" -H "Content-Type: application/json" -d '[{"customerId":"acme","eventId":"1","meter":"api.calls","quantity":1500,"eventTime":"2026-01-15T10:00:00Z"}]'
curl -X POST "localhost:8080/customers/acme/invoices?asOf=2026-02-01T00:00:00Z" -H "$KEY"
```

Endpoints: `PUT /plans`, `PUT /customers/{id}`, `POST /events` (max 5,000 per request, event time at most 24 h in the future), `GET /customers/{id}/usage?period=`, `GET /customers/{id}/invoice-preview?asOf=`, `POST /customers/{id}/invoices?asOf=`, `GET /customers/{id}/invoices`, `GET /health`.

```bash
dotnet test --solution Tally.slnx
```

## Known limits

* **No corrections or refunds.** Quantities must be positive and events cannot be deleted; there is no credit-note API. Over-billing is fixed outside the system today. Volume pricing can make a late event *lower* a bill (the adjustment is then negative), which is correct but surprising.
* **No taxes, discounts, coupons, credits, prepaid commitments or minimum charges.**
* **Monthly UTC periods only.** No anchored billing cycles, no mid-cycle plan changes (a customer is pinned to one plan version), no multi-currency conversion.
* **Prices are decimals with 9 fractional digits at most; quantities up to 10^12.** Very small unit prices combined with enormous quantities are not validated against `decimal` overflow beyond that.
* **Usage events are kept forever** and the events table is not partitioned; it is the largest table by far.
* **An unpriced meter (not in the customer's plan) is reported but its usage is never billed retroactively** when the plan changes, because plans cannot change.
* **Single shared API key,** no per-customer authorization, no rate limiting.
* **No payment collection, dunning or PDF rendering.**

Design decisions: [docs/adr/en](docs/adr/en).
