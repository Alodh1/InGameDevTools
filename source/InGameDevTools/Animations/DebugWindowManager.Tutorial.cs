using ImGuiNET;
using InGameDevTools.Tutorials;
using InGameDevTools.Utils;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private bool _tutorialActive;
    private DevToolsTab _tutorialEditor = DevToolsTab.Animations;
    private int _tutorialStep;
    private readonly HashSet<DevToolsTab> _tutorialHintDismissed = new();

    /// <summary>A thin, dismissable one-liner shown under the toolbar for an editor the user has not
    /// toured yet. Disappears once the tour is completed, dismissed, or the hint is turned off in Settings.</summary>
    private void DrawTutorialFirstOpenHint()
    {
        if (_tutorialActive || !_devToolsConfig.ShowTutorialHintOnFirstOpen)
        {
            return;
        }

        DevToolsTab tab = _activeDevToolsTab;
        if (GetTutorialScript(tab).Count == 0 || IsTutorialCompleted(tab) || _tutorialHintDismissed.Contains(tab))
        {
            return;
        }

        ImGui.TextColored(new NVector4(0.85f, 0.78f, 0.55f, 1f),
            DevToolsLang.Get("ui.tutorial.hint.firstOpen", "New to {0}? Take a quick guided tour.", DevToolsTabTitle(tab)));
        ImGui.SameLine();
        if (ImGui.Button(DevToolsLang.Label("ui.tutorial.hint.start", "Start tour", "devtools-tutorial-hint-start")))
        {
            StartTutorial(tab);
        }
        ImGui.SameLine();
        if (ImGui.Button(DevToolsLang.Label("ui.tutorial.hint.dismiss", "Dismiss", "devtools-tutorial-hint-dismiss")))
        {
            _tutorialHintDismissed.Add(tab);
        }
        ImGui.Separator();
    }

    /// <summary>One coachmark: the anchor whose rect to spotlight, plus localized title/body
    /// (key + English fallback, matching the rest of the UI). An empty <see cref="AnchorId"/>
    /// shows a centered step with no spotlight.</summary>
    private readonly record struct TutorialStep(
        string AnchorId,
        string TitleKey,
        string FallbackTitle,
        string BodyKey,
        string FallbackBody);

    // Order shown in the Settings "Help & tutorials" list.
    private static readonly DevToolsTab[] TutorialEditorOrder =
    [
        DevToolsTab.Animations,
        DevToolsTab.Models,
        DevToolsTab.RecipeEditor,
        DevToolsTab.Particles,
        DevToolsTab.Transforms,
        DevToolsTab.ConfigLib,
        DevToolsTab.BlockItemJson,
        DevToolsTab.LootDrops,
        DevToolsTab.Worldgen,
        DevToolsTab.Patches,
        DevToolsTab.EntityAi,
        DevToolsTab.Settings
    ];

    /// <summary>The "Help &amp; tutorials" block in the Settings tab: replay any editor's tour and
    /// reset completion. Drawn here so it can use the private <see cref="DevToolsTab"/> enum.</summary>
    private void DrawTutorialSettingsSection(ref bool changed)
    {
        NVector2 sectionTop = ImGui.GetCursorScreenPos();
        ImGui.SeparatorText(DevToolsLang.Get("ui.settings.section.tutorials", "Help & tutorials"));
        ImGui.TextWrapped(DevToolsLang.Get("ui.settings.tutorials.intro",
            "Start a guided tour for any editor. You can also press the ? button in the toolbar to tour the editor you are currently in."));
        ImGui.Spacing();

        foreach (DevToolsTab tab in TutorialEditorOrder)
        {
            if (GetTutorialScript(tab).Count == 0)
            {
                continue;
            }

            if (ImGui.Button(DevToolsLang.Label("ui.tutorial.common.start", "Start", $"devtools-tutorial-start-{tab}")))
            {
                StartTutorial(tab);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(DevToolsTabTitle(tab));
            if (IsTutorialCompleted(tab))
            {
                ImGui.SameLine();
                ImGui.TextColored(new NVector4(0.55f, 0.82f, 0.55f, 1f),
                    DevToolsLang.Get("ui.tutorial.common.done", "(completed)"));
            }
        }

        ImGui.Spacing();
        bool showHint = _devToolsConfig.ShowTutorialHintOnFirstOpen;
        if (ImGui.Checkbox(DevToolsLang.Label("ui.settings.tutorials.showHint", "Show a tutorial hint the first time an editor opens", "settings-tutorial-hint"), ref showHint))
        {
            _devToolsConfig.ShowTutorialHintOnFirstOpen = showHint;
            changed = true;
        }

        if ((_devToolsConfig.CompletedTutorials?.Count ?? 0) > 0
            && ImGui.Button(DevToolsLang.Label("ui.settings.tutorials.reset", "Reset tutorial progress", "settings-tutorial-reset")))
        {
            _devToolsConfig.CompletedTutorials?.Clear();
            changed = true;
        }

        DevToolsTutorialAnchors.MarkRect("settings.tutorials", sectionTop,
            new NVector2(sectionTop.X + ImGui.GetContentRegionAvail().X, ImGui.GetCursorScreenPos().Y));
    }

    private void StartTutorial(DevToolsTab tab)
    {
        if (GetTutorialScript(tab).Count == 0)
        {
            return;
        }

        _tutorialActive = true;
        _tutorialEditor = tab;
        _tutorialStep = 0;
        if (_activeDevToolsTab != tab)
        {
            RequestDevToolsTab(tab);
        }
    }

    private void EndTutorial(bool completed)
    {
        if (completed)
        {
            MarkTutorialCompleted(_tutorialEditor);
        }

        _tutorialActive = false;
        _tutorialStep = 0;
    }

    private void MarkTutorialCompleted(DevToolsTab tab)
    {
        _devToolsConfig.CompletedTutorials ??= new List<string>();
        string name = tab.ToString();
        if (!_devToolsConfig.CompletedTutorials.Contains(name))
        {
            _devToolsConfig.CompletedTutorials.Add(name);
            QueueDevToolsConfigSave(DevToolsLang.Get("ui.tutorial.completed", "Tutorial completed."));
        }
    }

    private bool IsTutorialCompleted(DevToolsTab tab)
    {
        return _devToolsConfig.CompletedTutorials?.Contains(tab.ToString()) == true;
    }

    /// <summary>Draws the dim spotlight overlay and the explanation popup for the active tutorial step.
    /// Called once per frame after the main DevTools window has finished drawing, so the anchor rects
    /// captured during this frame's editor draw are available.</summary>
    private void DrawTutorialOverlay(NVector2 displaySize)
    {
        IReadOnlyList<TutorialStep> steps = GetTutorialScript(_tutorialEditor);
        if (steps.Count == 0)
        {
            _tutorialActive = false;
            return;
        }

        _tutorialStep = Math.Clamp(_tutorialStep, 0, steps.Count - 1);
        TutorialStep step = steps[_tutorialStep];

        // Esc exits immediately, before drawing anything else this frame.
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            EndTutorial(false);
            return;
        }

        bool advance = false;
        bool back = false;
        if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter) || ImGui.IsKeyPressed(ImGuiKey.Space) || ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            advance = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) || ImGui.IsKeyPressed(ImGuiKey.Backspace))
        {
            back = true;
        }

        NVector2 targetMin = default;
        NVector2 targetMax = default;
        bool hasAnchor = !string.IsNullOrEmpty(step.AnchorId)
            && DevToolsTutorialAnchors.TryGet(step.AnchorId, out targetMin, out targetMax);
        if (hasAnchor)
        {
            const float pad = 4f;
            targetMin = new NVector2(targetMin.X - pad, targetMin.Y - pad);
            targetMax = new NVector2(targetMax.X + pad, targetMax.Y + pad);
        }

        // Full-screen background overlay: dims everything except the spotlight, and catches clicks so
        // they cannot fall through to the editor underneath. NoBringToFrontOnFocus keeps it below the
        // explanation box (begun afterwards) even when its invisible button is clicked.
        ImGui.SetNextWindowPos(NVector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(displaySize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavInputs | ImGuiWindowFlags.NoNavFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, NVector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##devtools-tutorial-overlay", overlayFlags))
        {
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            uint dim = ImGui.ColorConvertFloat4ToU32(new NVector4(0f, 0f, 0f, 0.62f));
            if (hasAnchor)
            {
                // Four bands around the target make the spotlight "hole".
                drawList.AddRectFilled(NVector2.Zero, new NVector2(displaySize.X, targetMin.Y), dim);
                drawList.AddRectFilled(new NVector2(0f, targetMax.Y), displaySize, dim);
                drawList.AddRectFilled(new NVector2(0f, targetMin.Y), new NVector2(targetMin.X, targetMax.Y), dim);
                drawList.AddRectFilled(new NVector2(targetMax.X, targetMin.Y), new NVector2(displaySize.X, targetMax.Y), dim);

                uint outline = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.84f, 0.4f, 1f));
                drawList.AddRect(targetMin, targetMax, outline, 4f, ImDrawFlags.None, 2.5f);
            }
            else
            {
                drawList.AddRectFilled(NVector2.Zero, displaySize, dim);
            }

            ImGui.SetCursorPos(NVector2.Zero);
            if (ImGui.InvisibleButton("##devtools-tutorial-catch", displaySize))
            {
                advance = true;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2);

        // Explanation box: begun after the overlay so it stacks on top and its buttons stay clickable.
        float boxWidth = Math.Clamp(displaySize.X * 0.28f, 280f, 420f);
        NVector2 boxPos = ComputeExplanationPosition(displaySize, hasAnchor, targetMin, targetMax, boxWidth);
        ImGui.SetNextWindowPos(boxPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVector2(boxWidth, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.97f);
        ImGuiWindowFlags boxFlags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNavInputs;
        if (ImGui.Begin("##devtools-tutorial-box", boxFlags))
        {
            ImGui.TextColored(new NVector4(0.66f, 0.76f, 0.96f, 1f),
                DevToolsLang.Get("ui.tutorial.common.step", "Step {0} / {1}", _tutorialStep + 1, steps.Count));
            ImGui.SameLine();
            ImGui.TextDisabled("·");
            ImGui.SameLine();
            ImGui.TextDisabled(DevToolsTabTitle(_tutorialEditor));
            ImGui.Separator();

            ImGui.PushTextWrapPos(boxWidth - 18f);
            ImGui.TextColored(new NVector4(1f, 0.88f, 0.55f, 1f), DevToolsLang.Get(step.TitleKey, step.FallbackTitle));
            ImGui.Spacing();
            ImGui.TextWrapped(DevToolsLang.Get(step.BodyKey, step.FallbackBody));
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            ImGui.Separator();

            bool isFirst = _tutorialStep == 0;
            bool isLast = _tutorialStep >= steps.Count - 1;
            if (isFirst)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button(DevToolsLang.Label("ui.tutorial.common.back", "Back", "devtools-tutorial-back")))
            {
                back = true;
            }
            if (isFirst)
            {
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            string nextLabel = isLast
                ? DevToolsLang.Label("ui.tutorial.common.finish", "Finish", "devtools-tutorial-next")
                : DevToolsLang.Label("ui.tutorial.common.next", "Next", "devtools-tutorial-next");
            if (ImGui.Button(nextLabel))
            {
                advance = true;
            }
            ImGui.SameLine();
            if (ImGui.Button(DevToolsLang.Label("ui.tutorial.common.exit", "Exit", "devtools-tutorial-exit")))
            {
                EndTutorial(false);
            }

            ImGui.Spacing();
            ImGui.TextDisabled(DevToolsLang.Get("ui.tutorial.common.exitHint", "Click anywhere or press Enter to continue · Esc to exit."));
        }
        ImGui.End();

        if (!_tutorialActive)
        {
            // Exit button was pressed while drawing the box.
            return;
        }

        if (back)
        {
            if (_tutorialStep > 0)
            {
                _tutorialStep--;
            }
        }
        else if (advance)
        {
            if (_tutorialStep >= steps.Count - 1)
            {
                EndTutorial(true);
            }
            else
            {
                _tutorialStep++;
            }
        }
    }

    private static NVector2 ComputeExplanationPosition(NVector2 display, bool hasAnchor, NVector2 targetMin, NVector2 targetMax, float boxWidth)
    {
        const float margin = 18f;
        const float estimatedHeight = 210f;
        if (!hasAnchor)
        {
            return new NVector2((display.X - boxWidth) * 0.5f, display.Y * 0.32f);
        }

        float x;
        if (display.X - targetMax.X - margin >= boxWidth + margin)
        {
            x = targetMax.X + margin;
        }
        else if (targetMin.X - margin >= boxWidth + margin)
        {
            x = targetMin.X - margin - boxWidth;
        }
        else
        {
            x = Math.Clamp(targetMin.X, margin, Math.Max(margin, display.X - boxWidth - margin));
        }

        float y = Math.Clamp(targetMin.Y, margin, Math.Max(margin, display.Y - estimatedHeight - margin));
        return new NVector2(x, y);
    }

    /// <summary>The ordered coachmark script for an editor. Steps may reference anchors that are not on
    /// screen this frame (e.g. needing a selection); those fall back to a centered step automatically.</summary>
    private IReadOnlyList<TutorialStep> GetTutorialScript(DevToolsTab tab)
    {
        return tab switch
        {
            DevToolsTab.Animations => AnimationsTutorialSteps,
            DevToolsTab.RecipeEditor => RecipeTutorialSteps,
            DevToolsTab.Particles => ParticlesTutorialSteps,
            DevToolsTab.Transforms => TransformsTutorialSteps,
            DevToolsTab.Models => ModelsTutorialSteps,
            DevToolsTab.ConfigLib => ConfigLibTutorialSteps,
            DevToolsTab.BlockItemJson => BlockItemJsonTutorialSteps,
            DevToolsTab.LootDrops => LootDropsTutorialSteps,
            DevToolsTab.Worldgen => WorldgenTutorialSteps,
            DevToolsTab.Patches => PatchesTutorialSteps,
            DevToolsTab.EntityAi => EntityAiTutorialSteps,
            DevToolsTab.Settings => SettingsTutorialSteps,
            _ => Array.Empty<TutorialStep>()
        };
    }

    // The first two steps of every tour explain shared navigation, then editor-specific panels follow.
    private static TutorialStep TabsStep => new(
        "global.tabs", "ui.tutorial.global.tabs.title", "Editor tabs",
        "ui.tutorial.global.tabs.body", "Each tab is a separate editor. Switch between Animations, Models, Recipes, Particles, Worldgen and more from this row.");

    private static TutorialStep ToolbarStep => new(
        "global.toolbar", "ui.tutorial.global.toolbar.title", "Shared toolbar",
        "ui.tutorial.global.toolbar.body", "Collapse the editor, change UI scale, open the command palette (Ctrl+P), and toggle runtime apply, live backups and diagnostics here. The ? button restarts this tutorial.");

    private static readonly TutorialStep[] AnimationsTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("anim.left", "ui.tutorial.anim.browser.title", "Animation browser",
            "ui.tutorial.anim.browser.body", "Pick a source (Entities, Blocks, Shapes, CO) and choose a creature or shape, then select one of its animations to load it for editing."),
        new("anim.center", "ui.tutorial.anim.viewport.title", "Preview viewport",
            "ui.tutorial.anim.viewport.body", "The live 3D preview. Drag to orbit, scroll to zoom, and watch the animation play on the model."),
        new("anim.right", "ui.tutorial.anim.inspector.title", "Element inspector",
            "ui.tutorial.anim.inspector.body", "Edit the selected element's rotation, translation and scale at the current keyframe. Changes preview instantly."),
        new("anim.timeline", "ui.tutorial.anim.timeline.title", "Timeline",
            "ui.tutorial.anim.timeline.body", "Scrub through frames and add or move keyframes. Save writes the edited animation back to the shape file."),
    ];

    private static readonly TutorialStep[] ModelsTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("models.toolbar", "ui.tutorial.models.toolbar.title", "Model toolbar",
            "ui.tutorial.models.toolbar.body", "Create a new shape, open existing ones, undo/redo, and view the full list of keyboard and mouse shortcuts."),
        new("models.left", "ui.tutorial.models.tree.title", "Shape tree",
            "ui.tutorial.models.tree.body", "The element hierarchy of the shape. Select a cube to edit it; right-click for add/remove/duplicate actions."),
        new("models.center", "ui.tutorial.models.viewport.title", "3D viewport",
            "ui.tutorial.models.viewport.body", "Orbit, pan and zoom around the model. Selected elements are highlighted and can be transformed with the gizmo."),
        new("models.right", "ui.tutorial.models.inspector.title", "Element inspector",
            "ui.tutorial.models.inspector.body", "Edit the selected element's size, position, rotation, origin and per-face UVs. The UV panel maps faces onto the texture."),
    ];

    private static readonly TutorialStep[] RecipeTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("recipe.left", "ui.tutorial.recipe.browser.title", "Recipe browser",
            "ui.tutorial.recipe.browser.body", "Browse and filter existing recipes by type, or start a new one. Select a recipe to load it into the canvas."),
        new("recipe.center", "ui.tutorial.recipe.canvas.title", "Recipe canvas",
            "ui.tutorial.recipe.canvas.body", "Lay out ingredients and the output here. Grid-shaped recipes show their pattern; shapeless ones list their inputs."),
        new("recipe.right", "ui.tutorial.recipe.inspector.title", "Recipe inspector",
            "ui.tutorial.recipe.inspector.body", "Edit ingredient codes, quantities, attributes and output, then save the recipe back to its JSON file."),
    ];

    private static readonly TutorialStep[] ParticlesTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("particles.left", "ui.tutorial.particles.list.title", "Effect list",
            "ui.tutorial.particles.list.body", "Your particle effects live here. Add a new one or select an existing effect to edit and preview it."),
        new("particles.center", "ui.tutorial.particles.preview.title", "Preview",
            "ui.tutorial.particles.preview.body", "A live simulation of the selected effect. Use play/pause to watch emission, motion and fade over time."),
        new("particles.right", "ui.tutorial.particles.props.title", "Properties",
            "ui.tutorial.particles.props.body", "Tune quantity, size, velocity, colour, gravity and lifetime. The preview updates as you edit."),
    ];

    private static readonly TutorialStep[] TransformsTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("transforms.left", "ui.tutorial.transforms.list.title", "Transform targets",
            "ui.tutorial.transforms.list.body", "Choose the item or block, and which transform to edit: ground, hands, GUI, fixed, wear or display-case."),
        new("transforms.center", "ui.tutorial.transforms.viewport.title", "Preview viewport",
            "ui.tutorial.transforms.viewport.body", "Shows the model in the selected transform context so you can judge placement, scale and rotation at a glance."),
        new("transforms.right", "ui.tutorial.transforms.fields.title", "Transform fields",
            "ui.tutorial.transforms.fields.body", "Edit translation, rotation, origin and scale. Apply writes the values back to the item or block JSON."),
    ];

    private static readonly TutorialStep[] ConfigLibTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("configlib.left", "ui.tutorial.configlib.browser.title", "Settings browser",
            "ui.tutorial.configlib.browser.body", "Browse the config settings your mod exposes through ConfigLib, organised into groups."),
        new("configlib.center", "ui.tutorial.configlib.document.title", "Settings editor",
            "ui.tutorial.configlib.document.body", "Define each setting's name, type and default value here."),
        new("configlib.right", "ui.tutorial.configlib.output.title", "Generated output",
            "ui.tutorial.configlib.output.body", "Preview and export the generated ConfigLib JSON, ready to drop into your mod."),
    ];

    private static readonly TutorialStep[] BlockItemJsonTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("blockjson.left", "ui.tutorial.blockjson.browser.title", "Block/Item browser",
            "ui.tutorial.blockjson.browser.body", "Find a block or item by code. Select it to load its definition for editing."),
        new("blockjson.center", "ui.tutorial.blockjson.editor.title", "JSON editor",
            "ui.tutorial.blockjson.editor.body", "Edit the block or item JSON directly with validation as you type."),
        new("blockjson.right", "ui.tutorial.blockjson.inspector.title", "Inspector & apply",
            "ui.tutorial.blockjson.inspector.body", "Review the parsed definition, then apply or save your changes."),
    ];

    private static readonly TutorialStep[] LootDropsTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("loot.left", "ui.tutorial.loot.list.title", "Drop sources",
            "ui.tutorial.loot.list.body", "Pick a block or entity whose drops you want to edit."),
        new("loot.center", "ui.tutorial.loot.table.title", "Drop table",
            "ui.tutorial.loot.table.body", "Edit each drop's item, quantity range and chance."),
        new("loot.right", "ui.tutorial.loot.inspector.title", "Inspector & save",
            "ui.tutorial.loot.inspector.body", "Fine-tune the selected drop and save the loot table back to its JSON."),
    ];

    private static readonly TutorialStep[] WorldgenTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("worldgen.left", "ui.tutorial.worldgen.list.title", "Worldgen features",
            "ui.tutorial.worldgen.list.body", "Browse worldgen patches and features such as deposits, trees and structures."),
        new("worldgen.center", "ui.tutorial.worldgen.preview.title", "Preview",
            "ui.tutorial.worldgen.preview.body", "Preview how the selected feature is placed in the world."),
        new("worldgen.right", "ui.tutorial.worldgen.params.title", "Parameters",
            "ui.tutorial.worldgen.params.body", "Adjust placement, frequency and conditions, then apply the changes."),
    ];

    private static readonly TutorialStep[] PatchesTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("patches.left", "ui.tutorial.patches.browser.title", "Asset browser",
            "ui.tutorial.patches.browser.body", "Find the asset you want to patch."),
        new("patches.center", "ui.tutorial.patches.path.title", "JSON path",
            "ui.tutorial.patches.path.body", "Pick the exact JSON path and the operation (add, replace, remove) to perform."),
        new("patches.right", "ui.tutorial.patches.output.title", "Patch output",
            "ui.tutorial.patches.output.body", "The editor builds a valid JSON patch you can copy into your mod's patches folder."),
    ];

    private static readonly TutorialStep[] EntityAiTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("ai.left", "ui.tutorial.ai.list.title", "Entities & behaviors",
            "ui.tutorial.ai.list.body", "Pick an entity and inspect its AI tasks such as wander, seek-food and flee."),
        new("ai.center", "ui.tutorial.ai.editor.title", "Task editor",
            "ui.tutorial.ai.editor.body", "Edit the selected task's parameters here."),
        new("ai.right", "ui.tutorial.ai.inspector.title", "Inspector & apply",
            "ui.tutorial.ai.inspector.body", "Use runtime apply to see behaviour changes on the live entity instantly."),
    ];

    private static readonly TutorialStep[] SettingsTutorialSteps =
    [
        TabsStep,
        ToolbarStep,
        new("settings.general", "ui.tutorial.settings.general.title", "General settings",
            "ui.tutorial.settings.general.body", "Language, open-on-startup, UI scale, runtime apply and backup behaviour live here."),
        new("settings.tutorials", "ui.tutorial.settings.tutorials.title", "Help & tutorials",
            "ui.tutorial.settings.tutorials.body", "Replay any editor's tutorial from here, or reset which tutorials you've completed."),
    ];
}
