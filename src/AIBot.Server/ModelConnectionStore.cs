using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIBot.Core.Config;
using AIBot.Core.Memory;
using Newtonsoft.Json;

namespace AIBot.Server
{
    public sealed class ModelConnection
    {
        public string id;
        public string name;
        public string baseUrl;
        public string apiFormat = "openai_chat_completions";
        public string apiKey;
        public string model;
    }

    public sealed class ModelConnectionSaveRequest
    {
        public ModelConnection connection;
        public bool clearApiKey;
    }

    public sealed class ModelConnectionBindingRequest
    {
        public string scope;
        public string gameId;
        public string npcId;
        public string connectionId;
        public string summaryMode;
    }

    public sealed class ModelConnectionDocument
    {
        public List<ModelConnection> connections = new List<ModelConnection>();
        public string globalMain;
        public Dictionary<string, string> npcMain = new Dictionary<string, string>();
        public Dictionary<string, string> npcSummary = new Dictionary<string, string>();
        public Dictionary<string, string> gameSummary = new Dictionary<string, string>();
        public Dictionary<string, string> npcSummaryMode = new Dictionary<string, string>();
    }

    public sealed class SummaryModelResolution
    {
        public ModelSettings settings;
        public string source;
        public string connectionId;
        public string connectionName;
        public string diagnostic;
        public bool dedicated;
    }

    /// <summary>Server 模式使用的完整模型连接配置及分配。所有运行路径读取同一文件。</summary>
    public static class ModelConnectionStore
    {
        private static readonly object IoLock = new object();
        internal static string OverridePath = null;
        public static string SettingsPath => OverridePath ?? (DataStore.FindDataRoot() is string root
            ? Path.Combine(root, "model-connections.json") : null);

        private static ModelConnectionDocument LoadUnsafe()
        {
            string path = SettingsPath;
            if (path == null) throw new InvalidOperationException("未找到 data/ 根目录");
            if (!File.Exists(path)) return new ModelConnectionDocument();
            var doc = JsonConvert.DeserializeObject<ModelConnectionDocument>(File.ReadAllText(path))
                ?? new ModelConnectionDocument();
            doc.connections ??= new List<ModelConnection>();
            doc.npcMain ??= new Dictionary<string, string>();
            doc.npcSummary ??= new Dictionary<string, string>();
            doc.gameSummary ??= new Dictionary<string, string>();
            doc.npcSummaryMode ??= new Dictionary<string, string>();
            return doc;
        }

        private static void SaveUnsafe(ModelConnectionDocument doc)
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonConvert.SerializeObject(doc, Formatting.Indented));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static ModelConnectionDocument Snapshot()
        {
            lock (IoLock) return LoadUnsafe();
        }

        internal static void SaveMigration(ModelConnectionDocument document)
        {
            lock (IoLock) SaveUnsafe(document);
        }

        public static string Validate(ModelConnection value, bool requireKey = false)
        {
            if (value == null || !DataStore.IsValidId(value.id)) return "连接 ID 不合法";
            if (string.IsNullOrWhiteSpace(value.name) || value.name.Length > 100) return "名称长度需为 1~100";
            if (value.apiFormat != "openai_chat_completions") return "目前只支持 OpenAI 兼容 Chat Completions";
            if (string.IsNullOrWhiteSpace(value.model) || value.model.Length > 128) return "模型 ID 长度需为 1~128";
            if (value.baseUrl == null || value.baseUrl != value.baseUrl.Trim()
                || !Uri.TryCreate(value.baseUrl, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo) || value.baseUrl.Length > 2048)
                return "Base URL 需为不含查询参数、片段或账号信息的 HTTP(S) 地址";
            if (value.baseUrl.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return "Base URL 请填写接口前缀，不要包含 /chat/completions";
            if (!string.IsNullOrEmpty(value.apiKey)
                && (value.apiKey.Length > 4096 || value.apiKey.Any(char.IsControl))) return "API Key 格式不合法";
            if (requireKey && string.IsNullOrWhiteSpace(value.apiKey)) return "API Key 必填";
            return null;
        }

        public static bool HasApiKey(string id)
        {
            lock (IoLock)
                return !string.IsNullOrWhiteSpace(LoadUnsafe().connections
                    .FirstOrDefault(x => x.id == id)?.apiKey);
        }

        public static bool Save(ModelConnection incoming, bool clearKey, bool create)
        {
            lock (IoLock)
            {
                ModelConnectionDocument doc = LoadUnsafe();
                ModelConnection existing = doc.connections.FirstOrDefault(x => x.id == incoming.id);
                if (create == (existing != null)) return false;
                if (clearKey || (create && string.IsNullOrWhiteSpace(incoming.apiKey))) return false;
                incoming.baseUrl = incoming.baseUrl.TrimEnd('/');
                incoming.apiKey = string.IsNullOrWhiteSpace(incoming.apiKey)
                    ? existing?.apiKey : incoming.apiKey.Trim();
                if (existing != null) doc.connections.Remove(existing);
                doc.connections.Add(incoming);
                SaveUnsafe(doc);
                return true;
            }
        }

        public static bool Delete(string id)
        {
            lock (IoLock)
            {
                ModelConnectionDocument doc = LoadUnsafe();
                // NPC 或 Game 已删除时清掉悬空分配，避免连接永远无法删除。
                foreach (string key in doc.npcMain.Keys.ToList())
                    if (!NpcExists(key)) doc.npcMain.Remove(key);
                foreach (string key in doc.npcSummary.Keys.ToList())
                    if (!NpcExists(key)) doc.npcSummary.Remove(key);
                var games = new HashSet<string>(DataStore.ListGameIds());
                foreach (string key in doc.gameSummary.Keys.ToList())
                    if (!games.Contains(key)) doc.gameSummary.Remove(key);
                if (doc.globalMain == id || doc.npcMain.Values.Contains(id) || doc.npcSummary.Values.Contains(id)
                    || doc.gameSummary.Values.Contains(id)) return false;
                int removed = doc.connections.RemoveAll(x => x.id == id);
                if (removed == 0) return false;
                SaveUnsafe(doc);
                return true;
            }
        }

        public static bool SetBinding(ModelConnectionBindingRequest request)
        {
            lock (IoLock)
            {
                ModelConnectionDocument doc = LoadUnsafe();
                if (!string.IsNullOrWhiteSpace(request.connectionId)
                    && !doc.connections.Any(x => x.id == request.connectionId
                        && (request.scope == "npc_main" || request.scope == "global_main"
                            ? !string.IsNullOrWhiteSpace(x.apiKey) : true))) return false;
                if (request.scope == "global_main")
                {
                    doc.globalMain = string.IsNullOrWhiteSpace(request.connectionId) ? null : request.connectionId;
                    SaveUnsafe(doc);
                    return true;
                }
                Dictionary<string, string> map = request.scope == "npc_main" ? doc.npcMain
                    : request.scope == "npc_summary" ? doc.npcSummary : doc.gameSummary;
                string key = request.scope == "game_summary" ? request.gameId : Pair(request.gameId, request.npcId);
                if (request.scope == "npc_summary" && request.summaryMode != null)
                {
                    if (!new[] { "inherit", "main", "dedicated" }.Contains(request.summaryMode)) return false;
                    doc.npcSummaryMode[key] = request.summaryMode;
                    if (request.summaryMode != "dedicated") map.Remove(key);
                    else if (string.IsNullOrWhiteSpace(request.connectionId)) map.Remove(key);
                    else map[key] = request.connectionId;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(request.connectionId)) map.Remove(key);
                    else map[key] = request.connectionId;
                    if (request.scope == "npc_summary")
                        doc.npcSummaryMode[key] = string.IsNullOrWhiteSpace(request.connectionId) ? "inherit" : "dedicated";
                }
                SaveUnsafe(doc);
                return true;
            }
        }

        public static void ClearNpcBindings(string gameId, string npcId)
        {
            lock (IoLock)
            {
                ModelConnectionDocument doc = LoadUnsafe();
                string key = Pair(gameId, npcId);
                bool changed = doc.npcMain.Remove(key) | doc.npcSummary.Remove(key) | doc.npcSummaryMode.Remove(key);
                if (changed) SaveUnsafe(doc);
            }
        }

        private static string Pair(string gameId, string npcId) => gameId + "/" + npcId;
        private static bool NpcExists(string pair)
        {
            int separator = pair.IndexOf('/');
            return separator > 0 && DataStore.LoadNpc(pair.Substring(0, separator),
                pair.Substring(separator + 1)) != null;
        }
        private static ModelConnection Bound(ModelConnectionDocument doc, Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string id)) return null;
            return doc.connections.FirstOrDefault(x => x.id == id)
                ?? throw new InvalidOperationException("模型连接配置不存在: " + id);
        }

        private static ModelSettings Settings(ModelConnection connection, ModelSettings defaults)
        {
            return new ModelSettings
            {
                baseUrl = connection.baseUrl,
                model = connection.model,
                apiKey = connection.apiKey,
                temperature = defaults?.temperature ?? 0.8f,
                maxTokens = defaults?.maxTokens ?? 500,
                timeoutMs = defaults?.timeoutMs ?? 20000
            };
        }

        private static ModelConnection MainConnection(ModelConnectionDocument doc, string gameId, string npcId)
        {
            ModelConnection connection = Bound(doc, doc.npcMain, Pair(gameId, npcId))
                ?? (doc.globalMain == null ? null : doc.connections.FirstOrDefault(x => x.id == doc.globalMain)
                    ?? throw new InvalidOperationException("模型连接配置不存在: " + doc.globalMain));
            if (connection == null)
                throw new InvalidOperationException("未分配 Server 主模型连接；请在管理台“模型”入口绑定 NPC 连接或控制台默认连接");
            return connection;
        }

        public static ModelSettings ResolveMain(string gameId, string npcId, ModelSettings legacy)
        {
            return Settings(MainConnection(Snapshot(), gameId, npcId), legacy);
        }

        public static SummaryModelResolution ResolveSummary(string gameId, string npcId, MemorySettings npcMemory,
            MemoryPolicy policy, ModelSettings mainSettings, MemoryPolicyOverrides sessionOverride = null)
        {
            ModelConnectionDocument doc = Snapshot();
            string pair = Pair(gameId, npcId);
            doc.npcSummaryMode.TryGetValue(pair, out string mode);
            bool sessionChoice = sessionOverride?.useMainSummaryModel.HasValue == true;
            bool chooseMain = sessionOverride?.useMainSummaryModel == true
                || !sessionChoice && (mode == "main" || mode == null && npcMemory?.useMainSummaryModel == true);
            ModelConnection connection = null;
            string source = "main";
            if (!chooseMain)
            {
                connection = Bound(doc, doc.npcSummary, pair);
                if (connection != null) source = "npc";
                else
                {
                    bool inheritGame = mode == "inherit"
                        ? npcMemory?.inheritGameDefaults == true
                        : mode == null && npcMemory?.inheritGameDefaults == true
                            && (sessionChoice || npcMemory?.useMainSummaryModel != false);
                    // An explicit inherit selection follows the established inheritGameDefaults switch.
                    if (inheritGame && npcMemory?.inheritGameDefaults == true)
                    {
                        connection = Bound(doc, doc.gameSummary, gameId);
                        if (connection != null) source = "game";
                    }
                    bool needsDedicated = sessionOverride?.useMainSummaryModel == false || mode == "dedicated"
                        || mode == null && npcMemory?.useMainSummaryModel == false;
                    if (connection == null && needsDedicated)
                        return new SummaryModelResolution { settings = mainSettings, source = "invalid", diagnostic =
                            "NPC 选择了独立摘要模型，但未分配有效摘要连接；请在模型连接页绑定 NPC/Game 连接或选择复用主模型" };
                }
            }
            if (connection == null)
                return new SummaryModelResolution { settings = CopySettings(mainSettings, policy.summaryModel), source = "main", connectionId = MainConnection(doc, gameId, npcId).id, connectionName = MainConnection(doc, gameId, npcId).name };
            string key = string.IsNullOrWhiteSpace(connection.apiKey) ? MainConnection(doc, gameId, npcId).apiKey : connection.apiKey;
            var settings = Settings(connection, policy.summaryModel);
            settings.apiKey = key;
            return new SummaryModelResolution { settings = settings, source = source, connectionId = connection.id, connectionName = connection.name, dedicated = true };
        }

        private static ModelSettings CopySettings(ModelSettings source, ModelSettings parameters) => new ModelSettings
        {
            baseUrl = source?.baseUrl, model = source?.model, apiKey = source?.apiKey,
            temperature = parameters?.temperature ?? source?.temperature ?? 0.8f,
            maxTokens = parameters?.maxTokens ?? source?.maxTokens ?? 500,
            timeoutMs = parameters?.timeoutMs ?? source?.timeoutMs ?? 20000
        };

        // Kept for existing internal callers/tests; runtime code uses ResolveSummary exactly once.
        public static bool ApplySummary(string gameId, string npcId, MemorySettings npcMemory, MemoryPolicy policy,
            MemoryPolicyOverrides sessionOverride = null)
        {
            ModelSettings main = null;
            try { main = ResolveMain(gameId, npcId, null); } catch (InvalidOperationException) { }
            SummaryModelResolution result = ResolveSummary(gameId, npcId, npcMemory, policy, main, sessionOverride);
            if (result.diagnostic != null) throw new InvalidOperationException(result.diagnostic);
            policy.summaryModel = result.settings;
            return result.dedicated;
        }

        public static object RedactedSnapshot()
        {
            ModelConnectionDocument doc = Snapshot();
            return new
            {
                connections = doc.connections.OrderBy(x => x.name).Select(x => new
                {
                    x.id, x.name, x.baseUrl, x.apiFormat, x.model,
                    hasApiKey = !string.IsNullOrWhiteSpace(x.apiKey),
                    maskedKey = Mask(x.apiKey)
                }).ToArray(),
                bindings = new { doc.globalMain, doc.npcMain, doc.npcSummary, doc.gameSummary, doc.npcSummaryMode }
            };
        }

        private static string Mask(string key) => string.IsNullOrEmpty(key) ? null
            : key.Length < 16 ? "***" : key.Substring(0, 7) + "*****" + key.Substring(key.Length - 4);
    }
}
