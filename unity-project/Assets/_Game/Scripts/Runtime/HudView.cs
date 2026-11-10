using System;
using System.Collections.Generic;
using SuUstasi.Core;
using UnityEngine;
using UnityEngine.UI;

namespace SuUstasi.Runtime
{
    /// <summary>A button row in a modal card.</summary>
    public struct ModalButton
    {
        public string Label;
        public string Sub;
        public Color Color;
        public Action OnClick;
        public bool Interactable;

        public ModalButton(string label, Color color, Action onClick, string sub = null, bool interactable = true)
        {
            Label = label;
            Sub = sub;
            Color = color;
            OnClick = onClick;
            Interactable = interactable;
        }
    }

    /// <summary>Gentle idle animation for mascot / card images (bounce = happy, tremble = worried).</summary>
    public sealed class UiWobble : MonoBehaviour
    {
        public bool Tremble;
        private RectTransform _rt;
        private Vector2 _base;

        private void Start()
        {
            _rt = (RectTransform)transform;
            _base = _rt.anchoredPosition;
        }

        private void Update()
        {
            if (_rt == null) return;
            float t = Time.unscaledTime;
            if (Tremble)
            {
                _rt.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 12) * 2f);
                _rt.anchoredPosition = _base + new Vector2(Mathf.Sin(t * 12) * 3f, 0);
            }
            else
            {
                float b = Mathf.Abs(Mathf.Sin(t * 2.8f));
                _rt.anchoredPosition = _base + new Vector2(0, b * 24f);
                _rt.localScale = new Vector3(1 + (1 - b) * 0.02f, 1 - (1 - b) * 0.02f, 1);
            }
        }
    }

    /// <summary>uGUI HUD built from code (no prefabs): lives, level info, coins, settings, boosters, toast, tooltip, modals.</summary>
    public sealed class HudView : MonoBehaviour
    {
        public static readonly Color Ink = PaletteColors.Hex(Palette.Ink);
        public static readonly Color Yellow = PaletteColors.Hex(Palette.Yellow);
        public static readonly Color GreenC = PaletteColors.Hex(Palette.Green);
        public static readonly Color Blue = PaletteColors.Hex(Palette.Water);
        public static readonly Color Red = PaletteColors.Hex(Palette.Error);
        public static readonly Color White = Color.white;
        private static readonly Color Bg = PaletteColors.Hex(Palette.Background);

        public event Action<BoosterType> BoosterClicked;
        public event Action SettingsClicked;
        public event Action CoinsClicked;

        private Font _font;
        private RectTransform _root, _livesRow, _modalRoot, _card, _tooltip;
        private Text _title, _sub, _coins, _toast, _tooltipText, _ftueMark;
        private GameObject _toastGo;
        private readonly List<Image> _drops = new List<Image>();
        private readonly Dictionary<BoosterType, (Image icon, Text badge, GameObject badgeGo, Text lockText, GameObject lockGo, Image ring)> _boosters =
            new Dictionary<BoosterType, (Image, Text, GameObject, Text, GameObject, Image)>();
        private Sprite _dropSprite;
        private float _toastUntil;

        public bool ModalOpen => _modalRoot != null && _modalRoot.gameObject.activeSelf;

        public void Build()
        {
            _font = SpriteLibrary.UiFont();
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            _root = (RectTransform)transform;

            // --- top-left: lives panel
            var lives = Panel(_root, "Lives", new Vector2(0, 1), new Vector2(40, -40), new Vector2(380, 150), PaletteColors.Hex("#FBE7C6"));
            var row = lives.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(24, 24, 20, 20);
            row.spacing = 10;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = row.childControlHeight = false;
            _livesRow = lives;
            _dropSprite = SpriteLibrary.Load("Sprites/UI/icon_drop");
            _ftueMark = Label(_root, "∞", 60, new Vector2(0, 1), new Vector2(430, -115), new Vector2(90, 90), new Color(0.18f, 0.61f, 0.6f));
            _ftueMark.gameObject.SetActive(false);

            // --- top-center: level info + coins
            _title = Label(_root, "Su Ustası", 66, new Vector2(0.5f, 1), new Vector2(0, -40), new Vector2(560, 90), Ink, FontStyle.Bold);
            _sub = Label(_root, "", 36, new Vector2(0.5f, 1), new Vector2(0, -132), new Vector2(700, 50), PaletteColors.Hex("#7A5A46"), FontStyle.Bold);
            var coinBtn = Panel(_root, "Coins", new Vector2(0.5f, 1), new Vector2(-110, -192), new Vector2(220, 70), PaletteColors.Hex("#FFF7E8"));
            coinBtn.gameObject.AddComponent<Button>().onClick.AddListener(() => CoinsClicked?.Invoke());
            var coinIcon = Img(coinBtn, ProceduralSprites.Circle, new Vector2(0, 0.5f), new Vector2(40, 0), new Vector2(46, 46), Yellow);
            coinIcon.raycastTarget = false;
            _coins = Label(coinBtn, "0", 40, new Vector2(0, 0.5f), new Vector2(92, 0), new Vector2(120, 60), Ink, FontStyle.Bold);

            // --- top-right: settings
            var gear = Img(_root, SpriteLibrary.Load("Sprites/UI/btn_settings"), new Vector2(1, 1), new Vector2(-210, -40), new Vector2(170, 170), Color.white);
            gear.preserveAspect = true;
            gear.gameObject.AddComponent<Button>().onClick.AddListener(() => SettingsClicked?.Invoke());

            // --- bottom: boosters
            BuildBooster(BoosterType.XRay, "booster_xray", "Röntgen", -290);
            BuildBooster(BoosterType.Hammer, "booster_hammer", "Çekiç", 0);
            BuildBooster(BoosterType.Patch, "booster_patch", "Yama", 290);

            // --- toast + tooltip
            var toast = Panel(_root, "Toast", new Vector2(0.5f, 0), new Vector2(-420, 380), new Vector2(840, 110), Ink);
            _toast = Label(toast, "", 34, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(800, 100), Color.white, FontStyle.Bold);
            _toastGo = toast.gameObject;
            toast.GetComponent<Image>().raycastTarget = false; // never swallow board taps
            _toastGo.SetActive(false);
            _tooltip = Panel(_root, "Tooltip", new Vector2(0.5f, 1), new Vector2(-450, -260), new Vector2(900, 150), Color.white);
            _tooltipText = Label(_tooltip, "", 36, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(860, 140), Ink, FontStyle.Bold);
            _tooltip.GetComponent<Image>().raycastTarget = false;
            _tooltip.gameObject.SetActive(false);

            // --- modal root
            var modal = new GameObject("Modal", typeof(RectTransform), typeof(Image), typeof(Button));
            _modalRoot = (RectTransform)modal.transform;
            _modalRoot.SetParent(_root, false);
            Stretch(_modalRoot);
            modal.GetComponent<Image>().color = new Color(0.29f, 0.18f, 0.13f, 0.45f);
            modal.SetActive(false);
        }

        private void BuildBooster(BoosterType b, string sprite, string name, float x)
        {
            var icon = Img(_root, SpriteLibrary.Load("Sprites/UI/" + sprite), new Vector2(0.5f, 0), new Vector2(x - 115, 120), new Vector2(230, 230), Color.white);
            icon.preserveAspect = true;
            icon.gameObject.AddComponent<Button>().onClick.AddListener(() => BoosterClicked?.Invoke(b));
            var ring = Img(icon.rectTransform, ProceduralSprites.Circle, new Vector2(0.5f, 0.5f), new Vector2(-130, 0), new Vector2(260, 260), PaletteColors.WithAlpha(PaletteColors.Hex(Palette.Orange), 0.35f));
            ring.raycastTarget = false;
            ring.transform.SetAsFirstSibling();
            ring.gameObject.SetActive(false);
            Label(_root, name, 32, new Vector2(0.5f, 0), new Vector2(x, 76), new Vector2(230, 44), PaletteColors.Hex("#7A5A46"), FontStyle.Bold);
            var badgeRt = Panel(icon.rectTransform, "Badge", new Vector2(1, 1), new Vector2(-70, -10), new Vector2(76, 76), Color.white);
            var badge = Label(badgeRt, "0", 40, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(76, 70), Ink, FontStyle.Bold);
            var lockRt = Panel(icon.rectTransform, "Lock", new Vector2(0.5f, 0.5f), new Vector2(-90, 0), new Vector2(180, 64), Ink);
            var lockText = Label(lockRt, "Lv", 32, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(170, 60), Color.white, FontStyle.Bold);
            _boosters[b] = (icon, badge, badgeRt.gameObject, lockText, lockRt.gameObject, ring);
        }

        // ------------------------------------------------------------------ setters
        public void SetLevel(int level, string theme, string rank, bool breather)
        {
            _title.text = "Seviye " + level;
            _sub.text = theme + " · " + rank + (breather ? " · Mola" : "");
        }

        public void SetCoins(int coins) => _coins.text = coins.ToString();

        public void SetLives(int leaks, int slots, bool ftue)
        {
            while (_drops.Count < slots)
            {
                var d = Img(_livesRow, _dropSprite, new Vector2(0, 0.5f), Vector2.zero, new Vector2(70, 90), Color.white);
                d.preserveAspect = true;
                d.raycastTarget = false;
                _drops.Add(d);
            }
            for (int i = 0; i < _drops.Count; i++)
            {
                _drops[i].gameObject.SetActive(i < slots);
                _drops[i].color = i < leaks ? Color.white : new Color(0.7f, 0.7f, 0.7f, 0.35f);
                _drops[i].rectTransform.localScale = Vector3.one * (i < leaks ? 1f : 0.82f);
            }
            _livesRow.sizeDelta = new Vector2(48 + slots * 80, 150);
            _ftueMark.gameObject.SetActive(ftue);
            _ftueMark.rectTransform.anchoredPosition = new Vector2(40 + _livesRow.sizeDelta.x + 55, -115);
        }

        public void SetBooster(BoosterType b, int count, bool locked, int unlockLevel, bool active)
        {
            var v = _boosters[b];
            v.icon.color = locked ? new Color(0.6f, 0.6f, 0.6f, 0.55f) : Color.white;
            v.badgeGo.SetActive(!locked);
            v.badge.text = count > 0 ? count.ToString() : "+";
            v.lockGo.SetActive(locked);
            v.lockText.text = "Lv " + unlockLevel;
            v.ring.gameObject.SetActive(active);
        }

        public void Toast(string msg, float seconds = 2.6f)
        {
            _toast.text = msg;
            _toastGo.SetActive(true);
            _toastUntil = Time.unscaledTime + seconds;
        }

        public void Tooltip(string msg, bool bottom)
        {
            _tooltipText.text = msg;
            _tooltip.anchorMin = _tooltip.anchorMax = bottom ? new Vector2(0.5f, 0) : new Vector2(0.5f, 1);
            _tooltip.pivot = new Vector2(0, bottom ? 0 : 1);
            _tooltip.anchoredPosition = bottom ? new Vector2(-450, 400) : new Vector2(-450, -260);
            _tooltip.gameObject.SetActive(true);
        }

        public void HideTooltip() => _tooltip.gameObject.SetActive(false);

        private void Update()
        {
            if (_toastGo != null && _toastGo.activeSelf && Time.unscaledTime > _toastUntil) _toastGo.SetActive(false);
            foreach (var kv in _boosters)
                if (kv.Value.ring.gameObject.activeSelf) kv.Value.ring.transform.Rotate(0, 0, -90 * Time.unscaledDeltaTime);
        }

        // ------------------------------------------------------------------ modals
        /// <summary>Shows a card: optional image (mascot / pipe sprite), title, body lines, buttons.</summary>
        public RectTransform ShowModal(Sprite image, string title, string body, IList<ModalButton> buttons, bool happy = false, bool worried = false)
        {
            ClearModal();
            _modalRoot.gameObject.SetActive(true);
            _card = Panel(_modalRoot, "Card", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, 400), Bg);
            _card.pivot = new Vector2(0.5f, 0.5f);
            _card.anchoredPosition = Vector2.zero;
            var layout = _card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(48, 48, 44, 48);
            layout.spacing = 22;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            var fitter = _card.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            if (image != null)
            {
                var img = Img(_card, image, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(500, 500), Color.white);
                img.preserveAspect = true;
                img.raycastTarget = false;
                var le = img.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = Mathf.Min(560, Screen.height * 0.28f / GetComponent<Canvas>().scaleFactor);
                if (happy || worried) img.gameObject.AddComponent<UiWobble>().Tremble = worried;
            }
            if (!string.IsNullOrEmpty(title)) LayoutLabel(_card, title, 64, Ink, FontStyle.Bold, 90);
            if (!string.IsNullOrEmpty(body)) LayoutLabel(_card, body, 38, PaletteColors.Hex("#7A5A46"), FontStyle.Bold, -1);
            if (buttons != null)
                foreach (var b in buttons) AddButton(_card, b);
            return _card;
        }

        public void AddButton(RectTransform parent, ModalButton b)
        {
            var rt = Panel(parent, "Btn_" + b.Label, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(800, 130), b.Color);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = string.IsNullOrEmpty(b.Sub) ? 130 : 160;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.interactable = b.Interactable;
            var onClick = b.OnClick;
            btn.onClick.AddListener(() => onClick?.Invoke());
            bool dark = b.Color == White || b.Color == Yellow;
            var text = Label(rt, string.IsNullOrEmpty(b.Sub) ? b.Label : b.Label + "\n<size=28>" + b.Sub + "</size>", 44, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(780, 150), dark ? Ink : Color.white, FontStyle.Bold);
            text.supportRichText = true;
            if (!b.Interactable) rt.GetComponent<Image>().color = PaletteColors.WithAlpha(b.Color, 0.45f);
        }

        /// <summary>Scrollable grid of level buttons.</summary>
        public void ShowLevelGrid(string title, IReadOnlyList<LevelEntry> entries, Func<int, bool> unlocked, Func<int, bool> completed, int current,
            Action<int> pick, Action close)
        {
            var card = ShowModal(null, title, null, null);
            var scroll = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            var srt = (RectTransform)scroll.transform;
            srt.SetParent(card, false);
            scroll.GetComponent<Image>().color = new Color(0, 0, 0, 0.03f);
            scroll.AddComponent<LayoutElement>().preferredHeight = 1000;
            var content = new GameObject("Content", typeof(RectTransform), typeof(GridLayoutGroup), typeof(ContentSizeFitter));
            var crt = (RectTransform)content.transform;
            crt.SetParent(srt, false);
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1);
            var grid = content.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(140, 140);
            grid.spacing = new Vector2(16, 16);
            grid.padding = new RectOffset(10, 10, 10, 10);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 5;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sr = scroll.GetComponent<ScrollRect>();
            sr.content = crt;
            sr.horizontal = false;
            sr.viewport = srt;
            for (int i = 0; i < entries.Count; i++)
            {
                int index = i;
                bool open = unlocked(i);
                var color = !open ? PaletteColors.Hex("#EADFCD") : i == current ? Yellow : completed(entries[i].Level) ? GreenC : White;
                var cell = Panel(crt, "L" + entries[i].Level, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(140, 140), color);
                var btn = cell.gameObject.AddComponent<Button>();
                btn.interactable = open;
                btn.onClick.AddListener(() => pick(index));
                Label(cell, entries[i].Level.ToString(), 46, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(130, 130),
                    !open ? PaletteColors.Hex("#B9A58E") : completed(entries[i].Level) && i != current ? Color.white : Ink, FontStyle.Bold);
            }
            AddButton(card, new ModalButton("Kapat", White, close));
        }

        public void CloseModal()
        {
            ClearModal();
            if (_modalRoot != null) _modalRoot.gameObject.SetActive(false);
        }

        private void ClearModal()
        {
            if (_card != null) Destroy(_card.gameObject);
            _card = null;
        }

        // ------------------------------------------------------------------ builders
        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private RectTransform Panel(RectTransform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0, anchor.y);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = ProceduralSprites.Panel;
            img.type = Image.Type.Sliced;
            img.color = color;
            return rt;
        }

        private Image Img(RectTransform parent, Sprite sprite, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(sprite != null ? sprite.name : "img", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0, anchor.y);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            return img;
        }

        private Text Label(RectTransform parent, string text, int size, Vector2 anchor, Vector2 pos, Vector2 box, Color color, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = box;
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private void LayoutLabel(RectTransform parent, string text, int size, Color color, FontStyle style, float height)
        {
            var t = Label(parent, text, size, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(800, height > 0 ? height : 100), color, style);
            var le = t.gameObject.AddComponent<LayoutElement>();
            if (height > 0) le.preferredHeight = height;
        }
    }
}
