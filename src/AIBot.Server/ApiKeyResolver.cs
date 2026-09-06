using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AIBot.Server
{
    /// <summary>
    /// LLM API Key 统一解析入口（此前在 Chat/Admin 连接测试/MemorySummaryQueue/StartupDiagnostics
    /// 四处各写了一遍降级链，收敛到这里避免改漏）。优先级：
    /// NPC 配置 > 控制台全局设置（data/system-settings.json）> 环境变量 AIBOT_LLM_KEY > appsettings Llm:ApiKey。
    /// </summary>
    public static class ApiKeyResolver
    {
        public const string EnvVarName = "AIBOT_LLM_KEY";

        public static string Resolve(string npcApiKey, IConfiguration config)
        {
            return FirstNonEmpty(npcApiKey,
                SystemSettingsStore.LoadLlmApiKey(),
                Environment.GetEnvironmentVariable(EnvVarName),
                config?["Llm:ApiKey"]);
        }

        /// <summary>诊断/设置页用：key 生效自哪一层；只报来源，绝不返回 key 本身。</summary>
        public static string SourceOf(string npcApiKey, IConfiguration config)
        {
            if (FirstNonEmpty(npcApiKey) != null) return "npc";
            if (FirstNonEmpty(SystemSettingsStore.LoadLlmApiKey()) != null) return "console";
            if (FirstNonEmpty(Environment.GetEnvironmentVariable(EnvVarName)) != null) return "env";
            if (FirstNonEmpty(config?["Llm:ApiKey"]) != null) return "appsettings";
            return "none";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
    }
}
