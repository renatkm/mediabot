using MediaBot.Configuration;
using MediaBot.Models;
using MediaBot.Services.Indexers;
using Microsoft.Extensions.Options;

namespace MediaBot.Services;

public class TorrentAggregator
{
    private readonly IEnumerable<ITorrentIndexer> _indexers;
    private readonly SearchConfig _searchConfig;
    private readonly ILogger<TorrentAggregator> _logger;

    public TorrentAggregator(
        IEnumerable<ITorrentIndexer> indexers,
        IOptions<SearchConfig> searchConfig,
        ILogger<TorrentAggregator> logger)
    {
        _indexers = indexers;
        _searchConfig = searchConfig.Value;
        _logger = logger;
    }

    public IReadOnlyList<string> IndexerNames => _indexers.Select(i => i.Name).ToList();

    public async Task<List<SearchResult>> SearchAsync(string query, string? indexerName = null, CancellationToken ct = default, bool applyFilters = true)
    {
        var targets = indexerName == null
            ? _indexers
            : _indexers.Where(i => i.Name.Equals(indexerName, StringComparison.OrdinalIgnoreCase));

        var tasks = targets.Select(async indexer =>
        {
            try
            {
                return await indexer.SearchAsync(query, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Indexer {Name} failed", indexer.Name);
                return new List<SearchResult>();
            }
        });

        var results = await Task.WhenAll(tasks);

        return results
            .SelectMany(r => r)
            .Where(r => !applyFilters || PassesFilter(r))
            .OrderByDescending(r => r.Seeders)
            .ToList();
    }

    private bool PassesFilter(SearchResult r)
    {
        if (r.SizeBytes > 0 && r.SizeBytes > _searchConfig.MaxFileSizeBytes)
            return false;

        foreach (var kw in _searchConfig.ExcludeKeywordList)
            if (r.Title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}
