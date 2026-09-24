# HistoryViewerSample

A small read-only web page showing what a "history screen" built on Hindsight looks like: a list of
products (deleted ones included), one product's timeline with who changed it, when and why, what each
version changed, and the product's state at any instant you pick.

```
dotnet run --project samples/HistoryViewerSample
```

Needs Docker. It starts and tears down its own disposable PostgreSQL 17 container, seeds it (about ten
seconds), and opens <http://localhost:5080> in your browser. Pass `-- --no-browser` to skip that.
Ctrl+C stops it and removes the container.

It uses the `Product` model from [`ProductCatalogSample`](../ProductCatalogSample) (the files are linked,
not copied) with `HistoryWriter.Trigger`, so the timeline also shows writes that bypass `SaveChanges`.
The seed has three users, a reason on every change, a rename, a delete, and one `ExecuteUpdate` that the
timeline shows with no change context.

- [`HistoryQueries.cs`](HistoryQueries.cs): every Hindsight call the pages make. `History<Product>()`
  for the list and the timeline, `Diff` between consecutive versions, `AsOf` for the state at an instant.
- [`Seed.cs`](Seed.cs): the history the page shows.
- [`Components/Pages`](Components/Pages): Blazor static server rendering. No JavaScript, no front-end
  build.

The page is read-only on purpose: history is not something you edit
([restrictions](https://horesse.github.io/hindsight-efcore/latest/querying/restrictions)).
