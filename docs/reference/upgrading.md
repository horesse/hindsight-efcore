---
order: 30
sidebarTitle: Upgrade guide
---

# Upgrade guide

What to do, and what to expect, when you move to a new version. For the complete list of changes in a
release, see the [release notes](https://github.com/horesse/hindsight-efcore/releases).

Hindsight follows semantic versioning: a minor or patch release never needs a code change on your side,
but it may add a manual database step (listed here) or start rejecting something that was silently
wrong before.

## Upgrading to 1.2

### A column type change on a temporal entity that now fails when the model is built

Changing a property's store type, precision/scale, max length, or value converter while its column
keeps the same name used to reach the differ as a genuine `AlterColumnOperation` on the history
table — which could fail outright or silently reshape values that history already holds under the old
type (`text` → `jsonb` against a history row that isn't valid JSON is a concrete example). It now
throws `InvalidOperationException` at model build, which includes `dotnet ef migrations add`, naming
the fix: give the property a different column name instead, so the old column is orphaned exactly like
a rename. See [Changing a property's type](/migrations/schema-evolution#changing-a-property-s-type).

## Upgrading to 1.1

### Add the period-range index to existing history tables

History tables created by 1.1 get a GiST index on their period; tables created by 1.0 do not, and
upgrading does not add it. Without it, point-in-time queries that are not filtered by key scan the
whole history table. See
[Backfilling the period-range index](/migrations/existing-tables#backfilling-the-period-range-index)
for the SQL.

### Configurations that now fail when the model is built

Each of these used to produce a history table that silently missed changes or corrupted history. They
now throw at model build, which includes `dotnet ef migrations add`:

- every primary-key property excluded with `Exclude(...)`;
- a column named like one of the history table's own columns, or like the period columns;
- the same name for the period start and end columns;
- a generated table, index, function or trigger name longer than 63 bytes;
- an owned reference or a complex property on a temporal entity.

See [Model validation](/configuration/model-validation) for the fixes.

### Queries that now throw

- `ExecuteUpdate` and `ExecuteDelete` on `AsOf`, `AllVersions` or `History<T>` throw
  `NotSupportedException`. `ExecuteUpdate` used to update the history table itself.
- `UseHindsight()` with a provider other than Npgsql throws on first use.

### Behavior changes

- **Ambient `TransactionScope` works** with both writers, instead of failing with "an ambient
  transaction has been detected". See [Transactions](/writing/transactions#transactionscope).
- **`EnableRetryOnFailure()` fails early** with a Hindsight message naming the fix, when a save would
  need Hindsight's own transaction. See [Transactions](/writing/transactions#enableretryonfailure).
- **`WithReason` applies to one `DbContext`**: a save on another context inside the same `using`
  block no longer picks up its reason.
- **Interceptor writer, deletes**: the row is re-read before the tombstone is written, one extra round
  trip per save that deletes. A delete of a row that no longer exists throws instead of writing an
  empty tombstone.
- **Interceptor writer, failures**: after a failed history write, `Added` and `Modified` entities are
  restored so that a retry works. See
  [If the history write fails](/writing/interceptor#if-the-history-write-fails).
- **Interceptor writer, concurrency**: concurrent updates of one row no longer produce overlapping
  periods; the previous version is closed with the same clamp the trigger writer uses.

## Choosing the writer explicitly

Whether `HistoryWriter.Trigger` should become the default in a future major version is an open
question. Whichever way it is decided, a context that calls `UseHistoryWriter(...)` explicitly keeps
its behavior across upgrades, so name the writer you use.
