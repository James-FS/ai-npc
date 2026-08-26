using System;
using System.Collections.Generic;
using System.Linq;

namespace AIBot.Core.Memory
{
    /// <summary>结构化事实的确定性合并：同 key 更新、固定事实保护、数量上限。</summary>
    public static class MemoryFactMerger
    {
        public static List<MemoryFact> Merge(IEnumerable<MemoryFact> existing,
            IEnumerable<MemoryFact> incoming, int maxFacts, DateTime nowUtc)
        {
            var result = new List<MemoryFact>();
            foreach (MemoryFact fact in existing ?? Array.Empty<MemoryFact>())
            {
                if (fact == null || IsExpired(fact, nowUtc)) continue;
                result.Add(Clone(fact));
            }

            foreach (MemoryFact raw in incoming ?? Array.Empty<MemoryFact>())
            {
                if (raw == null || string.IsNullOrWhiteSpace(raw.value)) continue;
                MemoryFact next = Normalize(raw, nowUtc);
                int index = FindByKey(result, next.key);
                if (index < 0)
                {
                    result.Add(next);
                    continue;
                }

                MemoryFact current = result[index];
                if (current.pinned && !string.Equals(current.value, next.value, StringComparison.Ordinal))
                    continue;
                if (next.confidence + 0.0001f < current.confidence
                    && !string.Equals(current.value, next.value, StringComparison.Ordinal))
                    continue;

                next.id = string.IsNullOrEmpty(current.id) ? next.id : current.id;
                next.createdUtc = current.createdUtc == default(DateTime) ? next.createdUtc : current.createdUtc;
                next.pinned = current.pinned || next.pinned;
                result[index] = next;
            }

            int limit = Math.Max(1, maxFacts);
            List<MemoryFact> ordered = result
                .OrderByDescending(f => f.pinned)
                .ThenByDescending(f => f.updatedUtc)
                .ThenByDescending(f => f.confidence)
                .ToList();
            int pinnedCount = ordered.Count(f => f.pinned);
            return ordered.Take(Math.Max(limit, pinnedCount)).ToList();
        }

        public static MemoryFact FromLegacyString(string value, DateTime nowUtc, string sourceSessionId = null)
        {
            return Normalize(new MemoryFact
            {
                category = "legacy",
                key = "legacy." + StableHash(value ?? string.Empty),
                value = value,
                confidence = 0.5f,
                source = "migration",
                sourceSessionId = sourceSessionId
            }, nowUtc);
        }

        private static MemoryFact Normalize(MemoryFact source, DateTime nowUtc)
        {
            MemoryFact fact = Clone(source);
            if (string.IsNullOrEmpty(fact.id)) fact.id = "fact-" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(fact.category)) fact.category = "general";
            if (string.IsNullOrEmpty(fact.key)) fact.key = fact.category + "." + StableHash(fact.value ?? string.Empty);
            if (fact.confidence <= 0f) fact.confidence = 0.5f;
            if (fact.confidence > 1f) fact.confidence = 1f;
            if (fact.createdUtc == default(DateTime)) fact.createdUtc = nowUtc;
            fact.updatedUtc = nowUtc;
            return fact;
        }

        private static int FindByKey(List<MemoryFact> facts, string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            for (int i = 0; i < facts.Count; i++)
                if (string.Equals(facts[i].key, key, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static bool IsExpired(MemoryFact fact, DateTime nowUtc)
        {
            return fact.expiresUtc.HasValue && fact.expiresUtc.Value <= nowUtc;
        }

        private static MemoryFact Clone(MemoryFact source)
        {
            return new MemoryFact
            {
                id = source.id,
                category = source.category,
                key = source.key,
                value = source.value,
                confidence = source.confidence,
                source = source.source,
                sourceSessionId = source.sourceSessionId,
                createdUtc = source.createdUtc,
                updatedUtc = source.updatedUtc,
                pinned = source.pinned,
                expiresUtc = source.expiresUtc
            };
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }
    }
}
