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

        // Pins are drawn with the map's pens (PinGlyphs): a pen line round a body filled in the
        // wash of the biome the pin stands in, with no terrain ink, as a peak's body is filled
        // in the mountains'; no halo, ring or backing. Other icons are traced from their sprite's
        // solid pixels (alpha over IconSolid). Names follow cartographic practice (Imhof, QGIS):
        // beside the icon, LabelGap from it, in regular weight with a little letter-spacing, bare
        // ink on the clearest ground round the pin (PinLayout). Canvas units; PinPixels is the
        // drawn pins' texture size, mipmapped down to their size on screen.
        private const float PinSize = 26f;
        private const int PinPixels = 96;
        private const float PinFontSize = 16f;
        private const byte IconSolid = 200;
        private const float LabelGap = 4f;
        private const float LabelSpacing = 3f;
        private const float LabelClearance = 2f;
        // The engraver's gap round a name's letters: the underlay's grow in TextMeshPro's 0 to 1,
        // and how far past the name's box the paper behind it reaches, in canvas units.
        private const float GapDilate = 0.45f;
        private const float GapReach = 4f;
        private static Material s_gapMaterial;
        private static TMP_FontAsset s_gapFont;
        // Names are placed again when the zoom has changed by more than this share.
        private const float LayoutStep = 0.03f;
        private static readonly Dictionary<string, PinTextures> s_pinTextures = new Dictionary<string, PinTextures>();
        private readonly List<PinLabel> m_labels = new List<PinLabel>();
        private readonly List<Rect> m_placed = new List<Rect>();
        private float m_layoutScale = -1f;
        private InkDensity m_density;
        private WorldSource m_world;

        private static readonly AccessTools.FieldRef<Minimap, GameObject> s_pinNamePrefab =
            AccessTools.FieldRefAccess<Minimap, GameObject>("m_pinNamePrefab");

        private GameObject m_root;
        private RectTransform m_viewport;
        private RectTransform m_content;
        private RectTransform m_pinLayer;
        private RawImage m_paper;
        private RawImage m_ink;
        private RectTransform m_detailLayer;
        private RawImage m_fog;
        private readonly Vector3[] m_corners = new Vector3[4];
        private readonly List<RectTransform> m_pins = new List<RectTransform>();
        // Each pin's lift (PinGlyphs.Lift), parallel to m_pins.
        private readonly List<float> m_pinLifts = new List<float>();
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

        public void Show(Texture paper, Texture ink, Texture fog, Snapshot snapshot, InkDensity density, WorldSource world)
        {
            if (!Build()) return;
            m_density = density;
            // Pins take their body colour from the world; built before it was decoded, they are
            // built again once it is.
            if (world != m_world) m_pinSnapshot = null;
            m_world = world;
            if (m_paper.texture != paper) m_pinSnapshot = null;
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
            m_pinLifts.Clear();
            m_pinSnapshot = null;
            m_density = null;
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
            if (m_layoutScale < 0f || Mathf.Abs(m_scale / m_layoutScale - 1f) > LayoutStep)
            {
                m_layoutScale = m_scale;
                LayoutLabels();
            }
            else
            {
                foreach (PinLabel label in m_labels)
                    if (label.m_rect && label.m_place >= 0) PlaceNamePaper(label);
            }
        }

        // Where close-up tiles go: over the ink, under the fog. Null before the window is built.
        public RectTransform DetailLayer => m_root ? m_detailLayer : null;

        // The most screen pixels per sheet pixel the view zooms to.
        public float MaxOnScreen => MaxScale * (m_canvas ? m_canvas.scaleFactor : 1f);

        // The part of the sheet in view (sheet pixels, y running south), the screen pixels per
        // sheet pixel, and the sheet point under the pointer (the view's centre when the pointer
        // is outside it), where a zoom would centre.
        public bool ViewRegion(out RectInt visible, out float onScreen, out Vector2 pointer)
        {
            visible = default;
            pointer = default;
            onScreen = m_scale * (m_canvas ? m_canvas.scaleFactor : 1f);
            if (!IsOpen || !m_root) return false;
            float half = InkPainter.CanvasSize * 0.5f;
            Camera camera = m_canvas && m_canvas.renderMode != RenderMode.ScreenSpaceOverlay ? m_canvas.worldCamera : null;
            Vector3 screen = ZInput.pointerPosition;
            if (RectTransformUtility.RectangleContainsScreenPoint(m_viewport, screen, camera)
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(m_content, screen, camera, out Vector2 under))
                pointer = new Vector2(under.x + half, half - under.y);
            else
                pointer = new Vector2(half - m_position.x / m_scale, half + m_position.y / m_scale);

            // The viewport's corners in the sheet's own units, centred on the sheet.
            m_viewport.GetWorldCorners(m_corners);
            float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
            foreach (Vector3 corner in m_corners)
            {
                Vector3 local = m_content.InverseTransformPoint(corner);
                left = Mathf.Min(left, local.x + half);
                right = Mathf.Max(right, local.x + half);
                top = Mathf.Min(top, half - local.y);
                bottom = Mathf.Max(bottom, half - local.y);
            }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(left), 0, InkPainter.CanvasSize);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(right), 0, InkPainter.CanvasSize);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(top), 0, InkPainter.CanvasSize);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(bottom), 0, InkPainter.CanvasSize);
            if (x1 <= x0 || y1 <= y0) return false;
            visible = new RectInt(x0, y0, x1 - x0, y1 - y0);
            return true;
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
            m_detailLayer = NewRect("Detail", m_content);
            Stretch(m_detailLayer, 0f);
            m_fog = NewLayer("Fog");
            m_pinLayer = NewRect("Pins", m_content);
            Stretch(m_pinLayer, 0f);

            Canvas canvas = m_root.GetComponentInParent<Canvas>();
            m_canvas = canvas ? canvas.rootCanvas : null;
            m_pins.Clear();
            m_pinLifts.Clear();
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
            m_pinLifts.Clear();
            m_labels.Clear();
            Minimap minimap = Minimap.instance;
            if (snapshot == null || !minimap) return;

            Dictionary<string, Sprite> sprites = PinSprites(minimap);
            Dictionary<string, PinGlyph> drawn = DrawnIcons(minimap);
            TMP_FontAsset font = PinFont(minimap);
            float half = InkPainter.HalfExtent;
            float size = InkPainter.CanvasSize;
            PinTextures check = Textures(PinGlyph.Checked, null);
            foreach (Snapshot.Pin pin in snapshot.m_pins)
            {
                PinTextures textures;
                PinGlyph glyph;
                if (drawn.TryGetValue(pin.m_icon, out glyph)) textures = Textures(glyph, null);
                else if (sprites.TryGetValue(pin.m_icon, out Sprite sprite) && sprite) textures = Textures(glyph = PinGlyph.Traced, sprite);
                else continue;
                if (textures == null) continue;
                var holder = NewRect("Pin", m_pinLayer);
                holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
                holder.anchoredPosition = new Vector2(pin.m_position.x / (2f * half) * size, pin.m_position.z / (2f * half) * size);
                holder.sizeDelta = new Vector2(PinSize, PinSize);
                // The holder sits on the pin's place; the glyph stands on it (PinGlyphs.Lift).
                float lift = PinGlyphs.Lift(glyph) * PinSize;
                AddBody(holder, textures.m_body, pin.m_position, lift);
                AddImage(holder, "Ink", textures.m_ink, lift);
                if (pin.m_checked && check != null) AddImage(holder, "Checked", check.m_ink, lift);
                string label = Localization.instance.Localize(pin.m_name);
                if (font && !string.IsNullOrEmpty(label))
                {
                    PinLabel placed = AddLabel(holder, label, font);
                    placed.m_lift = lift;
                    // Bosses and traders first, then named pins in the order they were made.
                    placed.m_priority = pin.m_type == Minimap.PinType.Boss || pin.m_type == Minimap.PinType.Hildir1
                        || pin.m_type == Minimap.PinType.Hildir2 || pin.m_type == Minimap.PinType.Hildir3 ? 0 : 1;
                    m_labels.Add(placed);
                }
                m_pins.Add(holder);
                m_pinLifts.Add(lift);
            }
            m_labels.Sort((a, b) => a.m_priority.CompareTo(b.m_priority));
            m_layoutScale = -1f;
        }

        // One pin's name: where it may go and where it went last, so it does not hop needlessly.
        private sealed class PinLabel
        {
            public RectTransform m_holder;
            public RectTransform m_rect;
            public TextMeshProUGUI m_text;
            public TextMeshProUGUI m_gapText;
            public RawImage m_paper;
            public Vector2 m_size;
            public int m_priority;
            public int m_place = -1;
            // How far the pin's glyph stands above its place (canvas units at screen size).
            public float m_lift;
        }

        // A name: the letters in bare ink over their gap, a stencil of the same letters grown a
        // little (a TextMeshPro underlay, shown only as the mask) through which the paper under
        // the name shows, so terrain ink breaks off round the lettering as an engraver's lines do.
        private PinLabel AddLabel(RectTransform pin, string name, TMP_FontAsset font)
        {
            var rect = NewRect("Name", pin);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            var label = new PinLabel { m_holder = pin, m_rect = rect };
            Material gapMaterial = GapMaterial(font);
            if (gapMaterial && m_paper && m_paper.texture)
            {
                var gap = NewRect("Gap", rect);
                Stretch(gap, 0f);
                label.m_gapText = NameText(gap, name, font);
                label.m_gapText.fontSharedMaterial = gapMaterial;
                gap.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                var paper = NewRect("Paper", gap, typeof(RawImage));
                Stretch(paper, -GapReach);
                label.m_paper = paper.GetComponent<RawImage>();
                label.m_paper.texture = m_paper.texture;
                label.m_paper.raycastTarget = false;
            }
            var ink = NewRect("Ink", rect);
            Stretch(ink, 0f);
            label.m_text = NameText(ink, name, font);
            label.m_size = label.m_text.GetPreferredValues(name);
            rect.sizeDelta = label.m_size;
            return label;
        }

        private static TextMeshProUGUI NameText(RectTransform rect, string label, TMP_FontAsset font)
        {
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = PinFontSize;
            text.characterSpacing = LabelSpacing;
            text.color = Paper.InkColor;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.text = label;
            return text;
        }

        // The paper under a name's gap, for the current zoom and the name's place.
        private void PlaceNamePaper(PinLabel label)
        {
            if (!label.m_paper) return;
            Vector2 centre = label.m_holder.anchoredPosition + label.m_rect.anchoredPosition / m_scale;
            Vector2 extent = (label.m_size + 2f * new Vector2(GapReach, GapReach)) / m_scale / InkPainter.CanvasSize;
            Vector2 at = centre / InkPainter.CanvasSize + new Vector2(0.5f, 0.5f);
            label.m_paper.uvRect = new Rect(at.x - extent.x * 0.5f, at.y - extent.y * 0.5f, extent.x, extent.y);
        }

        // Main thread. The gap's font material: an opaque underlay grown by GapDilate with a hard
        // edge, which the mask turns into the letters' grown shape. One copy, shared by every
        // name; vanilla's material is never changed.
        private static Material GapMaterial(TMP_FontAsset font)
        {
            if (s_gapMaterial && s_gapFont == font) return s_gapMaterial;
            Material source = font ? font.material : null;
            if (!source) return null;
            var material = new Material(source) { name = source.name + "_atlas_gap" };
            material.EnableKeyword(ShaderUtilities.Keyword_Underlay);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayColor)) material.SetColor(ShaderUtilities.ID_UnderlayColor, Color.white);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayDilate)) material.SetFloat(ShaderUtilities.ID_UnderlayDilate, GapDilate);
            if (material.HasProperty(ShaderUtilities.ID_UnderlaySoftness)) material.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0f);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayOffsetX)) material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
            if (material.HasProperty(ShaderUtilities.ID_UnderlayOffsetY)) material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
            s_gapMaterial = material;
            s_gapFont = font;
            return material;
        }

        // Places every name beside its pin, in priority order, where PinLayout finds the clearest
        // ground that overlaps no icon and no name already placed; a name with no room is hidden.
        // Pins keep their screen size whatever the zoom, so only the distances between them and
        // the stretch of sheet under each name change, and this runs only when the zoom has
        // changed by LayoutStep. Canvas units throughout.
        private void LayoutLabels()
        {
            m_placed.Clear();
            float icon = PinSize * 0.5f + LabelGap;
            for (int i = 0; i < m_pins.Count; i++)
            {
                RectTransform pin = m_pins[i];
                if (!pin) continue;
                Vector2 at = pin.anchoredPosition * m_scale + new Vector2(0f, m_pinLifts[i]);
                m_placed.Add(new Rect(at.x - PinSize * 0.5f, at.y - PinSize * 0.5f, PinSize, PinSize));
            }
            foreach (PinLabel label in m_labels)
            {
                if (!label.m_rect) continue;
                // Names are laid out round the glyph as drawn, which stands above the pin's place.
                Vector2 at = label.m_holder.anchoredPosition * m_scale + new Vector2(0f, label.m_lift);
                int chosen = PinLayout.Choose(at, label.m_size, label.m_place, icon, LabelClearance, m_placed, InkUnder);
                label.m_place = chosen;
                bool show = chosen >= 0;
                if (label.m_rect.gameObject.activeSelf != show) label.m_rect.gameObject.SetActive(show);
                if (!show) continue;
                Rect area = PinLayout.Area(at, label.m_size, chosen, icon);
                m_placed.Add(area);
                label.m_rect.pivot = new Vector2(0.5f, 0.5f);
                label.m_rect.anchoredPosition = area.center - label.m_holder.anchoredPosition * m_scale;
                int align = PinLayout.Alignment(chosen);
                TextAlignmentOptions alignment = align < 0 ? TextAlignmentOptions.Left : align > 0 ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
                label.m_text.alignment = alignment;
                if (label.m_gapText) label.m_gapText.alignment = alignment;
                PlaceNamePaper(label);
            }
        }

        // The mean ink under an area of the scaled sheet (canvas units from its centre, y up);
        // unexplored ground shows no ink.
        private float InkUnder(Rect area)
        {
            if (m_density == null) return 0f;
            float half = InkPainter.CanvasSize * 0.5f;
            float left = area.xMin / m_scale + half, right = area.xMax / m_scale + half;
            float top = half - area.yMax / m_scale, bottom = half - area.yMin / m_scale;
            if (m_pinSnapshot != null)
            {
                float mpp = InkPainter.MetresPerPixel;
                Vector2 centre = area.center / m_scale * mpp;
                if (!m_pinSnapshot.IsExplored(centre.x, centre.y)) return 0f;
            }
            return m_density.Mean(left, top, right, bottom);
        }

        // The pin's body: filled in the wash of the biome the pin stands in, as a peak's body is
        // filled in the mountains', so terrain ink stops at the pin's own outline. Before the
        // world is decoded, plain paper.
        private void AddBody(RectTransform holder, Texture body, Vector3 position, float lift)
        {
            Color32 colour = Paper.PaperColor;
            if (m_world != null) colour = Paper.PinBodyColor(m_world.LandAt(position.x, position.z), m_world.IsWaterAt(position.x, position.z));
            AddImage(holder, "Body", body, lift).color = colour;
        }

        private static RawImage AddImage(RectTransform holder, string name, Texture texture, float lift)
        {
            var rect = NewRect(name, holder, typeof(RawImage));
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, lift);
            rect.sizeDelta = new Vector2(PinSize, PinSize);
            var image = rect.GetComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
        }

        // The vanilla pin icons the atlas draws itself, by sprite name; any other icon is traced.
        private static Dictionary<string, PinGlyph> DrawnIcons(Minimap minimap)
        {
            var drawn = new Dictionary<string, PinGlyph>();
            foreach (Minimap.SpriteData data in minimap.m_icons)
            {
                if (!data.m_icon) continue;
                PinGlyph glyph;
                switch (data.m_name)
                {
                    case Minimap.PinType.Icon0: glyph = PinGlyph.Fire; break;
                    case Minimap.PinType.Icon1: glyph = PinGlyph.House; break;
                    case Minimap.PinType.Icon2: glyph = PinGlyph.Hammer; break;
                    case Minimap.PinType.Icon3: glyph = PinGlyph.Point; break;
                    case Minimap.PinType.Icon4: glyph = PinGlyph.Portal; break;
                    case Minimap.PinType.Death: glyph = PinGlyph.Death; break;
                    case Minimap.PinType.Bed: glyph = PinGlyph.Bed; break;
                    case Minimap.PinType.Boss: glyph = PinGlyph.Boss; break;
                    case Minimap.PinType.Hildir1: glyph = PinGlyph.Tomb; break;
                    case Minimap.PinType.Hildir2: glyph = PinGlyph.Cavern; break;
                    case Minimap.PinType.Hildir3: glyph = PinGlyph.Tower; break;
                    default: continue;
                }
                if (!drawn.ContainsKey(data.m_icon.name)) drawn[data.m_icon.name] = glyph;
            }
            // Location pins carry their location's sprite; known places by the location's prefab
            // name, any other location (a new one, another mod's) is traced.
            foreach (Minimap.LocationSpriteData data in minimap.m_locationIcons)
            {
                if (!data.m_icon || string.IsNullOrEmpty(data.m_name) || drawn.ContainsKey(data.m_icon.name)) continue;
                string name = data.m_name.ToLowerInvariant();
                if (name.Contains("starttemple")) drawn[data.m_icon.name] = PinGlyph.StoneCircle;
                else if (name.Contains("vendor") || name.Contains("haldor")) drawn[data.m_icon.name] = PinGlyph.Trader;
                else if (name.Contains("hildir")) drawn[data.m_icon.name] = PinGlyph.Tent;
                else if (name.Contains("bogwitch")) drawn[data.m_icon.name] = PinGlyph.Cauldron;
            }
            return drawn;
        }

        // Main thread. A pin's two textures, made once per glyph or traced sprite and kept for the
        // session; null when a sprite could not be read.
        private static PinTextures Textures(PinGlyph glyph, Sprite sprite)
        {
            string key = glyph == PinGlyph.Traced ? "sprite:" + sprite.name : glyph.ToString();
            if (s_pinTextures.TryGetValue(key, out PinTextures cached)) return cached;
            PinTextures made = null;
            try
            {
                PinMark mark = glyph == PinGlyph.Traced ? TraceSprite(sprite) : PinGlyphs.Draw(glyph, PinPixels);
                made = new PinTextures { m_ink = MarkTexture(mark.m_ink, mark.m_size, Paper.InkColor, "Atlas pin ink") };
                made.m_body = MarkTexture(mark.m_body, mark.m_size, new Color32(255, 255, 255, 255), "Atlas pin body");
            }
            catch (System.Exception e)
            {
                Jotunn.Logger.LogWarning($"The atlas could not draw pin icon {key}: {e.Message}");
            }
            s_pinTextures[key] = made;
            return made;
        }

        // Coverage (rows running south) as a mipmapped texture of one colour (rows running north).
        private static Texture2D MarkTexture(byte[] coverage, int size, Color32 colour, string name)
        {
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    colour.a = coverage[(size - 1 - y) * size + x];
                    pixels[y * size + x] = colour;
                }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        // Main thread. The sprite's texture is not readable, so its pixels are fetched through a
        // render texture; a texture reduced by quality settings only softens the trace.
        private static PinMark TraceSprite(Sprite sprite)
        {
            Rect area = sprite.textureRect;
            int w = Mathf.RoundToInt(area.width), h = Mathf.RoundToInt(area.height);
            Texture source = sprite.texture;
            RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                read.ReadPixels(new Rect(area.x, area.y, w, h), 0, 0);
                read.Apply(updateMipmaps: false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
            Color32[] icon = read.GetPixels32();
            Object.Destroy(read);
            var alpha = new byte[w * h];
            var luma = new byte[w * h];
            for (int i = 0; i < icon.Length; i++)
            {
                alpha[i] = icon[i].a;
                luma[i] = (byte)((icon[i].r * 54 + icon[i].g * 183 + icon[i].b * 19) >> 8);
            }
            return PinGlyphs.Trace(alpha, luma, w, h, PinPixels, IconSolid);
        }

        private sealed class PinTextures
        {
            public Texture2D m_ink;
            public Texture2D m_body;
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
