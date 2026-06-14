using System.Collections.Concurrent;

namespace MediaBot.Services;

public class CategoryService
{
    private readonly ConcurrentDictionary<string, string> _categories = new();
    private readonly string _downloadPath = "/downloads";

    public void ScanCategories()
    {
        _categories.Clear();

        if (!Directory.Exists(_downloadPath))
            return;

        var subdirs = Directory.GetDirectories(_downloadPath);
        foreach (var dir in subdirs)
        {
            var folderName = Path.GetFileName(dir);
            // Map folder name to display name
            var displayName = folderName switch
            {
                "movies" => "🎬 Фильм",
                "series" => "📺 Сериал",
                "music" => "🎵 Музыка",
                "ebooks" => "📚 Книга",
                _ => $"📁 {folderName}"
            };
            _categories[folderName] = displayName;
        }

        // Always include default categories even if folders don't exist
        if (!_categories.ContainsKey("movies"))
            _categories["movies"] = "🎬 Фильм";
        if (!_categories.ContainsKey("series"))
            _categories["series"] = "📺 Сериал";
        if (!_categories.ContainsKey("music"))
            _categories["music"] = "🎵 Музыка";
        if (!_categories.ContainsKey("ebooks"))
            _categories["ebooks"] = "📚 Книга";
    }

    public Dictionary<string, string> GetCategories()
        => new(_categories);
}
