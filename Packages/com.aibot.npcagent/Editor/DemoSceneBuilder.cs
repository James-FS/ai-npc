using AIBot.Unity;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace AIBot.Unity.EditorTools
{
    /// <summary>M1 Demo：一键在当前场景搭出 NPC + 对话 UI，连 data/ 下的示例配置。</summary>
    public static class DemoSceneBuilder
    {
        private const string NpcId = "blacksmith_wang";

        [MenuItem("AIBot/Demo/Create Demo Scene")]
        public static void Create()
        {
            AgentConfigAsset demoConfig = GetDemoConfig();
            var npcGo = new GameObject("NPC_老王");
            var agent = npcGo.AddComponent<NpcAgent>();
            agent.npcId = NpcId;
            agent.configAsset = demoConfig;
            var relay = npcGo.AddComponent<GameContextRelay>();
            relay.stage = 0;
            relay.favorability = 30;
            agent.gameContext = relay;

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.transform.SetParent(npcGo.transform, false);
            capsule.transform.localPosition = Vector3.zero;
            Object.DestroyImmediate(capsule.GetComponent<Collider>());

            var canvasGo = new GameObject("ChatCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var bubbleGo = new GameObject("Bubble", typeof(TextMeshProUGUI));
            bubbleGo.transform.SetParent(canvasGo.transform, false);
            var bubble = bubbleGo.GetComponent<TextMeshProUGUI>();
            bubble.fontSize = 22;
            bubble.richText = true;
            SetAnchors(bubble.rectTransform, new Vector2(0.05f, 0.25f), new Vector2(0.95f, 0.9f));

            var inputGo = new GameObject("InputField", typeof(TMP_InputField));
            inputGo.transform.SetParent(canvasGo.transform, false);
            var input = inputGo.GetComponent<TMP_InputField>();
            var inputRect = inputGo.GetComponent<RectTransform>();
            input.textViewport = inputRect;
            SetAnchors(inputRect, new Vector2(0.05f, 0.08f), new Vector2(0.75f, 0.18f));
            CreateChildText(inputGo.transform, "Placeholder", "跟老王说点什么…", new Color(1, 1, 1, 0.4f), out var phRect);
            input.placeholder = phRect;
            CreateChildText(inputGo.transform, "Text", "", Color.white, out var textRect);
            input.textComponent = textRect;
            input.textViewport = textRect.transform.parent.GetComponent<RectTransform>();

            var buttonGo = new GameObject("SendButton", typeof(Button), typeof(Image));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var button = buttonGo.GetComponent<Button>();
            var buttonRect = buttonGo.GetComponent<RectTransform>();
            SetAnchors(buttonRect, new Vector2(0.78f, 0.08f), new Vector2(0.95f, 0.18f));
            CreateChildText(buttonGo.transform, "Label", "发送", Color.black, out _);

            var chatGo = new GameObject("ChatUI");
            var chat = chatGo.AddComponent<ChatUI>();
            chat.agent = agent;
            chat.inputField = input;
            chat.sendButton = button;
            chat.bubble = bubble;

            Selection.activeGameObject = npcGo;
            Debug.Log("[AIBot] Demo 场景已生成并绑定 AgentConfigAsset。请先在 Assets/AIBotDemo/BlacksmithConfig.asset 中填写模型 API Key，再点击 Play。");
            EditorUtility.DisplayDialog("Demo 配置已创建", "Demo 已绑定 Local AgentConfigAsset。Play 前请在 Assets/AIBotDemo/BlacksmithConfig.asset 填写 API Key（或更换模型地址和模型 ID）。", "确定");
        }

        private static AgentConfigAsset GetDemoConfig()
        {
            const string folder = "Assets/AIBotDemo";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "AIBotDemo");
            const string path = folder + "/BlacksmithConfig.asset";
            var config = AssetDatabase.LoadAssetAtPath<AgentConfigAsset>(path);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<AgentConfigAsset>();
                config.npcId = NpcId;
                config.displayName = "老王";
                config.persona = "你是村里的铁匠，爽快、热心，喜欢聊武器和冒险。";
                config.backstory = "在村中经营铁匠铺多年，认识许多往来的冒险者。";
                config.worldId = "default";
                AssetDatabase.CreateAsset(config, path);
            }
            var world = AssetDatabase.LoadAssetAtPath<WorldConfigAsset>(folder + "/DemoWorld.asset");
            if (world == null)
            {
                world = ScriptableObject.CreateInstance<WorldConfigAsset>();
                world.worldId = "default";
                world.description = "一个有村庄、铁匠铺和冒险者的奇幻世界。";
                AssetDatabase.CreateAsset(world, folder + "/DemoWorld.asset");
            }
            config.runtimeMode = AgentRuntimeMode.Local;
            config.worldConfigAsset = world;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            return config;
        }

        private static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void CreateChildText(Transform parent, string name, string content, Color color, out TMP_Text text)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            text = go.GetComponent<TextMeshProUGUI>();
            text.text = content;
            text.fontSize = 20;
            text.color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}

