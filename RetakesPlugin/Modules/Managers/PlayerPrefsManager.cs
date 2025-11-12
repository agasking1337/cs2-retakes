using System.Text.Json;
using CounterStrikeSharp.API.Core;

namespace RetakesPlugin.Modules.Managers;

public class PlayerPrefsManager
{
    private readonly string _prefsPath;
    private Dictionary<ulong, Dictionary<string, int>> _byPlayer = new();

    public PlayerPrefsManager(string moduleDirectory)
    {
        _prefsPath = Path.Combine(moduleDirectory, "player_prefs.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_prefsPath))
            {
                _byPlayer = new();
                return;
            }
            var json = File.ReadAllText(_prefsPath);
            _byPlayer = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<string, int>>>(json) ?? new();
        }
        catch
        {
            _byPlayer = new();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_byPlayer, global::RetakesPlugin.Modules.Helpers.JsonSerializerOptions);
            var dir = Path.GetDirectoryName(_prefsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_prefsPath, json);
        }
        catch { }
    }

    public int? GetSpawnId(ulong steamId, string mapName)
    {
        if (_byPlayer.TryGetValue(steamId, out var byMap))
        {
            if (byMap.TryGetValue(mapName, out var spawnId))
            {
                return spawnId;
            }
        }
        return null;
    }

    public void SetSpawnId(ulong steamId, string mapName, int? spawnId)
    {
        if (!_byPlayer.TryGetValue(steamId, out var byMap))
        {
            byMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _byPlayer[steamId] = byMap;
        }
        if (spawnId == null)
        {
            if (byMap.ContainsKey(mapName)) byMap.Remove(mapName);
        }
        else
        {
            byMap[mapName] = spawnId.Value;
        }
        Save();
    }
}
