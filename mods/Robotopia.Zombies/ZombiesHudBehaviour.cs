using System;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Zombies
{
    internal sealed class ZombiesHudBehaviour : MonoBehaviour
    {
        private struct Floater
        {
            public Vector3 World;
            public string Text;
            public Color Color;
            public float Born;
            public float XDrift;
            public Text? Label;
            public bool Active;
        }

        private struct Speech
        {
            public Vector3 World;
            public string Text;
            public Color Color;
            public float Born;
            public GameObject? Root;
            public Image? Backing;
            public Text? Label;
            public bool Active;
        }

        private const int SpeechCapacity = 12;

        private ZombiesController? controller;
        private ZombiesConfig? config;
        private Camera? cachedCamera;

        private GameObject? canvasRoot;
        private GraphicRaycaster? graphicRaycaster;
        private RectTransform? canvasRect;
        private RectTransform? scaleRoot;
        private GameObject? liveRoot;
        private GameObject? modalRoot;
        private GameObject? worldRoot;

        private Floater[]? floaters;
        private Speech[]? speeches;

        private Text? waveText;
        private Text? scoreText;
        private Text? threatText;
        private Text? tallyText;
        private Text? stateText;
        private NeonBar? integrityBar;
        private NeonBar? zapperBar;

        private Image[] reticleTicks = Array.Empty<Image>();
        private Image[] hitMarkerTicks = Array.Empty<Image>();
        private Image? damageFlash;
        private Image? damageWedge;
        private Image[] vignetteEdges = Array.Empty<Image>();

        private GameObject? comboRoot;
        private Image? comboFill;
        private Image? comboDecay;
        private Text? comboText;

        private GameObject? uplinkRoot;
        private RectTransform? pipRoot;
        private Image[] pips = Array.Empty<Image>();
        private Text? uplinkTitle;
        private Text? jackText;
        private Text? broadcastText;
        private NeonBar? pressureBar;

        private Text? bannerTextElement;
        private GameObject? bannerRoot;

        private GameObject? conversationRoot;
        private Text? conversationTitle;
        private NeonBar? conversationTimer;
        private Text? conversationReply;
        private Text? conversationStatus;
        private Text? conversationTurn;
        private NeonBar? persuasionBar;
        private Text? inputModeText;
        private InputField? conversationInput;
        private Button? sendButton;
        private Text? conversationHint;
        private string conversationDraft = string.Empty;

        private GameObject? gameOverRoot;
        private Text? gameOverWave;
        private Text? gameOverScore;

        private float crosshairHitTime = -999f;
        private float chargeFraction;
        private float markerTime = -999f;
        private ZombieHitKind markerKind;
        private float bannerTime = -999f;
        private string bannerText = string.Empty;
        private Color bannerColor = Color.white;
        private float damageTime = -999f;
        private float damageBearing;
        private int cachedComboMultiplier = int.MinValue;
        private string comboLabel = "x1";

        public void Initialize(ZombiesController controller, ZombiesConfig config)
        {
            this.controller = controller;
            this.config = config;
            floaters = new Floater[Mathf.Max(1, config.FloatingNumberMaxConcurrent)];
            speeches = new Speech[SpeechCapacity];
            BuildUi();
        }

        public void PushSpeech(Vector3 world, string text, Color color)
        {
            if (speeches == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var slot = FindSpeechSlot();
            speeches[slot].World = world;
            speeches[slot].Text = text;
            speeches[slot].Color = color;
            speeches[slot].Born = Time.time;
            speeches[slot].Active = true;

            if (speeches[slot].Root != null)
            {
                speeches[slot].Root!.SetActive(true);
            }
        }

        public void PushFloater(Vector3 world, string text, Color color)
        {
            if (floaters == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var slot = FindFloaterSlot();
            floaters[slot].World = world;
            floaters[slot].Text = text;
            floaters[slot].Color = color;
            floaters[slot].Born = Time.time;
            floaters[slot].XDrift = UnityEngine.Random.Range(-18f, 18f);
            floaters[slot].Active = true;
            if (floaters[slot].Label != null)
            {
                floaters[slot].Label!.text = text;
                floaters[slot].Label!.gameObject.SetActive(true);
            }
        }

        public void FlashHitMarker(ZombieHitKind kind)
        {
            markerKind = kind;
            markerTime = Time.time;
        }

        public void FlashCrosshairHit()
        {
            crosshairHitTime = Time.time;
        }

        public void SetChargeFraction(float fraction)
        {
            chargeFraction = Mathf.Clamp01(fraction);
        }

        public void ShowBanner(string text, Color color)
        {
            bannerText = text ?? string.Empty;
            bannerColor = color;
            bannerTime = Time.time;
            if (bannerTextElement != null)
            {
                bannerTextElement.text = bannerText;
            }
        }

        public void FlashDamage(float bearingDegrees)
        {
            damageTime = Time.time;
            damageBearing = bearingDegrees;
        }

        public void ClearTransient()
        {
            if (floaters != null)
            {
                for (var index = 0; index < floaters.Length; index++)
                {
                    floaters[index].Active = false;
                    floaters[index].Label?.gameObject.SetActive(false);
                }
            }

            if (speeches != null)
            {
                for (var index = 0; index < speeches.Length; index++)
                {
                    speeches[index].Active = false;
                    speeches[index].Root?.SetActive(false);
                }
            }

            crosshairHitTime = -999f;
            markerTime = -999f;
            bannerTime = -999f;
            bannerText = string.Empty;
            damageTime = -999f;
            chargeFraction = 0f;
        }

        private void Update()
        {
            if (controller == null || config == null || canvasRoot == null)
            {
                return;
            }

            if (scaleRoot != null)
            {
                scaleRoot.localScale = Vector3.one * Mathf.Clamp(config.HudScale, 0.75f, 1.35f);
            }

            var gameOver = controller.GameOver;
            var conversing = controller.Conversing;
            liveRoot?.SetActive(!gameOver && !conversing);
            modalRoot?.SetActive(gameOver || conversing);
            gameOverRoot?.SetActive(gameOver);
            conversationRoot?.SetActive(conversing && !gameOver);
            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = gameOver || conversing;
            }

            if (gameOver || conversing)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            UpdateWorldLabels();

            if (gameOver)
            {
                UpdateGameOver();
                return;
            }

            if (conversing)
            {
                UpdateConversation();
                return;
            }

            UpdateStats();
            UpdateReticle();
            UpdateHitMarker();
            UpdateDamageFeedback();
            UpdateCombo();
            UpdateUplink();
            UpdateBanner();
        }

        private void OnDestroy()
        {
            if (canvasRoot != null)
            {
                UnityEngine.Object.Destroy(canvasRoot);
                canvasRoot = null;
            }
        }

        private void BuildUi()
        {
            if (canvasRoot != null)
            {
                return;
            }

            canvasRoot = NeonUi.CreateOverlayCanvas("RobotopiaZombiesHudCanvas", 31900, false);
            canvasRoot.transform.SetParent(transform, false);
            graphicRaycaster = canvasRoot.GetComponent<GraphicRaycaster>();
            if (graphicRaycaster != null)
            {
                graphicRaycaster.enabled = false;
            }

            canvasRect = canvasRoot.GetComponent<RectTransform>();

            scaleRoot = NeonUi.CreateObject("ScaleRoot", canvasRoot.transform).GetComponent<RectTransform>();
            NeonUi.Stretch(scaleRoot);
            liveRoot = NeonUi.CreateObject("Live", scaleRoot);
            NeonUi.Stretch(liveRoot.GetComponent<RectTransform>());
            worldRoot = NeonUi.CreateObject("WorldAnchors", canvasRoot.transform);
            NeonUi.Stretch(worldRoot.GetComponent<RectTransform>());
            modalRoot = NeonUi.CreateObject("Modal", canvasRoot.transform);
            NeonUi.Stretch(modalRoot.GetComponent<RectTransform>());

            BuildStats();
            BuildReticle();
            BuildDamageFeedback();
            BuildCombo();
            BuildUplink();
            BuildBanner();
            BuildWorldPools();
            NeonUi.SetRaycastRecursive(liveRoot.transform, false);
            NeonUi.SetRaycastRecursive(worldRoot.transform, false);
            BuildConversation();
            BuildGameOver();
            modalRoot.SetActive(false);
        }

        private void BuildStats()
        {
            var panel = NeonUi.CreatePanel(liveRoot!.transform, "ThreatPanel", NeonTheme.Panel, NeonTheme.CyanDim);
            var rect = panel.GetComponent<RectTransform>();
            NeonUi.Anchor(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, -18f), new Vector2(380f, 224f));

            var title = NeonUi.CreateText(panel.transform, "Title", "ZOMBIES // LIVE FIRE", 18, NeonTheme.Acid, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(title.rectTransform, 18f, 14f, 330f, 28f);
            waveText = NeonUi.CreateText(panel.transform, "Wave", string.Empty, 28, NeonTheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(waveText.rectTransform, 18f, 46f, 160f, 38f);
            scoreText = NeonUi.CreateText(panel.transform, "Score", string.Empty, 18, NeonTheme.Amber, TextAnchor.MiddleRight, FontStyle.Bold);
            Place(scoreText.rectTransform, 188f, 52f, 172f, 28f);
            threatText = NeonUi.CreateText(panel.transform, "Threat", string.Empty, 14, NeonTheme.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(threatText.rectTransform, 18f, 88f, 342f, 24f);
            tallyText = NeonUi.CreateText(panel.transform, "Tally", string.Empty, 12, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(tallyText.rectTransform, 18f, 114f, 342f, 22f);

            integrityBar = NeonUi.CreateBar(panel.transform, "Integrity", NeonTheme.Acid);
            Place(integrityBar.GetComponent<RectTransform>(), 18f, 146f, 220f, 18f);
            zapperBar = NeonUi.CreateBar(panel.transform, "Zapper", NeonTheme.Cyan);
            Place(zapperBar.GetComponent<RectTransform>(), 250f, 146f, 110f, 18f);
            stateText = NeonUi.CreateText(panel.transform, "State", string.Empty, 13, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(stateText.rectTransform, 18f, 176f, 342f, 28f);
        }

        private void BuildReticle()
        {
            reticleTicks = new Image[5];
            for (var index = 0; index < reticleTicks.Length; index++)
            {
                reticleTicks[index] = NeonUi.CreateImage(liveRoot!.transform, "Reticle" + index, NeonTheme.Cyan);
                var rect = reticleTicks[index].rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
            }

            hitMarkerTicks = new Image[4];
            for (var index = 0; index < hitMarkerTicks.Length; index++)
            {
                hitMarkerTicks[index] = NeonUi.CreateImage(liveRoot!.transform, "HitMarker" + index, Color.clear);
                var rect = hitMarkerTicks[index].rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(4f, 22f);
                rect.localRotation = Quaternion.Euler(0f, 0f, index < 2 ? 45f : -45f);
            }
        }

        private void BuildDamageFeedback()
        {
            damageFlash = NeonUi.CreateImage(liveRoot!.transform, "DamageFlash", Color.clear);
            NeonUi.Stretch(damageFlash.rectTransform);

            vignetteEdges = new Image[4];
            for (var index = 0; index < vignetteEdges.Length; index++)
            {
                vignetteEdges[index] = NeonUi.CreateImage(liveRoot.transform, "Vignette" + index, Color.clear);
            }

            NeonUi.Anchor(vignetteEdges[0].rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 90f));
            NeonUi.Anchor(vignetteEdges[1].rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 90f));
            NeonUi.Anchor(vignetteEdges[2].rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(90f, 0f));
            NeonUi.Anchor(vignetteEdges[3].rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(90f, 0f));

            damageWedge = NeonUi.CreateImage(liveRoot.transform, "DamageBearing", Color.clear);
            var wedgeRect = damageWedge.rectTransform;
            wedgeRect.anchorMin = new Vector2(0.5f, 0.5f);
            wedgeRect.anchorMax = new Vector2(0.5f, 0.5f);
            wedgeRect.pivot = new Vector2(0.5f, 0.5f);
            wedgeRect.anchoredPosition = new Vector2(0f, 160f);
            wedgeRect.sizeDelta = new Vector2(52f, 16f);
        }

        private void BuildCombo()
        {
            comboRoot = NeonUi.CreatePanel(liveRoot!.transform, "Combo", NeonTheme.PanelSoft, NeonTheme.Amber);
            var rect = comboRoot.GetComponent<RectTransform>();
            NeonUi.Anchor(rect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(72f, 230f));
            comboFill = NeonUi.CreateImage(comboRoot.transform, "Fill", NeonTheme.Amber);
            NeonUi.Anchor(comboFill.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(16f, 160f));
            comboDecay = NeonUi.CreateImage(comboRoot.transform, "Decay", NeonTheme.Danger);
            NeonUi.Anchor(comboDecay.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(11f, 18f), new Vector2(4f, 160f));
            comboText = NeonUi.CreateText(comboRoot.transform, "Label", "x1", 24, NeonTheme.Amber, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(comboText.rectTransform, 6f, 4f, 60f, 34f);
            comboRoot.SetActive(false);
        }

        private void BuildUplink()
        {
            uplinkRoot = NeonUi.CreatePanel(liveRoot!.transform, "Uplink", new Color(0.02f, 0.035f, 0.055f, 0.82f), NeonTheme.Violet);
            var rect = uplinkRoot.GetComponent<RectTransform>();
            NeonUi.Anchor(rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(560f, 112f));
            uplinkTitle = NeonUi.CreateText(uplinkRoot.transform, "Title", "UPLINK", 12, NeonTheme.TextMuted, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(uplinkTitle.rectTransform, 18f, 8f, 524f, 18f);
            pipRoot = NeonUi.CreateObject("Pips", uplinkRoot.transform).GetComponent<RectTransform>();
            Place(pipRoot, 190f, 30f, 180f, 14f);
            jackText = NeonUi.CreateText(uplinkRoot.transform, "Jack", string.Empty, 18, NeonTheme.Cyan, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(jackText.rectTransform, 18f, 48f, 250f, 28f);
            broadcastText = NeonUi.CreateText(uplinkRoot.transform, "Broadcast", string.Empty, 18, NeonTheme.Violet, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(broadcastText.rectTransform, 292f, 48f, 250f, 28f);
            pressureBar = NeonUi.CreateBar(uplinkRoot.transform, "Pressure", NeonTheme.Danger, "HORDE PRESSURE");
            Place(pressureBar.GetComponent<RectTransform>(), 90f, 82f, 380f, 14f);
        }

        private void BuildBanner()
        {
            bannerRoot = NeonUi.CreateObject("Banner", liveRoot!.transform);
            var rect = bannerRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, 130f);
            rect.sizeDelta = new Vector2(720f, 72f);
            bannerTextElement = NeonUi.CreateText(bannerRoot.transform, "Text", string.Empty, 42, NeonTheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            NeonUi.Stretch(bannerTextElement.rectTransform);
            bannerRoot.SetActive(false);
        }

        private void BuildWorldPools()
        {
            if (floaters != null)
            {
                for (var index = 0; index < floaters.Length; index++)
                {
                    var label = NeonUi.CreateText(worldRoot!.transform, "Floater" + index, string.Empty, 18, NeonTheme.Acid, TextAnchor.MiddleCenter, FontStyle.Bold);
                    label.raycastTarget = false;
                    label.rectTransform.sizeDelta = new Vector2(160f, 30f);
                    label.gameObject.SetActive(false);
                    floaters[index].Label = label;
                }
            }

            if (speeches != null)
            {
                for (var index = 0; index < speeches.Length; index++)
                {
                    var root = NeonUi.CreatePanel(worldRoot!.transform, "Speech" + index, new Color(0f, 0f, 0f, 0.62f), NeonTheme.CyanDim);
                    var rootRect = root.GetComponent<RectTransform>();
                    rootRect.sizeDelta = new Vector2(280f, 38f);
                    var label = NeonUi.CreateText(root.transform, "Text", string.Empty, 13, NeonTheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
                    label.raycastTarget = false;
                    label.horizontalOverflow = HorizontalWrapMode.Wrap;
                    NeonUi.Stretch(label.rectTransform, 8f, 3f, 8f, 3f);
                    root.SetActive(false);
                    speeches[index].Root = root;
                    speeches[index].Backing = root.GetComponent<Image>();
                    speeches[index].Label = label;
                }
            }
        }

        private void BuildConversation()
        {
            conversationRoot = NeonUi.CreateObject("Conversation", modalRoot!.transform);
            NeonUi.Stretch(conversationRoot.GetComponent<RectTransform>());

            var dim = NeonUi.CreateImage(conversationRoot.transform, "Dim", new Color(0.01f, 0.025f, 0.05f, 0.66f));
            NeonUi.Stretch(dim.rectTransform);
            conversationTitle = NeonUi.CreateText(conversationRoot.transform, "Title", string.Empty, 26, NeonTheme.Cyan, TextAnchor.MiddleCenter, FontStyle.Bold);
            NeonUi.Anchor(conversationTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(0f, 34f));
            conversationTimer = NeonUi.CreateBar(conversationRoot.transform, "Timer", NeonTheme.Cyan);
            NeonUi.Anchor(conversationTimer.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -78f), new Vector2(420f, 8f));

            var panel = NeonUi.CreatePanel(conversationRoot.transform, "Panel", NeonTheme.Panel, NeonTheme.Cyan);
            var panelRect = panel.GetComponent<RectTransform>();
            NeonUi.Anchor(panelRect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 42f), new Vector2(820f, 270f));
            conversationReply = NeonUi.CreateText(panel.transform, "Reply", string.Empty, 21, NeonTheme.Text, TextAnchor.UpperLeft, FontStyle.Bold);
            conversationReply.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(conversationReply.rectTransform, 24f, 20f, 772f, 66f);
            conversationStatus = NeonUi.CreateText(panel.transform, "Status", string.Empty, 14, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(conversationStatus.rectTransform, 24f, 92f, 520f, 22f);
            conversationTurn = NeonUi.CreateText(panel.transform, "Turn", string.Empty, 13, NeonTheme.Amber, TextAnchor.MiddleRight, FontStyle.Bold);
            Place(conversationTurn.rectTransform, 612f, 92f, 184f, 22f);
            persuasionBar = NeonUi.CreateBar(panel.transform, "Persuasion", NeonTheme.Amber, "PERSUASION");
            Place(persuasionBar.GetComponent<RectTransform>(), 24f, 124f, 772f, 18f);
            inputModeText = NeonUi.CreateBadge(panel.transform, "InputMode", "TYPE", NeonTheme.Cyan, new Vector2(108f, 34f));
            Place(inputModeText.transform.parent!.GetComponent<RectTransform>(), 24f, 162f, 108f, 34f);
            conversationInput = NeonUi.CreateInput(panel.transform, "Input", "Say something that changes its mind", string.Empty, value => conversationDraft = value);
            Place(conversationInput.GetComponent<RectTransform>(), 142f, 162f, 456f, 34f);
            sendButton = NeonUi.CreateButton(panel.transform, "Send", "SEND", SubmitConversationDraft, new Vector2(86f, 34f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            Place(sendButton.GetComponent<RectTransform>(), 610f, 162f, 86f, 34f);
            var leaveButton = NeonUi.CreateButton(panel.transform, "Leave", "LEAVE", () => controller?.LeaveConversationFromHud(), new Vector2(86f, 34f), new Color(0.16f, 0.06f, 0.07f, 0.95f), NeonTheme.Danger);
            Place(leaveButton.GetComponent<RectTransform>(), 710f, 162f, 86f, 34f);
            conversationHint = NeonUi.CreateText(panel.transform, "Hint", string.Empty, 13, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(conversationHint.rectTransform, 24f, 218f, 772f, 24f);
            conversationRoot.SetActive(false);
        }

        private void BuildGameOver()
        {
            gameOverRoot = NeonUi.CreateObject("GameOver", modalRoot!.transform);
            NeonUi.Stretch(gameOverRoot.GetComponent<RectTransform>());
            var dim = NeonUi.CreateImage(gameOverRoot.transform, "Dim", new Color(0f, 0f, 0f, 0.72f));
            NeonUi.Stretch(dim.rectTransform);
            var panel = NeonUi.CreatePanel(gameOverRoot.transform, "Panel", NeonTheme.Panel, NeonTheme.Danger);
            var panelRect = panel.GetComponent<RectTransform>();
            NeonUi.Anchor(panelRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520f, 360f));
            var title = NeonUi.CreateText(panel.transform, "Title", "SIGNAL LOST", 42, NeonTheme.Danger, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(title.rectTransform, 24f, 34f, 472f, 58f);
            gameOverWave = NeonUi.CreateText(panel.transform, "Wave", string.Empty, 20, NeonTheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(gameOverWave.rectTransform, 24f, 112f, 472f, 30f);
            gameOverScore = NeonUi.CreateText(panel.transform, "Score", string.Empty, 20, NeonTheme.Amber, TextAnchor.MiddleCenter, FontStyle.Bold);
            Place(gameOverScore.rectTransform, 24f, 148f, 472f, 30f);
            var restart = NeonUi.CreateButton(panel.transform, "Restart", "RESTART RUN", () => controller?.Restart(), new Vector2(300f, 46f), NeonTheme.PanelAlt, NeonTheme.Acid);
            Place(restart.GetComponent<RectTransform>(), 110f, 210f, 300f, 46f);
            var menu = NeonUi.CreateButton(panel.transform, "Menu", "RETURN TO MENU", () => controller?.ReturnToMenu(), new Vector2(300f, 46f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            Place(menu.GetComponent<RectTransform>(), 110f, 268f, 300f, 46f);
            gameOverRoot.SetActive(false);
        }

        private void UpdateStats()
        {
            if (controller == null || config == null)
            {
                return;
            }

            SetText(waveText, "WAVE " + controller.Wave);
            SetText(scoreText, controller.Score.ToString("N0"));
            var allies = controller.ConvertedAllyCount;
            var line = "HOSTILES " + controller.HostileCount + "  //  INCOMING " + controller.RemainingToSpawn;
            if (allies > 0)
            {
                line += "  //  ALLIES " + allies;
                if (controller.WaveringAllyCount > 0)
                {
                    line += " (" + controller.WaveringAllyCount + " WAVERING)";
                }
            }

            SetText(threatText, line);
            controller.GetArchetypeTally(out var grunts, out var sprinters, out var brutes, out var runts);
            SetText(tallyText, "GRUNT " + grunts + "   SPRINTER " + sprinters + "   BRUTE " + brutes + "   RUNT " + runts);

            var integrityFraction = controller.MaxPlayerIntegrity > 0f ? controller.PlayerIntegrity / controller.MaxPlayerIntegrity : 0f;
            var integrityColor = integrityFraction < config.CriticalIntegrityThreshold ? NeonTheme.Danger :
                integrityFraction < config.LowIntegrityVignetteThreshold ? NeonTheme.Amber : NeonTheme.Acid;
            integrityBar!.Set(integrityFraction, "INTEGRITY " + Mathf.CeilToInt(controller.PlayerIntegrity), HudColor(integrityColor));
            zapperBar!.Set(controller.ZapperReadyFraction, "ZAPPER", HudColor(NeonTheme.Cyan));
            SetText(stateText, controller.StateText.ToUpperInvariant());
        }

        private void UpdateReticle()
        {
            if (controller == null || config == null || reticleTicks.Length == 0)
            {
                return;
            }

            var gap = config.CrosshairBaseGapPixels + ((1f - controller.ZapperReadyFraction) * config.CrosshairBloomGapPixels);
            gap += chargeFraction * config.CrosshairBloomGapPixels;
            var hitFlash = Mathf.Clamp01(1f - ((Time.time - crosshairHitTime) / 0.08f));
            var color = hitFlash > 0f ? Color.Lerp(ReticleColor(), Color.white, hitFlash) : ReticleColor();
            const float tick = 12f;
            const float thick = 3f;
            SetTick(reticleTicks[0], new Vector2(0f, gap + (tick * 0.5f)), new Vector2(thick, tick), color);
            SetTick(reticleTicks[1], new Vector2(0f, -gap - (tick * 0.5f)), new Vector2(thick, tick), color);
            SetTick(reticleTicks[2], new Vector2(-gap - (tick * 0.5f), 0f), new Vector2(tick, thick), color);
            SetTick(reticleTicks[3], new Vector2(gap + (tick * 0.5f), 0f), new Vector2(tick, thick), color);
            SetTick(reticleTicks[4], Vector2.zero, new Vector2(3f, 3f), NeonTheme.WithAlpha(color, 0.72f));
        }

        private void UpdateHitMarker()
        {
            if (config == null)
            {
                return;
            }

            var age = Time.time - markerTime;
            if (age >= config.HitMarkerSeconds)
            {
                for (var index = 0; index < hitMarkerTicks.Length; index++)
                {
                    hitMarkerTicks[index].color = Color.clear;
                }

                return;
            }

            var t = age / Mathf.Max(0.01f, config.HitMarkerSeconds);
            var isKill = markerKind == ZombieHitKind.Kill || markerKind == ZombieHitKind.HeadshotKill;
            var headshot = markerKind == ZombieHitKind.Headshot || markerKind == ZombieHitKind.HeadshotKill;
            var radius = Mathf.Lerp(18f, isKill ? 42f : 30f, t);
            var color = isKill ? NeonTheme.Danger : (headshot ? NeonTheme.Amber : NeonTheme.Text);
            color.a = 1f - t;
            var offsets = new[]
            {
                new Vector2(-radius, radius),
                new Vector2(radius, -radius),
                new Vector2(radius, radius),
                new Vector2(-radius, -radius)
            };
            for (var index = 0; index < hitMarkerTicks.Length; index++)
            {
                hitMarkerTicks[index].rectTransform.anchoredPosition = offsets[index];
                hitMarkerTicks[index].color = color;
            }
        }

        private void UpdateDamageFeedback()
        {
            if (controller == null || config == null || damageFlash == null)
            {
                return;
            }

            var flashAge = Time.time - damageTime;
            var flashAlpha = 0f;
            if (flashAge < config.DamageFlashSeconds)
            {
                flashAlpha = config.DamageFlashMaxAlpha * (1f - (flashAge / config.DamageFlashSeconds));
            }

            damageFlash.color = new Color(0.9f, 0.03f, 0.03f, flashAlpha);

            var fraction = controller.MaxPlayerIntegrity > 0f ? controller.PlayerIntegrity / controller.MaxPlayerIntegrity : 1f;
            var edgeAlpha = 0f;
            if (fraction < config.LowIntegrityVignetteThreshold && config.LowIntegrityVignetteThreshold > 0f)
            {
                var intensity = (config.LowIntegrityVignetteThreshold - fraction) / config.LowIntegrityVignetteThreshold;
                var frequency = fraction < config.CriticalIntegrityThreshold ? 12.6f : 7.5f;
                var pulse = Mathf.Lerp(0.65f, 1f, 0.5f + (0.5f * Mathf.Sin(Time.time * frequency * Motion())));
                edgeAlpha = Mathf.Lerp(0f, config.HudHighContrast ? 0.72f : 0.46f, intensity) * pulse;
            }

            foreach (var edge in vignetteEdges)
            {
                edge.color = new Color(0.8f, 0.02f, 0.03f, edgeAlpha);
            }

            if (damageWedge == null)
            {
                return;
            }

            if (flashAge < 1f)
            {
                damageWedge.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -damageBearing);
                damageWedge.color = new Color(1f, 0.18f, 0.16f, 0.75f * (1f - flashAge));
            }
            else
            {
                damageWedge.color = Color.clear;
            }
        }

        private void UpdateCombo()
        {
            if (controller == null || comboRoot == null || comboFill == null || comboDecay == null || comboText == null)
            {
                return;
            }

            if (controller.ComboCount <= 0)
            {
                comboRoot.SetActive(false);
                return;
            }

            comboRoot.SetActive(true);
            if (controller.ComboMultiplier != cachedComboMultiplier)
            {
                cachedComboMultiplier = controller.ComboMultiplier;
                comboLabel = "x" + cachedComboMultiplier;
            }

            var fillHeight = 160f * Mathf.Clamp01(controller.ComboTierProgress);
            comboFill.rectTransform.sizeDelta = new Vector2(16f, fillHeight);
            var decayHeight = 160f * Mathf.Clamp01(controller.ComboWindowRemaining);
            comboDecay.rectTransform.sizeDelta = new Vector2(4f, decayHeight);
            var pulse = 1f + (0.12f * Motion() * Mathf.Sin(Time.time * 8f));
            comboText.rectTransform.localScale = Vector3.one * pulse;
            SetText(comboText, comboLabel);
            SetTextColor(comboText, ComboColor(controller.ComboMultiplier));
        }

        private void UpdateUplink()
        {
            if (controller == null || uplinkRoot == null || uplinkTitle == null || jackText == null || broadcastText == null)
            {
                return;
            }

            uplinkRoot.SetActive(controller.OverrideHudEnabled);
            if (!controller.OverrideHudEnabled)
            {
                return;
            }

            var maxCharges = Mathf.Max(0, controller.OverrideMaxCharges);
            EnsurePips(maxCharges);
            for (var index = 0; index < pips.Length; index++)
            {
                var active = index < controller.OverrideCharges;
                var regen = index == controller.OverrideCharges ? controller.OverrideRegenFraction : 0f;
                var color = active ? NeonTheme.Cyan : NeonTheme.WithAlpha(NeonTheme.CyanDim, 0.42f);
                if (!active && regen > 0f && regen < 1f)
                {
                    color = NeonTheme.WithAlpha(NeonTheme.Cyan, 0.28f + (0.5f * regen));
                }

                pips[index].color = HudColor(color);
            }

            SetText(uplinkTitle, "UPLINK  " + controller.OverrideCharges + "/" + maxCharges);
            if (!controller.ConversationAvailable)
            {
                SetText(jackText, "E  JACK-IN OFFLINE");
                SetTextColor(jackText, NeonTheme.TextMuted);
            }
            else if (controller.OverrideAimingHijackable && controller.OverrideCharges > 0)
            {
                SetText(jackText, "E  JACK IN");
                SetTextColor(jackText, HudColor(NeonTheme.Cyan));
            }
            else
            {
                SetText(jackText, controller.OverrideCharges > 0 ? "E  AIM A ROBOT" : "E  NO CHARGE");
                SetTextColor(jackText, NeonTheme.TextMuted);
            }

            var broadcastReady = controller.BroadcastReadyFraction >= 1f;
            SetText(broadcastText, broadcastReady ? "Q  STAND-DOWN" : "Q  RECHARGING");
            SetTextColor(broadcastText, broadcastReady ? HudColor(NeonTheme.Violet) : NeonTheme.TextMuted);
            var pressure = Mathf.Clamp01(controller.Pressure);
            pressureBar!.gameObject.SetActive(pressure > 0.02f);
            pressureBar.Set(pressure, "HORDE PRESSURE", HudColor(NeonTheme.Danger));
        }

        private void UpdateBanner()
        {
            if (bannerRoot == null || bannerTextElement == null || string.IsNullOrEmpty(bannerText))
            {
                bannerRoot?.SetActive(false);
                return;
            }

            const float punch = 0.18f;
            const float hold = 0.6f;
            const float fade = 0.4f;
            var age = Time.time - bannerTime;
            if (age >= punch + hold + fade)
            {
                bannerRoot.SetActive(false);
                return;
            }

            bannerRoot.SetActive(true);
            float scale;
            float alpha;
            if (age < punch)
            {
                scale = Mathf.Lerp(1.35f, 1f, age / punch);
                alpha = 1f;
            }
            else if (age < punch + hold)
            {
                scale = 1f;
                alpha = 1f;
            }
            else
            {
                scale = 1f;
                alpha = 1f - ((age - punch - hold) / fade);
            }

            bannerRoot.transform.localScale = Vector3.one * Mathf.Lerp(1f, scale, Motion());
            bannerTextElement.color = NeonTheme.WithAlpha(HudColor(bannerColor), alpha);
        }

        private void UpdateWorldLabels()
        {
            UpdateFloaters();
            UpdateSpeechBubbles();
        }

        private void UpdateFloaters()
        {
            if (floaters == null || config == null)
            {
                return;
            }

            var camera = ResolveCamera();
            if (camera == null || canvasRect == null)
            {
                return;
            }

            var rise = Mathf.Max(0.05f, config.FloatingNumberRiseSeconds);
            for (var index = 0; index < floaters.Length; index++)
            {
                if (!floaters[index].Active || floaters[index].Label == null)
                {
                    continue;
                }

                var age = Time.time - floaters[index].Born;
                if (age >= rise)
                {
                    floaters[index].Active = false;
                    floaters[index].Label!.gameObject.SetActive(false);
                    continue;
                }

                var screen = camera.WorldToScreenPoint(floaters[index].World);
                if (screen.z <= 0f)
                {
                    floaters[index].Label!.gameObject.SetActive(false);
                    continue;
                }

                floaters[index].Label!.gameObject.SetActive(true);
                var fraction = age / rise;
                var alpha = fraction > 0.7f ? 1f - ((fraction - 0.7f) / 0.3f) : 1f;
                var local = ScreenToCanvas(screen);
                local.x += floaters[index].XDrift;
                local.y += fraction * 48f;
                floaters[index].Label!.rectTransform.anchoredPosition = local;
                floaters[index].Label!.color = NeonTheme.WithAlpha(HudColor(floaters[index].Color), alpha);
            }
        }

        private void UpdateSpeechBubbles()
        {
            if (speeches == null || config == null)
            {
                return;
            }

            var camera = ResolveCamera();
            if (camera == null)
            {
                return;
            }

            var ttl = Mathf.Max(0.2f, config.SpeechBubbleSeconds);
            for (var index = 0; index < speeches.Length; index++)
            {
                if (!speeches[index].Active || speeches[index].Root == null || speeches[index].Label == null)
                {
                    continue;
                }

                var age = Time.time - speeches[index].Born;
                if (age >= ttl)
                {
                    speeches[index].Active = false;
                    speeches[index].Root!.SetActive(false);
                    continue;
                }

                var screen = camera.WorldToScreenPoint(speeches[index].World + (Vector3.up * 0.4f));
                if (screen.z <= 0f)
                {
                    speeches[index].Root!.SetActive(false);
                    continue;
                }

                var alpha = age > ttl * 0.7f ? 1f - ((age - (ttl * 0.7f)) / (ttl * 0.3f)) : 1f;
                var local = ScreenToCanvas(screen);
                local.y += 34f;
                speeches[index].Root!.SetActive(true);
                speeches[index].Root!.GetComponent<RectTransform>().anchoredPosition = local;
                speeches[index].Label!.text = speeches[index].Text;
                speeches[index].Label!.color = NeonTheme.WithAlpha(HudColor(speeches[index].Color), alpha);
                if (speeches[index].Backing != null)
                {
                    speeches[index].Backing!.color = new Color(0f, 0f, 0f, 0.62f * alpha);
                }
            }
        }

        private void UpdateConversation()
        {
            if (controller == null || conversationTitle == null || conversationRoot == null)
            {
                return;
            }

            SetText(conversationTitle, "CHANNEL OPEN // " + controller.ConversationTargetName.ToUpperInvariant());
            var windowFraction = Mathf.Clamp01(controller.ConversationWindowFraction);
            conversationTimer!.Set(windowFraction, string.Empty, windowFraction < 0.25f ? HudColor(NeonTheme.Danger) : HudColor(NeonTheme.Cyan));

            var reply = controller.ConversationThinking
                ? controller.ConversationTargetName + " is thinking" + Ellipsis()
                : (string.IsNullOrEmpty(controller.ConversationReply) ? "Open channel. Make a case." : "\"" + controller.ConversationReply + "\"");
            SetText(conversationReply, reply);
            SetText(conversationStatus, controller.ConversationStatus.ToUpperInvariant());
            SetText(conversationTurn, "TURN " + Mathf.Min(controller.ConversationTurn + 1, controller.ConversationMaxTurns) + "/" + controller.ConversationMaxTurns);
            var disposition = Mathf.Clamp01(controller.ConversationDisposition);
            var threshold = Mathf.Clamp01(controller.ConversationConvertThreshold);
            var persuasionColor = disposition >= threshold ? NeonTheme.Acid : NeonTheme.Amber;
            persuasionBar!.Set(disposition, "PERSUASION  " + Mathf.RoundToInt(disposition * 100f) + "%  //  CONVERT " + Mathf.RoundToInt(threshold * 100f) + "%", HudColor(persuasionColor));

            var voiceMode = controller.ConversationVoiceMode;
            SetText(inputModeText, voiceMode ? (controller.ConversationVoiceRecording ? "REC" : "VOICE") : "TYPE");
            SetTextColor(inputModeText, controller.ConversationVoiceRecording ? NeonTheme.Danger : (voiceMode ? NeonTheme.Violet : NeonTheme.Cyan));
            if (conversationInput != null)
            {
                conversationInput.interactable = !voiceMode && !controller.ConversationThinking;
                var echo = controller.ConversationPlayerEcho;
                if (!conversationInput.isFocused && !string.Equals(conversationInput.text, echo, StringComparison.Ordinal))
                {
                    conversationInput.text = echo;
                    conversationDraft = echo;
                }
            }

            if (sendButton != null)
            {
                sendButton.interactable = !controller.ConversationThinking && !voiceMode;
            }

            SetText(conversationHint, controller.ConversationVoiceAvailable
                ? "ENTER SEND  //  TAB TYPE/VOICE  //  ESC LEAVE"
                : "ENTER SEND  //  ESC LEAVE");
        }

        private void UpdateGameOver()
        {
            if (controller == null)
            {
                return;
            }

            SetText(gameOverWave, "WAVE REACHED  " + controller.Wave);
            SetText(gameOverScore, "FINAL SCORE  " + controller.Score.ToString("N0"));
        }

        private void SubmitConversationDraft()
        {
            var text = conversationInput != null ? conversationInput.text : conversationDraft;
            controller?.SubmitConversationTextFromHud(text);
            conversationDraft = string.Empty;
            if (conversationInput != null)
            {
                conversationInput.text = string.Empty;
            }
        }

        private void EnsurePips(int count)
        {
            if (pipRoot == null || pips.Length == count)
            {
                return;
            }

            NeonUi.DestroyChildren(pipRoot);
            pips = new Image[count];
            var totalWidth = (count * 24f) + (Mathf.Max(0, count - 1) * 5f);
            for (var index = 0; index < count; index++)
            {
                var pip = NeonUi.CreateImage(pipRoot, "Pip" + index, NeonTheme.CyanDim);
                var rect = pip.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(24f, 10f);
                rect.anchoredPosition = new Vector2((-totalWidth * 0.5f) + 12f + (index * 29f), 0f);
                pips[index] = pip;
            }
        }

        private void SetTick(Image image, Vector2 position, Vector2 size, Color color)
        {
            image.rectTransform.anchoredPosition = position;
            image.rectTransform.sizeDelta = size;
            image.color = HudColor(color);
        }

        private Color ReticleColor()
        {
            if (controller == null)
            {
                return HudColor(NeonTheme.Cyan);
            }

            if (chargeFraction >= 1f)
            {
                return HudColor(NeonTheme.Amber);
            }

            return controller.OverrideHudEnabled && controller.OverrideAimingHijackable
                ? HudColor(NeonTheme.Violet)
                : HudColor(NeonTheme.Cyan);
        }

        private int FindFloaterSlot()
        {
            if (floaters == null)
            {
                return 0;
            }

            var slot = 0;
            var oldest = float.MaxValue;
            for (var index = 0; index < floaters.Length; index++)
            {
                if (!floaters[index].Active)
                {
                    return index;
                }

                if (floaters[index].Born < oldest)
                {
                    oldest = floaters[index].Born;
                    slot = index;
                }
            }

            return slot;
        }

        private int FindSpeechSlot()
        {
            if (speeches == null)
            {
                return 0;
            }

            var slot = 0;
            var oldest = float.MaxValue;
            for (var index = 0; index < speeches.Length; index++)
            {
                if (!speeches[index].Active)
                {
                    return index;
                }

                if (speeches[index].Born < oldest)
                {
                    oldest = speeches[index].Born;
                    slot = index;
                }
            }

            return slot;
        }

        private Vector2 ScreenToCanvas(Vector3 screen)
        {
            if (canvasRect == null)
            {
                return new Vector2(screen.x, screen.y);
            }

            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, new Vector2(screen.x, screen.y), null, out var local);
            return local;
        }

        private Camera? ResolveCamera()
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            {
                return cachedCamera;
            }

            cachedCamera = Camera.main;
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            {
                return cachedCamera;
            }

            var cameras = Camera.allCameras;
            for (var index = 0; index < cameras.Length; index++)
            {
                if (cameras[index] != null && cameras[index].isActiveAndEnabled)
                {
                    cachedCamera = cameras[index];
                    return cachedCamera;
                }
            }

            return null;
        }

        private Color HudColor(Color color)
        {
            if (config == null || !config.HudHighContrast)
            {
                return color;
            }

            var max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (max <= 0f)
            {
                return color;
            }

            color.r = Mathf.Lerp(color.r / max, 1f, 0.25f);
            color.g = Mathf.Lerp(color.g / max, 1f, 0.25f);
            color.b = Mathf.Lerp(color.b / max, 1f, 0.25f);
            return color;
        }

        private float Motion()
        {
            return config == null ? 1f : Mathf.Clamp(config.HudMotionIntensity, 0f, 2f);
        }

        private static Color ComboColor(int multiplier)
        {
            switch (multiplier)
            {
                case 2:
                    return NeonTheme.Cyan;
                case 3:
                    return NeonTheme.Amber;
                case 4:
                    return new Color(1f, 0.50f, 0.12f, 1f);
                default:
                    return multiplier >= 5 ? NeonTheme.Danger : NeonTheme.Text;
            }
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void SetText(Text? label, string value)
        {
            if (label != null && label.text != value)
            {
                label.text = value;
            }
        }

        private static void SetTextColor(Text? label, Color color)
        {
            if (label != null && label.color != color)
            {
                label.color = color;
            }
        }

        private static string Ellipsis()
        {
            var dots = 1 + (Mathf.FloorToInt(Time.unscaledTime * 2f) % 3);
            return new string('.', dots);
        }
    }
}
