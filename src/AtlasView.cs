using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Atlas
{
    /// <summary>
    /// The unrolled atlas: a window over the HUD with three stacked sheets (parchment, ink, fog)
    /// and the recorded pins, panned by dragging and zoomed with the wheel. It hangs beside the HUD
    /// root rather than under it, so hiding the HUD does not hide an open atlas. There is no
    /// player marker on purpose: the atlas is a drawing, it does not know where you stand.
    /// </summary>
    internal sealed class AtlasView
    {
        // Screen margin around the window, the frame's width and its colour, in canvas units.
        private const float Margin = 60f;
        private const float Border = 4f;
        private static readonly Color s_shade = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color s_border = new Color(0.2f, 0.15f, 0.1f, 1f);

        // Zoom: the largest scale, the opening scale relative to fitting the whole sheet, and the
        // step per wheel notch and per second of gamepad stick.
        private const float MaxScale = 4f;
        private const float OpenZoom = 2.5f;
        private const float WheelStep = 1.18f;
        private const float StickZoom = 1.8f;
        private const float StickPan = 900f;

        private const float PinSize = 26f;
        private const float PinFontSize = 16f;

        private static readonly AccessTools.FieldRef<Minimap, GameObject> s_pinNamePrefab =
            AccessTools.FieldRefAccess<Minimap, GameObject>("m_pinNamePrefab");

        private GameObject m_root;
        private RectTransform m_viewport;
        private RectTransform m_content;
        private RectTransform m_pinLayer;
        private RawImage m_paper;
        private RawImage m_ink;
        private RawImage m_fog;
        private readonly List<RectTransform> m_pins = new List<RectTransform>();
        private Snapshot m_pinSnapshot;
        private Canvas m_canvas;

        private float m_scale;
        // What the sheet and pins were last laid out with; layout is only touched when these change.
        private float m_appliedScale = -1f;
        private Vector2 m_appliedPosition;
        private Vector2 m_position;
        private bool m_hasView;
        private bool m_dragging;
        private Vector3 m_lastPointer;
        private int m_shownFrames;

        public bool IsOpen { get; private set; }
        public int ClosedFrames { get; private set; } = 999;

        public void Show(Texture paper, Texture ink, Texture fog, Snapshot snapshot)
        {
            if (!Build()) return;
            m_paper.texture = paper;
            m_ink.texture = ink;
            m_fog.texture = fog;
            SetPins(snapshot);
            m_root.SetActive(true);
            m_root.transform.SetAsLastSibling();
            IsOpen = true;
            m_shownFrames = 0;
            m_dragging = false;
            Canvas.ForceUpdateCanvases();
            if (!m_hasView) ResetView();
            Apply();
        }

        public void Hide()
        {
            if (!IsOpen) return;
            IsOpen = false;
            ClosedFrames = 0;
            m_dragging = false;
            if (m_root) m_root.SetActive(false);
        }

        // Forgets the remembered view and pins, for a new world.
        public void Clear()
        {
            Hide();
            m_hasView = false;
            if (m_root) Object.Destroy(m_root);
            m_root = null;
            m_pins.Clear();
            m_pinSnapshot = null;
        }

        public void Refresh(Texture fog, Snapshot snapshot)
        {
            if (!m_root) return;
            m_fog.texture = fog;
            SetPins(snapshot);
            Apply();
        }

        public void Update()
        {
            if (!IsOpen)
            {
                if (ClosedFrames < 999) ClosedFrames++;
                return;
            }
            if (!m_root)
            {
                Hide();
                return;
            }
            m_shownFrames++;
            if (m_shownFrames > 1 && ClosePressed())
            {
                Hide();
                return;
            }

            float canvasScale = m_canvas ? m_canvas.scaleFactor : 1f;
            Camera camera = m_canvas && m_canvas.renderMode != RenderMode.ScreenSpaceOverlay ? m_canvas.worldCamera : null;
            Vector3 pointer = ZInput.pointerPosition;

            float scroll = ZInput.IsGamepadActive() ? 0f : ZInput.GetMouseScrollWheel();
            if (scroll != 0f && RectTransformUtility.RectangleContainsScreenPoint(m_viewport, pointer, camera))
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(m_viewport, pointer, camera, out Vector2 local);
                ZoomAround(local, scroll > 0f ? WheelStep : 1f / WheelStep);
            }
            if (ZInput.GetMouseButtonDown(1)) ResetView();

            if (ZInput.GetMouseButtonDown(0) && RectTransformUtility.RectangleContainsScreenPoint(m_viewport, pointer, camera))
            {
                m_dragging = true;
                m_lastPointer = pointer;
            }
            if (!ZInput.GetMouseButton(0)) m_dragging = false;
            if (m_dragging)
            {
                Vector3 delta = pointer - m_lastPointer;
                m_lastPointer = pointer;
                m_position += new Vector2(delta.x, delta.y) / canvasScale;
            }

            if (ZInput.IsGamepadActive())
            {
                float dt = Time.unscaledDeltaTime;
                m_position -= new Vector2(ZInput.GetJoyLeftStickX(), -ZInput.GetJoyLeftStickY()) * StickPan * dt;
                float zoom = -ZInput.GetJoyRightStickY();
                if (Mathf.Abs(zoom) > 0.2f) ZoomAround(Vector2.zero, Mathf.Pow(StickZoom, zoom * dt));
            }
            Apply();
        }

        private static bool ClosePressed()
        {
            return ZInput.GetButtonDown("Map") || AtlasMap.JoyMapPressed() || ZInput.GetButtonDown("JoyButtonB")
                || ZInput.GetKeyDown(KeyCode.Escape, logWarning: false);
        }

        // Zooms keeping the sheet point under the given viewport-local position in place.
        private void ZoomAround(Vector2 local, float factor)
        {
            float scale = Mathf.Clamp(m_scale * factor, FitScale(), MaxScale);
            Vector2 sheet = (local - m_position) / m_scale;
            m_scale = scale;
            m_position = local - sheet * m_scale;
        }

        private void ResetView()
        {
            m_scale = Mathf.Min(FitScale() * OpenZoom, MaxScale);
            m_position = Vector2.zero;
            m_hasView = true;
        }

        private float FitScale()
        {
            Rect rect = m_viewport.rect;
            float fit = Mathf.Min(rect.width, rect.height) / InkPainter.CanvasSize;
            return fit > 0f ? fit : 0.1f;
        }

        // Keeps the sheet's centre inside its own bounds, so it can never be dragged out of sight.
        private void Apply()
        {
            m_scale = Mathf.Clamp(m_scale, FitScale(), MaxScale);
            float limit = InkPainter.CanvasSize * m_scale * 0.5f;
            m_position = new Vector2(Mathf.Clamp(m_position.x, -limit, limit), Mathf.Clamp(m_position.y, -limit, limit));
            if (m_position != m_appliedPosition)
            {
                m_content.anchoredPosition = m_position;
                m_appliedPosition = m_position;
            }
            if (m_scale == m_appliedScale) return;
            m_appliedScale = m_scale;
            m_content.localScale = new Vector3(m_scale, m_scale, 1f);
            var counter = new Vector3(1f / m_scale, 1f / m_scale, 1f);
            foreach (RectTransform pin in m_pins)
                if (pin) pin.localScale = counter;
        }

        private bool Build()
        {
            if (m_root) return true;
            if (!Hud.instance || !Hud.instance.m_rootObject) return false;
            Transform parent = Hud.instance.m_rootObject.transform.parent;
            if (!parent) parent = Hud.instance.m_rootObject.transform;

            m_root = new GameObject("AtlasView", typeof(RectTransform), typeof(Image));
            var root = (RectTransform)m_root.transform;
            root.SetParent(parent, false);
            Stretch(root, 0f);
            m_root.GetComponent<Image>().color = s_shade;

            var frame = NewRect("Frame", root, typeof(Image));
            Stretch(frame, Margin);
            frame.GetComponent<Image>().color = s_border;

            m_viewport = NewRect("Viewport", frame, typeof(RectMask2D));
            Stretch(m_viewport, Border);

            m_content = NewRect("Sheet", m_viewport);
            m_content.anchorMin = m_content.anchorMax = m_content.pivot = new Vector2(0.5f, 0.5f);
            m_content.sizeDelta = new Vector2(InkPainter.CanvasSize, InkPainter.CanvasSize);

            m_paper = NewLayer("Paper");
            m_ink = NewLayer("Ink");
            m_fog = NewLayer("Fog");
            m_pinLayer = NewRect("Pins", m_content);
            Stretch(m_pinLayer, 0f);

            Canvas canvas = m_root.GetComponentInParent<Canvas>();
            m_canvas = canvas ? canvas.rootCanvas : null;
            m_pins.Clear();
            m_pinSnapshot = null;
            m_appliedScale = -1f;
            m_appliedPosition = new Vector2(float.NaN, float.NaN);
            m_root.SetActive(false);
            return true;
        }

        private RawImage NewLayer(string name)
        {
            var rect = NewRect(name, m_content, typeof(RawImage));
            Stretch(rect, 0f);
            var image = rect.GetComponent<RawImage>();
            image.raycastTarget = false;
            return image;
        }

        // Rebuilds the markers only for a new snapshot; reopening the atlas reuses them.
        private void SetPins(Snapshot snapshot)
        {
            if (snapshot == m_pinSnapshot && m_pins.Count > 0) return;
            m_pinSnapshot = snapshot;
            m_appliedScale = -1f;
            foreach (RectTransform pin in m_pins)
                if (pin) Object.Destroy(pin.gameObject);
            m_pins.Clear();
            Minimap minimap = Minimap.instance;
            if (snapshot == null || !minimap || !minimap.m_pinPrefab) return;

            Dictionary<string, Sprite> sprites = PinSprites(minimap);
            TMP_FontAsset font = PinFont(minimap);
            float half = InkPainter.HalfExtent;
            float size = InkPainter.CanvasSize;
            foreach (Snapshot.Pin pin in snapshot.m_pins)
            {
                if (!sprites.TryGetValue(pin.m_icon, out Sprite sprite) || !sprite) continue;
                GameObject marker = Object.Instantiate(minimap.m_pinPrefab, m_pinLayer);
                var rect = (RectTransform)marker.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(pin.m_position.x / (2f * half) * size, pin.m_position.z / (2f * half) * size);
                rect.sizeDelta = new Vector2(PinSize, PinSize);
                foreach (Graphic graphic in marker.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                var image = marker.GetComponent<Image>();
                if (image)
                {
                    image.sprite = sprite;
                    image.color = Paper.InkColor;
                }
                Transform check = marker.transform.Find("Checked");
                if (check) check.gameObject.SetActive(pin.m_checked);
                string label = Localization.instance.Localize(pin.m_name);
                if (font && !string.IsNullOrEmpty(label)) AddLabel(rect, label, font);
                m_pins.Add(rect);
            }
        }

        private static void AddLabel(RectTransform pin, string label, TMP_FontAsset font)
        {
            var rect = NewRect("Name", pin);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(220f, 24f);
            rect.anchoredPosition = new Vector2(0f, -2f);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = PinFontSize;
            text.color = Paper.InkColor;
            text.alignment = TextAlignmentOptions.Top;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.text = label;
        }

        // Every sprite a pin may use, by name: the pin icons and the location icons (traders,
        // start, bosses). Recorded pins keep only the sprite's name.
        private static Dictionary<string, Sprite> PinSprites(Minimap minimap)
        {
            var sprites = new Dictionary<string, Sprite>();
            foreach (Minimap.SpriteData data in minimap.m_icons)
                if (data.m_icon && !sprites.ContainsKey(data.m_icon.name)) sprites[data.m_icon.name] = data.m_icon;
            foreach (Minimap.LocationSpriteData data in minimap.m_locationIcons)
                if (data.m_icon && !sprites.ContainsKey(data.m_icon.name)) sprites[data.m_icon.name] = data.m_icon;
            return sprites;
        }

        // The font of vanilla's own pin names, or the HUD's.
        private static TMP_FontAsset PinFont(Minimap minimap)
        {
            GameObject prefab = s_pinNamePrefab(minimap);
            TMP_Text text = prefab ? prefab.GetComponentInChildren<TMP_Text>(true) : null;
            if (text && text.font) return text.font;
            return Hud.instance && Hud.instance.m_hoverName ? Hud.instance.m_hoverName.font : null;
        }

        private static RectTransform NewRect(string name, Transform parent, params System.Type[] components)
        {
            var go = new GameObject(name, typeof(RectTransform));
            foreach (System.Type component in components) go.AddComponent(component);
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
