namespace Callsign.Core.Economy;

/// <summary>
/// Fans a single board refresh across several job sources (cargo, passenger, …), splitting the
/// requested count by each source's relative share with a largest-remainder allocation so the parts
/// always sum to the whole. Each source gets a distinct derived seed, so the board stays deterministic
/// per seed yet the streams don't mirror one another. The board sorts the merged result for display.
/// </summary>
public sealed class CompositeJobSource : IJobSource
{
    private readonly IReadOnlyList<(IJobSource Source, double Weight)> _sources;

    public CompositeJobSource(params (IJobSource Source, double Weight)[] sources)
    {
        if (sources is null || sources.Length == 0)
            throw new ArgumentException("At least one source is required.", nameof(sources));
        _sources = sources.Where(s => s.Weight > 0).ToList();
        if (_sources.Count == 0)
            throw new ArgumentException("At least one source must have a positive weight.", nameof(sources));
    }

    public IReadOnlyList<GeneratedJob> Generate(JobGenerationRequest request)
    {
        var counts = Split(request.Count, _sources.Select(s => s.Weight).ToList());
        var result = new List<GeneratedJob>(request.Count);
        var produced = new bool[_sources.Count];
        for (int i = 0; i < _sources.Count; i++)
        {
            if (counts[i] == 0)
                continue;
            int seed = unchecked(request.Seed * 397 + i * 31 + 17); // distinct, deterministic per source
            var jobs = _sources[i].Source.Generate(request with { Count = counts[i], Seed = seed });
            produced[i] = jobs.Count > 0;
            result.AddRange(jobs);
        }

        // Backfill (Phase 12): a source can now come back empty — e.g. every mission of that type sits above the
        // pilot's rank — which would leave the board short of the count we asked for. Re-apportion the shortfall
        // across the sources that DID produce work (with a fresh, distinct seed), so the board still fills up with
        // jobs the pilot can actually take instead of showing a handful. Deterministic, and a single pass: if no
        // source produced anything (an isolated field with no reachable work), the board is simply empty.
        int deficit = request.Count - result.Count;
        if (deficit > 0)
        {
            var live = new List<int>();
            for (int i = 0; i < _sources.Count; i++)
                if (produced[i]) live.Add(i);
            if (live.Count > 0)
            {
                var fill = Split(deficit, live.Select(i => _sources[i].Weight).ToList());
                for (int k = 0; k < live.Count; k++)
                {
                    if (fill[k] == 0)
                        continue;
                    int i = live[k];
                    int seed = unchecked(request.Seed * 911 + i * 53 + 29); // distinct from the first pass
                    result.AddRange(_sources[i].Source.Generate(request with { Count = fill[k], Seed = seed }));
                }
            }
        }
        return result;
    }

    /// <summary>Largest-remainder apportionment of <paramref name="total"/> across the given weights.</summary>
    private static int[] Split(int total, IReadOnlyList<double> weights)
    {
        var counts = new int[weights.Count];
        double sum = weights.Sum();
        if (sum <= 0 || total <= 0)
            return counts;

        var remainders = new List<(int Index, double Frac)>(weights.Count);
        int assigned = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            double exact = total * weights[i] / sum;
            counts[i] = (int)Math.Floor(exact);
            assigned += counts[i];
            remainders.Add((i, exact - counts[i]));
        }
        foreach (var r in remainders.OrderByDescending(x => x.Frac).ThenBy(x => x.Index))
        {
            if (assigned >= total)
                break;
            counts[r.Index]++;
            assigned++;
        }
        return counts;
    }
}
