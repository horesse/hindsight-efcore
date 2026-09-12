using System.Collections.Concurrent;
using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hindsight.Writers;

/// <summary>
/// Resolves the application's <see cref="IChangeContextProvider"/>, shared by
/// <see cref="HistoryWriterInterceptor"/> and <see cref="HistoryTriggerContextInterceptor"/> — each
/// calls <see cref="Resolve"/> once per <c>SaveChanges</c> (DESIGN.md D3), never per row. Resolution
/// goes through <see cref="CoreOptionsExtension.ApplicationServiceProvider"/> — the same provider
/// <c>TimeProvider</c> is resolved from — falling back to a parameterless constructor when the type is
/// not registered there.
/// </summary>
/// <remarks>
/// <b>Pooled and factory-created contexts.</b> For <c>AddDbContextPool&lt;T&gt;()</c> and
/// <c>AddDbContextFactory&lt;T&gt;()</c> (pooled or not), every pooled/factory-created context shares
/// one <c>DbContextOptions</c> instance built once when the pool/factory is configured, so
/// <c>ApplicationServiceProvider</c> is whichever provider was active at that moment — normally the
/// application's root provider, never a request's scope (confirmed empirically against EF Core 10.0.12:
/// identical across every simulated request that rents from the same pool or factory). A change context
/// provider registered with a scoped lifetime can therefore not be resolved correctly from it: with
/// <c>ServiceProviderOptions.ValidateScopes</c> on (ASP.NET Core's Development default) the call below
/// throws immediately, every time; with it off (the common production default) the container silently
/// hands back a captive singleton — the first caller's instance, with whatever it captured at
/// construction, reused for every later <c>SaveChanges</c> regardless of which request is actually
/// running. There is no registration pattern for the provider that fixes this — the fixed point is
/// <c>ApplicationServiceProvider</c> itself, resolved fresh from the context instance every time but
/// pointing at the same pinned provider regardless. See the "Pooled and factory-created contexts"
/// section of docs/articles/configuration.md for the safe pattern (a singleton provider that reads
/// per-request ambient state, e.g. <c>IHttpContextAccessor.HttpContext</c>, fresh inside
/// <see cref="IChangeContextProvider.GetChangeContext"/> instead of capturing a scoped dependency in its
/// constructor).
/// </remarks>
internal static class ChangeContextProviderResolver
{
    // Parameterless-constructor factories for change context providers that are not registered on the
    // application service provider. Compiled once per type, not per SaveChanges (library-code rule: no
    // reflection on the hot path).
    private static readonly ConcurrentDictionary<Type, Func<object>> _activators = new();

    public static IChangeContextProvider? Resolve(DbContext context)
    {
        var providerType = context.GetService<IDbContextOptions>()
            .FindExtension<HindsightOptionsExtension>()
            ?.ChangeContextProviderType;
        if (providerType is null)
        {
            return null;
        }

        var applicationServiceProvider = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.ApplicationServiceProvider;

        if (applicationServiceProvider is not null
            && ResolveFromServices(applicationServiceProvider, providerType) is { } fromServices)
        {
            return fromServices;
        }

        var activator = _activators.GetOrAdd(providerType, CreateActivator);
        return (IChangeContextProvider)activator();
    }

    private static IChangeContextProvider? ResolveFromServices(IServiceProvider applicationServiceProvider, Type providerType)
    {
        object? resolved;
        try
        {
            resolved = applicationServiceProvider.GetService(providerType);
        }
        catch (InvalidOperationException ex)
        {
            // The one failure mode this application service provider can hit that a plain per-request
            // AddDbContext<T>() never would: see the "Pooled and factory-created contexts" remarks
            // above. Rethrown with that context rather than letting ASP.NET Core's generic, Hindsight-
            // unaware message ("Cannot resolve scoped service '...' from root provider.") surface on its
            // own; the original exception is preserved as the inner exception either way.
            throw new InvalidOperationException(
                $"Failed to resolve change context provider '{providerType.FullName}' from the "
                + "application service provider. If this DbContext was created through "
                + "AddDbContextPool<T>() or AddDbContextFactory<T>(), the application service provider "
                + "is captured once (usually the app's root provider), not per request — a provider "
                + $"registered with a scoped lifetime cannot be resolved from it. Register "
                + $"'{providerType.FullName}' as a singleton and read per-request state (e.g. "
                + "IHttpContextAccessor.HttpContext) inside GetChangeContext instead of injecting a "
                + "scoped dependency into its constructor. See the \"Pooled and factory-created "
                + "contexts\" section of docs/articles/configuration.md. The underlying resolution "
                + "error is the inner exception.", ex);
        }

        return resolved as IChangeContextProvider;
    }

    private static Func<object> CreateActivator(Type providerType)
    {
        if (providerType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Change context provider '{providerType.FullName}' is not registered on the "
                + "application service provider and has no parameterless constructor. Register it with "
                + "the DbContext's application service provider, or give it a parameterless constructor.");
        }

        return () => Activator.CreateInstance(providerType)!;
    }
}
