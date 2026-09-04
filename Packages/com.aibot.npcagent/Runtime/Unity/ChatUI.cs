using System.Text;
using AIBot.Core.Output;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AIBot.Unity
{
    /// <summary>最简对话 UI：输入框 + 发送按钮 + 流式气泡（DemoSceneBuilder 自动装配）。</summary>
    public class ChatUI : MonoBehaviour
    {
        public NpcAgent agent;
        public TMP_InputField inputField;
        public Button sendButton;
        public TMP_Text bubble;

        private readonly StringBuilder _transcript = new StringBuilder();
        private readonly StringBuilder _streaming = new StringBuilder();
        private float _nextRefreshAt;
        private bool _refreshQueued;

        private string NpcName
        {
            get { return agent != null && agent.Config != null ? agent.Config.displayName : "NPC"; }
        }

        private void Start()
        {
            if (agent != null)
            {
                agent.onToken.AddListener(OnToken);
                agent.onReply.AddListener(OnReply);
                agent.onError.AddListener(OnError);
            }
            if (sendButton != null) sendButton.onClick.AddListener(Send);
            if (inputField != null) inputField.onSubmit.AddListener(_ => Send());
        }

        public void Send()
        {
            if (agent == null || inputField == null) return;
            string text = inputField.text.Trim();
            if (text.Length == 0) return;
            inputField.text = "";
            inputField.ActivateInputField();
            _transcript.AppendLine("你：" + text);
            _streaming.Length = 0;
            Refresh();
            agent.Chat(text);
        }

        private void OnToken(string delta)
        {
            _streaming.Append(delta);
            Refresh(false);
        }

        private void OnReply(StructuredReply reply)
        {
            _streaming.Length = 0;
            _transcript.AppendLine(NpcName + "：" + reply.say + "  [" + reply.emotion + "/" + reply.action + "]");
            Refresh(true);
        }

        private void OnError(string message)
        {
            _streaming.Length = 0;
            _transcript.AppendLine("<color=red>出错了：" + message + "</color>");
            Refresh(true);
        }

        private void Update()
        {
            if (_refreshQueued && Time.unscaledTime >= _nextRefreshAt)
                Refresh(true);
        }

        private void Refresh(bool immediate = true)
        {
            if (bubble == null) return;
            if (!immediate && Time.unscaledTime < _nextRefreshAt)
            {
                _refreshQueued = true;
                return;
            }
            bubble.text = _streaming.Length > 0
                ? _transcript.ToString() + NpcName + "：" + _streaming
                : _transcript.ToString();
            _nextRefreshAt = Time.unscaledTime + 0.05f;
            _refreshQueued = false;
        }
    }
}
