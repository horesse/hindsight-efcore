---
layout: home

hero:
  name: Hindsight
  text: Temporal entities for EF Core on PostgreSQL
  tagline: Every change to an entity kept as a version, queryable with LINQ, created by your migrations, stamped with who and why.
  image:
    src: /icon.png
    alt: Hindsight
  actions:
    - theme: brand
      text: Get started
      link: /introduction/getting-started
    - theme: alt
      text: What is Hindsight?
      link: /introduction/what-is-hindsight
    - theme: alt
      text: Tutorial
      link: /tutorials/audit-trail

features:
  - title: History lives in your migrations
    details: Mark an entity with IsTemporal() and the next dotnet ef migrations add creates its history table and indexes. No database extensions, no superuser, no hand-written DDL.
  - title: AsOf() is plain LINQ
    details: Ask what an entity looked like at any instant. Where, OrderBy and Select compose after it and translate into a single SQL query.
  - title: Who changed it, and why
    details: The user, the correlation id and the reason come from your application and are stored on every version.
  - title: Catches every write
    details: The trigger writer records ExecuteUpdate, ExecuteDelete, raw SQL and other processes, in the same transaction as the change itself.
  - title: History is never destroyed
    details: Removing a property or a whole entity keeps its history. No migration Hindsight generates drops a history column or table.
  - title: Wrong answers throw
    details: A query Hindsight cannot answer exactly, such as AsOf() with Include(), throws a clear exception instead of returning a plausible, wrong result.
---

## At a glance

<<< @/snippets/Overview.cs#overview

Requires .NET 10, EF Core 10 with the Npgsql provider, and PostgreSQL 14 or later.
