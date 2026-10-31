using System;
using System.Collections.Generic;
using System.Linq;

namespace Hibira
{
    /// <summary>A player waiting in a <see cref="HibiraQueue"/>, as the queue host sees them.</summary>
    public sealed class HibiraTicket
    {
        public string Id { get; }
        /// <summary>When the auth endpoint signed the subscription, ms since epoch.</summary>
        public double JoinedAt { get; }
        public double Rating { get; }
        public IReadOnlyDictionary<string, object> Criteria { get; }
        /// <summary>The member's user_info.</summary>
        public object Info { get; }

        public HibiraTicket(string id, double joinedAt, double rating = 0, IReadOnlyDictionary<string, object> criteria = null, object info = null)
        {
            Id = id;
            JoinedAt = joinedAt;
            Rating = rating;
            Criteria = criteria ?? new Dictionary<string, object>();
            Info = info;
        }

        internal static HibiraTicket FromMember(string id, object info)
        {
            var i = info as Dictionary<string, object>;
            var t = Json.Get(i, "joined_at");
            var r = Json.Get(i, "rating");
            return new HibiraTicket(
                id,
                t != null && Json.IsNumber(t) ? Json.ToDouble(t) : double.MaxValue,
                r != null && Json.IsNumber(r) ? Json.ToDouble(r) : 0,
                Json.Get(i, "criteria") as Dictionary<string, object>,
                info);
        }
    }

    /// <summary>
    /// Matching rules for <see cref="HibiraQueue"/>, the same as
    /// findMatches() in @hibira-io/lobby so Unity and browser hosts match alike.
    /// Times are in seconds.
    /// </summary>
    public class HibiraMatchRules
    {
        /// <summary>Players per match.</summary>
        public int Size = 2;
        /// <summary>Smallest match once <see cref="FillAfter"/> has passed (0 = Size).</summary>
        public int MinSize;
        /// <summary>Seconds of waiting before a match smaller than Size is allowed.</summary>
        public double FillAfter = double.PositiveInfinity;
        /// <summary>Rating difference allowed at first.</summary>
        public double RatingRange = double.PositiveInfinity;
        /// <summary>Added to the range every <see cref="WidenEvery"/> seconds of waiting.</summary>
        public double WidenBy;
        public double WidenEvery = 5;
        /// <summary>The range never grows past this.</summary>
        public double MaxRange = double.PositiveInfinity;
        /// <summary>Extra rule on two tickets; return false to keep them apart.</summary>
        public Func<HibiraTicket, HibiraTicket, bool> Compatible;
    }

    public static class HibiraMatch
    {
        /// <summary>The rating range a ticket accepts after waiting <paramref name="waited"/> seconds.</summary>
        public static double AllowedRange(double waited, HibiraMatchRules rules)
        {
            var steps = rules.WidenBy > 0 && rules.WidenEvery > 0 ? Math.Floor(Math.Max(0, waited) / rules.WidenEvery) : 0;
            return Math.Min(rules.MaxRange, rules.RatingRange + steps * rules.WidenBy);
        }

        /// <summary>
        /// True when every criteria key both tickets have agrees: equal values,
        /// or arrays that share a value. A key only one side has is a wildcard.
        /// </summary>
        public static bool CriteriaFit(IReadOnlyDictionary<string, object> a, IReadOnlyDictionary<string, object> b)
        {
            if (a == null || b == null) return true;
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other)) continue;
                var xs = Values(kv.Value);
                var ys = Values(other);
                if (!xs.Any(x => ys.Any(y => Same(x, y)))) return false;
            }
            return true;
        }

        /// <summary>
        /// Group tickets into matches: longest wait first, closest ratings within
        /// the longest-waiting ticket's range, every pair fitting the criteria.
        /// Returns the tickets of each match, in queue order.
        /// </summary>
        public static List<List<HibiraTicket>> FindMatches(IEnumerable<HibiraTicket> tickets, HibiraMatchRules rules, double nowMs)
        {
            var size = Math.Max(1, rules.Size);
            var minSize = Math.Max(1, Math.Min(size, rules.MinSize > 0 ? rules.MinSize : size));
            var queue = tickets.ToList();
            queue.Sort(ByQueueOrder);
            var taken = new HashSet<string>();
            var matches = new List<List<HibiraTicket>>();

            foreach (var anchor in queue)
            {
                if (taken.Contains(anchor.Id)) continue;
                var waited = (nowMs - anchor.JoinedAt) / 1000;
                var range = AllowedRange(waited, rules);
                var candidates = queue
                    .Where(t => t != anchor && !taken.Contains(t.Id) && Fits(anchor, t, range, rules))
                    .ToList();
                candidates.Sort((a, b) =>
                {
                    var c = Math.Abs(a.Rating - anchor.Rating).CompareTo(Math.Abs(b.Rating - anchor.Rating));
                    return c != 0 ? c : ByQueueOrder(a, b);
                });

                var group = new List<HibiraTicket> { anchor };
                foreach (var c in candidates)
                {
                    if (group.Count >= size) break;
                    if (group.All(g => g == anchor || Fits(g, c, double.PositiveInfinity, rules))) group.Add(c);
                }
                var enough = group.Count >= size || (waited >= rules.FillAfter && group.Count >= minSize);
                if (!enough) continue;
                group.Sort(ByQueueOrder);
                foreach (var t in group) taken.Add(t.Id);
                matches.Add(group);
            }
            return matches;
        }

        static bool Fits(HibiraTicket a, HibiraTicket b, double range, HibiraMatchRules rules)
        {
            if (Math.Abs(a.Rating - b.Rating) > range) return false;
            if (!CriteriaFit(a.Criteria, b.Criteria)) return false;
            return rules.Compatible == null || rules.Compatible(a, b);
        }

        static IEnumerable<object> Values(object v) =>
            v is IEnumerable<object> list && !(v is string) ? list : new[] { v };

        static bool Same(object a, object b)
        {
            if (a != null && b != null && Json.IsNumber(a) && Json.IsNumber(b)) return Json.ToDouble(a) == Json.ToDouble(b);
            return Equals(a, b);
        }

        static int ByQueueOrder(HibiraTicket a, HibiraTicket b) =>
            a.JoinedAt != b.JoinedAt ? a.JoinedAt.CompareTo(b.JoinedAt) : string.CompareOrdinal(a.Id, b.Id);
    }
}
