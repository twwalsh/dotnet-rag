namespace SupportRag.Core.Retrieval;

/// <summary>
/// Combines ranked lists without comparing their scores, which is the point: a cosine distance and a
/// ts_rank are not on the same scale. Each list contributes 1 / (k + rank); k = 60 is the value from the
/// original paper and from every search engine that has copied it since. Pure, so it has tests.
/// </summary>
public static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<string> Fuse(IEnumerable<IReadOnlyList<string>> rankedLists, int k = DefaultK)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal); // tie-break: earliest appearance wins
        var order = 0;
        foreach (var list in rankedLists)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank];
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (k + rank + 1);
                firstSeen.TryAdd(id, order++);
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => kv.Key)
            .ToList();
    }
}
