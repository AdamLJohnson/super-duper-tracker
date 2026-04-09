namespace DisneylandClient.Models;

public class DestinationParkEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class DestinationEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public List<DestinationParkEntry> Parks { get; set; } = [];
}

public class DestinationsResponse
{
    public List<DestinationEntry> Destinations { get; set; } = [];
}

