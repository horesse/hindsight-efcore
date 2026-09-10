using BenchmarkDotNet.Running;
using Hindsight.Benchmarks;
using Testcontainers.PostgreSql;

// BenchmarkDotNet runs every benchmark case in its own child process. Start one PostgreSQL
// container here, in the host process, and hand its connection string to the children through an
// environment variable they inherit; each benchmark then creates its own throwaway database on it
// in [GlobalSetup]. This keeps BenchmarkDotNet's per-benchmark process isolation while paying
// container startup only once for the whole run.
await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .WithCommand("-c", "max_connections=400")
    .Build();

await postgres.StartAsync();
Environment.SetEnvironmentVariable(BenchmarkDatabase.ConnectionStringVariable, postgres.GetConnectionString());

BenchmarkSwitcher.FromAssembly(typeof(BenchmarkDatabase).Assembly).Run(args);
