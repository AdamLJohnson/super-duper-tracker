using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Refit;
using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;
using DisneylandClient;
using DisneylandClient.Models;

var builder = Host.CreateApplicationBuilder(args);

// Configure Refit with case-insensitive JSON deserialization and string enum support.
var refitSettings = new RefitSettings
{
    ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    }),
};

builder.Services
    .AddRefitClient<IThemeParksApi>(refitSettings)
    .ConfigureHttpClient(c => c.BaseAddress = new Uri("https://api.themeparks.wiki/v1"));

var app = builder.Build();

IThemeParksApi api = app.Services.GetRequiredService<IThemeParksApi>();

// ── Ctrl+C: request graceful exit without immediate OS termination ────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let us call StopAsync ourselves
    cts.Cancel();
};

// ── State persisted across poll cycles ───────────────────────────────────────
var previousWaitTimes = new Dictionary<string, int>();
var previousLastUpdated = new Dictionary<string, DateTimeOffset>();
DateTimeOffset? lastRenderedUpdated = null;

// ── Polling loop ──────────────────────────────────────────────────────────────
while (!cts.IsCancellationRequested)
{
    // Fetch live data.
    EntityLiveDataResponse? disneyLand = null;
    try
    {
        disneyLand = await api.GetEntityLiveDataAsync("disneylandresort");
    }
    catch (Exception ex) when (!cts.IsCancellationRequested)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[red]  \u26a0  Error fetching data: {Markup.Escape(ex.Message)}[/]");
    }

    if (disneyLand is not null)
    {
        var allAttractions = disneyLand.LiveData.Where(e => e.EntityType == EntityType.ATTRACTION).OrderBy(x => x.Name).ToList();
        var referbishingAttractions = allAttractions.Where(e => e.Status == LiveStatusType.REFURBISHMENT).ToList();
        // Filter to Lightning Lane attractions only, sorted by descending standby wait.
        var llAttractions = allAttractions
            .Where(e => e.Queue?.HasLightningLane ?? false)
            .OrderByDescending(e => e.Queue?.Standby?.WaitTime ?? 0)
            .ToList();

        var maxUpdated = llAttractions.Count > 0
            ? llAttractions.Max(e => e.LastUpdated)
            : DateTimeOffset.UtcNow;

        // Only redraw when the API has returned data newer than the last render.
        if (maxUpdated != lastRenderedUpdated)
        {
            // Build the table using previous wait times for trend arrows,
            // then snapshot current wait times and LastUpdated before rendering.
            var table = BuildTable(llAttractions, previousWaitTimes, previousLastUpdated);
            foreach (var attraction in llAttractions)
            {
                previousWaitTimes[attraction.Id] = attraction.Queue?.Standby?.WaitTime ?? 0;
                previousLastUpdated[attraction.Id] = attraction.LastUpdated;
            }

            lastRenderedUpdated = maxUpdated;

            AnsiConsole.Clear();
            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine(
                $"[dim]  {llAttractions.Count} Lightning Lane attraction(s) \u00b7 Data as of {maxUpdated.ToLocalTime():h:mm:ss tt}[/]");

            AnsiConsole.MarkupLine("[dim]  Press R to refresh \u00b7 Q to quit[/]");
            AnsiConsole.WriteLine();
        }
    }

    // Wait up to 60 s, sampling for a keypress every 100 ms.
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Q) { cts.Cancel(); break; }
            if (key.Key == ConsoleKey.R) { break; } // skip remaining wait → re-fetch immediately
        }

        await Task.Delay(100);
    }
}

await app.StopAsync();

// ── Table builder (static local function) ─────────────────────────────────────
static Table BuildTable(List<EntityLiveData> llAttractions, IReadOnlyDictionary<string, int> previousWaitTimes, IReadOnlyDictionary<string, DateTimeOffset> previousLastUpdated)
{
    var table = new Table
    {
        Title = new TableTitle("⚡ Disneyland Resort \u2014 Lightning Lane Attractions"),
        Border = TableBorder.Rounded,
    };

    table.AddColumn(new TableColumn("[bold]Attraction Name[/]").LeftAligned());
    table.AddColumn(new TableColumn("[bold]Status[/]").Centered());
    table.AddColumn(new TableColumn("[bold]Standby Wait[/]").RightAligned());
    table.AddColumn(new TableColumn("[bold]Lightning Lane Time[/]").Centered());

    foreach (var attraction in llAttractions)
    {
        var statusMarkup = attraction.Status switch
        {
            LiveStatusType.OPERATING     => "[green]OPERATING[/]",
            LiveStatusType.DOWN          => "[red]DOWN[/]",
            LiveStatusType.CLOSED        => "[red]CLOSED[/]",
            LiveStatusType.REFURBISHMENT => "[yellow]REFURBISHMENT[/]",
            _                            => Markup.Escape(attraction.Status?.ToString() ?? "\u2014"),
        };

        var waitText = attraction.Queue?.Standby?.WaitTime is int mins ? $"{mins} min" : "N/A";

        if (attraction.Queue?.Standby?.WaitTime is int currentWait)
        {
            var lastUpdatedUnchanged =
                previousLastUpdated.TryGetValue(attraction.Id, out var prevLastUpdated) &&
                prevLastUpdated == attraction.LastUpdated;

            if (!lastUpdatedUnchanged && previousWaitTimes.TryGetValue(attraction.Id, out var prevWait))
            {
                // LastUpdated has changed: compute the trend fresh.
                if (currentWait > prevWait)      waitText += " [green]▼[/]";
                else if (currentWait < prevWait) waitText += " [red]▲[/]";
                else                             waitText += " [cyan1]-[/]";
            }
            else if (lastUpdatedUnchanged && previousWaitTimes.TryGetValue(attraction.Id, out var prevWaitUnchanged))
            {
                // LastUpdated is unchanged: reuse the trend from the previous cycle.
                if (currentWait > prevWaitUnchanged)      waitText += " [green]▼[/]";
                else if (currentWait < prevWaitUnchanged) waitText += " [red]▲[/]";
                else                                      waitText += " [cyan1]-[/]";
            }
            else
            {
                waitText += " [cyan1]-[/]";
            }
        }

        string returnWindow;

        if (attraction.Queue?.PaidReturnTime is { } paid)
        {
            if (paid.State == ReturnTimeState.AVAILABLE)
                returnWindow = paid.ReturnStart.HasValue
                    ? $"[gold1]{paid.ReturnStart.Value.ToLocalTime():h:mm tt}[/]"
                    : "\u2014";
            else if (paid.State == ReturnTimeState.FINISHED)
                returnWindow = $"[grey30]{paid.State}[/]";
            else
                returnWindow = $"[red]{paid.State}[/]";
        }
        else
        {
            var rt = attraction.Queue!.ReturnTime!;
            if (rt.State == ReturnTimeState.AVAILABLE)
                returnWindow = rt.ReturnStart.HasValue
                    ? $"[cyan1]{rt.ReturnStart.Value.ToLocalTime():h:mm tt}[/]"
                    : "\u2014";
            else if (rt.State == ReturnTimeState.FINISHED)
                returnWindow = $"[grey30]{rt.State}[/]";
            else
                returnWindow = $"[red]{rt.State}[/]";
        }

        table.AddRow(
            new Markup(Markup.Escape(attraction.Name)),
            new Markup(statusMarkup),
            new Markup(waitText),
            new Markup(returnWindow)
        );
    }

    return table;
}
