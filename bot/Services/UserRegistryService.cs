using System.Collections.Concurrent;
using System.Text.Json;

namespace MediaBot.Services;

public class UserRegistryService
{
    public const string RoleFull = "full";
    public const string RoleGuest = "guest";

    private readonly string _filePath;
    private readonly ConcurrentDictionary<long, string> _users = new();  // chatId -> role

    public UserRegistryService(IConfiguration config)
    {
        _filePath = config["UserRegistryPath"] ?? "/config/users.json";
        Load();
    }

    public bool IsRegistered(long chatId) => _users.ContainsKey(chatId);

    public bool IsFull(long chatId) => _users.TryGetValue(chatId, out var role) && role == RoleFull;

    public string? GetRole(long chatId) => _users.TryGetValue(chatId, out var role) ? role : null;

    public void Register(long chatId, string role)
    {
        _users[chatId] = role;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        var json = File.ReadAllText(_filePath);

        // New format: { "12345": "full", "67890": "guest" }
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<long, string>>(json);
            if (dict != null)
            {
                foreach (var kv in dict) _users[kv.Key] = kv.Value;
                return;
            }
        }
        catch (JsonException) { }

        // Legacy format: [12345, 67890] — all treated as full-access
        try
        {
            var ids = JsonSerializer.Deserialize<List<long>>(json) ?? [];
            foreach (var id in ids) _users[id] = RoleFull;
        }
        catch (JsonException) { }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath,
            JsonSerializer.Serialize(_users.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }
}
