namespace ThemeParkPoller.ApiClient.Models;

public class SchedulePriceObject
{
    public string? Type { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public PriceData? Price { get; set; }
    public bool Available { get; set; }
}

public class ScheduleEntry
{
    public string Date { get; set; } = string.Empty;
    public DateTime? OpeningTime { get; set; }
    public DateTime? ClosingTime { get; set; }
    public string Type { get; set; } = string.Empty;
    public List<SchedulePriceObject> Purchases { get; set; } = [];
}

public class EntityScheduleResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public string? Timezone { get; set; }
    public List<ScheduleEntry> Schedule { get; set; } = [];

    /// <summary>
    /// Only included for destinations: lists all parks within the destination.
    /// </summary>
    public List<EntityScheduleResponse>? Parks { get; set; }
}

