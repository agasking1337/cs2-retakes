using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesPlugin.Modules.Configs;
using RetakesPluginShared.Enums;

namespace RetakesPlugin.Modules.Managers;

public class SpawnManager
{
    private readonly MapConfig _mapConfig;
    private readonly Dictionary<Bombsite, Dictionary<CsTeam, List<Spawn>>> _spawns = new();

    public SpawnManager(MapConfig mapConfig)
    {
        _mapConfig = mapConfig;

        CalculateMapSpawns();
    }

    public void CalculateMapSpawns()
    {
        _spawns.Clear();

        _spawns.Add(Bombsite.A, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });
        _spawns.Add(Bombsite.B, new Dictionary<CsTeam, List<Spawn>>()
        {
            { CsTeam.Terrorist, [] },
            { CsTeam.CounterTerrorist, [] }
        });

        foreach (var spawn in _mapConfig.GetSpawnsClone())
        {
            _spawns[spawn.Bombsite][spawn.Team].Add(spawn);
        }
    }

    public List<Spawn> GetSpawns(Bombsite bombsite, CsTeam? team = null)
    {
        if (_spawns[bombsite][CsTeam.Terrorist].Count == 0 && _spawns[bombsite][CsTeam.CounterTerrorist].Count == 0)
        {
            return [];
        }

        if (team == null)
        {
            return _spawns[bombsite].SelectMany(entry => entry.Value).ToList();
        }

        return _spawns[bombsite][(CsTeam)team];
    }

    /**
     * This function returns the player who should be the planter and moves all players to random spawns based on bombsite.
     * getPreferredSpawnId: returns a preferred Spawn.Id (or null) for the given player on this map.
     */
    public CCSPlayerController? HandleRoundSpawns(
        Bombsite bombsite,
        HashSet<CCSPlayerController> players,
        Func<CCSPlayerController, int?>? getPreferredSpawnId = null)
    {
        Helpers.Debug($"Moving players to spawns.");

        // Clone the spawns so we can mutate them
        var spawns = _spawns[bombsite].ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToList()
        );

        if (
            Helpers.GetCurrentNumPlayers(CsTeam.CounterTerrorist) > spawns[CsTeam.CounterTerrorist].Count ||
            Helpers.GetCurrentNumPlayers(CsTeam.Terrorist) > spawns[CsTeam.Terrorist].Count
        )
        {
            // TODO: Potentially update the maxRetakesPlayers on the fly.
            throw new Exception($"There are not enough spawns in the map config for Bombsite {bombsite.ToString()}!");
        }

        var planterSpawns = spawns[CsTeam.Terrorist].Where(spawn => spawn.CanBePlanter).ToList();

        if (planterSpawns.Count == 0)
        {
            throw new Exception($"There are no planter spawns for Bombsite {bombsite.ToString()}!");
        }

        var randomPlanterSpawn = planterSpawns[Helpers.Random.Next(planterSpawns.Count)];
        spawns[CsTeam.Terrorist].Remove(randomPlanterSpawn);

        CCSPlayerController? planter = null;

        foreach (var player in Helpers.Shuffle(players))
        {
            if (!Helpers.DoesPlayerHaveAlivePawn(player))
            {
                continue;
            }

            var team = player.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist)
            {
                continue;
            }

            if (planter == null && team == CsTeam.Terrorist)
            {
                planter = player;
            }

            // Try to use player's preferred spawn if provided and available
            var availableSpawns = spawns[team];
            Spawn? preferred = null;
            if (getPreferredSpawnId != null)
            {
                var prefId = getPreferredSpawnId(player);
                if (prefId != null)
                {
                    preferred = availableSpawns.FirstOrDefault(s => s.Id == prefId.Value);
                }
            }

            var count = availableSpawns.Count;
            if (count == 0)
            {
                continue;
            }

            var spawn = player == planter ? randomPlanterSpawn : (preferred ?? availableSpawns[Helpers.Random.Next(count)]);

            player.Pawn.Value!.Teleport(spawn.Vector, spawn.QAngle, new Vector());
            spawns[team].Remove(spawn);
        }

        Helpers.Debug($"Moving players to spawns COMPLETE.");

        return planter;
    }
}
