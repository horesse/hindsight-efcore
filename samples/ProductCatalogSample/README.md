# ProductCatalogSample

A runnable tour of `HistoryWriter.Interceptor` (the default writer) and the query API: `AsOf`,
`AllVersions`, `History<T>`, and what a delete leaves behind.

```
dotnet run --project samples/ProductCatalogSample
```

Needs Docker. It starts and tears down its own disposable PostgreSQL 17 container — nothing to
configure, nothing left behind.

What it does, in order: creates a product, changes its price twice, asks what it looked like right
after launch (`AsOf`), prints its full timeline (`AllVersions`), prints the same timeline with who/when
metadata (`History<Product>`), then deletes it and shows the tombstone that's still there in history.

See [`Program.cs`](Program.cs) for the whole thing, or the
[querying](https://horesse.github.io/hindsight-efcore/latest/querying/as-of) docs for how each piece works.
