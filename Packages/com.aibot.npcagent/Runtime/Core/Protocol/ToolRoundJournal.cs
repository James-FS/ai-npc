using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using AIBot.Core.Llm;
using Newtonsoft.Json;

namespace AIBot.Core.Protocol
{
    public sealed class JournalToolCall
    {
        public ToolCallDto call;
        public bool executing;
        public ClientToolResult result;
    }

    public sealed class JournalToolRound
    {
        public string roundToken;
        public string resumeRequestId;
        public List<JournalToolCall> calls = new List<JournalToolCall>();
    }

    /// <summary>
    /// Durable client-side execution journal. Persist intent before each side effect and
    /// results before resuming. An interrupted side effect is never automatically repeated.
    /// Keep the journal alongside the game's save data for the session's entire lifetime.
    /// </summary>
    public sealed class ToolRoundJournal
    {
        public sealed class Document
        {
            public string pendingToken;
            public Dictionary<string, JournalToolRound> rounds = new Dictionary<string, JournalToolRound>();
        }

        private readonly string _path;
        private Document _document;

        public ToolRoundJournal(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _document = File.Exists(path)
                ? JsonConvert.DeserializeObject<Document>(File.ReadAllText(path))
                    ?? throw new IOException("工具执行记录损坏，拒绝重新执行")
                : new Document();
            if (_document.rounds == null) throw new IOException("工具执行记录损坏，拒绝重新执行");
        }

        public JournalToolRound Pending
        {
            get
            {
                if (_document.pendingToken == null) return null;
                if (!_document.rounds.TryGetValue(_document.pendingToken, out JournalToolRound round))
                    throw new IOException("工具续跑记录缺失，拒绝重新执行");
                return Copy(round);
            }
        }

        public async Task<JournalToolRound> ExecuteAsync(string token, List<ToolCallDto> calls,
            Func<ToolCallDto, Task<ClientToolResult>> execute, CancellationToken ct = default(CancellationToken))
        {
            using (AcquireFileLock())
            {
                Reload();
                if (string.IsNullOrWhiteSpace(token) || calls == null || calls.Count == 0)
                    throw new ArgumentException("无效的工具挂起轮");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (ToolCallDto call in calls)
                    if (string.IsNullOrEmpty(call?.Id) || string.IsNullOrEmpty(call.Function?.Name) || !ids.Add(call.Id))
                        throw new ArgumentException("工具调用必须具有唯一的 callId 和工具名");

                if (!_document.rounds.TryGetValue(token, out JournalToolRound round))
                {
                    round = new JournalToolRound { roundToken = token, resumeRequestId = ChatRequestIds.NewId() };
                    foreach (ToolCallDto call in calls) round.calls.Add(new JournalToolCall { call = Copy(call) });
                    _document.rounds.Add(token, round);
                }
                if (round.calls.Count != calls.Count) throw new InvalidOperationException("工具挂起轮重放内容不一致");
                for (int i = 0; i < calls.Count; i++)
                    if (JsonConvert.SerializeObject(round.calls[i].call) != JsonConvert.SerializeObject(calls[i]))
                        throw new InvalidOperationException("工具挂起轮重放内容不一致");

                if (_document.pendingToken != token)
                {
                    _document.pendingToken = token;
                    Save();
                }
                foreach (JournalToolCall item in round.calls)
                {
                    if (item.result != null) continue;
                    if (item.executing)
                        throw new InvalidOperationException("工具执行状态不确定，请查询游戏业务结果并调用 ConfirmServerToolResult：" + item.call.Id);
                    ct.ThrowIfCancellationRequested();
                    item.executing = true;
                    Save();
                    ClientToolResult result = await execute(Copy(item.call));
                    if (result == null) throw new InvalidOperationException("工具未返回可持久化的执行结果");
                    result.CallId = item.call.Id;
                    CommitResult(item, result);
                }
                return Copy(round);
            }
        }

        public void ConfirmResult(string token, string callId, ClientToolResult result)
        {
            using (AcquireFileLock())
            {
                Reload();
                if (result == null) throw new ArgumentNullException(nameof(result));
                if (!_document.rounds.TryGetValue(token, out JournalToolRound round))
                    throw new KeyNotFoundException("工具轮不存在");
                JournalToolCall item = round.calls.Find(x => x.call.Id == callId)
                    ?? throw new KeyNotFoundException("callId 不存在");
                if (item.result != null) throw new InvalidOperationException("该工具已具有确定结果");
                result.CallId = callId;
                CommitResult(item, result);
            }
        }

        public void Complete(string roundToken)
        {
            using (AcquireFileLock())
            {
                Reload();
                if (_document.pendingToken != roundToken) return;
                string previous = _document.pendingToken;
                _document.pendingToken = null;
                try { Save(); }
                catch { _document.pendingToken = previous; throw; }
            }
        }

        private FileStream AcquireFileLock()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private void Reload()
        {
            if (!File.Exists(_path)) return;
            _document = JsonConvert.DeserializeObject<Document>(File.ReadAllText(_path))
                ?? throw new IOException("工具执行记录损坏，拒绝重新执行");
            if (_document.rounds == null) throw new IOException("工具执行记录损坏，拒绝重新执行");
        }

        private void CommitResult(JournalToolCall item, ClientToolResult result)
        {
            item.result = Copy(result);
            item.executing = false;
            try { Save(); }
            catch { item.result = null; item.executing = true; throw; }
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                // Flush the intent/result before executing or acknowledging a side effect.
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file))
                {
                    writer.Write(JsonConvert.SerializeObject(_document));
                    writer.Flush();
                    file.Flush(true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(_path)) File.Replace(temp, _path, null, true);
                        else File.Move(temp, _path);
                        break;
                    }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(20 * (attempt + 1)); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
    }
}
