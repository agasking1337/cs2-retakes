using CounterStrikeSharp.API;
using System;
using System.Linq;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesPlugin.Modules;
using RetakesPluginShared.Enums;
using RetakesPlugin.Modules.Configs;
using RetakesPlugin.Modules.Managers;
using RetakesPluginShared;
using RetakesPluginShared.Events;
using Helpers = RetakesPlugin.Modules.Helpers;
using T3MenuSharedApi;

namespace RetakesPlugin;

[MinimumApiVersion(335)]
public class RetakesPlugin : BasePlugin
{
    private const string Version = "2.1.6";

    #region Plugin info
    public override string ModuleName => "Retakes Plugin";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "B3none";
    public override string ModuleDescription => "https://github.com/b3none/cs2-retakes";
    #endregion

    #region Constants
    public static readonly string LogPrefix = $"[Retakes {Version}] ";

    // These two static variables are overwritten in the Load / OnMapStart with config values.
    public static string MessagePrefix = $"[{ChatColors.Green}Retakes{ChatColors.White}] ";
    public static bool IsDebugMode;
    #endregion

    private IT3MenuManager? _menuManager;

    #region Helpers
    private Translator _translator;
    private GameManager? _gameManager;
    private SpawnManager? _spawnManager;
    private BreakerManager? _breakerManager;
    private PlayerPrefsManager? _playerPrefs;
    private readonly HashSet<ulong> _openSpawnMenuNextRound = [];
    private readonly HashSet<ulong> _menuOpenedThisRound = [];

    public static PluginCapability<IRetakesPluginEventSender> RetakesPluginEventSenderCapability { get; } = new("retakes_plugin:event_sender");
    #endregion

    #region Configs
    private MapConfig? _mapConfig;
    private RetakesConfig? _retakesConfig;
    #endregion

    #region State
    private Bombsite _currentBombsite = Bombsite.A;
    private CCSPlayerController? _planter;
    private CsTeam _lastRoundWinner = CsTeam.None;
    private Bombsite? _showingSpawnsForBombsite;
    private string? _showingGroup;
    private Bombsite? _forcedBombsite;

    // TODO: We should really store this in SQLite, but for now we'll just store it in memory.
    private readonly HashSet<CCSPlayerController> _hasMutedVoices = [];

    private void ResetState()
    {
        _currentBombsite = Bombsite.A;
        _planter = null;
        _lastRoundWinner = CsTeam.None;
        _showingSpawnsForBombsite = null;
        _showingGroup = null;
    }

    [ConsoleCommand("css_spawns", "Arm next-round CT spawn selection menu (VIP only).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandPlayerSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (!RetakesConfig.IsLoaded(_retakesConfig) || _retakesConfig!.RetakesConfigData == null)
        {
            player.PrintToChat($"{MessagePrefix}Config not loaded.");
            return;
        }

        var cfg = _retakesConfig.RetakesConfigData;
        if (!cfg.EnablePlayerSpawnGroupChange)
        {
            player.PrintToChat($"{MessagePrefix}Spawn selection is disabled.");
            return;
        }

        var tokens = (cfg.AllowSpawnChangePermission ?? "").Split(',').Select(t => (t ?? string.Empty).Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.StartsWith("@") ? t : "@" + t).ToArray();
        var authorized = tokens.Length == 0 || tokens.Any(flag => AdminManager.PlayerHasPermissions(player, flag));
        if (!authorized)
        {
            player.PrintToChat($"{MessagePrefix}You are not allowed to choose spawns.");
            return;
        }

        if (_openSpawnMenuNextRound.Contains(player.SteamID))
        {
            _openSpawnMenuNextRound.Remove(player.SteamID);
            player.PrintToChat($"{MessagePrefix}Spawn selection auto-open disabled.");
            return;
        }

        _openSpawnMenuNextRound.Add(player.SteamID);
        var teamMsg = player.Team == CsTeam.CounterTerrorist ? "It will open each round." : "It will only open if you are CT.";
        player.PrintToChat($"{MessagePrefix}Spawn selection auto-open enabled. {teamMsg}");
    }

    private void OpenPlayerSpawnGroupMenu(CCSPlayerController player, Bombsite bombsite)
    {
        if (_menuManager == null)
        {
            player.PrintToChat($"{MessagePrefix}T3Menu-API not found.");
            return;
        }

        if (player.Team != CsTeam.CounterTerrorist)
        {
            player.PrintToChat($"{MessagePrefix}Spawn selection is available to CT only.");
            return;
        }

        var mapName = Server.MapName;
        var allSpawns = _mapConfig?.GetSpawnsClone() ?? new List<Spawn>();
        var ctSpawns = allSpawns.Where(s => s.Team == CsTeam.CounterTerrorist && s.Bombsite == bombsite).ToList();

        var menu = _menuManager.CreateMenu($"Choose Spawn — {mapName}", true);

        // Reset option
        menu.AddOption("Auto (no preference)", (p, o) =>
        {
            _playerPrefs?.SetSpawnId(p.SteamID, mapName, null);
            p.PrintToChat($"{MessagePrefix}Spawn selection cleared.");
        });

        foreach (var s in ctSpawns.OrderBy(s => s.Bombsite).ThenBy(s => string.IsNullOrWhiteSpace(s.Name) ? $"Spawn {s.Id}" : s.Name))
        {
            var display = string.IsNullOrWhiteSpace(s.Name) ? $"Spawn {s.Id}" : s.Name!;
            var site = s.Bombsite == Bombsite.A ? "A" : "B";
            var label = $"{display} [{site}]";
            var spawnId = s.Id;
            var pos = s.Vector;
            var ang = s.QAngle;
            menu.AddOption(label, (p, o) =>
            {
                _playerPrefs?.SetSpawnId(p.SteamID, mapName, spawnId);
                // Teleport instantly to the chosen spawn
                if (Helpers.IsValidPlayer(p) && p.PawnIsAlive)
                {
                    p.Pawn.Value!.Teleport(pos, ang, new Vector());
                }
                p.PrintToChat($"{MessagePrefix}Spawn set to {label}.");
            });
        }

        _menuManager.OpenMainMenu(player, menu);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _menuManager ??= new PluginCapability<IT3MenuManager>("t3menu:manager").Get();
    }
    #endregion

    public RetakesPlugin()
    {
        _translator = new Translator(Localizer);
    }

    public override void Load(bool hotReload)
    {
        _translator = new Translator(Localizer);

        MessagePrefix = _translator["retakes.prefix"];

        Helpers.Debug($"Plugin loaded!");

        // Initialize player preferences storage
        _playerPrefs = new PlayerPrefsManager(ModuleDirectory);

        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            OnMapStart(mapName);
        });

        AddCommandListener("jointeam", OnCommandJoinTeam);

        var retakesPluginEventSender = new RetakesPluginEventSender();
        Capabilities.RegisterPluginCapability(RetakesPluginEventSenderCapability, () => retakesPluginEventSender);

        if (hotReload)
        {
            Server.PrintToChatAll($"{LogPrefix}Update detected, restarting map...");
            Server.ExecuteCommand($"map {Server.MapName}");
        }
    }

    #region Commands
    [ConsoleCommand("css_mapconfig", "Forces a specific map config file to load.")]
    [ConsoleCommand("css_setmapconfig", "Forces a specific map config file to load.")]
    [ConsoleCommand("css_loadmapconfig", "Forces a specific map config file to load.")]
    [CommandHelper(minArgs: 1, usage: "[filename]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandMapConfig(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var mapConfigDirectory = Path.Combine(ModuleDirectory, "map_config");

        if (!Directory.Exists(mapConfigDirectory))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No map configs found.");
            return;
        }

        var mapConfigFileName = commandInfo.GetArg(1).Trim().Replace(".json", "");

        var mapConfigFilePath = Path.Combine(mapConfigDirectory, $"{mapConfigFileName}.json");

        if (!File.Exists(mapConfigFilePath))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config file not found.");
            return;
        }

        OnMapStart(Server.MapName, mapConfigFileName);

        commandInfo.ReplyToCommand($"{MessagePrefix}The new map config has been successfully loaded.");
    }

    [ConsoleCommand("css_mapconfigs", "Displays a list of available map configs.")]
    [ConsoleCommand("css_viewmapconfigs", "Displays a list of available map configs.")]
    [ConsoleCommand("css_listmapconfigs", "Displays a list of available map configs.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandMapConfigs(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var mapConfigDirectory = Path.Combine(ModuleDirectory, "map_config");

        var files = Directory.GetFiles(mapConfigDirectory);

        // organise files alphabetically
        Array.Sort(files);

        if (!Directory.Exists(mapConfigDirectory) || files.Length == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No map configs found.");
            return;
        }

        foreach (var file in files)
        {
            var transformedFile = file
                .Replace($"{mapConfigDirectory}/", "")
                .Replace(".json", "");

            commandInfo.ReplyToCommand($"{MessagePrefix}!mapconfig {transformedFile}");
            player?.PrintToConsole($"{MessagePrefix}!mapconfig {transformedFile}");
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}A list of available map configs has been outputted above.");
    }

    [ConsoleCommand("css_forcebombsite", "Force the retakes to occur from a single bombsite.")]
    [CommandHelper(minArgs: 1, usage: "[A/B]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandForceBombsite(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        var bombsite = commandInfo.GetArg(1).ToUpper();
        if (bombsite != "A" && bombsite != "B")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B].");
            return;
        }

        _forcedBombsite = bombsite == "A" ? Bombsite.A : Bombsite.B;

        commandInfo.ReplyToCommand($"{MessagePrefix}The bombsite will now be forced to {_forcedBombsite}.");
    }

    [ConsoleCommand("css_forcebombsitestop", "Clear the forced bombsite and return back to normal.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandForceBombsiteStop(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        _forcedBombsite = null;

        commandInfo.ReplyToCommand($"{MessagePrefix}The bombsite will no longer be forced.");
    }

    [ConsoleCommand("css_showspawns", "Show the spawns for the specified bombsite.")]
    [ConsoleCommand("css_editspawns", "Open the spawns edit menu (admin).")]
    [ConsoleCommand("css_edit", "Show the spawns for the specified bombsite.")]
    [CommandHelper(minArgs: 0, usage: "[A/B] [group]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandShowSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (string.Equals(commandInfo.GetArg(0), "css_editspawns", StringComparison.OrdinalIgnoreCase) && commandInfo.ArgCount < 2)
        {
            if (_menuManager == null)
            {
                commandInfo.ReplyToCommand($"{MessagePrefix}T3Menu-API not found.");
                return;
            }
            OpenSpawnsRootMenu(player!);
            return;
        }

        var bombsite = commandInfo.GetArg(1).ToUpper();
        if (bombsite != "A" && bombsite != "B")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B].");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        _showingSpawnsForBombsite = bombsite == "A" ? Bombsite.A : Bombsite.B;

        // Optional group filter as second argument
        _showingGroup = null;
        if (commandInfo.ArgCount >= 3)
        {
            var groupArg = commandInfo.GetArg(2).Trim();
            if (!string.IsNullOrWhiteSpace(groupArg))
            {
                var resolved = ResolveGroupSlug(groupArg);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    commandInfo.ReplyToCommand($"{MessagePrefix}Group '{groupArg}' not found. Use css_listgroups.");
                    return;
                }
                _showingGroup = resolved;
            }
        }

        Helpers.SetWorldTextFacingPlayer(player);
        if (_mapConfig != null)
        {
            Helpers.SetGroupDisplayNames(_mapConfig.GetGroupsClone());
        }

        // This will fire the OnRoundStart event listener
        Server.ExecuteCommand("mp_warmup_start");
        Server.ExecuteCommand("mp_warmuptime 120");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        StartSpawnTextFacingLoop(player);
    }

    private void OpenSpawnsRootMenu(CCSPlayerController player)
    {
        if (_menuManager == null) return;
        var menu = _menuManager.CreateMenu("Spawns", false);

        menu.AddOption("Site Spawns", (p, o) => BuildSiteSpawnsMenu(p));
        menu.AddOption("Team Spawns", (p, o) => BuildTeamSpawnsMenu(p));
        menu.AddOption("Group Spawns", (p, o) => BuildGroupSpawnsMenu(p));

        _menuManager.OpenMainMenu(player, menu);
    }

    private void BuildSiteSpawnsMenu(CCSPlayerController player)
    {
        if (_menuManager == null) return;
        var menu = _menuManager.CreateMenu("Site Spawns", true);

        menu.AddOption("Show A", (p, o) => ShowSpawnsFromMenu(p, "A"));
        menu.AddOption("Show B", (p, o) => ShowSpawnsFromMenu(p, "B"));
        menu.AddOption("Hide", (p, o) => HideSpawnsFromMenu(p));

        _menuManager.OpenMainMenu(player, menu);
    }

    private void BuildTeamSpawnsMenu(CCSPlayerController player)
    {
        if (_menuManager == null) return;
        var menu = _menuManager.CreateMenu("Team Spawns", true);

        var teams = new List<object> { "T", "CT" };
        var sites = new List<object> { "A", "B" };
        object selectedTeam = teams[0];
        object selectedSite = sites[0];

        menu.AddSliderOption("Team", teams, teams[0], 2, (p, o, i) =>
        {
            if (o is IT3Option so && so.DefaultValue != null) selectedTeam = so.DefaultValue;
        });
        menu.AddSliderOption("Site", sites, sites[0], 2, (p, o, i) =>
        {
            if (o is IT3Option so && so.DefaultValue != null) selectedSite = so.DefaultValue;
        });

        menu.AddOption("Show", (p, o) =>
        {
            ShowSpawnsFromMenu(p, selectedSite.ToString() ?? "A");
        });

        menu.AddOption("List", (p, o) =>
        {
            var team = selectedTeam.ToString();
            var site = selectedSite.ToString();
            Server.ExecuteCommand($"css_listspawns {site} {team}");
        });

        _menuManager.OpenMainMenu(player, menu);
    }

    private void BuildGroupSpawnsMenu(CCSPlayerController player)
    {
        if (_menuManager == null) return;
        var menu = _menuManager.CreateMenu("Group Spawns", true);

        var groups = _mapConfig?.GetGroupsClone() ?? new List<string>();
        var sites = new List<object> { "A", "B" };
        object? selectedGroup = groups.Count > 0 ? groups[0] : null;
        object selectedSite = sites[0];

        if (_mapConfig != null)
        {
            Helpers.SetGroupDisplayNames(_mapConfig.GetGroupsClone());
        }

        if (groups.Count > 0)
        {
            menu.AddSliderOption("Group", groups.Cast<object>().ToList(), groups[0], 4, (p, o, i) =>
            {
                if (o is IT3Option so && so.DefaultValue != null) selectedGroup = so.DefaultValue;
            });
        }

        menu.AddSliderOption("Site", sites, sites[0], 2, (p, o, i) =>
        {
            if (o is IT3Option so && so.DefaultValue != null) selectedSite = so.DefaultValue;
        });

        menu.AddOption("Show", (p, o) =>
        {
            if (selectedGroup != null)
            {
                var site = selectedSite.ToString();
                var grp = selectedGroup.ToString();
                if (!string.IsNullOrWhiteSpace(site) && !string.IsNullOrWhiteSpace(grp))
                {
                    ShowSpawnsFromMenu(p, site!, grp);
                }
            }
        });

        menu.AddOption("Hide", (p, o) => HideSpawnsFromMenu(p));

        menu.AddOption("Set Spawn Group", (p, o) => BuildSetSpawnGroupMenu(p));

        _menuManager.OpenMainMenu(player, menu);
    }

    private void BuildSetSpawnGroupMenu(CCSPlayerController player)
    {
        if (_menuManager == null) return;
        var menu = _menuManager.CreateMenu("Set Spawn Group", true);

        var groups = _mapConfig?.GetGroupsClone() ?? new List<string>();
        int spawnId = 0;
        object? selected = groups.Count > 0 ? groups[0] : null;

        menu.AddInputOption("Spawn Id", "id", (p, o, input) =>
        {
            var s = input.ToString();
            if (int.TryParse(s, out var id)) spawnId = id;
        }, "Type spawn id or 'cancel'.");

        if (groups.Count > 0)
        {
            var values = groups.Cast<object>().ToList();
            menu.AddSliderOption("Group", values, values[0], 4, (p, o, idx) =>
            {
                if (o is IT3Option so && so.DefaultValue != null)
                {
                    selected = so.DefaultValue;
                }
            });
        }

        menu.AddOption("Apply", (p, o) =>
        {
            if (spawnId > 0 && selected != null)
            {
                var name = selected.ToString();
                if (!string.IsNullOrWhiteSpace(name)) Server.ExecuteCommand($"css_setspawngroup {spawnId} {name}");
                _menuManager.Refresh();
            }
        });

        _menuManager.OpenMainMenu(player, menu);
    }

    private void StartSpawnTextFacingLoop(CCSPlayerController? player)
    {
        AddTimer(0.2f, () =>
        {
            if (_showingSpawnsForBombsite == null)
            {
                return;
            }
            Helpers.UpdateSpawnTextFacing(player);
            StartSpawnTextFacingLoop(player);
        });
    }

    private void ShowSpawnsFromMenu(CCSPlayerController player, string site, string? group = null)
    {
        if (_mapConfig == null)
        {
            player.PrintToChat($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        var bombsite = site.ToUpperInvariant();
        if (bombsite != "A" && bombsite != "B")
        {
            player.PrintToChat($"{MessagePrefix}You must specify a bombsite [A / B].");
            return;
        }

        _showingSpawnsForBombsite = bombsite == "A" ? Bombsite.A : Bombsite.B;

        _showingGroup = null;
        if (!string.IsNullOrWhiteSpace(group))
        {
            var resolved = ResolveGroupSlug(group);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                player.PrintToChat($"{MessagePrefix}Group '{group}' not found. Use css_listgroups.");
                return;
            }
            _showingGroup = resolved;
        }

        Helpers.SetWorldTextFacingPlayer(player);
        Helpers.SetGroupDisplayNames(_mapConfig.GetGroupsClone());
        
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            Helpers.RemoveSpawnTextLabels();
            var spawnsToShow = _mapConfig.GetSpawnsClone();
            if (!string.IsNullOrWhiteSpace(_showingGroup))
            {
                spawnsToShow = spawnsToShow
                    .Where(s => !string.IsNullOrWhiteSpace(s.Group) && s.Group!.Equals(_showingGroup, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            Helpers.ShowSpawns(spawnsToShow, _showingSpawnsForBombsite);
        }
        else
        {
            Server.ExecuteCommand("mp_warmup_start");
            Server.ExecuteCommand("mp_warmuptime 120");
            Server.ExecuteCommand("mp_warmup_pausetimer 1");
        }
        StartSpawnTextFacingLoop(player);
    }

    private void HideSpawnsFromMenu(CCSPlayerController player)
    {
        _showingSpawnsForBombsite = null;
        _showingGroup = null;
        Helpers.RemoveSpawnTextLabels();
        Helpers.ClearWorldTextFacingPlayer();
        Helpers.ClearGroupDisplayNames();
        Server.ExecuteCommand("mp_warmup_end");
    }

    [ConsoleCommand("css_add", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_addspawn", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_new", "Creates a new retakes spawn for the bombsite currently shown.")]
    [ConsoleCommand("css_newspawn", "Creates a new retakes spawn for the bombsite currently shown.")]
    [CommandHelper(minArgs: 1, usage: "[T/CT] [Y/N can be planter]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandAddSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't add a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must have an alive player pawn.");
            return;
        }

        var team = commandInfo.GetArg(1).ToUpper();
        if (team != "T" && team != "CT")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a team [T / CT] - [Value: {team}].");
            return;
        }

        var canBePlanterInput = commandInfo.GetArg(2).ToUpper();
        if (!string.IsNullOrWhiteSpace(canBePlanterInput) && canBePlanterInput != "Y" && canBePlanterInput != "N")
        {
            commandInfo.ReplyToCommand(
                $"{MessagePrefix}Incorrect value passed for can be a planter [Y / N] - [Value: {canBePlanterInput}].");
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        var closestDistance = 9999.9;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
        }

        if (closestDistance <= 72)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You are too close to another spawn, move away and try again.");
            return;
        }

        var newSpawn = new Spawn(
            vector: player!.PlayerPawn.Value!.AbsOrigin!,
            qAngle: player!.PlayerPawn.Value!.AbsRotation!
        )
        {
            Team = team == "T" ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
            CanBePlanter = team == "T" && !string.IsNullOrWhiteSpace(canBePlanterInput)
                ? canBePlanterInput == "Y"
                : player.PlayerPawn.Value.InBombZoneTrigger,
            Bombsite = (Bombsite)_showingSpawnsForBombsite
        };
        Helpers.ShowSpawn(newSpawn);

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        var didAddSpawn = _mapConfig.AddSpawn(newSpawn);
        if (didAddSpawn)
        {
            _spawnManager.CalculateMapSpawns();
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{(didAddSpawn ? "Spawn added" : "Error adding spawn")}");
    }

    [ConsoleCommand("css_remove", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_removespawn", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_delete", "Deletes the nearest retakes spawn.")]
    [ConsoleCommand("css_deletespawn", "Deletes the nearest retakes spawn.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemoveSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't remove a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        if (spawns.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found.");
            return;
        }

        var closestDistance = 9999.9;
        Spawn? closestSpawn = null;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestSpawn = spawn;
        }

        if (closestSpawn == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found within 128 units.");
            return;
        }

        // Remove the beam entity that is showing for the closest spawn.
        var beamEntities = Utilities.FindAllEntitiesByDesignerName<CBeam>("beam");
        foreach (var beamEntity in beamEntities)
        {
            if (beamEntity.AbsOrigin == null)
            {
                continue;
            }

            if (
                beamEntity.AbsOrigin.Z - closestSpawn.Vector.Z == 0 &&
                beamEntity.AbsOrigin.X - closestSpawn.Vector.X == 0 &&
                beamEntity.AbsOrigin.Y - closestSpawn.Vector.Y == 0
            )
            {
                beamEntity.Remove();
            }
        }

        var didRemoveSpawn = _mapConfig.RemoveSpawn(closestSpawn);
        if (didRemoveSpawn)
        {
            _spawnManager.CalculateMapSpawns();
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{(didRemoveSpawn ? "Spawn removed" : "Error removing spawn")}");
    }

    [ConsoleCommand("css_nearestspawn", "Goes to nearest retakes spawn.")]
    [ConsoleCommand("css_nearest", "Goes to nearest retakes spawn.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandNearestSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_showingSpawnsForBombsite == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You can't remove a spawn if you're not showing the spawns.");
            return;
        }

        if (_spawnManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn manager not loaded for some reason...");
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            return;
        }

        var spawns = _spawnManager.GetSpawns((Bombsite)_showingSpawnsForBombsite);

        if (spawns.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found.");
            return;
        }

        var closestDistance = 9999.9;
        Spawn? closestSpawn = null;

        foreach (var spawn in spawns)
        {
            var distance = Helpers.GetDistanceBetweenVectors(spawn.Vector, player!.PlayerPawn.Value!.AbsOrigin!);

            if (distance > 128.0 || distance > closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestSpawn = spawn;
        }

        if (closestSpawn == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found within 128 units.");
            return;
        }

        player!.PlayerPawn.Value!.Teleport(closestSpawn.Vector, closestSpawn.QAngle, new Vector());
        commandInfo.ReplyToCommand($"{MessagePrefix}Teleported to nearest spawn");
    }

    [ConsoleCommand("css_gotospawn", "Goes to specified retakes spawn by Id.")]
    [CommandHelper(minArgs: 1, usage: "<id>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandGoToSpawn(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.DoesPlayerHaveAlivePawn(player))
        {
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!int.TryParse(commandInfo.GetArg(1), out var id))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid id. Usage: css_gotospawn <id>");
            return;
        }

        var spawn = _mapConfig.GetSpawnsClone().FirstOrDefault(s => s.Id == id);

        if (spawn == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Spawn with Id={id} not found.");
            return;
        }

        player!.PlayerPawn.Value!.Teleport(spawn.Vector, spawn.QAngle, new Vector());
        commandInfo.ReplyToCommand($"{MessagePrefix}Teleported to spawn Id={id}");
    }

    [ConsoleCommand("css_hidespawns", "Exits the spawn editing mode.")]
    [ConsoleCommand("css_done", "Exits the spawn editing mode.")]
    [ConsoleCommand("css_exitedit", "Exits the spawn editing mode.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandHideSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        _showingSpawnsForBombsite = null;
        _showingGroup = null;
        Helpers.RemoveSpawnTextLabels();
        Helpers.ClearWorldTextFacingPlayer();
        Helpers.ClearGroupDisplayNames();
        Server.ExecuteCommand("mp_warmup_end");
    }

    [ConsoleCommand("css_listspawns", "Lists spawns with IDs and groups.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandListSpawns(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        Bombsite? bombsiteFilter = null;
        CsTeam? teamFilter = null;
        string? groupFilter = null;

        if (commandInfo.ArgCount >= 2)
        {
            var site = commandInfo.GetArg(1).Trim().ToUpper();
            if (site == "A") bombsiteFilter = Bombsite.A; else if (site == "B") bombsiteFilter = Bombsite.B; else if (!string.IsNullOrWhiteSpace(site))
            {
                commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B].");
                return;
            }
        }

        if (commandInfo.ArgCount >= 3)
        {
            var team = commandInfo.GetArg(2).Trim().ToUpper();
            if (team == "T") teamFilter = CsTeam.Terrorist; else if (team == "CT") teamFilter = CsTeam.CounterTerrorist; else if (!string.IsNullOrWhiteSpace(team))
            {
                commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a team [T / CT].");
                return;
            }
        }

        if (commandInfo.ArgCount >= 4)
        {
            var grp = commandInfo.GetArg(3).Trim();
            if (!string.IsNullOrWhiteSpace(grp)) groupFilter = grp;
        }

        var spawns = _mapConfig.GetSpawnsClone();
        if (bombsiteFilter != null) spawns = spawns.Where(s => s.Bombsite == bombsiteFilter).ToList();
        if (teamFilter != null) spawns = spawns.Where(s => s.Team == teamFilter).ToList();
        if (!string.IsNullOrWhiteSpace(groupFilter)) spawns = spawns.Where(s => !string.IsNullOrWhiteSpace(s.Group) && s.Group!.Equals(groupFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (spawns.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No spawns found.");
            return;
        }

        foreach (var s in spawns.OrderBy(s => s.Id))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Id={s.Id} Group={(s.Group ?? "-")} Team={(s.Team == CsTeam.Terrorist ? "T" : "CT")} Site={s.Bombsite} Planter={(s.CanBePlanter ? "Y" : "N")} Vec=({s.Vector.X:0.00},{s.Vector.Y:0.00},{s.Vector.Z:0.00})");
            player?.PrintToConsole($"Id={s.Id} Group={(s.Group ?? "-")} Team={s.Team} Site={s.Bombsite} Vec=({s.Vector.X},{s.Vector.Y},{s.Vector.Z}) Planter={s.CanBePlanter}");
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{spawns.Count} spawns listed.");
    }

    [ConsoleCommand("css_setspawngroup", "Sets group for a spawn by Id.")]
    [CommandHelper(minArgs: 2, usage: "<id> <group>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandSetSpawnGroup(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!int.TryParse(commandInfo.GetArg(1), out var id))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid id. Usage: css_setspawngroup <id> <group>");
            return;
        }

        // Join remaining args to support spaces in group names
        var parts = new List<string>();
        for (var i = 2; i < commandInfo.ArgCount; i++)
        {
            var a = commandInfo.GetArg(i);
            if (!string.IsNullOrWhiteSpace(a)) parts.Add(a);
        }
        var groupInput = string.Join(" ", parts).Trim();
        if (string.IsNullOrWhiteSpace(groupInput))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid group. Usage: css_setspawngroup <id> <group>");
            return;
        }

        // Resolve to existing group slug (supports prefixes). Do not create implicit groups here.
        var resolved = ResolveGroupSlug(groupInput);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Group '{groupInput}' not found. Use css_addgroup first or try full name.");
            return;
        }

        var ok = _mapConfig.SetSpawnGroup(id, resolved);
        commandInfo.ReplyToCommand(ok
            ? $"{MessagePrefix}Set group '{groupInput}' on spawn Id={id}."
            : $"{MessagePrefix}Spawn with Id={id} not found.");

        if (ok && _showingSpawnsForBombsite != null)
        {
            Helpers.RemoveSpawnTextLabels();
            Helpers.SetGroupDisplayNames(_mapConfig.GetGroupsClone());
            var spawnsToShow = _mapConfig.GetSpawnsClone();
            if (!string.IsNullOrWhiteSpace(_showingGroup))
            {
                spawnsToShow = spawnsToShow
                    .Where(s => !string.IsNullOrWhiteSpace(s.Group) && s.Group!.Equals(_showingGroup, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            Helpers.ShowSpawns(spawnsToShow, _showingSpawnsForBombsite);
        }
    }

    [ConsoleCommand("css_clearspawngroup", "Clears group for a spawn by Id.")]
    [CommandHelper(minArgs: 1, usage: "<id>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandClearSpawnGroup(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        if (!int.TryParse(commandInfo.GetArg(1), out var id))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid id. Usage: css_clearspawngroup <id>");
            return;
        }

        var ok = _mapConfig.SetSpawnGroup(id, null);
        commandInfo.ReplyToCommand(ok
            ? $"{MessagePrefix}Cleared group on spawn Id={id}."
            : $"{MessagePrefix}Spawn with Id={id} not found.");

        if (ok && _showingSpawnsForBombsite != null)
        {
            Helpers.RemoveSpawnTextLabels();
            Helpers.SetGroupDisplayNames(_mapConfig.GetGroupsClone());
            var spawnsToShow = _mapConfig.GetSpawnsClone();
            if (!string.IsNullOrWhiteSpace(_showingGroup))
            {
                spawnsToShow = spawnsToShow
                    .Where(s => !string.IsNullOrWhiteSpace(s.Group) && s.Group!.Equals(_showingGroup, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            Helpers.ShowSpawns(spawnsToShow, _showingSpawnsForBombsite);
        }
    }

    [ConsoleCommand("css_addgroup", "Creates a new spawn group for this map.")]
    [CommandHelper(minArgs: 1, usage: "<group>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandAddGroup(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !Helpers.IsValidPlayer(player))
        {
            return;
        }

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        // Join remaining args to support spaces in group names
        var parts = new List<string>();
        for (var i = 1; i < commandInfo.ArgCount; i++)
        {
            var a = commandInfo.GetArg(i);
            if (!string.IsNullOrWhiteSpace(a)) parts.Add(a);
        }
        var group = string.Join(" ", parts).Trim();

        if (string.IsNullOrWhiteSpace(group))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid group. Usage: css_addgroup <group>");
            return;
        }

        var ok = _mapConfig.AddGroup(group);
        commandInfo.ReplyToCommand(ok
            ? $"{MessagePrefix}Created group '{group}'."
            : $"{MessagePrefix}Group '{group}' already exists.");
    }

    [ConsoleCommand("css_listgroups", "Lists all spawn groups for this map.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandListGroups(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        var groups = _mapConfig.GetGroupsClone();
        if (groups.Count == 0)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}No groups exist yet.");
            return;
        }

        foreach (var g in groups)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}{g}");
            player?.PrintToConsole(g);
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{groups.Count} groups listed.");
    }

    [ConsoleCommand("css_removegroup", "Deletes a spawn group and clears it from any spawns.")]
    [CommandHelper(minArgs: 1, usage: "<group>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandRemoveGroup(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }

        // Join remaining args to support spaces in group names
        var parts = new List<string>();
        for (var i = 1; i < commandInfo.ArgCount; i++)
        {
            var a = commandInfo.GetArg(i);
            if (!string.IsNullOrWhiteSpace(a)) parts.Add(a);
        }
        var group = string.Join(" ", parts).Trim();

        if (string.IsNullOrWhiteSpace(group))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid group. Usage: css_removegroup <group>");
            return;
        }

        var resolved = ResolveGroupSlug(group);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Group '{group}' not found. Use css_listgroups.");
            return;
        }

        var ok = _mapConfig.RemoveGroup(resolved);
        commandInfo.ReplyToCommand(ok
            ? $"{MessagePrefix}Removed group '{group}'. Any spawns using it were cleared."
            : $"{MessagePrefix}Group '{group}' not found.");
    }

    private string? ResolveGroupSlug(string input)
    {
        if (_mapConfig == null) return null;
        var groups = _mapConfig.GetGroupsClone();
        if (groups.Count == 0) return null;

        var slugIn = Helpers.Slugify(input);
        var candidates = new List<(string Name, string Slug)>();
        foreach (var name in groups)
        {
            var slug = Helpers.Slugify(name);
            candidates.Add((name, slug));
        }

        // Exact display name match
        var exactName = candidates.FirstOrDefault(c => c.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exactName.Name)) return exactName.Slug;

        // Exact slug match
        var exactSlug = candidates.FirstOrDefault(c => c.Slug.Equals(slugIn, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(exactSlug.Slug)) return exactSlug.Slug;

        // Unique prefix match on slug or name
        var prefix = candidates.Where(c => c.Slug.StartsWith(slugIn, StringComparison.OrdinalIgnoreCase)
                                           || c.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1) return prefix[0].Slug;

        return null;
    }

    [ConsoleCommand("css_scramble", "Sets teams to scramble on the next round.")]
    [ConsoleCommand("css_scrambleteams", "Sets teams to scramble on the next round.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/admin")]
    public void OnCommandScramble(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_gameManager == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Game manager not loaded.");
            return;
        }

        _gameManager.ScrambleNextRound(player);
    }

    [ConsoleCommand("css_debugqueues", "Prints the state of the queues to the console.")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    [RequiresPermissions("@css/root")]
    public void OnCommandDebugState(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return;
        }

        _gameManager.QueueManager.DebugQueues(true);
    }

    [ConsoleCommand("css_voices", "Toggles whether or not you want to hear bombsite voice announcements.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandVoices(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.IsValidPlayer(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must be a valid player to use this command.");
            return;
        }

        if (RetakesConfig.IsLoaded(_retakesConfig) && !_retakesConfig!.RetakesConfigData!.EnableBombsiteAnnouncementVoices)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Bombsite voice announcements are permanently disabled on this server.");
            return;
        }

        var didMute = false;
        if (!_hasMutedVoices.Contains(player!))
        {
            didMute = true;
            _hasMutedVoices.Add(player!);
        }
        else
        {
            _hasMutedVoices.Remove(player!);
        }

        commandInfo.ReplyToCommand($"{MessagePrefix}{_translator["retakes.voices.toggle", didMute ? $"{ChatColors.Red}disabled{ChatColors.White}" : $"{ChatColors.Green}enabled{ChatColors.White}"]}");
    }
    #endregion

    #region Listeners
    private void OnMapStart(string mapName, string? customMapConfig = null)
    {
        Helpers.Debug("OnMapStart listener triggered!");

        ResetState();

        AddTimer(1.0f, () =>
        {
            // Execute the retakes configuration.
            Helpers.ExecuteRetakesConfiguration(ModuleDirectory);
        });

        // If we don't have a map config loaded, load it.
        if (!MapConfig.IsLoaded(_mapConfig, customMapConfig ?? mapName))
        {
            _mapConfig = new MapConfig(ModuleDirectory, customMapConfig ?? mapName);
            _mapConfig.Load();
        }

        if (!RetakesConfig.IsLoaded(_retakesConfig))
        {
            _retakesConfig = new RetakesConfig(ModuleDirectory);
            _retakesConfig.Load();
        }

        if (_mapConfig == null)
        {
            throw new Exception("Map config is null");
        }

        _spawnManager = new SpawnManager(_mapConfig);

        _gameManager = new GameManager(
            _translator,
            new QueueManager(
                _translator,
                _retakesConfig?.RetakesConfigData?.MaxPlayers,
                _retakesConfig?.RetakesConfigData?.TerroristRatio,
                _retakesConfig?.RetakesConfigData?.QueuePriorityFlag,
                _retakesConfig?.RetakesConfigData?.QueueImmunityFlag,
                _retakesConfig?.RetakesConfigData?.ShouldForceEvenTeamsWhenPlayerCountIsMultipleOf10,
                _retakesConfig?.RetakesConfigData?.ShouldPreventTeamChangesMidRound
            ),
            _retakesConfig?.RetakesConfigData?.RoundsToScramble,
            _retakesConfig?.RetakesConfigData?.IsScrambleEnabled,
            _retakesConfig?.RetakesConfigData?.ShouldRemoveSpectators,
            _retakesConfig?.RetakesConfigData?.IsBalanceEnabled
        );

        _breakerManager = new BreakerManager(
            _retakesConfig?.RetakesConfigData?.ShouldBreakBreakables,
            _retakesConfig?.RetakesConfigData?.ShouldOpenDoors
        );

        IsDebugMode = _retakesConfig?.RetakesConfigData?.IsDebugMode ?? false;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Helpers.IsValidPlayer(player))
        {
            return HookResult.Continue;
        }

        // TODO: We can make use of sv_human_autojoin_team 3 to prevent needing to do this.
        player.ForceTeamTime = 3600.0f;

        // Create a timer to do this as it would occasionally fire too early.
        AddTimer(1.0f, () =>
        {
            if (!player.IsValid)
            {
                return;
            }

            player.ChangeTeam(CsTeam.Spectator);
            player.ExecuteClientCommand("teammenu");
        });

        // Many hours of hard work went into this.
        if (new List<ulong> { 76561198028510846, 76561198044886803, 76561198414501446 }.Contains(player.SteamID))
        {
            var grant = _retakesConfig?.RetakesConfigData?.QueuePriorityFlag.Split(",")[0].Trim() ?? "@css/vip";
            player.PrintToConsole($"{LogPrefix}You have been given queue priority {grant} for being a Retakes contributor!");
            AdminManager.AddPlayerPermissions(player, grant);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundPreStart(EventRoundPrestart @event, GameEventInfo info)
    {
        // If we are in warmup, skip.
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            Helpers.Debug($"Warmup round, skipping.");
            return HookResult.Continue;
        }

        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        // Reset round teams to allow team changes.
        _gameManager.QueueManager.ClearRoundTeams();

        // Update Queue status
        Helpers.Debug($"Updating queues...");
        _gameManager.QueueManager.DebugQueues(true);
        _gameManager.QueueManager.Update();
        _gameManager.QueueManager.DebugQueues(false);
        Helpers.Debug($"Updated queues.");

        Helpers.Debug($"Calling GameManager.OnRoundPreStart({_lastRoundWinner})");
        _gameManager.OnRoundPreStart(_lastRoundWinner);
        Helpers.Debug($"GameManager.OnRoundPreStart call complete");

        // Set round teams to prevent team changes mid round
        _gameManager.QueueManager.SetRoundTeams();

        // Reset per-round menu open tracking
        _menuOpenedThisRound.Clear();

        // No longer opening here; handled in OnRoundPostStart after allocation

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // TODO: FIGURE OUT WHY THE FUCK I NEED TO DO THIS
        var weirdAliveSpectators = Utilities.GetPlayers()
            .Where(x => x is { TeamNum: < (int)CsTeam.Terrorist, PawnIsAlive: true });
        foreach (var weirdAliveSpectator in weirdAliveSpectators)
        {
            // I **think** it's caused by auto team balance being on, so turn it off
            Server.ExecuteCommand("mp_autoteambalance 0");
            weirdAliveSpectator.CommitSuicide(false, true);
        }

        // If we are in warmup, skip.
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            Helpers.Debug($"Warmup round, skipping.");

            if (_mapConfig != null)
            {
                var spawnsToShow = _mapConfig.GetSpawnsClone();
                if (!string.IsNullOrWhiteSpace(_showingGroup))
                {
                    spawnsToShow = spawnsToShow
                        .Where(s => !string.IsNullOrWhiteSpace(s.Group) &&
                                    s.Group!.Equals(_showingGroup, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                Helpers.ShowSpawns(spawnsToShow, _showingSpawnsForBombsite);
            }

            return HookResult.Continue;
        }

        // If we are not in warmup, ensure any world-text labels are cleaned up
        Helpers.RemoveSpawnTextLabels();
        Helpers.ClearWorldTextFacingPlayer();

        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        if (_spawnManager == null)
        {
            Helpers.Debug($"Spawn manager not loaded.");
            return HookResult.Continue;
        }

        // Reset round state.
        _breakerManager?.Handle();
        _currentBombsite = _forcedBombsite ?? (Helpers.Random.Next(0, 2) == 0 ? Bombsite.A : Bombsite.B);
        _gameManager.ResetPlayerScores();

        Helpers.Debug("Clearing _showingSpawnsForBombsite");
        _showingSpawnsForBombsite = null;
        _showingGroup = null;

        _planter = _spawnManager.HandleRoundSpawns(
            _currentBombsite,
            _gameManager.QueueManager.ActivePlayers,
            p => _playerPrefs?.GetSpawnId(p.SteamID, Server.MapName)
        );

        if (!RetakesConfig.IsLoaded(_retakesConfig) ||
            _retakesConfig!.RetakesConfigData!.EnableFallbackBombsiteAnnouncement)
        {
            AnnounceBombsite(_currentBombsite);
        }

        RetakesPluginEventSenderCapability.Get()?.TriggerEvent(new AnnounceBombsiteEvent(_currentBombsite));

        // Redundant open: if FreezeEnd missed it, open now for CTs who toggled and haven't been opened this round
        if (_menuManager != null && _openSpawnMenuNextRound.Count > 0)
        {
            foreach (var steamId in _openSpawnMenuNextRound)
            {
                if (_menuOpenedThisRound.Contains(steamId)) continue;
                var p = Utilities.GetPlayers().FirstOrDefault(pl => pl.SteamID == steamId);
                if (!Helpers.IsValidPlayer(p)) continue;
                if (p.Team != CsTeam.CounterTerrorist) continue;
                OpenPlayerSpawnGroupMenu(p, _currentBombsite);
                _menuOpenedThisRound.Add(steamId);
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundPostStart(EventRoundPoststart @event, GameEventInfo info)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        // If we are in warmup, skip.
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            Helpers.Debug($"Warmup round, skipping.");
            return HookResult.Continue;
        }

        Helpers.Debug($"Trying to loop valid active players.");
        foreach (var player in _gameManager.QueueManager.ActivePlayers.Where(Helpers.IsValidPlayer))
        {
            Helpers.Debug($"[{player.PlayerName}] Handling allocation...");

            if (!Helpers.IsValidPlayer(player))
            {
                continue;
            }

            // Strip the player of all of their weapons and the bomb before any spawn / allocation occurs.
            Helpers.RemoveHelmetAndHeavyArmour(player);
            player.RemoveWeapons();

            if (player == _planter && RetakesConfig.IsLoaded(_retakesConfig) &&
                !_retakesConfig!.RetakesConfigData!.IsAutoPlantEnabled)
            {
                Helpers.Debug($"Player is planter and auto plant is disabled, allocating bomb.");
                Helpers.GiveAndSwitchToBomb(player);
            }

            if (!RetakesConfig.IsLoaded(_retakesConfig) ||
                _retakesConfig!.RetakesConfigData!.EnableFallbackAllocation)
            {
                Helpers.Debug($"Allocating...");
                AllocationManager.Allocate(player);
            }
            else
            {
                Helpers.Debug($"Fallback allocation disabled, skipping.");
            }
        }

        RetakesPluginEventSenderCapability.Get()?.TriggerEvent(new AllocateEvent());

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        // If we are in warmup, skip.
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            Helpers.Debug($"Warmup round, skipping.");
            return HookResult.Continue;
        }

        if (Helpers.GetCurrentNumPlayers(CsTeam.Terrorist) > 0)
        {
            HandleAutoPlant();
        }

        // Auto-close any spawn selection menus when freeze time ends
        if (_menuManager != null && _menuOpenedThisRound.Count > 0)
        {
            var opened = _menuOpenedThisRound.ToList();
            foreach (var steamId in opened)
            {
                var p = Utilities.GetPlayers().FirstOrDefault(pl => pl.SteamID == steamId);
                if (!Helpers.IsValidPlayer(p)) { continue; }
                var active = _menuManager.GetActiveMenu(p);
                if (active != null)
                {
                    _menuManager.CloseMenu(p);
                }
            }
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        var player = @event.Userid;

        if (!Helpers.IsValidPlayer(player) || !Helpers.IsPlayerConnected(player))
        {
            return HookResult.Continue;
        }

        // debug and check if the player is in the queue.
        Helpers.Debug($"[{player.PlayerName}] Checking ActivePlayers.");
        if (!_gameManager.QueueManager.ActivePlayers.Contains(player))
        {
            Helpers.Debug($"[{player.PlayerName}] Checking player pawn {player.PlayerPawn.Value != null}.");
            if (player.PlayerPawn.Value != null && player.PlayerPawn.IsValid && player.PlayerPawn.Value.IsValid)
            {
                Helpers.Debug(
                    $"[{player.PlayerName}] player pawn is valid {player.PlayerPawn.IsValid} && {player.PlayerPawn.Value.IsValid}.");
                Helpers.Debug($"[{player.PlayerName}] calling playerpawn.commitsuicide()");
                player.PlayerPawn.Value.CommitSuicide(false, true);
            }

            Helpers.Debug($"[{player.PlayerName}] Player not in ActivePlayers, moving to spectator.");
            if (!player.IsBot)
            {
                Helpers.Debug($"[{player.PlayerName}] moving to spectator.");
                player.ChangeTeam(CsTeam.Spectator);
            }
            if (player.IsBot && !player.IsHLTV)
            {
                _gameManager.QueueManager.ActivePlayers.Add(player);
                Helpers.Debug($"[{player.PlayerName}] Force added bot to active players.");
            }

            return HookResult.Continue;
        }
        else
        {
            Helpers.Debug($"[{player.PlayerName}] Player is in ActivePlayers.");
        }

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        Helpers.Debug($"OnBombPlanted event fired");

        AddTimer(4.1f, () => AnnounceBombsite(_currentBombsite, true));

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        var attacker = @event.Attacker;
        var assister = @event.Assister;

        if (Helpers.IsValidPlayer(attacker))
        {
            _gameManager.AddScore(attacker, GameManager.ScoreForKill);
        }

        if (Helpers.IsValidPlayer(assister))
        {
            _gameManager.AddScore(assister, GameManager.ScoreForAssist);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        var player = @event.Userid;

        if (Helpers.IsValidPlayer(player))
        {
            _gameManager.AddScore(player, GameManager.ScoreForDefuse);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // Ignore warmup rounds when tracking winners to prevent bad streak counts
        if (Helpers.GetGameRules().WarmupPeriod)
        {
            _lastRoundWinner = CsTeam.None;
            return HookResult.Continue;
        }

        _lastRoundWinner = (CsTeam)@event.Winner;

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        // Ensure all team join events are silent.
        @event.Silent = true;

        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        return _gameManager.RemoveSpectators(@event, _hasMutedVoices);
    }

    [GameEventHandler]
    public HookResult OnPlayerTeamPost(EventPlayerTeam @event, GameEventInfo info)
    {
        if (_menuManager == null)
        {
            return HookResult.Continue;
        }

        var player = @event.Userid;
        if (!Helpers.IsValidPlayer(player))
        {
            return HookResult.Continue;
        }

        // Only act if player toggled auto-open and became CT
        if (_openSpawnMenuNextRound.Contains(player.SteamID) && player.Team == CsTeam.CounterTerrorist)
        {
            // During active rounds, prefer opening at RoundStart/FreezeEnd. If missed, open here with per-round guard.
            if (!_menuOpenedThisRound.Contains(player.SteamID) && !Helpers.GetGameRules().WarmupPeriod)
            {
                OpenPlayerSpawnGroupMenu(player, _currentBombsite);
                _menuOpenedThisRound.Add(player.SteamID);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnCommandJoinTeam(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        if (
            !Helpers.IsValidPlayer(player)
            || commandInfo.ArgCount < 2
            || !Enum.TryParse<CsTeam>(commandInfo.GetArg(1), out var toTeam)
        )
        {
            return HookResult.Handled;
        }

        var fromTeam = player!.Team;

        Helpers.Debug($"[{player.PlayerName}] {fromTeam} -> {toTeam}");

        _gameManager.QueueManager.DebugQueues(true);
        var response = _gameManager.QueueManager.PlayerJoinedTeam(player, fromTeam, toTeam);
        _gameManager.QueueManager.DebugQueues(false);

        Helpers.Debug($"[{player.PlayerName}] checking to ensure we have active players");
        // If we don't have any active players, setup the active players and restart the game.
        if (_gameManager.QueueManager.ActivePlayers.Count == 0)
        {
            Helpers.Debug($"[{player.PlayerName}] clearing round teams to allow team changes");
            _gameManager.QueueManager.ClearRoundTeams();

            Helpers.Debug(
                $"[{player.PlayerName}] no active players found, calling QueueManager.Update()");
            _gameManager.QueueManager.DebugQueues(true);
            _gameManager.QueueManager.Update();
            _gameManager.QueueManager.DebugQueues(false);

            Helpers.RestartGame();
        }

        return response;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null)
        {
            return HookResult.Continue;
        }

        if (_gameManager == null)
        {
            Helpers.Debug($"Game manager not loaded.");
            return HookResult.Continue;
        }

        _gameManager.QueueManager.RemovePlayerFromQueues(player);
        _hasMutedVoices.Remove(player);

        return HookResult.Continue;
    }
    #endregion

    // Helpers (with localization so they must be in here until I can figure out how to use it nicely elsewhere)
    private void AnnounceBombsite(Bombsite bombsite, bool onlyCenter = false)
    {
        string[] bombsiteAnnouncers =
        [
            "balkan_epic",
            "leet_epic",
            "professional_epic",
            "professional_fem",
            "seal_epic",
            "swat_epic",
            "swat_fem"
        ];

        // Get translation message
        var numTerrorist = Helpers.GetCurrentNumPlayers(CsTeam.Terrorist);
        var numCounterTerrorist = Helpers.GetCurrentNumPlayers(CsTeam.CounterTerrorist);

        var isRetakesConfigLoaded = RetakesConfig.IsLoaded(_retakesConfig);

        // TODO: Once we implement per client translations this will need to be inside the loop
        var announcementMessage = _translator["retakes.bombsite.announcement", bombsite.ToString(), numTerrorist,
            numCounterTerrorist];
        var centerAnnouncementMessage = _translator["center.retakes.bombsite.announcement", bombsite.ToString(), numTerrorist,
            numCounterTerrorist];

        foreach (var player in Utilities.GetPlayers())
        {
            if (!onlyCenter)
            {
                // Don't use Server.PrintToChat as it'll add another loop through the players.
                player.PrintToChat($"{MessagePrefix}{announcementMessage}");

                if (
                    (!isRetakesConfigLoaded || _retakesConfig!.RetakesConfigData!.EnableBombsiteAnnouncementVoices)
                    && !_hasMutedVoices.Contains(player)
                )
                {
                    // Do this here so every player hears a random announcer each round.
                    var bombsiteAnnouncer = bombsiteAnnouncers[Helpers.Random.Next(bombsiteAnnouncers.Length)];

                    player.ExecuteClientCommand(
                        $"play sounds/vo/agents/{bombsiteAnnouncer}/loc_{bombsite.ToString().ToLower()}_01");
                }

                continue;
            }

            if (isRetakesConfigLoaded && !_retakesConfig!.RetakesConfigData!.EnableBombsiteAnnouncementCenter)
            {
                continue;
            }

            if (player.Team == CsTeam.CounterTerrorist)
            {
                player.PrintToCenter(centerAnnouncementMessage);
            }
        }
    }

    private void HandleAutoPlant()
    {
        if (RetakesConfig.IsLoaded(_retakesConfig) && !_retakesConfig!.RetakesConfigData!.IsAutoPlantEnabled)
        {
            return;
        }

        if (_planter != null && Helpers.IsValidPlayer(_planter))
        {
            Helpers.PlantTickingBomb(_planter, _currentBombsite);
        }
        else
        {
            Helpers.TerminateRound(RoundEndReason.RoundDraw);
        }
    }
}
