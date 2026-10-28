using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Web;
using SPTarkov.Server.Web.Models.Configs;
using SPTarkov.Server.Web.Services;

namespace ZombieHorde;

// SPT 4.1 replaced AbstractModMetadata + IModWebMetadata with a single IModMetadata
// interface. IsBundleMod is gone; HasPrepatcher is new (unrelated to this mod).
public record ZombieHordeMetadata : IModMetadata
// IModBlazorMetadata registers this mod on the SIC Mod Pages list.
public record ZombieHordeMetadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.vonbraunz.roamingzombies";
    public string Name { get; init; } = "Roaming Zombies";
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";

    // SIC Mod Pages entry (separate from Config Editor registration below)
    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/roaming-zombies";
    public string? HomePageDescription { get; init; } = "Enable/disable hordes and tune spawn rates";
}


/// <summary>
/// Mutable config so SIC's Config Editor can copy property values onto the live instance.
/// Do not try to drive PMC/Scav/boss faction chances from here — ABPS owns that surface.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ZombieHordeConfig
{
    /// <summary>Master toggle. When false, no infected spawns are injected.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("hordeSize")]
    public HordeSizeConfig HordeSize { get; set; } = new() { Min = 2, Max = 4 };

    /// <summary>
    /// Count of pistol (BotDifficulty.hard / EZombieMode.Shooting) zombies to spawn
    /// per infected type, in addition to the melee (normal) zombies. Set min=max=0 to disable.
    /// </summary>
    [JsonPropertyName("pistolHordeSize")]
    public HordeSizeConfig PistolHordeSize { get; set; } = new() { Min = 1, Max = 2 };

    [JsonPropertyName("spawnDelaySeconds")]
    public int SpawnDelaySeconds { get; set; } = 120;

    [JsonPropertyName("spawnChance")]
    public Dictionary<string, int> SpawnChance { get; set; } = new()
    {
        ["bigmap"] = 70,
        ["factory4_night"] = 100,
        ["interchange"] = 75,
        ["laboratory"] = 100,
        ["lighthouse"] = 40,
        ["rezervbase"] = 65,
        ["sandbox"] = 65,
        ["sandbox_high"] = 65,
        ["shoreline"] = 35,
        ["tarkovstreets"] = 80,
        ["woods"] = 30,
        ["labyrinth"] = 80
    };

    [JsonPropertyName("ignoreMaxBots")]
    public bool IgnoreMaxBots { get; set; } = true;

    /// <summary>
    /// When true, zombies always spawn on every raid regardless of spawnChance.
    /// Also sets ForceSpawn=true to bypass bot limits.
    /// </summary>
    [JsonPropertyName("alwaysSpawn")]
    public bool AlwaysSpawn { get; set; } = false;

    /// <summary>
    /// Multiplies each map's spawnChance (clamped 0–100). 1.0 = unchanged.
    /// Does not affect alwaysSpawn.
    /// </summary>
    [JsonPropertyName("spawnChanceMultiplier")]
    public double SpawnChanceMultiplier { get; set; } = 1.0;

    public void CopyFrom(ZombieHordeConfig other)
    {
        Enabled = other.Enabled;
        SpawnDelaySeconds = other.SpawnDelaySeconds;
        IgnoreMaxBots = other.IgnoreMaxBots;
        AlwaysSpawn = other.AlwaysSpawn;
        SpawnChanceMultiplier = other.SpawnChanceMultiplier;
        HordeSize = new HordeSizeConfig { Min = other.HordeSize.Min, Max = other.HordeSize.Max };
        PistolHordeSize = new HordeSizeConfig { Min = other.PistolHordeSize.Min, Max = other.PistolHordeSize.Max };
        SpawnChance = new Dictionary<string, int>(other.SpawnChance);
    }
}

public class HordeSizeConfig
{
    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
}

/// <summary>
/// Singleton service that owns the loaded config and can re-inject zombie
/// BossLocationSpawn entries on demand — called at startup and after each raid end.
/// BossLocationSpawn entries on demand — called at startup, after each raid end,
/// and when SIC applies a config change.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ZombieSpawnService(
    ISptLogger<ZombieSpawnService> logger,
    LocationTable locationTable,
    RandomUtil randomUtil)
    RandomUtil randomUtil,
    ZombieHordeConfig config)
{
    public static readonly Dictionary<string, string> MapZones = new()
    {
        ["labyrinth"]      = "BotZone"
    };

    private static readonly string[] ZombieTypes =
    internal static readonly string[] ZombieTypes =
    [
        "infectedAssault",
        "infectedPmc",
        "infectedCivil",
        "infectedLaborant"
    ];

    private ZombieHordeConfig? _config;
    private static readonly HashSet<string> ZombieTypeSet = new(ZombieTypes, StringComparer.OrdinalIgnoreCase);

    private bool _loaded;

    public void Initialize(ZombieHordeConfig config)
    public ZombieHordeConfig Config => config;

    public void MarkLoaded() => _loaded = true;

    /// <summary>
    /// Removes only our infected BossLocationSpawn rows. Leaves ABPS / vanilla / other
    /// faction entries untouched — we never edit PMC/Scav/boss chances.
    /// </summary>
    public void ClearZombieSpawns()
    {
        _config = config;
        var locationDict = locationTable.GetDictionary();
        var removed = 0;

        foreach (var map in MapZones.Keys)
        {
            var actualKey = locationTable.GetMappedKey(map);
            if (!locationDict.TryGetValue(actualKey, out var location))
                continue;

            var list = location.Base.BossLocationSpawn;
            removed += list.RemoveAll(spawn =>
                spawn.BossName != null && ZombieTypeSet.Contains(spawn.BossName));
        }

        if (removed > 0)
            logger.Info($"[RoamingZombies] Cleared {removed} infected BossLocationSpawn entries");
    }

    public void InjectSpawns()
    {
        if (_config == null)
        if (!_loaded)
        {
            logger.Warning("[RoamingZombies] InjectSpawns called before config was loaded — skipping");
            return;
        }

        // Always strip our previous rows first so SIC re-apply and post-raid re-inject
        // cannot stack duplicate infected waves. ABPS wipe already empties the list;
        // clearing infected-only is still safe and leaves ABPS rows alone.
        ClearZombieSpawns();

        if (!config.Enabled)
        {
            logger.Info("[RoamingZombies] Disabled via config — no zombie spawns injected");
            return;
        }

        var locationDict = locationTable.GetDictionary();
        var totalMaps = 0;
        var multiplier = Math.Clamp(config.SpawnChanceMultiplier, 0.0, 10.0);

        foreach (var (map, zone) in MapZones)
        {
            if (!_config.SpawnChance.TryGetValue(map, out var chance))
            if (!config.SpawnChance.TryGetValue(map, out var baseChance))
                continue;

            var chance = (int)Math.Clamp(Math.Round(baseChance * multiplier), 0, 100);

            // Roll dice server-side so the percentage actually works.
            // alwaysSpawn bypasses the roll entirely and forces spawns.
            if (!_config.AlwaysSpawn)
            if (!config.AlwaysSpawn)
            {
                if (chance <= 0)
                    continue;
            for (int wave = 0; wave < WaveCount; wave++)
            {
                // First wave at SpawnDelaySeconds, each subsequent wave WaveInterval later.
                var waveTime = _config.SpawnDelaySeconds + (wave * WaveIntervalSeconds);
                var waveTime = config.SpawnDelaySeconds + (wave * WaveIntervalSeconds);

                foreach (var zombieType in ZombieTypes)
                {
                    // Melee zombies (BotDifficulty.normal → EZombieMode.Fast → knife).
                    // Full hordeSize count.
                    var meleeCount = randomUtil.GetInt(_config.HordeSize.Min, _config.HordeSize.Max + 1);
                    var meleeCount = randomUtil.GetInt(config.HordeSize.Min, config.HordeSize.Max + 1);
                    location.Base.BossLocationSpawn.Add(new BossLocationSpawn
                    {
                        BossName             = zombieType,
                        Delay                = 0,
                        DependKarma          = false,
                        DependKarmaPVE       = false,
                        ForceSpawn           = _config.AlwaysSpawn,
                        IgnoreMaxBots        = _config.IgnoreMaxBots,
                        ForceSpawn           = config.AlwaysSpawn,
                        IgnoreMaxBots        = config.IgnoreMaxBots,
                        SpawnMode            = null,
                        Supports             = null!,
                        Time                 = waveTime,
                    });

                    // Pistol zombies (BotDifficulty.hard → EZombieMode.Shooting → Makarov).
                    // Smaller count for variety — these use shootFromPlace / attackMoving
                    // decisions which go through EFT's standard bot logic nodes and should
                    // attack natively without our ForceKnifeKick workaround.
                    var pistolCount = randomUtil.GetInt(_config.PistolHordeSize.Min, _config.PistolHordeSize.Max + 1);
                    var pistolCount = randomUtil.GetInt(config.PistolHordeSize.Min, config.PistolHordeSize.Max + 1);
                    if (pistolCount > 0)
                    {
                        location.Base.BossLocationSpawn.Add(new BossLocationSpawn
                            Delay                = 0,
                            DependKarma          = false,
                            DependKarmaPVE       = false,
                            ForceSpawn           = _config.AlwaysSpawn,
                            IgnoreMaxBots        = _config.IgnoreMaxBots,
                            ForceSpawn           = config.AlwaysSpawn,
                            IgnoreMaxBots        = config.IgnoreMaxBots,
                            SpawnMode            = null,
                            Supports             = null!,
                            Time                 = waveTime,
public class ZombieHordeServer(
    ISptLogger<ZombieHordeServer> logger,
    ZombieSpawnService spawnService,
    ZombieHordeConfig config,
    ModHelper modHelper,
    JsonUtil jsonUtil) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath  = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config   = await jsonUtil.DeserializeFromFileAsync<ZombieHordeConfig>(Path.Combine(modPath, "config.json"));
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var path = Path.Combine(modPath, "config.json");
        var loaded = await jsonUtil.DeserializeFromFileAsync<ZombieHordeConfig>(path);

        if (config == null)
        if (loaded == null)
        {
            logger.Error("[RoamingZombies] Failed to load config.json");
            return;
            logger.Error("[RoamingZombies] Failed to load config.json — using in-memory defaults");
        }
        else
        {
            config.CopyFrom(loaded);
        }

        spawnService.Initialize(config);
        spawnService.MarkLoaded();
        spawnService.InjectSpawns();
    }
}
        ])
{ }

public record ZombieHordeConfig
/// <summary>
/// Registers config.json with SPT's SIC Config Editor (Mod Configs tab).
/// </summary>
[Injectable]
public class ZombieHordeConfigEditorProvider(
    ZombieHordeConfig config,
    ZombieSpawnService spawnService,
    ModHelper modHelper) : IConfigEditorConfigProvider
{
    [JsonPropertyName("hordeSize")]
    public required HordeSizeConfig HordeSize { get; set; }

    /// <summary>
    /// Count of pistol (BotDifficulty.hard / EZombieMode.Shooting) zombies to spawn
    /// per infected type, in addition to the melee (normal) zombies. Set min=max=0 to disable.
    /// </summary>
    [JsonPropertyName("pistolHordeSize")]
    public HordeSizeConfig PistolHordeSize { get; set; } = new HordeSizeConfig { Min = 1, Max = 2 };

    [JsonPropertyName("spawnDelaySeconds")]
    public int SpawnDelaySeconds { get; set; }

    [JsonPropertyName("spawnChance")]
    public required Dictionary<string, int> SpawnChance { get; set; }

    [JsonPropertyName("ignoreMaxBots")]
    public bool IgnoreMaxBots { get; set; } = true;

    /// <summary>
    /// When true, zombies always spawn on every raid regardless of spawnChance.
    /// Also sets ForceSpawn=true to bypass bot limits.
    /// </summary>
    [JsonPropertyName("alwaysSpawn")]
    public bool AlwaysSpawn { get; set; } = false;
}
    public IEnumerable<ConfigEditorConfigRegistration> GetConfigs()
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var filePath = Path.Combine(modPath, "config.json");

public record HordeSizeConfig
{
    [JsonPropertyName("min")]
    public int Min { get; set; }
        // After SIC copies edited JSON onto the live singleton, rebuild spawn tables.
        yield return ConfigEditorConfigRegistration.Create(
            id: "com.vonbraunz.roamingzombies",
            displayName: "Roaming Zombies",
            runtimeConfig: config,
            filePath: filePath) with
        {
            OnAppliedToRuntimeAsync = (edited, cancellationToken) =>
            {
                if (edited is ZombieHordeConfig updated)
                    config.CopyFrom(updated);

    [JsonPropertyName("max")]
    public int Max { get; set; }
                spawnService.InjectSpawns();
                return ValueTask.CompletedTask;
            }
        };
    }
}
