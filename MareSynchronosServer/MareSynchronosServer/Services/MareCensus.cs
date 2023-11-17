using MareSynchronos.API.Dto.User;
using Prometheus;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MareSynchronosServer.Services;

public class MareCensus : IHostedService
{
    private record CensusEntry(ushort WorldId, short Race, short Subrace, short Gender)
    {
        public static CensusEntry FromDto(CensusDataDto dto)
        {
            return new CensusEntry(dto.WorldId, dto.RaceId, dto.TribeId, dto.Gender);
        }
    }

    private readonly ConcurrentDictionary<string, CensusEntry> _censusEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<short, string> _dcs = new();
    private readonly Dictionary<short, string> _gender = new();
    private readonly ILogger<MareCensus> _logger;
    private readonly Dictionary<short, string> _races = new();
    private readonly Dictionary<short, string> _tribes = new();
    private readonly Dictionary<ushort, (string, short)> _worlds = new();
    private readonly string _xivApiKey;
    private Gauge? _gauge;

    public MareCensus(ILogger<MareCensus> logger, string xivApiKey)
    {
        _logger = logger;
        _xivApiKey = xivApiKey;
    }

    private bool Initialized => _gauge != null;

    public void ClearStatistics(string uid)
    {
        if (!Initialized) return;

        if (_censusEntries.Remove(uid, out var censusEntry))
        {
            ModifyGauge(censusEntry, increase: false);
        }
    }

    public void PublishStatistics(string uid, CensusDataDto? censusDataDto)
    {
        if (!Initialized || censusDataDto == null) return;

        var newEntry = CensusEntry.FromDto(censusDataDto);

        if (_censusEntries.TryGetValue(uid, out var entry))
        {
            if (entry != newEntry)
            {
                ModifyGauge(entry, increase: false);
                ModifyGauge(newEntry, increase: true);
                _censusEntries[uid] = newEntry;
            }
        }
        else
        {
            _censusEntries[uid] = newEntry;
            ModifyGauge(newEntry, increase: true);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_xivApiKey)) return;

        _logger.LogInformation("Loading XIVAPI data");

        using HttpClient client = new HttpClient();

        Dictionary<ushort, short> worldDcs = new();
        var dcs = await client.GetStringAsync("https://xivapi.com/worlddcgrouptype?private_key" + _xivApiKey, cancellationToken).ConfigureAwait(false);
        using var dcsJson = JsonSerializer.Deserialize<JsonElement>(dcs).GetProperty("Results").EnumerateArray();

        foreach (var dcValue in dcsJson)
        {
            var id = dcValue.GetProperty("ID").GetInt16();
            var name = dcValue.GetProperty("Name").GetString();
            _dcs[id] = name;
            _logger.LogInformation("DC: ID: {id}, Name: {name}", id, name);
            var dcData = await client.GetStringAsync("https://xivapi.com/worlddcgrouptype/" + id.ToString(CultureInfo.InvariantCulture) + "?private_key=" + _xivApiKey, cancellationToken).ConfigureAwait(false);
            if (JsonSerializer.Deserialize<JsonElement>(dcData).TryGetProperty("GameContentLinks", out var gameContentLinks))
            {
                if (gameContentLinks.ValueKind == JsonValueKind.Object && gameContentLinks.TryGetProperty("World", out var worldProp))
                {
                    using var json = worldProp.GetProperty("DataCenter").EnumerateArray();
                    foreach (var world in json)
                    {
                        worldDcs[(ushort)world.GetInt32()] = id;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        var worlds = await client.GetStringAsync("https://xivapi.com/world?limit=1000&private_key" + _xivApiKey, cancellationToken).ConfigureAwait(false);
        using var worldJson = JsonSerializer.Deserialize<JsonElement>(worlds).GetProperty("Results").EnumerateArray();
        foreach (var worldValue in worldJson)
        {
            var id = (ushort)worldValue.GetProperty("ID").GetInt32();
            var name = worldValue.GetProperty("Name").GetString();
            if (worldDcs.TryGetValue(id, out var dc))
            {
                _worlds[(ushort)id] = (name, dc);
                _logger.LogInformation("World: ID: {id}, Name: {name}, DC: {dc}", id, name, dc);
            }
        }

        var races = await client.GetStringAsync("https://xivapi.com/race?private_key" + _xivApiKey, cancellationToken).ConfigureAwait(false);
        using var racesJson = JsonSerializer.Deserialize<JsonElement>(races).GetProperty("Results").EnumerateArray();
        foreach (var racesValue in racesJson)
        {
            var id = racesValue.GetProperty("ID").GetInt16();
            var name = racesValue.GetProperty("Name").GetString();
            _races[id] = name;
            _logger.LogInformation("Race: ID: {id}, Name: {name}", id, name);
        }

        var tribe = await client.GetStringAsync("https://xivapi.com/tribe?private_key=" + _xivApiKey, cancellationToken).ConfigureAwait(false);
        using var tribeJson = JsonSerializer.Deserialize<JsonElement>(tribe).GetProperty("Results").EnumerateArray();
        foreach (var tribeValue in tribeJson)
        {
            var id = tribeValue.GetProperty("ID").GetInt16();
            var name = tribeValue.GetProperty("Name").GetString();
            _tribes[id] = name;
            _logger.LogInformation("Tribe: ID: {id}, Name: {name}", id, name);
        }

        _gender[0] = "Male";
        _gender[1] = "Female";

        _gauge = Metrics.CreateGauge("mare_census", "mare informational census data", new[] { "dc", "world", "gender", "race", "subrace" });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ModifyGauge(CensusEntry censusEntry, bool increase)
    {
        var subraceSuccess = _tribes.TryGetValue(censusEntry.Subrace, out var subrace);
        var raceSuccess = _races.TryGetValue(censusEntry.Race, out var race);
        var worldSuccess = _worlds.TryGetValue(censusEntry.WorldId, out var world);
        var genderSuccess = _gender.TryGetValue(censusEntry.Gender, out var gender);
        if (subraceSuccess && raceSuccess && worldSuccess && genderSuccess && _dcs.TryGetValue(world.Item2, out var dc))
        {
            if (increase)
                _gauge.WithLabels(dc, world.Item1, gender, race, subrace).Inc();
            else
                _gauge.WithLabels(dc, world.Item1, gender, race, subrace).Dec();
        }
    }
}
