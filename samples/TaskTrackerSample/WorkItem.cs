namespace TaskTrackerSample;

public enum WorkItemStatus
{
    Todo,
    InProgress,
    Done,
    Cancelled,
}

public sealed class WorkItem
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public WorkItemStatus Status { get; set; }
    public string? AssignedTo { get; set; }

    /// <summary>Excluded from versioning — changes to this property alone do not create a history row.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
