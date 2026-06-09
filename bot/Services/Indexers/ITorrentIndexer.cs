using MediaBot.Models;

namespace MediaBot.Services.Indexers;

public interface ITorrentIndexer
{
    string Name { get; }
    Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default);
}
