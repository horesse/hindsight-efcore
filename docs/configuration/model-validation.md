---
order: 30
---

# Model validation

Hindsight checks every temporal entity when the model is built, which is also when
`dotnet ef migrations add` builds it. A configuration it cannot version correctly fails right there,
with a message that names the entity and the fix, instead of producing a history table that silently
misses changes.

| rejected | exception | why | what to do |
|---|---|---|---|
| No primary key | `InvalidOperationException` | History rows are matched to their entity by key. | Give the entity a key. |
| Every primary-key property excluded with `Exclude(...)` | `InvalidOperationException` | No column would be left to identify the entity's versions. | Keep at least one key property versioned. |
| Not mapped to a table | `InvalidOperationException` | There is no table to version. | Map the entity with `ToTable(...)`. |
| Owned reference (`OwnsOne`) or complex property | `NotSupportedException` | Their columns belong to another type, so history could not mirror them and would miss changes to them. | Make the entity standalone, or drop the owned or complex members. |
| Part of an inheritance hierarchy | `NotSupportedException` | Not supported in this version. | Map the entity on its own. |
| A column named like a history column: `history_id`, `operation`, `changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`, or the entity's period columns | `InvalidOperationException` | The history table already has a column with that name. | Rename the column, exclude the property, or choose other period column names. |
| Period start and end with the same name | `InvalidOperationException` | They are two columns. | Choose two names. |
| A generated name over 63 bytes | `InvalidOperationException` | PostgreSQL silently truncates longer identifiers, so two entities could share a table, an index, or, silently, a trigger function. | Shorten the name with `UseHistoryTable("shorter_name")`, or rename the main table. |

The generated names are the history table, its two indexes, and in trigger mode the trigger function
and the trigger; see [What a migration creates](/migrations/generated-schema#generated-names). The
limit is 63 bytes of UTF-8, not 63 characters. The longest ASCII main-table name that fits with the
default history table name is 44 characters.

A history table name that collides with another table in the model is rejected too, by EF Core's own
validation, since the history table is an ordinary entity type as far as EF Core is concerned.

Removing a primary-key property from an entity that is already temporal is also rejected; see
[Evolving a temporal entity](/migrations/schema-evolution#removing-a-primary-key-property).
