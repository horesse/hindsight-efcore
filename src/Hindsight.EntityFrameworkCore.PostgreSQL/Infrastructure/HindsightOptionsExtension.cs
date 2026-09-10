using Hindsight.Conventions;
using Hindsight.Writers;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hindsight.Infrastructure;

/// <summary>
/// The <see cref="IDbContextOptionsExtension"/> that <c>UseHindsight()</c> adds. It registers
/// <see cref="HindsightConventionSetPlugin"/> so the history entity types are built into the model,
/// and — for <see cref="HistoryWriter.Interceptor"/> — the <see cref="HistoryWriterInterceptor"/>
/// that writes history rows on <c>SaveChanges</c>.
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
            ServiceDescriptor.Scoped<IInterceptor, HistoryWriterInterceptor>());
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
