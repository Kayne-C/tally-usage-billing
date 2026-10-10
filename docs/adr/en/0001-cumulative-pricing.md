# ADR 0001 — Bill the difference of cumulative prices

**Status:** Accepted

## Context

With tiered pricing the price of an event depends on how much usage came before it. Late events, retries and billing mid-stream all break the naive "price each event / price each batch" approach: a graduated price cannot be split by batch without error, and rounding each piece to cents drifts.

## Decision

* `Cost(total)` is a deterministic function of a rollup's total quantity (exact decimal arithmetic, rounded once to the plan's currency decimals, half away from zero).
* Each rollup stores what has been billed: `billed_quantity`, `billed_amount`. A billing run bills `Cost(quantity) − billed_amount` and stores `(quantity, Cost(quantity))`.
* The first bill for a (customer, meter, month) is a `usage` line; every later change is an `adjustment` line with the quantity delta. Both are produced by the same code path.
* Therefore, summed over all invoices, `amount = Cost(final quantity)` exactly, independent of batching, order, number of runs, or when late events arrive. The consistency test asserts this.
* A month in progress is never billed; only months that ended at or before `asOf` are due.
* Volume tiers are allowed to make a bill go down when usage goes up (a cheaper tier is reached). The adjustment is then negative. That is a property of the pricing model, not of the engine.

## Consequences

* Pricing code is a pure function and is tested against an independent unit-by-unit oracle.
* Changing a plan retroactively would need a different mechanism (plans are immutable versions for this reason).
