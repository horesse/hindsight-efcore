using Testcontainers.PostgreSql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// One PostgreSQL container per test collection. Each test creates its own database on it
/// via <see cref="CreateDatabaseAsync"/> so tests stay isolated without paying container startup per test.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
        : this("postgres:17-alpine")
    {
    }

    // Every test gets its own database on this one container, and each keeps its own Npgsql pool. Raise
    // the server's connection ceiling and keep the per-database pools small and short-lived so a long
    // sequential run does not accumulate idle connections past max_connections.
    protected PostgresFixture(string image)
    {
        _container = new PostgreSqlBuilder(image)
            .WithCommand("-c", "max_connections=400")
            .Build();
    }

    public string AdminConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        GC.SuppressFinalize(this);
    }

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

/// <summary>
/// The oldest PostgreSQL Hindsight supports (README: 14 or later), for the few tests whose outcome
/// depends on the server version. Started once per test class that asks for it.
/// </summary>
public sealed class Postgres14Fixture() : PostgresFixture("postgres:14-alpine");
