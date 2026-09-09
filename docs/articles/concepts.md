# Concepts

## System time vs. application time

Two different questions hide behind the word "temporal":

- **System time** — *when did the database hold this row?* Every insert, update and delete opens a new
  version and closes the previous one. This is what Hindsight tracks. It answers "what did the policy
  look like at the moment the claim was filed" and "who changed it, and why".
- **Application (valid) time** — *when was this fact true in the real world?* "This tariff applies from
  March 1." The application chooses the dates; the database enforces that periods don't overlap.
  PostgreSQL 19 supports this natively with `FOR PORTION OF` and `WITHOUT OVERLAPS`. Hindsight does
  **not** implement it in v1; the API is shaped so it can be added later without breaking changes.

## Versions and periods

Each row in a history table is a *version*: a snapshot of the entity's versioned columns plus a period
`[valid_from, valid_to)`. Periods are half-open and stored as `timestamptz` in UTC. The current version
has `valid_to = 'infinity'`, which compares and indexes better than `null`.

A point-in-time query is therefore a single predicate: `valid_from <= @t AND valid_to > @t`.

## Change context

The database only knows that a row changed. The application knows who was acting, on behalf of which
request or message, and why. Hindsight captures that as a `ChangeContext` (user id, user name,
correlation id, reason, arbitrary extras) and stores it on every version.

The context is pushed into the transaction once, with `set_config('hindsight.*', ..., true)`, so it is
available both to the application-side writer and to the database-side trigger writer.
See [History writers](history-writers.md).

## Two tables, not a flag

The main table stays exactly as it is — same queries, indexes and foreign keys. History is a separate
table without foreign keys back to the main table (the parent may be deleted; history must survive)
and without the original's `NOT NULL`, unique or check constraints (a unique index on history breaks
on the second edit).
