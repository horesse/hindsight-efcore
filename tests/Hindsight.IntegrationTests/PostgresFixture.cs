using Testcontainers.PostgreSql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// One PostgreSQL container per test collection. Each test creates its own database on it
/// via <see cref="CreateDatabaseAsync"/> so tests stay isolated without paying container startup per test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Every test gets its own database on this one container, and each keeps its own Npgsql pool. Raise
    // the server's connection ceiling and keep the per-database pools small and short-lived so a long
    // sequential run does not accumulate idle connections past max_connections.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=400")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = new Npgsql.NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{name}\"";
        await command.ExecuteNonQueryAsync(cancellationToken);

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = name,
            MaxPoolSize = 5,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        };
        return builder.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
