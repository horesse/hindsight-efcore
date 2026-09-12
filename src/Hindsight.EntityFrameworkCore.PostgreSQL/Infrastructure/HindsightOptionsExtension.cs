using Hindsight.Conventions;
using Hindsight.Migrations;
using Hindsight.Query;
using Hindsight.Writers;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace Hindsight.Infrastructure;

/// <summary>
/// The <see cref="IDbContextOptionsExtension"/> that <c>UseHindsight()</c> adds. It always registers
/// <see cref="HindsightConventionSetPlugin"/> so the history entity types are built into the model, the
/// <see cref="HistorySnapshotGuardInterceptor"/>, the <see cref="HindsightQueryExpressionInterceptor"/>,
/// and the <see cref="HindsightMigrationsSqlGenerator"/> that emits every history table's period-range
/// index (DESIGN.md D5). For <see cref="HistoryWriter.Interceptor"/> it also registers the
/// <see cref="HistoryWriterInterceptor"/> that writes history rows on <c>SaveChanges</c>; for
/// <see cref="HistoryWriter.Trigger"/> it instead registers the <see cref="HistoryTriggerContextInterceptor"/>
/// that pushes the change context into the transaction, and <see cref="HindsightMigrationsSqlGenerator"/>
/// additionally generates the history trigger DDL.
/// </summary>
internal sealed class HindsightOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public HindsightOptionsExtension()
    {
    }

    private HindsightOptionsExtension(HindsightOptionsExtension copyFrom)
    {
        HistoryWriter = copyFrom.HistoryWriter;
        ChangeContextProviderType = copyFrom.ChangeContextProviderType;
    }

    public HistoryWriter HistoryWriter { get; private set; } = HistoryWriter.Interceptor;

    /// <summary>
    /// The <see cref="IChangeContextProvider"/> implementation type registered with
    /// <see cref="HindsightOptionsBuilder.WithChangeContext{TProvider}"/>, or <see langword="null"/>
    /// when none was registered (history context columns are then written <see langword="null"/>).
    /// The <see cref="Writers.HistoryWriterInterceptor"/> resolves it per <c>SaveChanges</c> from the
    /// application service provider, falling back to a parameterless constructor.
    /// </summary>
    public Type? ChangeContextProviderType { get; private set; }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public HindsightOptionsExtension WithHistoryWriter(HistoryWriter historyWriter)
        => new(this) { HistoryWriter = historyWriter };

    public HindsightOptionsExtension WithChangeContextProvider(Type providerType)
        => new(this) { ChangeContextProviderType = providerType };

    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IConventionSetPlugin, HindsightConventionSetPlugin>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInterceptor, HistorySnapshotGuardInterceptor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IInterceptor, HindsightQueryExpressionInterceptor>());

        if (HistoryWriter == HistoryWriter.Trigger)
        {
            // The trigger does the writing; the app only needs to push the change context into the
            // transaction, and the migration needs to grow the trigger DDL (DESIGN.md D3).
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<IInterceptor, HistoryTriggerContextInterceptor>());
        }
        else
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<IInterceptor, HistoryWriterInterceptor>());
        }

        // Decorate the provider's generator: resolve NpgsqlMigrationsSqlGenerator (public type) as a
        // concrete service — DI fills its constructor, including the internal option — and wrap it.
        // Registered in both writer modes: the history table's period-range index (DESIGN.md D5) has
        // nothing to do with which writer is configured, only the trigger DDL is Trigger-mode-only, and
        // HindsightMigrationsSqlGenerator itself decides which of the two to emit from HistoryWriter.
        services.AddScoped<NpgsqlMigrationsSqlGenerator>();
        services.AddScoped<IMigrationsSqlGenerator>(serviceProvider => new HindsightMigrationsSqlGenerator(
            serviceProvider.GetRequiredService<NpgsqlMigrationsSqlGenerator>(),
            serviceProvider.GetRequiredService<ISqlGenerationHelper>(),
            HistoryWriter));
    }

    // The name of the assembly Npgsql.EntityFrameworkCore.PostgreSQL registers its provider extension
    // from. It is the same string DbContext.Database.ProviderName reports once a context built with
    // UseNpgsql(...) exists (verified against Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3) — Validate
    // runs earlier than that, before a service provider exists to ask, so this reads the equivalent
    // value straight off the extension's own declaring assembly instead. 'internal' rather than
    // 'private' only to satisfy the repo's private-field naming rule, which expects a leading
    // underscore.
    internal const string NpgsqlProviderAssemblyName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public void Validate(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Every relational (or other) database provider registers exactly one extension that reports
        // IsDatabaseProvider — that is the public marker for "this extension selects the provider",
        // see DbContextOptionsExtensionInfo.IsDatabaseProvider. It can be absent: UseHindsight() can be
        // called before, or without, UseNpgsql()/UseSqlite()/etc. That is not Hindsight's mistake to
        // report — EF Core itself throws its own clear "No database provider has been configured" error
        // the moment the context is used, so just let that happen instead of second-guessing it here.
        var providerExtension = options.Extensions.FirstOrDefault(extension => extension.Info.IsDatabaseProvider);
        if (providerExtension is null)
        {
            return;
        }

        // Identify which provider it is by the assembly its extension type was declared in. This is
        // deliberately not a cast to, or a using of, Npgsql's own NpgsqlOptionsExtension — that type
        // lives in an `.Internal` namespace (golden rule 1) and is never referenced here. Reading
        // Type.Assembly.GetName().Name is public System.Reflection metadata, nothing more than asking
        // "which package produced this object" — the same identity DbContext.Database.ProviderName
        // exposes for a fully built context.
        var providerAssemblyName = providerExtension.GetType().Assembly.GetName().Name;
        if (string.Equals(providerAssemblyName, NpgsqlProviderAssemblyName, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Hindsight only supports the Npgsql/PostgreSQL provider ('{NpgsqlProviderAssemblyName}'), but "
            + $"this DbContext is configured with '{providerAssemblyName}'. Providers other than "
            + "Npgsql/PostgreSQL are a non-goal (README.md, Non-goals); configure the context with "
            + "UseNpgsql(...) instead of UseSqlite(...)/UseSqlServer(...)/etc.");
    }

    private sealed class ExtensionInfo(HindsightOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new HindsightOptionsExtension Extension => (HindsightOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
        {
            get
            {
                var provider = Extension.ChangeContextProviderType?.Name ?? "none";
                return $"using Hindsight (history writer: {Extension.HistoryWriter}, change context: {provider}) ";
            }
        }

        public override int GetServiceProviderHashCode()
            => HashCode.Combine(Extension.HistoryWriter, Extension.ChangeContextProviderType);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info
                && info.Extension.HistoryWriter == Extension.HistoryWriter
                && info.Extension.ChangeContextProviderType == Extension.ChangeContextProviderType;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["Hindsight:HistoryWriter"] = Extension.HistoryWriter.ToString();
            debugInfo["Hindsight:ChangeContextProvider"] =
                Extension.ChangeContextProviderType?.FullName ?? "none";
        }
    }
}
