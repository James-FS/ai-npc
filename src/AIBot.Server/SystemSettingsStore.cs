using System;
using System.IO;
using Newtonsoft.Json;

namespace AIBot.Server
{
    /// <summary>
    /// 控制台集中管理的系统级设置（data/system-settings.json，已加入 .gitignore）。
    /// 目前只有全局 LLM API Key。部署级密钥始终留在部署侧文件，不进 MySQL 快照/迁移链路，
    /// 与环境变量 AIBOT_LLM_KEY 属同一信任层级。
    /// </summary>
    public static class SystemSettingsStore
    {
        private const string FileName = "system-settings.json";
        private const int SchemaVersion = 1;
        private static readonly object IoLock = new object();

        /// <summary>测试注入点：非 null 时直接使用该路径，不再查找 data/ 根目录。</summary>
        internal static string OverridePath = null;

        private sealed class SystemSettingsDto
        {
            [JsonProperty("schemaVersion")] public int schemaVersion = SchemaVersion;
            [JsonProperty("llm")] public LlmSettingsDto Llm = new LlmSettingsDto();
        }

        private sealed class LlmSettingsDto
        {
            [JsonProperty("apiKey")] public string ApiKey;
        }

        public static string SettingsPath
        {
            get
            {
                if (OverridePath != null) return OverridePath;
                string root = DataStore.FindDataRoot();
                return root == null ? null : Path.Combine(root, FileName);
            }
        }

        /// <summary>控制台保存的全局 LLM Key；未配置或文件不可读时返回 null（不抛异常，聊天主链路不容失败）。</summary>
        public static string LoadLlmApiKey()
        {
            try
            {
                string path = SettingsPath;
                if (path == null || !File.Exists(path)) return null;
                lock (IoLock)
                {
                    if (!File.Exists(path)) return null;
                    var dto = JsonConvert.DeserializeObject<SystemSettingsDto>(File.ReadAllText(path));
                    string key = dto?.Llm?.ApiKey;
                    return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SystemSettingsStore][warning] 读取系统设置失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>写入全局 Key；key 为空白视为清除。data/ 根目录未找到时返回 false。</summary>
        public static bool SaveLlmApiKey(string apiKey)
        {
            string path = SettingsPath;
            if (path == null) return false;
            string trimmed = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            try
            {
                lock (IoLock)
                {
                    if (trimmed == null && !File.Exists(path)) return true;   // 无可清除项，幂等成功
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var dto = new SystemSettingsDto { Llm = new LlmSettingsDto { ApiKey = trimmed } };
                    WriteAtomic(path, JsonConvert.SerializeObject(dto, Formatting.Indented));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SystemSettingsStore][warning] 写入系统设置失败: " + ex.Message);
                return false;
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content);
                if (File.Exists(path)) File.Move(temp, path, true);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
