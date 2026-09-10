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
/// <see cref="HistorySnapshotGuardInterceptor"/> and the <see cref="HindsightQueryExpressionInterceptor"/>.
/// For <see cref="HistoryWriter.Interceptor"/> it also registers the <see cref="HistoryWriterInterceptor"/>
/// that writes history rows on <c>SaveChanges</c>; for <see cref="HistoryWriter.Trigger"/> it instead
/// registers the <see cref="HindsightMigrationsSqlGenerator"/> that generates the history trigger and the
/// <see cref="HistoryTriggerContextInterceptor"/> that pushes the change context into the transaction.
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

            // Decorate the provider's generator: resolve NpgsqlMigrationsSqlGenerator (public type) as a
            // concrete service — DI fills its constructor, including the internal option — and wrap it.
            services.AddScoped<NpgsqlMigrationsSqlGenerator>();
            services.AddScoped<IMigrationsSqlGenerator>(serviceProvider => new HindsightMigrationsSqlGenerator(
                serviceProvider.GetRequiredService<NpgsqlMigrationsSqlGenerator>(),
                serviceProvider.GetRequiredService<ISqlGenerationHelper>()));
        }
        else
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Scoped<IInterceptor, HistoryWriterInterceptor>());
        }
    }

    public void Validate(IDbContextOptions options)
    {
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
