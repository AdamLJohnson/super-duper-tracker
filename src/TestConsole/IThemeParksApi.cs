using Refit;
using DisneylandClient.Models;

namespace DisneylandClient;

public interface IThemeParksApi
{
    /// <summary>Gets the list of all supported destinations.</summary>
    [Get("/destinations")]
    Task<DestinationsResponse> GetDestinationsAsync();

    /// <summary>Gets the full entity document for a given entity ID or slug.</summary>
    [Get("/entity/{entityId}")]
    Task<EntityData> GetEntityAsync(string entityId);

    /// <summary>Gets all children (parks, rides, etc.) that belong to this entity.</summary>
    [Get("/entity/{entityId}/children")]
    Task<EntityChildrenResponse> GetEntityChildrenAsync(string entityId);

    /// <summary>Gets live queue/show data for this entity and any child entities.</summary>
    [Get("/entity/{entityId}/live")]
    Task<EntityLiveDataResponse> GetEntityLiveDataAsync(string entityId);

    /// <summary>Gets this entity's schedule for the next 30 days.</summary>
    [Get("/entity/{entityId}/schedule")]
    Task<EntityScheduleResponse> GetEntityScheduleAsync(string entityId);

    /// <summary>Gets this entity's schedule for a specific year and zero-padded month.</summary>
    [Get("/entity/{entityId}/schedule/{year}/{month}")]
    Task<EntityScheduleResponse> GetEntityScheduleAsync(string entityId, int year, int month);
}

