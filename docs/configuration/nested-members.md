---
order: 15
sidebarTitle: Complex and owned members
---

# Complex properties and owned references

*A policy's insured address is part of the policy. When the address changes, that is a new version of
the policy.*

A temporal entity can have [complex properties](https://learn.microsoft.com/ef/core/modeling/complex-types)
and [owned references](https://learn.microsoft.com/ef/core/modeling/owned-entities) stored in its own
table, the default mapping for both. Their columns are versioned exactly like the entity's own columns.

<<< @/snippets/NestedMembers.cs#nested-member-types

<<< @/snippets/NestedMembers.cs#configure-nested-members

## What gets versioned

- **Every column of the member goes into history**, under the same name as in the main table, nested
  members of nested members included. The history table has `Address_Street`, `Address_City`,
  `PaymentAccount_Iban`, `Holder_Name` and so on, nullable like every history column.
- **Changing only a member writes a version of the entity**: `policy.Address.City = "Paris"` or
  `policy.Holder.Email = …` followed by `SaveChanges` closes the current version and opens a new one,
  in both [writers](/writing/history-writers). So does replacing the owned object, or setting an
  optional member to `null` or back.
- **One version per save.** Changing the entity and its members in the same `SaveChanges` writes one
  version, not one per member.
- **Replacing a complex property with an equal value is not a change.** EF Core compares complex
  properties by value, so no version is written.

## Reading it back

[`AsOf`](/querying/as-of), [`AllVersions`](/querying/all-versions), [`FromTo` and
`ContainedIn`](/querying/time-ranges) and [`History<T>`](/querying/history) rebuild the members from
the history columns. Filters and projections on them translate to SQL as usual:

<<< @/snippets/NestedMembers.cs#query-nested-members

The query reads the members' columns straight from the history table:

<<< @/../tests/Hindsight.IntegrationTests/NestedMemberHistoryTests.AsOf_with_nested_members_generates_the_expected_sql.verified.sql

An optional member (a nullable complex property, or an owned reference) comes back as `null` by the
rule EF Core itself uses when it reads the main table:

- if the member has a required property of its own (such as `PolicyHolder.Name` above), it is `null`
  when that property's column is `NULL`;
- otherwise it is `null` when all of its columns are `NULL`.

::: warning A filter on an optional member with no required property
For the second kind, EF Core cannot translate a `Where` or `OrderBy` on the member's properties into
SQL, and the query throws `InvalidOperationException`. Reading the member works. Give the member a
required property, or filter after the query runs.
:::

## Diff

[`Diff`](/querying/diff) reports a member's properties one by one. `PropertyChange.Path` names where each
one sits:

<<< @/snippets/NestedMembers.cs#diff-nested-members

## Adding, removing and renaming members

A member's columns follow the rules for any other column; see
[Evolving a temporal entity](/migrations/schema-evolution). Adding a member, or a property to one, adds
history columns. Removing one keeps its history columns as nullable orphans with their old values.
Renaming a complex property or an owned reference renames its columns, so history gets new columns and
keeps the old ones.

Versions written before a member existed have `NULL` in its columns, like versions written before any
other added column. An optional member reads back as `null` for them.

## What is not supported

These are rejected when the model is built, with a `NotSupportedException` that names the member:

| member | why |
|---|---|
| Owned collection (`OwnsMany`) | Its rows live in another table, not in the entity's columns. |
| Owned reference mapped to a table of its own (`OwnsOne(...).ToTable(...)`) | Same: another table. |
| Complex property or owned reference mapped to JSON (`ToJson()`), complex collection | History would hold the JSON document, but rebuilding the member from it would not follow EF Core's JSON mapping, so values could come back wrong. |

Map such a member as a table-split `ComplexProperty` or `OwnsOne`, or flatten it into properties of the
entity. `Exclude(...)` takes only the entity's own properties: a member is versioned as a whole.
