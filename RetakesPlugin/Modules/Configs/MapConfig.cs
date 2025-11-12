using System.Text.Json;

namespace RetakesPlugin.Modules.Configs;

public class MapConfig
{
    private readonly string _mapName;
    private readonly string _mapConfigDirectory;
    private readonly string _mapConfigPath;
    private MapConfigData? _mapConfigData;

    public MapConfig(string moduleDirectory, string mapName)
    {
        _mapName = mapName;
        _mapConfigDirectory = Path.Combine(moduleDirectory, "map_config");
        _mapConfigPath = Path.Combine(_mapConfigDirectory, $"{mapName}.json");
        _mapConfigData = null;
    }

    public void Load(bool isViaCommand = false)
    {
        Helpers.Debug($"Attempting to load map data from {_mapConfigPath}");

        try
        {
            if (!File.Exists(_mapConfigPath))
            {
                throw new FileNotFoundException();
            }

            var jsonData = File.ReadAllText(_mapConfigPath);
            _mapConfigData = JsonSerializer.Deserialize<MapConfigData>(jsonData, Helpers.JsonSerializerOptions);

            // TODO: Implement validation to make sure the config is valid / has enough spawns.
            // if (_mapConfigData!.Spawns == null || _mapConfigData.Spawns.Count < 0)
            // {
            //     throw new Exception("No spawns found in config");
            // }

            // Ensure all spawns have a unique incremental Id
            if (_mapConfigData != null)
            {
                _mapConfigData.Groups ??= new List<string>();
                var changed = false;
                var maxId = _mapConfigData.Spawns.Count == 0 ? 0 : _mapConfigData.Spawns.Max(s => s.Id);
                var seen = new HashSet<int>();
                foreach (var s in _mapConfigData.Spawns)
                {
                    if (s.Id > 0 && !seen.Contains(s.Id))
                    {
                        seen.Add(s.Id);
                        continue;
                    }

                    maxId += 1;
                    s.Id = maxId;
                    seen.Add(s.Id);
                    changed = true;
                }

                if (changed)
                {
                    Save();
                }
            }

            Helpers.Debug($"Data loaded from {_mapConfigPath}");
        }
        catch (FileNotFoundException)
        {
            Helpers.Debug($"No config for map {_mapName}");

            if (!isViaCommand)
            {
                _mapConfigData = new MapConfigData();
                Save();
            }
        }
        catch (Exception ex)
        {
            Helpers.Debug($"An error occurred while loading data: {ex.Message}");
        }
    }

    /**
     * This function returns a clone of the spawns list. (free to mutate :>)
     */
    public List<Spawn> GetSpawnsClone()
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        return _mapConfigData.Spawns.ToList();
    }

    public List<string> GetGroupsClone()
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        return (_mapConfigData.Groups ?? new List<string>()).ToList();
    }

    public bool AddSpawn(Spawn spawn)
    {
        _mapConfigData ??= new MapConfigData();

        // Check if the spawn already exists based on vector and bombsite
        if (_mapConfigData.Spawns.Any(existingSpawn =>
                existingSpawn.Vector == spawn.Vector && existingSpawn.Bombsite == spawn.Bombsite))
        {
            return false; // Spawn already exists, avoid duplication
        }

        // Assign an Id if missing
        if (spawn.Id <= 0)
        {
            var maxId = _mapConfigData.Spawns.Count == 0 ? 0 : _mapConfigData.Spawns.Max(s => s.Id);
            spawn.Id = maxId + 1;
        }

        _mapConfigData.Spawns.Add(spawn);

        Save();
        Load();

        return true;
    }

    public bool RemoveGroup(string group)
    {
        _mapConfigData ??= new MapConfigData();

        var name = group.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        _mapConfigData.Groups ??= new List<string>();

        if (!_mapConfigData.Groups.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _mapConfigData.Groups = _mapConfigData.Groups
            .Where(g => !g.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var s in _mapConfigData.Spawns)
        {
            if (!string.IsNullOrWhiteSpace(s.Group) && s.Group!.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                s.Group = null;
            }
        }

        Save();
        Load();

        return true;
    }

    public bool AddGroup(string group)
    {
        _mapConfigData ??= new MapConfigData();

        var name = group.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        _mapConfigData.Groups ??= new List<string>();

        if (_mapConfigData.Groups.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _mapConfigData.Groups.Add(name);

        Save();
        Load();

        return true;
    }

    public bool RemoveSpawn(Spawn spawn)
    {
        _mapConfigData ??= new MapConfigData();

        if (!_mapConfigData.Spawns.Any(existingSpawn =>
                existingSpawn.Vector == spawn.Vector && existingSpawn.Bombsite == spawn.Bombsite))
        {
            return false; // Spawn doesn't exist, avoid removing
        }

        _mapConfigData.Spawns.Remove(spawn);

        Save();
        Load();

        return true;
    }

    private MapConfigData GetSanitisedMapConfigData()
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        // Remove any duplicate spawns in the list
        _mapConfigData.Spawns = _mapConfigData.Spawns
            .GroupBy(spawn => new { spawn.Vector, spawn.Bombsite })
            .Select(group => group.First())
            .ToList();

        _mapConfigData.Groups ??= new List<string>();
        _mapConfigData.Groups = _mapConfigData.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _mapConfigData;
    }

    public bool SetSpawnGroup(int id, string? group)
    {
        if (_mapConfigData == null)
        {
            throw new Exception("Map config data is null");
        }

        var spawn = _mapConfigData.Spawns.FirstOrDefault(s => s.Id == id);
        if (spawn == null)
        {
            return false;
        }

        spawn.Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();

        Save();
        Load();

        return true;
    }

    private void Save()
    {
        var jsonString = JsonSerializer.Serialize(GetSanitisedMapConfigData(), Helpers.JsonSerializerOptions);

        try
        {
            if (!Directory.Exists(_mapConfigDirectory))
            {
                Directory.CreateDirectory(_mapConfigDirectory);
            }

            File.WriteAllText(_mapConfigPath, jsonString);

            Helpers.Debug($"Data has been written to {_mapConfigPath}");
        }
        catch (IOException e)
        {
            Helpers.Debug($"An error occurred while writing to the file: {e.Message}");
        }
    }

    public static bool IsLoaded(MapConfig? mapConfig, string currentMap)
    {
        if (mapConfig == null || mapConfig._mapName != currentMap)
        {
            return false;
        }

        return true;
    }
}
