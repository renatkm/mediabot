using System.Collections.Concurrent;
using System.Text.Json;

namespace MediaBot.Services;

public class UserRegistryService
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<long, bool> _registered = new();

    public UserRegistryService(IConfiguration config)
    {
        _filePath = config["UserRegistryPath"] ?? "/config/users.json";
        Load();
    }

    public bool IsRegistered(long chatId) => _registered.ContainsKey(chatId);

    public void Register(long chatId)
    {
        if (_registered.TryAdd(chatId, true))
            Save();
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var ids = JsonSerializer.Deserialize<List<long>>(File.ReadAllText(_filePath)) ?? [];
            foreach (var id in ids) _registered[id] = true;
        }
        catch { }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_registered.Keys.ToList()));
    }
}
