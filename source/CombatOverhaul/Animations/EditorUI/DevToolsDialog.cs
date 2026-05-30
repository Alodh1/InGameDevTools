#if DEBUG
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using ImGuiNET;
using Vintagestory.API.MathTools;

namespace CombatOverhaul.Animations.EditorUI;

internal sealed class DevToolsDialog : GuiDialog
{
    private const string ComposerKey = "combatoverhaul:dev-tools-dialog";
    private readonly DebugWindowManager _manager;
    private readonly EditorAppState _state;
    private readonly EditorInputRouter _inputRouter;
    private readonly Matrixf _viewportLightMatrix = new();
    private readonly Vec4f _viewportLightPosition = new(1f, -1f, 0f, 0f);
    private ElementBounds? _viewportSceneBounds;
    private double _lastWindowWidth;
    private double _lastWindowHeight;
    private float _viewportYaw;
    private float _viewportZoom = 1f;
    private float _viewportPanX;
    private float _viewportPanY;
    private int _lastViewportMouseX;
    private int _lastViewportMouseY;
    private bool _lastViewportMouseInScene;

    public DevToolsDialog(ICoreClientAPI capi, DebugWindowManager manager, EditorAppState state, EditorInputRouter inputRouter) : base(capi)
    {
        _manager = manager;
        _state = state;
        _inputRouter = inputRouter;
    }

    public override string ToggleKeyCombinationCode => "";
    public override bool PrefersUngrabbedMouse => false;

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        _inputRouter.SetActive(true);
        _state.SetStatus("Proper UI active.");
        _manager.EnsureProperAnimationSelection(_state);
        ComposeDialog();
    }

    public override void OnGuiClosed()
    {
        _inputRouter.SetActive(false);
        base.OnGuiClosed();
        _manager.NotifyProperDevToolsClosed();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (WindowSizeChanged())
        {
            ComposeDialog();
        }

        base.OnRenderGUI(deltaTime);
        _manager.UpdateProperDevTools(deltaTime);
        RenderViewport(deltaTime);
    }

    internal void RecomposeIfOpen()
    {
        if (IsOpened())
        {
            ComposeDialog();
        }
    }

    private void ComposeDialog()
    {
        ClearComposers();
        _viewportSceneBounds = null;

        double windowWidth = WindowWidth;
        double windowHeight = WindowHeight;
        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;

        ElementBounds rootBounds = ElementBounds.Fixed(0, 0, windowWidth, windowHeight);
        ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 0, 0, windowWidth, windowHeight)
            .WithChild(rootBounds);

        SingleComposer = capi.Gui.CreateCompo(ComposerKey, dialogBounds);
        ElementBounds backgroundBounds = rootBounds.FlatCopy();
        SingleComposer.AddShadedDialogBG(backgroundBounds, true);
        SingleComposer.AddDialogTitleBar("Dev tools", () => TryClose());

        ComposeToolbar();
        ComposePanels();
        ComposeFooter();

        SingleComposer.Compose();
    }

    private void ComposeToolbar()
    {
        double x = EditorTheme.Padding;
        double y = 34;
        double buttonWidth = 118;
        double buttonHeight = 28;
        double windowWidth = WindowWidth;

        AddTabButton("Animations", DevToolsTab.Animations, x, y, buttonWidth, buttonHeight); x += buttonWidth + EditorTheme.Gap;
        AddTabButton("Transforms", DevToolsTab.Transforms, x, y, buttonWidth, buttonHeight); x += buttonWidth + EditorTheme.Gap;
        AddTabButton("Particles", DevToolsTab.Particles, x, y, buttonWidth, buttonHeight); x += buttonWidth + EditorTheme.Gap;
        AddTabButton("Colliders", DevToolsTab.Colliders, x, y, buttonWidth, buttonHeight); x += buttonWidth + EditorTheme.Gap;
        AddTabButton("Debug", DevToolsTab.Debug, x, y, buttonWidth, buttonHeight); x += buttonWidth + EditorTheme.Gap;
        AddTabButton("Generic", DevToolsTab.GenericDisplay, x, y, buttonWidth, buttonHeight);

        ElementBounds modeTextBounds = ElementBounds.Fixed(windowWidth - 342, y + 5, 150, 24);
        SingleComposer.AddDynamicText("UI mode: Proper UI", EditorTheme.BodyFont, modeTextBounds, "editor-ui-mode-label");
        ElementBounds fallbackButtonBounds = ElementBounds.Fixed(windowWidth - 184, y, 158, buttonHeight);
        SingleComposer.AddButton("Use ImGui", SwitchToImGui, fallbackButtonBounds, EditorTheme.ButtonFont, EnumButtonStyle.Normal, "switch-to-imgui");
    }

    private void ComposePanels()
    {
        double top = 34 + EditorTheme.ToolbarHeight + EditorTheme.Gap;
        double windowWidth = WindowWidth;
        double windowHeight = WindowHeight;
        double bottomPanelHeight = Math.Min(EditorTheme.BottomPanelHeight, Math.Max(120, windowHeight * 0.22));
        double bottomTop = windowHeight - EditorTheme.FooterHeight - bottomPanelHeight - EditorTheme.Padding;
        double mainHeight = bottomTop - top - EditorTheme.Gap;
        double left = EditorTheme.Padding;
        double leftPanelWidth = Math.Min(EditorTheme.LeftPanelWidth, Math.Max(210, windowWidth * 0.18));
        double rightPanelWidth = Math.Min(EditorTheme.RightPanelWidth, Math.Max(240, windowWidth * 0.20));
        double centerLeft = left + leftPanelWidth + EditorTheme.Gap;
        double rightLeft = windowWidth - EditorTheme.Padding - rightPanelWidth;
        double centerWidth = rightLeft - centerLeft - EditorTheme.Gap;

        ElementBounds browser = ElementBounds.Fixed(left, top, leftPanelWidth, mainHeight);
        ElementBounds viewport = ElementBounds.Fixed(centerLeft, top, centerWidth, mainHeight);
        ElementBounds properties = ElementBounds.Fixed(rightLeft, top, rightPanelWidth, mainHeight);
        ElementBounds timeline = ElementBounds.Fixed(left, bottomTop, windowWidth - EditorTheme.Padding * 2, bottomPanelHeight);

        if (_state.SelectedTab == DevToolsTab.Animations)
        {
            ComposeAnimationBrowser(browser);
            ComposeViewportPanel(viewport);
            ComposeAnimationProperties(properties);
            ComposeTimelinePanel(timeline);
            return;
        }

        AddPanel(browser, $"{GetTabLabel(_state.SelectedTab)} browser", GetBrowserText());
        ComposeSharedViewportPanel(viewport);
        AddPanel(properties, $"{GetTabLabel(_state.SelectedTab)} properties", GetPropertiesText());
        AddPanel(timeline, $"{GetTabLabel(_state.SelectedTab)} timeline", GetTimelineText());
    }

    private void ComposeAnimationBrowser(ElementBounds bounds)
    {
        AddPanelFrame(bounds, "Animation browser");
        SingleComposer.AddDynamicText(_manager.GetProperAnimationBrowserText(_state), EditorTheme.BodyFont, TextBounds(bounds, 38, 74), "animation-browser-summary");

        double y = bounds.fixedY + 116;
        double buttonHeight = 24;
        double halfWidth = (bounds.fixedWidth - 26) / 2;
        SingleComposer.AddButton("Prev", () => Run(state => _manager.SelectProperAnimationOffset(state, -1)), ElementBounds.Fixed(bounds.fixedX + 10, y, halfWidth, buttonHeight), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "animation-prev");
        SingleComposer.AddButton("Next", () => Run(state => _manager.SelectProperAnimationOffset(state, 1)), ElementBounds.Fixed(bounds.fixedX + 16 + halfWidth, y, halfWidth, buttonHeight), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "animation-next");

        y += buttonHeight + 10;
        int row = 0;
        foreach (string code in _manager.GetProperVisibleAnimationCodes(_state, 7))
        {
            string label = code == _state.SelectedAnimation ? $"* {Trim(code, 31)}" : Trim(code, 33);
            string localCode = code;
            SingleComposer.AddButton(label, () => Run(state => _manager.SelectProperAnimation(state, localCode)), ElementBounds.Fixed(bounds.fixedX + 10, y, bounds.fixedWidth - 20, buttonHeight), EditorTheme.ButtonFont, EnumButtonStyle.Normal, $"animation-row-{row++}");
            y += buttonHeight + 4;
        }
    }

    private void ComposeViewportPanel(ElementBounds bounds)
    {
        AddPanelFrame(bounds, "Viewport");
        double controlsHeight = 150;
        double sceneHeight = Math.Max(120, bounds.fixedHeight - controlsHeight - 54);
        _viewportSceneBounds = ElementBounds.Fixed(bounds.fixedX + 10, bounds.fixedY + 38, bounds.fixedWidth - 20, sceneHeight);
        SingleComposer.AddInset(_viewportSceneBounds, 1);

        double x = bounds.fixedX + 10;
        double y = _viewportSceneBounds.fixedY + _viewportSceneBounds.fixedHeight + 10;
        double width = 78;
        double height = 24;
        SingleComposer.AddButton("Play", () => Run(state => _manager.ProperPlay(state)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-play"); x += width + 6;
        SingleComposer.AddButton("Pause", () => Run(state => _manager.ProperTogglePause(state)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-pause"); x += width + 6;
        SingleComposer.AddButton("Frame <", () => Run(state => _manager.ProperStepFrame(state, -1)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-frame-prev"); x += width + 6;
        SingleComposer.AddButton("Frame >", () => Run(state => _manager.ProperStepFrame(state, 1)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-frame-next");

        x = bounds.fixedX + 10;
        y += height + 8;
        SingleComposer.AddButton("Key <", () => Run(state => _manager.ProperStepKeyframe(state, -1)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-key-prev"); x += width + 6;
        SingleComposer.AddButton("Key >", () => Run(state => _manager.ProperStepKeyframe(state, 1)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-key-next"); x += width + 6;
        SingleComposer.AddButton("Speed -", () => Run(state => _manager.ProperAdjustPlaybackSpeed(state, -0.1f)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-speed-down"); x += width + 6;
        SingleComposer.AddButton("Speed +", () => Run(state => _manager.ProperAdjustPlaybackSpeed(state, 0.1f)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "playback-speed-up");

        x = bounds.fixedX + 10;
        y += height + 16;
        SingleComposer.AddDynamicText("Camera", EditorTheme.HeaderFont, ElementBounds.Fixed(x, y, 86, height), "camera-header");
        x += 86;
        AddCameraButton("First", EditorCameraMode.FirstPerson, x, y); x += width + 6;
        AddCameraButton("Orbit", EditorCameraMode.Orbit, x, y); x += width + 6;
        AddCameraButton("Detach", EditorCameraMode.Detached, x, y);

        x = bounds.fixedX + 10;
        y += height + 10;
        SingleComposer.AddButton("Third person", () => Run(state => _manager.ProperToggleThirdPersonAnimations(state)), ElementBounds.Fixed(x, y, 128, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "third-person-toggle");
        SingleComposer.AddButton("Render offset", () => Run(state => _manager.ProperToggleRenderingOffset(state)), ElementBounds.Fixed(x + 136, y, 128, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "render-offset-toggle");
    }

    private void ComposeSharedViewportPanel(ElementBounds bounds)
    {
        AddPanelFrame(bounds, $"{GetTabLabel(_state.SelectedTab)} viewport");
        double controlsHeight = 110;
        double sceneHeight = Math.Max(120, bounds.fixedHeight - controlsHeight - 54);
        _viewportSceneBounds = ElementBounds.Fixed(bounds.fixedX + 10, bounds.fixedY + 38, bounds.fixedWidth - 20, sceneHeight);
        SingleComposer.AddInset(_viewportSceneBounds, 1);

        double x = bounds.fixedX + 10;
        double y = _viewportSceneBounds.fixedY + _viewportSceneBounds.fixedHeight + 10;
        double height = 24;
        SingleComposer.AddDynamicText(GetViewportText(), EditorTheme.MutedFont, ElementBounds.Fixed(x, y, bounds.fixedWidth - 20, 34), "shared-viewport-status");

        y += 42;
        SingleComposer.AddDynamicText("Camera", EditorTheme.HeaderFont, ElementBounds.Fixed(x, y, 86, height), "shared-camera-header");
        x += 86;
        AddCameraButton("First", EditorCameraMode.FirstPerson, x, y); x += 84;
        AddCameraButton("Orbit", EditorCameraMode.Orbit, x, y); x += 84;
        AddCameraButton("Detach", EditorCameraMode.Detached, x, y);

        x = bounds.fixedX + 10;
        y += height + 10;
        SingleComposer.AddButton("Use ImGui", SwitchToImGui, ElementBounds.Fixed(x, y, 128, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "shared-imgui-fallback");
    }

    private void ComposeAnimationProperties(ElementBounds bounds)
    {
        AddPanelFrame(bounds, "Properties");
        SingleComposer.AddDynamicText(_manager.GetProperAnimationPropertiesText(_state), EditorTheme.BodyFont, TextBounds(bounds, 38, 210), "animation-properties");

        double y = bounds.fixedY + 260;
        double height = 24;
        double halfWidth = (bounds.fixedWidth - 26) / 2;
        SingleComposer.AddButton("Undo", () => Run(state => _manager.ProperUndo(state)), ElementBounds.Fixed(bounds.fixedX + 10, y, halfWidth, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-undo");
        SingleComposer.AddButton("Redo", () => Run(state => _manager.ProperRedo(state)), ElementBounds.Fixed(bounds.fixedX + 16 + halfWidth, y, halfWidth, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-redo");

        y += height + 8;
        SingleComposer.AddButton("Clear history", () => Run(state => _manager.ProperClearHistory(state)), ElementBounds.Fixed(bounds.fixedX + 10, y, bounds.fixedWidth - 20, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-clear-history");
        y += height + 8;
        SingleComposer.AddButton("Save to source", () => Run(state => _manager.ProperSaveAnimationToSource(state)), ElementBounds.Fixed(bounds.fixedX + 10, y, bounds.fixedWidth - 20, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-save-source");
        y += height + 8;
        SingleComposer.AddButton("Export JSON", () => Run(state => _manager.ProperExportAnimationToClipboard(state)), ElementBounds.Fixed(bounds.fixedX + 10, y, bounds.fixedWidth - 20, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-export-json");

        y += height + 8;
        SingleComposer.AddButton("Save buffer", () => Run(state => _manager.ProperSaveAnimationBuffer(state)), ElementBounds.Fixed(bounds.fixedX + 10, y, halfWidth, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-save-buffer");
        SingleComposer.AddButton("Load buffer", () => Run(state => _manager.ProperLoadAnimationBuffer(state)), ElementBounds.Fixed(bounds.fixedX + 16 + halfWidth, y, halfWidth, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "proper-load-buffer");

        y += height + 14;
        SingleComposer.AddDynamicText(_manager.GetProperValidationText(_state), EditorTheme.MutedFont, ElementBounds.Fixed(bounds.fixedX + 10, y, bounds.fixedWidth - 20, bounds.fixedHeight - (y - bounds.fixedY) - 10), "proper-validation");
    }

    private void ComposeTimelinePanel(ElementBounds bounds)
    {
        AddPanelFrame(bounds, "Timeline / dope sheet");
        SingleComposer.AddDynamicText(_manager.GetProperTimelineText(_state), EditorTheme.BodyFont, ElementBounds.Fixed(bounds.fixedX + 10, bounds.fixedY + 38, 360, bounds.fixedHeight - 48), "timeline-text");

        double x = bounds.fixedX + 390;
        double y = bounds.fixedY + 38;
        double width = 74;
        double height = 24;
        SingleComposer.AddButton("0%", () => Run(state => _manager.ProperScrubFraction(state, 0)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "scrub-0"); x += width + 6;
        SingleComposer.AddButton("25%", () => Run(state => _manager.ProperScrubFraction(state, 0.25)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "scrub-25"); x += width + 6;
        SingleComposer.AddButton("50%", () => Run(state => _manager.ProperScrubFraction(state, 0.5)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "scrub-50"); x += width + 6;
        SingleComposer.AddButton("75%", () => Run(state => _manager.ProperScrubFraction(state, 0.75)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "scrub-75"); x += width + 6;
        SingleComposer.AddButton("100%", () => Run(state => _manager.ProperScrubFraction(state, 1)), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "scrub-100");

        x = bounds.fixedX + 390;
        y += height + 10;
        SingleComposer.AddButton("Loop start", () => Run(state => _manager.ProperSetLoopStart(state)), ElementBounds.Fixed(x, y, 120, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "loop-start");
        SingleComposer.AddButton("Loop end", () => Run(state => _manager.ProperSetLoopEnd(state)), ElementBounds.Fixed(x + 128, y, 120, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "loop-end");
        SingleComposer.AddButton("Use ImGui timeline", SwitchToImGui, ElementBounds.Fixed(x + 256, y, 160, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, "imgui-timeline");
    }

    private void ComposeFooter()
    {
        double y = WindowHeight - EditorTheme.FooterHeight;
        ElementBounds footerBounds = ElementBounds.Fixed(EditorTheme.Padding, y, WindowWidth - EditorTheme.Padding * 2, EditorTheme.FooterHeight - 4);
        SingleComposer.AddInset(footerBounds, 1);
        SingleComposer.AddDynamicText(_state.StatusText, EditorTheme.MutedFont, footerBounds.FlatCopy().FixedGrow(-8, -2), "status-footer");
    }

    private void RenderViewport(float deltaTime)
    {
        if (_viewportSceneBounds == null) return;
        if (capi.World?.Player?.Entity == null) return;
        if (_viewportSceneBounds.InnerWidth <= 32 || _viewportSceneBounds.InnerHeight <= 32) return;

        UpdateViewportInput();

        float size = (float)Math.Min(_viewportSceneBounds.InnerHeight * 0.82, _viewportSceneBounds.InnerWidth * 0.58) * _viewportZoom;
        if (size <= 1) return;

        double posX = _viewportSceneBounds.renderX + _viewportSceneBounds.InnerWidth / 2 - size * 0.30 + _viewportPanX;
        double posY = _viewportSceneBounds.renderY + _viewportSceneBounds.InnerHeight / 2 - size * 0.52 + _viewportPanY;
        double posZ = GuiElement.scaled(250);

        capi.Render.GlPushMatrix();
        if (focused)
        {
            capi.Render.GlTranslate(0f, 0f, 150f);
        }

        capi.Render.GlRotate(-12f, 1f, 0f, 0f);
        _viewportLightMatrix.Identity();
        _viewportLightMatrix.RotateXDeg(-12f);
        Vec4f light = _viewportLightMatrix.TransformVector(_viewportLightPosition);
        capi.Render.CurrentActiveShader?.Uniform("lightPosition", light.X, light.Y, light.Z);

        capi.Render.PushScissor(_viewportSceneBounds, false);
        capi.Render.RenderEntityToGui(deltaTime, capi.World.Player.Entity, posX, posY, posZ, _viewportYaw, size, ColorUtil.WhiteArgb);
        capi.Render.PopScissor();

        capi.Render.CurrentActiveShader?.Uniform("lightPosition", 0.7071068f, -0.7071068f, 0f);
        capi.Render.GlPopMatrix();
    }

    private void UpdateViewportInput()
    {
        if (_viewportSceneBounds == null) return;

        int mouseX = capi.Input.MouseX;
        int mouseY = capi.Input.MouseY;
        bool inScene = _viewportSceneBounds.PointInside(mouseX, mouseY);
        int deltaX = _lastViewportMouseInScene ? mouseX - _lastViewportMouseX : 0;
        int deltaY = _lastViewportMouseInScene ? mouseY - _lastViewportMouseY : 0;
        _lastViewportMouseX = mouseX;
        _lastViewportMouseY = mouseY;
        _lastViewportMouseInScene = inScene;

        if (!inScene) return;

        bool shift = capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftLeft] || capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftRight];
        if (capi.Input.MouseButton.Middle || (shift && capi.Input.MouseButton.Right))
        {
            _viewportPanX = Math.Clamp(_viewportPanX + deltaX, (float)-_viewportSceneBounds.InnerWidth, (float)_viewportSceneBounds.InnerWidth);
            _viewportPanY = Math.Clamp(_viewportPanY + deltaY, (float)-_viewportSceneBounds.InnerHeight, (float)_viewportSceneBounds.InnerHeight);
        }
        else if (capi.Input.MouseButton.Right)
        {
            _viewportYaw += deltaX * 0.01f;
        }

        float wheel = ImGui.GetIO().MouseWheel;
        if (Math.Abs(wheel) > 0.001f)
        {
            _viewportZoom = Math.Clamp(_viewportZoom + wheel * 0.06f, 0.55f, 1.85f);
        }
    }

    private bool WindowSizeChanged()
    {
        return Math.Abs(WindowWidth - _lastWindowWidth) > 0.5 || Math.Abs(WindowHeight - _lastWindowHeight) > 0.5;
    }

    private double WindowWidth => Math.Max(960, capi.Render.FrameWidth / RuntimeEnv.GUIScale);

    private double WindowHeight => Math.Max(540, capi.Render.FrameHeight / RuntimeEnv.GUIScale);

    private void AddTabButton(string label, DevToolsTab tab, double x, double y, double width, double height)
    {
        string display = _state.SelectedTab == tab ? $"* {label}" : label;
        SingleComposer.AddButton(display, () => SelectTab(tab), ElementBounds.Fixed(x, y, width, height), EditorTheme.ButtonFont, EnumButtonStyle.Normal, $"tab-{tab}");
    }

    private void AddPanel(ElementBounds bounds, string title, string body)
    {
        AddPanelFrame(bounds, title);
        SingleComposer.AddDynamicText(body, EditorTheme.BodyFont, TextBounds(bounds, 38, bounds.fixedHeight - 46), $"panel-body-{SanitizeKey(title)}");
    }

    private void AddPanelFrame(ElementBounds bounds, string title)
    {
        SingleComposer.AddInset(bounds, 1);
        SingleComposer.AddDynamicText(title, EditorTheme.HeaderFont, ElementBounds.Fixed(bounds.fixedX + 10, bounds.fixedY + 8, bounds.fixedWidth - 20, 24), $"panel-title-{SanitizeKey(title)}");
    }

    private ElementBounds TextBounds(ElementBounds panelBounds, double top, double height)
    {
        return ElementBounds.Fixed(panelBounds.fixedX + 10, panelBounds.fixedY + top, panelBounds.fixedWidth - 20, height);
    }

    private void AddCameraButton(string label, EditorCameraMode mode, double x, double y)
    {
        string display = _state.CameraMode == mode ? $"* {label}" : label;
        SingleComposer.AddButton(display, () => Run(state => _manager.ProperSetCameraMode(state, mode)), ElementBounds.Fixed(x, y, 78, 24), EditorTheme.ButtonFont, EnumButtonStyle.Normal, $"camera-{mode}");
    }

    private bool SelectTab(DevToolsTab tab)
    {
        _state.SelectedTab = tab;
        if (tab == DevToolsTab.Animations)
        {
            _manager.EnsureProperAnimationSelection(_state);
            _state.SetStatus("Animation tools active.");
        }
        else
        {
            _state.SetStatus($"{GetTabLabel(tab)} is not ported yet. Use ImGui fallback for full tools.");
        }
        ComposeDialog();
        return true;
    }

    private bool SwitchToImGui()
    {
        _manager.SwitchToImGuiDevTools();
        return true;
    }

    private bool Run(Func<EditorAppState, bool> action)
    {
        bool result = action(_state);
        if (IsOpened())
        {
            ComposeDialog();
        }
        return result;
    }

    private string GetBrowserText()
    {
        return _state.SelectedTab switch
        {
            DevToolsTab.Transforms => "Transform editing is still in ImGui fallback.",
            DevToolsTab.Particles => "Particle editing is still in ImGui fallback.",
            DevToolsTab.Colliders => "Collider editing is still in ImGui fallback.",
            DevToolsTab.Debug => "Runtime debug toggles are still in ImGui fallback.",
            DevToolsTab.GenericDisplay => "Generic display tools are still in ImGui fallback.",
            _ => "Browser."
        };
    }

    private string GetViewportText()
    {
        return $"Shared preview for {GetTabLabel(_state.SelectedTab)}. Full editing controls are still in ImGui fallback.";
    }

    private string GetPropertiesText()
    {
        return $"Selected tab: {GetTabLabel(_state.SelectedTab)}\nAnimation: {_state.SelectedAnimation}\nKeyframe: {_state.SelectedKeyframe}\nRig part: {_state.SelectedRigPart}\nTool: {_state.SelectedTool}\nCamera: {_state.CameraMode}\n\nUse ImGui fallback for this tab until it is ported.";
    }

    private static string GetTimelineText()
    {
        return "Timeline controls are only ported on the Animations tab in this slice.";
    }

    private static string GetTabLabel(DevToolsTab tab)
    {
        return tab switch
        {
            DevToolsTab.GenericDisplay => "Generic Display",
            _ => tab.ToString()
        };
    }

    private static string Trim(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 1)] + "...";
    }

    private static string SanitizeKey(string value)
    {
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }
        return new string(chars);
    }
}
#endif
