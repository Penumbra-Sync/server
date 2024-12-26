using MareSynchronos.API.Dto.User;
using Microsoft.VisualBasic.FileIO;
using Prometheus;
using System.Collections.Concurrent;
using System.Globalization;

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
    private Gauge? _gauge;

    public MareCensus(ILogger<MareCensus> logger)
    {
        _logger = logger;
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
        _logger.LogInformation("Loading XIVAPI data");

        using HttpClient client = new HttpClient();

        Dictionary<ushort, short> worldDcs = new();

        var dcs = await client.GetStringAsync("https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/WorldDCGroupType.csv", cancellationToken).ConfigureAwait(false);
        // dc: https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/WorldDCGroupType.csv
        // id, name, region

        using var dcsReader = new StringReader(dcs);
        using var dcsParser = new TextFieldParser(dcsReader);
        dcsParser.Delimiters = [","];
        // read 3 lines and discard
        dcsParser.ReadLine(); dcsParser.ReadLine(); dcsParser.ReadLine();

        while (!dcsParser.EndOfData)
        {
            var fields = dcsParser.ReadFields();
            var id = short.Parse(fields[0], CultureInfo.InvariantCulture);
            var name = fields[1];
            if (string.IsNullOrEmpty(name) || id == 0) continue;
            _logger.LogInformation("DC: ID: {id}, Name: {name}", id, name);
            _dcs[id] = name;
        }

        var worlds = await client.GetStringAsync("https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/World.csv", cancellationToken).ConfigureAwait(false);
        // world: https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/World.csv
        // id, internalname, name, region, usertype, datacenter, ispublic

        using var worldsReader = new StringReader(worlds);
        using var worldsParser = new TextFieldParser(worldsReader);
        worldsParser.Delimiters = [","];
        // read 3 lines and discard
        worldsParser.ReadLine(); worldsParser.ReadLine(); worldsParser.ReadLine();

        while (!worldsParser.EndOfData)
        {
            var fields = worldsParser.ReadFields();
            var id = ushort.Parse(fields[0], CultureInfo.InvariantCulture);
            var name = fields[1];
            var dc = short.Parse(fields[5], CultureInfo.InvariantCulture);
            var isPublic = bool.Parse(fields[6]);
            if (!_dcs.ContainsKey(dc) || !isPublic) continue;
            _worlds[id] = (name, dc);
            _logger.LogInformation("World: ID: {id}, Name: {name}, DC: {dc}", id, name, dc);
        }

        var races = await client.GetStringAsync("https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/Race.csv", cancellationToken).ConfigureAwait(false);
        // race: https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/Race.csv
        // id, masc name, fem name, other crap I don't care about

        using var raceReader = new StringReader(races);
        using var raceParser = new TextFieldParser(raceReader);
        raceParser.Delimiters = [","];
        // read 3 lines and discard
        raceParser.ReadLine(); raceParser.ReadLine(); raceParser.ReadLine();

        while (!raceParser.EndOfData)
        {
            var fields = raceParser.ReadFields();
            var id = short.Parse(fields[0], CultureInfo.InvariantCulture);
            var name = fields[1];
            if (string.IsNullOrEmpty(name) || id == 0) continue;
            _races[id] = name;
            _logger.LogInformation("Race: ID: {id}, Name: {name}", id, name);
        }

        var tribe = await client.GetStringAsync("https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/Tribe.csv", cancellationToken).ConfigureAwait(false);
        // tribe: https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/Tribe.csv
        // id masc name, fem name, other crap I don't care about

        using var tribeReader = new StringReader(tribe);
        using var tribeParser = new TextFieldParser(tribeReader);
        tribeParser.Delimiters = [","];
        // read 3 lines and discard
        tribeParser.ReadLine(); tribeParser.ReadLine(); tribeParser.ReadLine();

        while (!tribeParser.EndOfData)
        {
            var fields = tribeParser.ReadFields();
            var id = short.Parse(fields[0], CultureInfo.InvariantCulture);
            var name = fields[1];
            if (string.IsNullOrEmpty(name) || id == 0) continue;
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
