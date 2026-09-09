namespace InsuranceSample;

public enum PolicyStatus
{
    Draft,
    Active,
    Suspended,
    Cancelled,
}

public sealed class Policy
{
    public int Id { get; set; }
    public required string Number { get; set; }
    public PolicyStatus Status { get; set; }
    public decimal Premium { get; set; }
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Excluded from versioning — changes to this property alone do not create a history row.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
