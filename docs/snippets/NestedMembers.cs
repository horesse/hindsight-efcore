using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

// The sample project's Policy has no complex or owned members, so this page declares its own entity.
#region nested-member-types
public sealed class HomePolicy
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public InsuredAddress Address { get; set; } = new(); // complex property
    public BankAccount? PaymentAccount { get; set; }     // optional complex property
    public PolicyHolder? Holder { get; set; }            // owned reference
}

public sealed class InsuredAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
}

public sealed class BankAccount
{
    public string Iban { get; set; } = "";
}

public sealed class PolicyHolder
{
    public string Name { get; set; } = "";
    public string? Email { get; set; }
}
#endregion nested-member-types

public sealed class HomePolicyContext(DbContextOptions<HomePolicyContext> options) : DbContext(options)
{
    public DbSet<HomePolicy> HomePolicies => Set<HomePolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        #region configure-nested-members
        modelBuilder.Entity<HomePolicy>(policy =>
        {
            policy.ToTable("home_policies");
            policy.ComplexProperty(p => p.Address);        // columns Address_Street, Address_City, …
            policy.ComplexProperty(p => p.PaymentAccount); // column PaymentAccount_Iban
            policy.OwnsOne(p => p.Holder);                 // columns Holder_Name, Holder_Email

            policy.IsTemporal();                           // every one of those columns is versioned
        });
        #endregion configure-nested-members
    }
}

public static class NestedMemberQueries
{
    public static async Task QueryAsync(HomePolicyContext db, DateTimeOffset renewalDate)
    {
        #region query-nested-members
        var insuredInMinskAtRenewal = await db.HomePolicies
            .AsOf(renewalDate)
            .Where(p => p.Address.City == "Minsk")
            .Select(p => new { p.Number, Holder = p.Holder!.Name, p.PaymentAccount })
            .ToListAsync();
        #endregion query-nested-members
    }

    public static async Task DiffAsync(HomePolicyContext db, int policyId)
    {
        #region diff-nested-members
        var versions = await db.History<HomePolicy>()
            .Where(v => v.Entity.Id == policyId && v.Operation != VersionOperation.Delete)
            .OrderBy(v => v.ValidFrom)
            .ToListAsync();

        foreach (var change in db.Diff(versions[^2], versions[^1]))
        {
            // Path names the member: "Address.City", "Holder.Email", "Number".
            Console.WriteLine($"{change.Path}: {change.OldValue} → {change.NewValue}");
        }
        #endregion diff-nested-members
    }
}
