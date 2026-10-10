# ADR 0002 — Idempotent ingestion with exactly-once aggregation

**Status:** Accepted

## Decision

* An event is identified by `(customer_id, event_id)`, the primary key of the events table. Producers retry freely; the table is the deduplication.
* One SQL statement per batch (arrays unpacked with `unnest`): filter to known customers and times at or after the subscription start, insert-or-ignore events, aggregate **only the rows that were actually inserted**, and upsert the rollups. The rollup effect therefore happens exactly once per distinct event, in the same atomic statement as the event insert. A crash cannot leave an event without its rollup effect or the reverse.
* The statement returns `received / accepted / duplicates / rejected` so a client can tell redelivery from rejection.
* Invalid fields (non-positive or huge quantity, bad meter name, non-UTC time, empty ids, batch over 5,000) reject the whole batch before any database work; the API turns them into HTTP 400. Events for unknown customers or before the subscription are counted as `rejected`, not stored.
* **Lock order is part of the design.** Rows are inserted and rollups upserted in `(customer, meter, period)` order, and billing locks rollups in `(meter, period)` order. The consistency test found a deadlock when billing used `(period, meter)`.
* The month is derived once, in UTC, at insert time and stored.

## Consequences

* Ingestion cost is dominated by one index insert per event plus one upsert per distinct rollup in the batch (about 255,000 events/s on the benchmark machine).
* A rollup row is a hot spot for one customer-meter-month under heavy parallel ingestion; batches that touch it serialize on its row lock.
* Rollups can always be rebuilt from events, and a test asserts they equal `SUM()` of events.
