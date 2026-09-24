---
order: 35
sidebarTitle: Diff — what changed
---

# What changed between two versions: `Diff`

*Which fields did this edit change, and from what to what?*

<<< @/snippets/VersionDiff.cs#diff-versions

`db.Diff(older, newer)` compares two versions of the same entity and returns the properties whose
values differ, each as a `PropertyChange`:

| member | value |
|---|---|
| `Property` | the EF Core `IProperty` that changed: `Property.Name`, its CLR type, column name and annotations |
| `OldValue` | the value in the older version, or `null` when `older` is `null` |
| `NewValue` | the value in the newer version |

Values are your entity's CLR values: an enum stored as text comes back as the enum. Who made the change
and when is not repeated on a `PropertyChange`; read it from the `Version<Policy>` you passed in.

The diff runs in memory over versions you have already loaded. It sends no query.

## Which properties are compared

Every property that history records, in model order (key first). A property you
[excluded](/configuration/temporal-entities#excluding-properties) is not in history, so it is never
compared. Values are compared with each property's EF Core value comparer, the same one change
tracking uses: arrays and collections compare by content, and a property with a value converter
compares by its CLR value.

The result is empty when every recorded value is equal.

## Versions and snapshots

There are two overloads:

- **`Diff(Version<T>? older, Version<T> newer)`** for [`History<T>`](/querying/history) results. It
  checks that `older` starts before `newer`. `History<T>` returns newest first, so order the versions
  oldest first (as above) or pass them in reverse.
- **`Diff(T? older, T newer)`** for plain snapshots: two [`AsOf`](/querying/as-of) results, two
  [`AllVersions`](/querying/all-versions) rows, or a snapshot against the entity as it is now:

<<< @/snippets/VersionDiff.cs#diff-since

A plain entity has no period, so this overload cannot tell which snapshot is older. It reports
`older`'s values as old, whatever you pass.

## The first version and deletes

- **Against nothing.** Pass `null` as `older` to diff the insert: every recorded property comes back,
  key included, with `OldValue` set to `null`.
- **Delete tombstones throw.** The `VersionOperation.Delete` row that `History<T>` returns records *when*
  and *by whom* a policy was deleted. Its values repeat the last version before the delete, so it is not
  a state to compare, and passing it on either side throws `ArgumentException`. Filter it out, as the
  first sample does, and read the delete from `Operation`.
- **Re-created entities.** If a policy is deleted and later inserted again with the same key, diffing
  the last version before the delete with the new insert compares the two states across the delete.

## An empty diff from the interceptor writer

If EF Core marks a property as modified but its value did not change, the
[interceptor writer](/writing/history-writers#differences-you-can-observe) still records an update
version, and diffing it against the previous version returns an empty list. The trigger writer records no
version in that case. Skip empty results if your audit screen should not show them.

## What throws

| situation | exception |
|---|---|
| the two versions have different keys | `ArgumentException` |
| `older` does not start before `newer` (`Version<T>` overload), including the same version twice | `ArgumentException` |
| either version is a delete tombstone | `ArgumentException` |
| the entity type is not temporal | `InvalidOperationException` |
| the entity has a recorded [shadow property](https://learn.microsoft.com/ef/core/modeling/shadow-properties), such as a foreign key with no CLR property | `NotSupportedException` |

History records shadow properties, but the snapshots that `History<T>`, `AsOf` and `AllVersions` return
have no member to hold them, so a change to one would be invisible. `Diff` throws rather than return a
diff that misses it. Map the property on the entity class to diff it.
