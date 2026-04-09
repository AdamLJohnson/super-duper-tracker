namespace ThemeParkPoller.ApiClient.Models;

public class EntityData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public string? ParentId { get; set; }
    public string? DestinationId { get; set; }
    public string Timezone { get; set; } = string.Empty;
    public Location? Location { get; set; }
    public List<TagData> Tags { get; set; } = [];
}

public class EntityChild
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public string? ExternalId { get; set; }
    public string? ParentId { get; set; }
    public Location? Location { get; set; }
}

public class EntityChildrenResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public string? Timezone { get; set; }
    public List<EntityChild> Children { get; set; } = [];
}

