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
    }

    public HistoryWriter HistoryWriter { get; private set; } = HistoryWriter.Interceptor;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public HindsightOptionsExtension WithHistoryWriter(HistoryWriter historyWriter)
        => new(this) { HistoryWriter = historyWriter };

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

        public override string LogFragment => $"using Hindsight (history writer: {Extension.HistoryWriter}) ";

        public override int GetServiceProviderHashCode() => (int)Extension.HistoryWriter;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info && info.Extension.HistoryWriter == Extension.HistoryWriter;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["Hindsight:HistoryWriter"] = Extension.HistoryWriter.ToString();
        }
    }
}
