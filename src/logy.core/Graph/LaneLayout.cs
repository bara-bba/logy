using System.Text;

namespace Logy.Core.Graph;

/// <summary>A line piece inside one half of a row: from slot <see cref="From"/> to slot <see cref="To"/>.</summary>
public readonly record struct Segment(int From, int To, string ColorKey);

/// <summary>One drawn row (design doc 6.4). Never changed after it is built.</summary>
public sealed record GraphRow(
    Commit Commit,
    int CommitSlot,
    string CommitColorKey,
    IReadOnlyList<Segment> Top,
    IReadOnlyList<Segment> Bottom)
{
    /// <summary>Slots this row needs: max of its Top and Bottom extent.</summary>
    public int SlotCount
    {
        get
        {
            var max = CommitSlot;
            foreach (var s in Top)
            {
                max = Math.Max(max, Math.Max(s.From, s.To));
            }

            foreach (var s in Bottom)
            {
                max = Math.Max(max, Math.Max(s.From, s.To));
            }

            return max + 1;
        }
    }
}

/// <summary>One-pass lane layout, top to bottom (similar in spirit to git log --graph).</summary>
public static class LaneLayout
{
    private sealed class Slot
    {
        public required string Expected;
        public required string ColorKey;
    }

    public static List<GraphRow> Build(
        IReadOnlyList<Commit> commits,
        string mainBranch,
        IReadOnlyDictionary<string, string> branchOf)
    {
        _ = mainBranch; // colour is resolved at draw time via ColorIndex; kept for API symmetry
        var slots = new List<Slot?>();
        var rows = new List<GraphRow>(commits.Count);
        var branchNames = new HashSet<string>(branchOf.Values, StringComparer.Ordinal);

        foreach (var commit in commits)
        {
            var parents = commit.Parents.Distinct(StringComparer.Ordinal).ToList(); // step 0

            // Top half, from the slots before this row.
            var expecting = new List<int>();
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i]?.Expected == commit.Hash)
                {
                    expecting.Add(i);
                }
            }

            var top = new List<Segment>();
            int lane;
            string colorKey;
            if (expecting.Count > 0)
            {
                lane = expecting[0]; // step 1
                colorKey = slots[lane]!.ColorKey;
            }
            else
            {
                lane = FirstFree(slots); // step 2
                colorKey = branchOf.TryGetValue(commit.Hash, out var b) ? b : commit.Hash;
            }

            for (var i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s is null)
                {
                    continue;
                }

                top.Add(expecting.Contains(i) ? new Segment(i, lane, s.ColorKey) : new Segment(i, i, s.ColorKey));
            }

            foreach (var i in expecting.Skip(1))
            {
                slots[i] = null; // merge-ins end here
            }

            // Step 3: upgrade a hash key to the branch name carried by this commit.
            if (!branchNames.Contains(colorKey) && branchOf.TryGetValue(commit.Hash, out var named))
            {
                colorKey = named;
            }

            EnsureSize(slots, lane);
            var linked = new List<int>();
            if (parents.Count == 0)
            {
                slots[lane] = null; // step 6
            }
            else
            {
                slots[lane] = new Slot { Expected = parents[0], ColorKey = colorKey }; // step 4
                foreach (var p in parents.Skip(1)) // step 5
                {
                    var existing = -1;
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (i != lane && slots[i]?.Expected == p)
                        {
                            existing = i;
                            break;
                        }
                    }

                    if (existing < 0)
                    {
                        existing = FirstFree(slots);
                        EnsureSize(slots, existing);
                        slots[existing] = new Slot
                        {
                            Expected = p,
                            ColorKey = branchOf.TryGetValue(p, out var pb) ? pb : p,
                        };
                    }

                    if (!linked.Contains(existing))
                    {
                        linked.Add(existing);
                    }
                }
            }

            Trim(slots); // step 7

            // Bottom half, from the slots after this row.
            var bottom = new List<Segment>();
            for (var i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s is null)
                {
                    continue;
                }

                if (i == lane)
                {
                    bottom.Add(new Segment(lane, lane, colorKey));
                }
                else if (linked.Contains(i))
                {
                    bottom.Add(new Segment(lane, i, s.ColorKey));
                }
                else
                {
                    bottom.Add(new Segment(i, i, s.ColorKey));
                }
            }

            rows.Add(new GraphRow(commit, lane, colorKey, top, bottom));
        }

        return rows;
    }

    /// <summary>
    /// Palette index: paletteCount &lt; 2 → 0; the main branch → 0; otherwise
    /// FNV-1a-32(UTF-8 key) % (paletteCount - 1) + 1, so a branch keeps its colour.
    /// </summary>
    public static int ColorIndex(string colorKey, string mainBranch, int paletteCount)
    {
        if (paletteCount < 2 || colorKey == mainBranch)
        {
            return 0;
        }

        return (int)(Fnv1a(colorKey) % (uint)(paletteCount - 1)) + 1;
    }

    internal static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    private static int FirstFree(List<Slot?> slots)
    {
        var i = slots.IndexOf(null);
        return i >= 0 ? i : slots.Count;
    }

    private static void EnsureSize(List<Slot?> slots, int index)
    {
        while (slots.Count <= index)
        {
            slots.Add(null);
        }
    }

    private static void Trim(List<Slot?> slots)
    {
        while (slots.Count > 0 && slots[^1] is null)
        {
            slots.RemoveAt(slots.Count - 1);
        }
    }
}
