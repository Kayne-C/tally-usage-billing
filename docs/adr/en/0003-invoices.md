# ADR 0003 — Invoices: immutability, numbering and concurrency

**Status:** Accepted

## Decision

* **Immutable at the database.** `BEFORE UPDATE OR DELETE` triggers on `invoices` and `invoice_lines` raise an exception, so neither a bug nor an operator typing SQL can rewrite history. Corrections are new invoices with adjustment lines.
* **Gapless per-customer numbering.** The number is read from `customers.next_invoice_no` under a row lock and incremented in the same transaction that inserts the invoice. A rollback consumes no number.
* **A billing run is one transaction:** lock the customer row, lock the rollups to be billed (`FOR UPDATE`), compute lines, insert the invoice, its lines, update the rollups' billed pair, record billed base-fee months, increment the number. Simultaneous runs for one customer serialize on the customer row; the second finds nothing left to bill.
* **Events racing a run** either committed their rollup update before the run locked the rollup (included) or wait for it and become an adjustment next time. Nothing is lost and nothing is counted twice.
* **Base fees** are recorded in `base_fees_billed` per (customer, period), so a catch-up run bills each missed month exactly once; the first month is prorated by remaining days.
* **Plans are immutable versions** and customers reference `(plan, version)`, so an invoice can always be explained by the plan that produced it.
* **Preview** runs the same code without locks or writes.
* Meters not in the customer's plan are reported in the result and left unbilled instead of silently priced at zero.

## Consequences

* Invoice creation for one customer is serialized; different customers bill in parallel (about 1,470 invoices/s on the benchmark machine).
* There is no way to void an invoice yet (see known limits).
