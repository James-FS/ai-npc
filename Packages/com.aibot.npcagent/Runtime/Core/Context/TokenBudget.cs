using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AIBot.Core.Context
{
    /// <summary>token 粗估与历史裁剪。初始按汉字≈1.2、其他≈1/4 估算，运行期用 usage 校准系数。</summary>
    public static class TokenBudget
    {
        public const int DefaultBudget = 6000;

        public static int Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int cjk = 0, other = 0;
            foreach (char c in text)
            {
                if (c >= 0x2E80) cjk++; else other++;
            }
            return (int)Math.Ceiling(cjk * 1.2 + other / 4.0);
        }

        /// <summary>从最旧开始裁剪历史消息，直到总估算进入预算。system prompt 不参与裁剪。</summary>
        public static List<T> TrimHistory<T>(List<T> history, string systemPrompt, int budget)
        {
            int used = Estimate(systemPrompt);
            foreach (T item in history) used += Estimate(item.ToString());
            List<T> result = new List<T>(history);
            while (used > budget && result.Count > 0)
            {
                used -= Estimate(result[0].ToString());
                result.RemoveAt(0);
            }
            return result;
        }

        /// <summary>按 NPC 用真实 usage 校准估算系数（0.3~3.0），让裁剪决策越来越准。</summary>
        public static class Calibration
        {
            private static readonly ConcurrentDictionary<string, double> Factors =
                new ConcurrentDictionary<string, double>();

            public static void Update(string npcId, int actualPromptTokens, int estimatedTokens)
            {
                if (actualPromptTokens <= 0 || estimatedTokens <= 0) return;
                double factor = (double)actualPromptTokens / estimatedTokens;
                if (factor < 0.3) factor = 0.3;
                if (factor > 3.0) factor = 3.0;
                Factors[npcId ?? "?"] = factor;
            }

            public static double Factor(string npcId)
            {
                double v;
                return Factors.TryGetValue(npcId ?? "?", out v) ? v : 1.0;
            }

            public static int EstimateCalibrated(string npcId, string text)
            {
                return (int)(Estimate(text) * Factor(npcId));
            }
        }
    }
}
