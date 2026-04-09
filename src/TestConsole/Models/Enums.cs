using System.Text.Json.Serialization;

namespace DisneylandClient.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntityType
{
    DESTINATION,
    PARK,
    ATTRACTION,
    RESTAURANT,
    HOTEL,
    SHOW,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LiveStatusType
{
    OPERATING,
    DOWN,
    CLOSED,
    REFURBISHMENT,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReturnTimeState
{
    AVAILABLE,
    TEMP_FULL,
    FINISHED,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoardingGroupState
{
    AVAILABLE,
    PAUSED,
    CLOSED,
}

