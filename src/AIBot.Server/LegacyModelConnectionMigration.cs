using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AIBot.Core.Config;
using AIBot.Core.Memory;

namespace AIBot.Server
{
    /// <summary>
    /// Moves legacy inline Server credentials into model-connections.json before the old fields
    /// are removed. The connection file is committed first, so an interrupted run can resume.
    /// </summary>
    public static class LegacyModelConnectionMigration
    {
        public static int Run()
        {
            var migrated = new List<(string gameId, string npcId, string scope, ModelSettings settings)>();
            var document = ModelConnectionStore.Snapshot();
            var connectionByKey = document.connections.GroupBy(ConnectionKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (string gameId in DataStore.ListGameIds())
            {
                MemoryPolicy policy = DataStore.LoadMemoryPolicy(gameId);
                if (policy?.summaryModel != null && HasLegacySummary(policy.summaryModel))
                {
                    ModelConnection connection = Ensure(policy.summaryModel, $"{gameId} summary", document, connectionByKey);
                    document.gameSummary.TryAdd(gameId, connection.id);
                    migrated.Add((gameId, null, "game-summary", policy.summaryModel));
                }

                foreach (string npcId in DataStore.ListNpcIds(gameId))
                {
                    AgentConfigDto npc = DataStore.LoadNpc(gameId, npcId);
                    if (npc?.model != null && HasLegacyMain(npc.model))
                    {
                        ModelConnection connection = Ensure(npc.model, $"{gameId}/{npcId}", document, connectionByKey);
                        string pair = gameId + "/" + npcId;
                        // Before migration NPC binding > global binding > inline legacy settings.
                        // Preserve any effective stored binding and only bind inline credentials
                        // when they were the actual source before this migration.
                        if (!document.npcMain.ContainsKey(pair) && string.IsNullOrWhiteSpace(document.globalMain))
                            document.npcMain[pair] = connection.id;
                        migrated.Add((gameId, npcId, "npc-main", npc.model));
                    }
                    else if (npc?.model != null
                        && (!string.IsNullOrWhiteSpace(npc.model.baseUrl) || !string.IsNullOrWhiteSpace(npc.model.model))
                        && (!string.IsNullOrWhiteSpace(document.globalMain)
                            || document.npcMain.ContainsKey(gameId + "/" + npcId)))
                    {
                        // A stored connection already won over these legacy defaults before
                        // migration. Remove the shadowed address/model fields without creating
                        // a per-NPC binding or inventing credentials.
                        migrated.Add((gameId, npcId, "npc-main", npc.model));
                    }
                    if (npc?.memory?.summaryModel != null && HasLegacySummary(npc.memory.summaryModel))
                    {
                        ModelConnection connection = Ensure(npc.memory.summaryModel, $"{gameId}/{npcId} summary", document, connectionByKey);
                        document.npcSummary.TryAdd(gameId + "/" + npcId, connection.id);
                        migrated.Add((gameId, npcId, "npc-summary", npc.memory.summaryModel));
                    }
                }
            }

            if (migrated.Count == 0) return 0;
            ModelConnectionStore.SaveMigration(document);

            foreach (var item in migrated)
            {
                if (item.scope == "game-summary")
                {
                    MemoryPolicy policy = DataStore.LoadMemoryPolicy(item.gameId);
                    ClearInlineSettings(policy.summaryModel);
                    if (!DataStore.SaveMemoryPolicy(item.gameId, policy))
                        throw new InvalidOperationException("Game 模型配置迁移保存失败: " + item.gameId);
                }
                else
                {
                    AgentConfigDto npc = DataStore.LoadNpc(item.gameId, item.npcId);
                    ModelSettings settings = item.scope == "npc-main" ? npc.model : npc.memory.summaryModel;
                    ClearInlineSettings(settings);
                    if (!DataStore.SaveNpc(item.gameId, npc))
                        throw new InvalidOperationException("NPC 模型配置迁移保存失败: " + item.gameId + "/" + item.npcId);
                }
            }
            return migrated.Count;
        }

        private static bool HasLegacyMain(ModelSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.baseUrl) && !string.IsNullOrWhiteSpace(settings.model)
            && !string.IsNullOrWhiteSpace(settings.apiKey);

        private static bool HasLegacySummary(ModelSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.baseUrl) && !string.IsNullOrWhiteSpace(settings.model);

        private static ModelConnection Ensure(ModelSettings settings, string name,
            ModelConnectionDocument document, Dictionary<string, ModelConnection> byKey)
        {
            string key = ConnectionKey(settings);
            if (byKey.TryGetValue(key, out ModelConnection found)) return found;
            var connection = new ModelConnection
            {
                id = "legacy_" + Hash(key), name = "Imported " + name,
                baseUrl = settings.baseUrl.TrimEnd('/'), model = settings.model,
                apiFormat = "openai_chat_completions", apiKey = settings.apiKey
            };
            document.connections.Add(connection);
            byKey[key] = connection;
            return connection;
        }

        private static string ConnectionKey(ModelConnection connection) =>
            connection.baseUrl + "\n" + connection.model + "\n" + connection.apiKey;

        private static string ConnectionKey(ModelSettings settings) =>
            settings.baseUrl.TrimEnd('/') + "\n" + settings.model + "\n" + settings.apiKey;

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 12)
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void ClearInlineSettings(ModelSettings settings)
        {
            settings.baseUrl = null;
            settings.model = null;
            settings.apiKey = null;
        }
    }
}
