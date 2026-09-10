using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.Benchmarks;

/// <summary>
/// Shared plumbing for the benchmark classes: reads the container connection string that
/// <c>Program</c> put in the environment, creates and drops a throwaway database per benchmark case,
/// and builds a <see cref="BenchmarkContext"/> for a given <see cref="HistoryMode"/>.
/// </summary>
public static class BenchmarkDatabase
{
    /// <summary>Environment variable carrying the container's admin connection string.</summary>
    public const string ConnectionStringVariable = "HINDSIGHT_BENCH_PG";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable)
        ?? throw new InvalidOperationException(
            $"{ConnectionStringVariable} is not set. Run the benchmarks through the project entry "
            + "point: dotnet run -c Release --project benchmarks/Hindsight.Benchmarks -- --filter '*'.");

    /// <summary>Drops <paramref name="name"/> if it lingers from a previous run, creates it fresh.</summary>
    public static string CreateDatabase(string name)
    {
        using var connection = new NpgsqlConnection(AdminConnectionString);
        connection.Open();

        Execute(connection, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        Execute(connection, $"CREATE DATABASE \"{name}\"");

        return new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = name,
            MaxPoolSize = 10,
        }.ConnectionString;
    }

    /// <summary>Drops the throwaway database created by <see cref="CreateDatabase"/>.</summary>
    public static void DropDatabase(string name)
    {
        using var connection = new NpgsqlConnection(AdminConnectionString);
        connection.Open();
        Execute(connection, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    /// <summary>Builds a context configured for <paramref name="mode"/>, optionally with a change context.</summary>
    public static BenchmarkContext NewContext(string connectionString, HistoryMode mode, bool withChangeContext = false)
    {
        var builder = new DbContextOptionsBuilder<BenchmarkContext>().UseNpgsql(connectionString);

        if (mode != HistoryMode.None)
        {
            var writer = mode == HistoryMode.Trigger ? HistoryWriter.Trigger : HistoryWriter.Interceptor;
            builder.UseHindsight(h =>
            {
                h.UseHistoryWriter(writer);
                if (withChangeContext)
                {
                    h.WithChangeContext<FixedChangeContextProvider>();
                }
            });
        }

        return new BenchmarkContext(builder.Options);
    }

    /// <summary>Truncates the tables touched by a benchmark case, resetting identity.</summary>
    public static string TruncateSql(HistoryMode mode) =>
        mode == HistoryMode.None
            ? "TRUNCATE policies RESTART IDENTITY"
            : "TRUNCATE policies, policies_history RESTART IDENTITY";

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
