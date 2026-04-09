using System.Diagnostics;
using System.Text.Json.Serialization;

namespace DisneylandClient.Models;

public class StandbyQueue
{
    public int? WaitTime { get; set; }
}

public class SingleRiderQueue
{
    public int? WaitTime { get; set; }
}

public class ReturnTimeQueue
{
    public ReturnTimeState State { get; set; }
    public DateTimeOffset? ReturnStart { get; set; }
    public DateTimeOffset? ReturnEnd { get; set; }
}

public class PaidReturnTimeQueue : ReturnTimeQueue
{
    public PriceData? Price { get; set; }
}

public class BoardingGroupQueue
{
    public BoardingGroupState AllocationStatus { get; set; }
    public int? CurrentGroupStart { get; set; }
    public int? CurrentGroupEnd { get; set; }
    public DateTimeOffset? NextAllocationTime { get; set; }
    public int? EstimatedWait { get; set; }
}

public class PaidStandbyQueue
{
    public int? WaitTime { get; set; }
}

public class LiveQueue
{
    [JsonPropertyName("STANDBY")]
    public StandbyQueue? Standby { get; set; }

    [JsonPropertyName("SINGLE_RIDER")]
    public SingleRiderQueue? SingleRider { get; set; }

    [JsonPropertyName("RETURN_TIME")]
    public ReturnTimeQueue? ReturnTime { get; set; }

    [JsonPropertyName("PAID_RETURN_TIME")]
    public PaidReturnTimeQueue? PaidReturnTime { get; set; }

    [JsonPropertyName("BOARDING_GROUP")]
    public BoardingGroupQueue? BoardingGroup { get; set; }

    [JsonPropertyName("PAID_STANDBY")]
    public PaidStandbyQueue? PaidStandby { get; set; }

    public bool HasLightningLane => ReturnTime != null || PaidReturnTime != null;
}

public class LiveShowTime
{
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
}

public class DiningAvailability
{
    public int? PartySize { get; set; }
    public int? WaitTime { get; set; }
}

[DebuggerDisplay("{Name} ({EntityType}) - Status: {Status}, Last Updated: {LastUpdated}")]
public class EntityLiveData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public LiveStatusType? Status { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public LiveQueue? Queue { get; set; }
    public List<LiveShowTime> Showtimes { get; set; } = [];
    public List<LiveShowTime> OperatingHours { get; set; } = [];
    public List<DiningAvailability> DiningAvailability { get; set; } = [];

    public override string ToString()
    {
        return $"{Name} ({EntityType}) - Status: {Status}, Last Updated: {LastUpdated}";
    }
}

public class EntityLiveDataResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public EntityType EntityType { get; set; }
    public string? Timezone { get; set; }
    public List<EntityLiveData> LiveData { get; set; } = [];
}

