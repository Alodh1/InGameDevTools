using System.Diagnostics;
using System.Text;
using InGameDevTools.Utils;
using InGameDevTools.Integration.Transpilers;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    private int _vanillaIkConfigRevision;
    private bool _vanillaIkChainCacheResult;
    private VanillaIkManualChain _vanillaIkChainCacheChain;
    private string _vanillaIkChainCacheError = "";
    private string _vanillaIkChainCacheWarning = "";
    private VanillaAnimationDocument? _vanillaIkChainCacheDocument;
    private VanillaAnimation? _vanillaIkChainCacheAnimation;
    private AnimationKeyFrame? _vanillaIkChainCacheKeyFrame;
    private string _vanillaIkChainCacheElementName = "";
    private VanillaIkChainMode _vanillaIkChainCacheMode;
    private int _vanillaIkChainCacheEditVersion = -1;
    private int _vanillaIkChainCacheConfigRevision = -1;

    private void InvalidateVanillaIkChainCache()
    {
        _vanillaIkConfigRevision++;
    }

    private bool ContainsVanillaIkChainElement(string elementName)
    {
        return _vanillaIkChainElementNames.Any(name => string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleVanillaIkChainElement(string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return;

        int existingIndex = _vanillaIkChainElementNames.FindIndex(name => string.Equals(name, elementName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _vanillaIkChainElementNames.RemoveAt(existingIndex);
            _vanillaStatus = $"Removed {elementName} from the IK chain.";
        }
        else
        {
            _vanillaIkChainElementNames.Add(elementName);
            _vanillaStatus = $"Added {elementName} to the IK chain.";
        }

        InvalidateVanillaIkChainCache();
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
    }

    private void DrawVanillaSymmetryControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, string selectedElementName, AnimationKeyFrameElement selectedElement)
    {
        VanillaAnimationDocument document = entry.Document;
        VanillaAnimation animation = entry.Animation;
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        if (allElements.Length <= 1)
        {
            ImGui.SeparatorText("Symmetry");
            ImGui.TextDisabled("No other elements available for symmetry.");
            return;
        }

        ImGui.SeparatorText("Symmetry");
        DrawVanillaLiveSymmetryControls(row, animation);
        DrawVanillaSymmetryPairSelector(document, selectedElementName, allElements);

        bool hasPair = TryResolveVanillaSymmetryPair(document, selectedElementName, allElements, out string pairElementName, out VanillaSymmetrySide sourceSide, out bool manualPair);
        if (hasPair)
        {
            ImGui.TextDisabled(manualPair
                ? $"Pair: {pairElementName} (manual)"
                : $"Pair: {pairElementName} (auto)");
        }
        else
        {
            ImGui.TextDisabled("Pair: none");
        }

        if (sourceSide == VanillaSymmetrySide.Unknown)
        {
            ImGui.TextDisabled("Source side: unknown; all-pair actions need a left/right-style element name.");
        }
        else
        {
            ImGui.TextDisabled($"Source side: {sourceSide}");
        }

        bool hasAnimationLength = animation.QuantityFrames > 0;
        bool pairInCurrentKeyframe = hasPair && keyFrame.Elements != null && keyFrame.Elements.ContainsKey(pairElementName);

        bool canMirrorSelected = hasPair;
        if (!canMirrorSelected) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror selected -> pair##vanilla-symmetry-selected-to-pair"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaElementInKeyFrame(keyFrame, selectedElementName, pairElementName, selectedElement));
        }
        if (!canMirrorSelected) ImGui.EndDisabled();

        ImGui.SameLine();
        bool canMirrorPair = hasPair && pairInCurrentKeyframe;
        if (!canMirrorPair) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror pair -> selected##vanilla-symmetry-pair-to-selected"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaPairToSelected(keyFrame, selectedElementName, pairElementName));
        }
        if (!canMirrorPair) ImGui.EndDisabled();

        bool canMirrorAll = sourceSide != VanillaSymmetrySide.Unknown;
        if (!canMirrorAll) ImGui.BeginDisabled();
        if (ImGui.Button("Mirror all source-side pairs in keyframe##vanilla-symmetry-all-keyframe"))
        {
            ApplyVanillaSymmetryAction(row, document, () => MirrorVanillaSourceSidePairsInKeyFrame(document, animation, keyFrame, sourceSide));
        }
        if (!canMirrorAll) ImGui.EndDisabled();

        bool canBakeSelected = hasPair && hasAnimationLength;
        if (!canBakeSelected) ImGui.BeginDisabled();
        if (ImGui.Button("Bake half-cycle selected -> pair##vanilla-symmetry-bake-selected"))
        {
            ApplyVanillaSymmetryAction(row, document, () => BakeVanillaHalfCycleSymmetry(document, animation, selectedElementName, pairElementName));
        }
        if (!canBakeSelected) ImGui.EndDisabled();

        ImGui.SameLine();
        bool canBakeAll = sourceSide != VanillaSymmetrySide.Unknown && hasAnimationLength;
        if (!canBakeAll) ImGui.BeginDisabled();
        if (ImGui.Button("Bake half-cycle all source-side pairs##vanilla-symmetry-bake-all"))
        {
            ApplyVanillaSymmetryAction(row, document, () => BakeVanillaHalfCycleSymmetryForSide(document, animation, sourceSide));
        }
        if (!canBakeAll) ImGui.EndDisabled();

        bool hasKeyFrameElements = keyFrame.Elements is { Count: > 0 };
        if (!hasKeyFrameElements) ImGui.BeginDisabled();
        if (ImGui.Button("Flip pose##vanilla-symmetry-flip-pose"))
        {
            ApplyVanillaSymmetryAction(row, document, () => FlipVanillaPoseInKeyFrame(document, animation, keyFrame));
        }
        if (!hasKeyFrameElements) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Mirrors the entire keyframe: swaps left/right pairs and mirrors unpaired center elements in place. Pairs are found by name, manual override, or mirrored pivot position.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Create mirrored animation copy##vanilla-symmetry-mirror-copy"))
        {
            CreateMirroredVanillaAnimationCopy(entry);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Duplicates this animation with every keyframe pose flipped left<->right. Useful for creating strafe-left from strafe-right and similar mirrored motions.");
        }

        if (!hasPair)
        {
            ImGui.TextDisabled("Select or auto-detect an opposite element before mirroring.");
        }
        else if (!pairInCurrentKeyframe)
        {
            ImGui.TextDisabled($"{pairElementName} is not present in this keyframe; selected-to-pair can create it.");
        }

        if (!hasAnimationLength)
        {
            ImGui.TextDisabled("Half-cycle bake needs a positive animation frame count.");
        }
    }

    private void DrawVanillaLiveSymmetryControls(VanillaBrowserRow row, VanillaAnimation animation)
    {
        bool enabled = _vanillaLiveSymmetryEnabled;
        if (ImGui.Checkbox("Live symmetry##vanilla-live-symmetry", ref enabled))
        {
            _vanillaLiveSymmetryEnabled = enabled;
            if (_vanillaLiveSymmetryEnabled)
            {
                PauseVanillaLiveSymmetryPreview(row, animation);
            }

            _vanillaStatus = _vanillaLiveSymmetryEnabled
                ? "Live symmetry enabled. Edits to driver elements mirror to their pair."
                : "Live symmetry disabled.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Writes mirrored pair keyframes as you edit the selected element.");
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("In-place##vanilla-live-symmetry-mode", _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace))
        {
            _vanillaLiveSymmetryMode = VanillaLiveSymmetryMode.InPlace;
            _vanillaStatus = "Live symmetry mode: in-place mirror.";
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Half-cycle gait##vanilla-live-symmetry-mode", _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.HalfCycle))
        {
            _vanillaLiveSymmetryMode = VanillaLiveSymmetryMode.HalfCycle;
            PauseVanillaLiveSymmetryPreview(row, animation);
            _vanillaStatus = "Live symmetry mode: half-cycle gait.";
        }

        string[] driverOptions = ["Selected drives pair", "Left drives right", "Right drives left"];
        int driverIndex = (int)_vanillaLiveSymmetryDriver;
        ImGui.SetNextItemWidth(190);
        if (ImGui.Combo("Driver##vanilla-live-symmetry-driver", ref driverIndex, driverOptions, driverOptions.Length))
        {
            _vanillaLiveSymmetryDriver = (VanillaLiveSymmetryDriver)Math.Clamp(driverIndex, 0, driverOptions.Length - 1);
            _vanillaStatus = $"Live symmetry driver: {driverOptions[(int)_vanillaLiveSymmetryDriver]}.";
        }

        if (_vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.HalfCycle)
        {
            int maxPhase = Math.Max(0, Math.Max(1, animation.QuantityFrames) - 1);
            int phase = _vanillaLiveSymmetryPhaseFrames >= 0
                ? Math.Clamp(_vanillaLiveSymmetryPhaseFrames, 0, maxPhase)
                : Math.Clamp(GetVanillaHalfCycleFrames(animation), 0, maxPhase);

            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Phase frames##vanilla-live-symmetry-phase", ref phase))
            {
                _vanillaLiveSymmetryPhaseFrames = Math.Clamp(phase, 0, maxPhase);
                _vanillaStatus = $"Live symmetry phase: {_vanillaLiveSymmetryPhaseFrames} frame(s).";
            }

            ImGui.SameLine();
            if (ImGui.Button("Half cycle##vanilla-live-symmetry-phase-reset"))
            {
                _vanillaLiveSymmetryPhaseFrames = -1;
                _vanillaStatus = $"Live symmetry phase: half cycle ({GetVanillaLiveSymmetryPhaseFrames(animation)} frame(s)).";
            }

            int activePhase = GetVanillaLiveSymmetryPhaseFrames(animation);
            ImGui.TextDisabled(_vanillaLiveSymmetryPhaseFrames < 0
                ? $"Using half-cycle phase: {activePhase} frame(s)."
                : $"Using custom phase: {activePhase} frame(s).");
            if (_vanillaLiveSymmetryEnabled)
            {
                ImGui.TextDisabled("Half-cycle mode writes the pair at the shifted frame; the ghost shows that pose while paused.");
            }
        }

        if (_vanillaLiveSymmetryEnabled)
        {
            bool showGhost = _vanillaShowLiveSymmetryGhost;
            if (ImGui.Checkbox("Show symmetry ghost##vanilla-live-symmetry-ghost", ref showGhost))
            {
                _vanillaShowLiveSymmetryGhost = showGhost;
                _vanillaStatus = _vanillaShowLiveSymmetryGhost
                    ? "Live symmetry ghost enabled."
                    : "Live symmetry ghost hidden.";
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Shows a translucent phase-shifted preview of the mirrored pose without playing the animation.");
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("Ghost opacity##vanilla-live-symmetry-ghost-opacity", ref _vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f, "%.2f"))
            {
                _vanillaLiveSymmetryGhostOpacity = Math.Clamp(_vanillaLiveSymmetryGhostOpacity, 0.05f, 0.8f);
            }
        }
    }

    private void DrawVanillaIkControls(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, string selectedElementName)
    {
        VanillaAnimationDocument document = entry.Document;
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, entry.Animation, keyFrame);

        ImGui.SeparatorText("IK");
        if (allElements.Length < 1)
        {
            ImGui.TextDisabled("IK needs at least one shape element.");
            return;
        }

        PruneVanillaIkChainElements(allElements);

        if (ImGui.RadioButton("Auto chain##vanilla-ik-mode", _vanillaIkMode != VanillaIkChainMode.ManualOverride))
        {
            _vanillaIkMode = _vanillaIkMode == VanillaIkChainMode.AutoExtended
                ? VanillaIkChainMode.AutoExtended
                : VanillaIkChainMode.AutoConservative;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings("IK mode: auto chain. Select an element; the chain is detected from the shape hierarchy and anchors.");
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Manual override##vanilla-ik-mode", _vanillaIkMode == VanillaIkChainMode.ManualOverride))
        {
            _vanillaIkMode = VanillaIkChainMode.ManualOverride;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings("IK mode: manual override. Click body parts or Ctrl+Click elements to edit the chain.");
        }

        if (_vanillaIkMode != VanillaIkChainMode.ManualOverride)
        {
            int profile = _vanillaIkMode == VanillaIkChainMode.AutoExtended ? 1 : 0;
            string[] profiles = ["Conservative", "Extended"];
            ImGui.SetNextItemWidth(180f);
            if (ImGui.Combo("Auto profile##vanilla-ik-profile", ref profile, profiles, profiles.Length))
            {
                _vanillaIkMode = profile == 1 ? VanillaIkChainMode.AutoExtended : VanillaIkChainMode.AutoConservative;
                _vanillaIkHasTarget = false;
                ClearVanillaViewportGizmoDrag();
                SaveVanillaIkSettings(_vanillaIkMode == VanillaIkChainMode.AutoExtended
                    ? "Auto IK profile: extended topology."
                    : "Auto IK profile: conservative topology.");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Conservative favors short chains that stop at anchors and body hubs. Extended allows longer linear appendages such as tails, necks, wings, and tentacles.");
            }
        }

        string[] solverOptions = ["CCD (classic)", "FABRIK (smooth)"];
        int solverIndex = _vanillaIkSolver == VanillaIkSolverKind.Fabrik ? 1 : 0;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Solver##vanilla-ik-solver", ref solverIndex, solverOptions, solverOptions.Length))
        {
            _vanillaIkSolver = solverIndex == 1 ? VanillaIkSolverKind.Fabrik : VanillaIkSolverKind.Ccd;
            SaveVanillaIkSettings(_vanillaIkSolver == VanillaIkSolverKind.Fabrik
                ? "IK solver: FABRIK. Distributes bending smoothly along the chain and supports swivel control."
                : "IK solver: CCD. Classic per-bone solving that favors rotating bones near the end effector.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("CCD rotates bones one at a time from the effector toward the root. FABRIK solves joint positions in both directions, giving smoother, more even bends, plus a swivel (pole) control.");
        }

        if (_vanillaIkSolver == VanillaIkSolverKind.Fabrik)
        {
            ImGui.SetNextItemWidth(180f);
            float swivel = _vanillaIkSwivelDegrees;
            if (ImGui.SliderFloat("Swivel##vanilla-ik-swivel", ref swivel, -180f, 180f, "%.0f deg"))
            {
                _vanillaIkSwivelDegrees = Math.Clamp(swivel, -180f, 180f);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Pole control: rotates the solved chain's middle joints around the root-to-effector axis. Use it to aim elbows and knees. Applied on the next solve or IK Move drag.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("0##vanilla-ik-swivel-reset"))
            {
                _vanillaIkSwivelDegrees = 0f;
            }
        }

        bool hasChain = TryGetActiveVanillaIkChain(document, entry.Animation, keyFrame, selectedElementName, out VanillaIkManualChain chain, out string chainError, out string chainWarning);

        if (ImGui.Checkbox("IK on Move##vanilla-ik-follow-move", ref _vanillaIkFollowMove))
        {
            _vanillaStatus = _vanillaIkFollowMove
                ? "IK Move enabled. Drag the Move gizmo on the active IK chain end."
                : "IK Move disabled.";
        }

        if (_vanillaIkFollowMove)
        {
            bool lockToDragAxis = _vanillaIkLockMoveToDragAxis;
            if (ImGui.Checkbox("Lock IK to drag axis##vanilla-ik-lock-drag-axis", ref lockToDragAxis))
            {
                _vanillaIkLockMoveToDragAxis = lockToDragAxis;
                SaveVanillaIkSettings(_vanillaIkLockMoveToDragAxis
                    ? "IK Move target locked to the active Move gizmo axis."
                    : "IK Move target is free.");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("When enabled, dragging a Move gizmo axis constrains the IK target to that same world/local axis instead of rebuilding the target from all offset channels.");
            }
        }

        bool preserveHandle = _vanillaIkPreserveDraggedPartRotation;
        if (ImGui.Checkbox("Preserve dragged part rotation##vanilla-ik-preserve-handle", ref preserveHandle))
        {
            _vanillaIkPreserveDraggedPartRotation = preserveHandle;
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            SaveVanillaIkSettings(_vanillaIkPreserveDraggedPartRotation
                ? "IK handle rotation lock enabled."
                : "IK handle rotation lock disabled.");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("On keeps the dragged body part's world orientation locked and solves the parent chain around it. Off allows the handle itself to rotate.");
        }

        if (_vanillaIkMode == VanillaIkChainMode.ManualOverride && ImGui.Button("Clear IK chain##vanilla-ik-clear"))
        {
            _vanillaIkChainElementNames.Clear();
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
            _vanillaStatus = "IK chain cleared.";
        }

        DrawVanillaIkAnchorControls(document, selectedElementName, allElements);

        if (hasChain)
        {
            ImGui.TextDisabled(_vanillaIkMode != VanillaIkChainMode.ManualOverride
                ? $"Auto chain: {chain.DisplayName} -> distal end of {chain.EndElementName}"
                : $"Manual chain: {chain.DisplayName} -> distal end of {chain.EndElementName}");
            if (!string.IsNullOrWhiteSpace(chainWarning))
            {
                ImGui.TextColored(new NVector4(1f, 0.72f, 0.32f, 1f), chainWarning);
            }

            if (!string.Equals(selectedElementName, chain.EndElementName, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextDisabled($"IK Move handle: {chain.EndElementName}.");
            }
            ImGui.TextDisabled($"End effector: distal end of {chain.EndElementName}.");
            if (_vanillaIkMode != VanillaIkChainMode.ManualOverride)
            {
                DrawVanillaAutoIkChainAdjusters(selectedElementName);
            }
        }
        else
        {
            ImGui.TextDisabled(chainError);
        }

        if (!hasChain) ImGui.BeginDisabled();
        if (ImGui.Button("Target = current end##vanilla-ik-current-target"))
        {
            SetVanillaIkTargetFromCurrentEnd(row, entry, keyFrame, chain);
        }
        if (!hasChain) ImGui.EndDisabled();

        NVector3 target = new(_vanillaIkTargetX, _vanillaIkTargetY, _vanillaIkTargetZ);
        ImGui.SetNextItemWidth(220);
        if (ImGui.DragFloat3("Target##vanilla-ik-target", ref target, 0.01f))
        {
            _vanillaIkTargetX = target.X;
            _vanillaIkTargetY = target.Y;
            _vanillaIkTargetZ = target.Z;
            _vanillaIkHasTarget = true;
        }

        bool canSolve = hasChain && _vanillaIkHasTarget;
        if (!canSolve) ImGui.BeginDisabled();
        if (ImGui.Button("Solve IK to target##vanilla-ik-solve"))
        {
            ApplyVanillaIkTarget(row, entry, keyFrame, chain);
        }
        if (!canSolve) ImGui.EndDisabled();

        if (!_vanillaIkHasTarget)
        {
            ImGui.TextDisabled("Set a target from the current end or edit target coordinates.");
        }

        ImGui.TextDisabled(_vanillaIkMode != VanillaIkChainMode.ManualOverride
            ? "Auto IK uses topology, anchors, and body hubs to choose the driver chain."
            : "Manual override edits the exact chain from viewport clicks or Ctrl+Click rows.");
    }

    private void DrawVanillaAutoIkChainAdjusters(string selectedElementName)
    {
        NormalizeVanillaIkAutoAdjustmentSelection(selectedElementName);

        ImGui.TextDisabled("Auto chain length:");
        ImGui.SameLine();
        if (ImGui.SmallButton("- root##vanilla-ik-auto-root-minus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoRootExtraBones, -1, "root");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ root##vanilla-ik-auto-root-plus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoRootExtraBones, 1, "root");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("- end##vanilla-ik-auto-end-minus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoEndExtraBones, -1, "end");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ end##vanilla-ik-auto-end-plus"))
        {
            AdjustVanillaAutoIkChainLength(ref _vanillaIkAutoEndExtraBones, 1, "end");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Extends the auto end effector farther down the selected element's child path.");
        }
    }

    private void AdjustVanillaAutoIkChainLength(ref int value, int delta, string side)
    {
        int next = Math.Clamp(value + delta, 0, VanillaIkAutoMaxAdjustmentBones);
        if (next == value) return;

        value = next;
        InvalidateVanillaIkChainCache();
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        _vanillaStatus = side == "root"
            ? $"Auto IK root expansion: {value} extra bone(s)."
            : $"Auto IK end extension: {value} extra bone(s).";
    }

    private void DrawVanillaIkAnchorControls(VanillaAnimationDocument document, string selectedElementName, string[] allElements)
    {
        PruneVanillaIkAnchors(document, allElements);

        bool hasSelection = !string.IsNullOrWhiteSpace(selectedElementName);
        bool selectedPinned = hasSelection && ContainsVanillaIkAnchor(document, selectedElementName);
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.SmallButton(selectedPinned ? "Unpin IK anchor##vanilla-ik-anchor-toggle" : "Pin IK anchor##vanilla-ik-anchor-toggle"))
        {
            ToggleVanillaIkAnchor(document, selectedElementName);
        }
        if (!hasSelection) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Pinned body parts stop automatic IK chains and are never rotated by auto IK. Manual override can still include them explicitly.");
        }

        string[] anchors = GetVanillaIkAnchors(document);
        if (anchors.Length > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear anchors##vanilla-ik-anchor-clear"))
            {
                ClearVanillaIkAnchors(document);
            }

            ImGui.TextDisabled($"Anchors: {string.Join(", ", anchors)}");
        }
    }

    private void SaveVanillaIkSettings(string status)
    {
        _devToolsConfig.AnimationIkMode = FormatVanillaIkChainMode(_vanillaIkMode);
        _devToolsConfig.AnimationIkSolver = FormatVanillaIkSolverKind(_vanillaIkSolver);
        _devToolsConfig.AnimationIkPreserveDraggedPartRotation = _vanillaIkPreserveDraggedPartRotation;
        _devToolsConfig.AnimationIkLockMoveToDragAxis = _vanillaIkLockMoveToDragAxis;
        _vanillaStatus = status;
        QueueDevToolsConfigSave(status);
    }

    private static VanillaIkChainMode ParseVanillaIkChainMode(string? value)
    {
        if (string.Equals(value, "AutoExtended", StringComparison.OrdinalIgnoreCase)) return VanillaIkChainMode.AutoExtended;
        if (string.Equals(value, "ManualOverride", StringComparison.OrdinalIgnoreCase)) return VanillaIkChainMode.ManualOverride;
        return VanillaIkChainMode.AutoConservative;
    }

    private static VanillaIkSolverKind ParseVanillaIkSolverKind(string? value)
    {
        return string.Equals(value, "Fabrik", StringComparison.OrdinalIgnoreCase) ? VanillaIkSolverKind.Fabrik : VanillaIkSolverKind.Ccd;
    }

    private static string FormatVanillaIkSolverKind(VanillaIkSolverKind solver)
    {
        return solver == VanillaIkSolverKind.Fabrik ? "Fabrik" : "Ccd";
    }

    private static string FormatVanillaIkChainMode(VanillaIkChainMode mode)
    {
        return mode switch
        {
            VanillaIkChainMode.AutoExtended => "AutoExtended",
            VanillaIkChainMode.ManualOverride => "ManualOverride",
            _ => "AutoConservative"
        };
    }

    private static string GetVanillaIkAnchorKey(VanillaAnimationDocument document)
    {
        return document.HistoryKey;
    }

    private string[] GetVanillaIkAnchors(VanillaAnimationDocument document)
    {
        string key = GetVanillaIkAnchorKey(document);
        if (!_devToolsConfig.AnimationIkAnchors.TryGetValue(key, out string[]? anchors) || anchors == null) return [];

        return anchors
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool ContainsVanillaIkAnchor(VanillaAnimationDocument document, string elementName)
    {
        return GetVanillaIkAnchors(document).Any(anchor => string.Equals(anchor, elementName, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleVanillaIkAnchor(VanillaAnimationDocument document, string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return;

        string key = GetVanillaIkAnchorKey(document);
        List<string> anchors = GetVanillaIkAnchors(document).ToList();
        int index = anchors.FindIndex(anchor => string.Equals(anchor, elementName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            anchors.RemoveAt(index);
            _vanillaStatus = $"IK anchor removed: {elementName}.";
        }
        else
        {
            anchors.Add(elementName.Trim());
            _vanillaStatus = $"IK anchor pinned: {elementName}.";
        }

        if (anchors.Count == 0)
        {
            _devToolsConfig.AnimationIkAnchors.Remove(key);
        }
        else
        {
            _devToolsConfig.AnimationIkAnchors[key] = anchors
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        InvalidateVanillaIkChainCache();
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        SaveVanillaIkSettings(_vanillaStatus);
    }

    private void ClearVanillaIkAnchors(VanillaAnimationDocument document)
    {
        _devToolsConfig.AnimationIkAnchors.Remove(GetVanillaIkAnchorKey(document));
        InvalidateVanillaIkChainCache();
        _vanillaIkHasTarget = false;
        ClearVanillaViewportGizmoDrag();
        SaveVanillaIkSettings("IK anchors cleared.");
    }

    private void PruneVanillaIkAnchors(VanillaAnimationDocument document, string[] allElements)
    {
        string key = GetVanillaIkAnchorKey(document);
        if (!_devToolsConfig.AnimationIkAnchors.TryGetValue(key, out string[]? anchors) || anchors == null) return;

        string[] pruned = anchors
            .Where(anchor => ContainsElementName(allElements, anchor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pruned.Length == anchors.Length && pruned.SequenceEqual(anchors, StringComparer.OrdinalIgnoreCase)) return;

        if (pruned.Length == 0) _devToolsConfig.AnimationIkAnchors.Remove(key);
        else _devToolsConfig.AnimationIkAnchors[key] = pruned;
        InvalidateVanillaIkChainCache();
        QueueDevToolsConfigSave("IK anchors pruned.");
    }

    private void PruneVanillaIkChainElements(string[] allElements)
    {
        for (int index = _vanillaIkChainElementNames.Count - 1; index >= 0; index--)
        {
            if (ContainsElementName(allElements, _vanillaIkChainElementNames[index])) continue;

            _vanillaIkChainElementNames.RemoveAt(index);
            InvalidateVanillaIkChainCache();
            _vanillaIkHasTarget = false;
            ClearVanillaViewportGizmoDrag();
        }
    }

    private bool TryGetActiveVanillaIkChain(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        string selectedElementName,
        out VanillaIkManualChain chain,
        out string error,
        out string warning)
    {
        if (ReferenceEquals(document, _vanillaIkChainCacheDocument) &&
            ReferenceEquals(animation, _vanillaIkChainCacheAnimation) &&
            ReferenceEquals(keyFrame, _vanillaIkChainCacheKeyFrame) &&
            string.Equals(selectedElementName, _vanillaIkChainCacheElementName, StringComparison.OrdinalIgnoreCase) &&
            _vanillaIkMode == _vanillaIkChainCacheMode &&
            document.EditVersion == _vanillaIkChainCacheEditVersion &&
            _vanillaIkConfigRevision == _vanillaIkChainCacheConfigRevision)
        {
            chain = _vanillaIkChainCacheChain;
            error = _vanillaIkChainCacheError;
            warning = _vanillaIkChainCacheWarning;
            return _vanillaIkChainCacheResult;
        }

        bool result = _vanillaIkMode == VanillaIkChainMode.ManualOverride
            ? TryGetManualVanillaIkChain(document.Shape, out chain, out error, out warning)
            : TryGetAutoVanillaIkChain(document, animation, keyFrame, selectedElementName, out chain, out error, out warning);

        _vanillaIkChainCacheDocument = document;
        _vanillaIkChainCacheAnimation = animation;
        _vanillaIkChainCacheKeyFrame = keyFrame;
        _vanillaIkChainCacheElementName = selectedElementName ?? "";
        _vanillaIkChainCacheMode = _vanillaIkMode;
        _vanillaIkChainCacheEditVersion = document.EditVersion;
        _vanillaIkChainCacheConfigRevision = _vanillaIkConfigRevision;
        _vanillaIkChainCacheResult = result;
        _vanillaIkChainCacheChain = chain;
        _vanillaIkChainCacheError = error;
        _vanillaIkChainCacheWarning = warning;
        return result;
    }

    private bool TryGetAutoVanillaIkChain(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        string selectedElementName,
        out VanillaIkManualChain chain,
        out string error,
        out string warning)
    {
        chain = default;
        error = "";
        warning = "";

        if (string.IsNullOrWhiteSpace(selectedElementName))
        {
            error = "Select an element for auto IK.";
            return false;
        }

        NormalizeVanillaIkAutoAdjustmentSelection(selectedElementName);

        Shape? shape = document.Shape;
        if (shape?.Elements == null || shape.Elements.Length == 0)
        {
            error = "IK needs a loaded shape hierarchy.";
            return false;
        }

        if (!TryFindShapeElementPath(shape, selectedElementName, out List<ShapeElement> path) || path.Count == 0)
        {
            error = $"IK element {selectedElementName} was not found in the shape hierarchy.";
            return false;
        }

        ShapeElement selected = path[^1];
        string resolvedSelectedName = string.IsNullOrWhiteSpace(selected.Name) ? selectedElementName.Trim() : selected.Name!;
        int selectedIndex = path.Count - 1;

        if (ContainsVanillaIkAnchor(document, resolvedSelectedName))
        {
            error = $"{resolvedSelectedName} is pinned as an IK anchor. Select a downstream handle, unpin it, or use Manual override.";
            return false;
        }

        bool selectedIsStructuralHub = IsVanillaIkStructuralHub(selected, selectedIndex == 0, out string selectedHubReason);
        bool selectedIsTrunkOrRoot = selectedIndex == 0 || IsVanillaIkTrunkName(NormalizeVanillaIkStructureName(selected.Name));
        if (selectedIsStructuralHub && selectedIsTrunkOrRoot)
        {
            if (_vanillaIkMode != VanillaIkChainMode.AutoExtended)
            {
                error = $"{resolvedSelectedName} is {selectedHubReason}. Select an appendage, switch to Extended, or use Manual override.";
                return false;
            }

            if (TryGetVanillaIkLongestChildPath(selected, GetVanillaIkAutoMaxChainLength(), out List<ShapeElement> childPath, out string childNote) &&
                TryBuildVanillaIkChainNames(childPath, out string[] childNames))
            {
                warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; Extended auto uses child chain {childNames[0]}.";
                if (!string.IsNullOrWhiteSpace(childNote)) warning += $" {childNote}";
                chain = new VanillaIkManualChain(childNames, childNames[^1], string.Join(" -> ", childNames));
                return true;
            }

            warning = $"Selected {resolvedSelectedName} is {selectedHubReason}; auto uses a one-bone chain.";
            chain = new VanillaIkManualChain([resolvedSelectedName], resolvedSelectedName, resolvedSelectedName);
            return true;
        }

        int detectedStartIndex = FindVanillaIkAutoChainStart(document, path, selectedIndex, out string stopReason);
        int hardAnchorStartIndex = FindVanillaIkHardAnchorStart(document, path, selectedIndex);
        int startIndex = Math.Max(hardAnchorStartIndex, detectedStartIndex - _vanillaIkAutoRootExtraBones);
        int maxChainLength = GetVanillaIkAutoMaxChainLength();
        if (selectedIndex - startIndex + 1 > maxChainLength)
        {
            startIndex = Math.Max(hardAnchorStartIndex, selectedIndex - maxChainLength + 1);
        }

        var chainElements = path.Skip(startIndex).Take(selectedIndex - startIndex + 1).ToList();
        int remaining = Math.Min(_vanillaIkAutoEndExtraBones, Math.Max(0, maxChainLength - chainElements.Count));
        AppendVanillaIkLongestDescendantPath(chainElements, selected, remaining, out string extensionNote);
        TrimVanillaIkChainAtAnchors(document, chainElements);

        if (!TryBuildVanillaIkChainNames(chainElements, out string[] orderedNames))
        {
            orderedNames = BuildVanillaIkFallbackChainNames(path, selectedElementName);
            warning = "Auto IK could not build a named structural path; using a fallback chain.";
        }

        if (orderedNames.Length == 0)
        {
            error = $"IK element {selectedElementName} has no usable named chain.";
            return false;
        }

        if (_vanillaIkMode == VanillaIkChainMode.AutoConservative &&
            !IsVanillaIkConservativeChainAllowed(document, animation, keyFrame, path, startIndex, selectedIndex, orderedNames))
        {
            error = "Conservative auto IK could not prove a paired or clear linear appendage. Switch to Extended, pin anchors, or use Manual override.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(warning))
        {
            warning = string.IsNullOrWhiteSpace(stopReason)
                ? "Auto chain used the nearest structural path."
                : $"Auto chain stopped before {stopReason}.";
            if (_vanillaIkAutoRootExtraBones > 0) warning += $" Root expanded by {_vanillaIkAutoRootExtraBones}.";
            if (!string.IsNullOrWhiteSpace(extensionNote)) warning += $" {extensionNote}";
        }
        if (orderedNames.Length == 1 && !warning.Contains("one-bone", StringComparison.OrdinalIgnoreCase))
        {
            warning += " Using a one-bone chain.";
        }

        chain = new VanillaIkManualChain(orderedNames, orderedNames[^1], string.Join(" -> ", orderedNames));
        return true;
    }

    private void NormalizeVanillaIkAutoAdjustmentSelection(string selectedElementName)
    {
        string normalized = selectedElementName?.Trim() ?? "";
        if (string.Equals(_vanillaIkAutoAdjustmentElementName, normalized, StringComparison.OrdinalIgnoreCase)) return;

        _vanillaIkAutoAdjustmentElementName = normalized;
        _vanillaIkAutoRootExtraBones = 0;
        _vanillaIkAutoEndExtraBones = 0;
    }

    private int GetVanillaIkAutoMaxChainLength()
    {
        int baseLength = _vanillaIkMode == VanillaIkChainMode.AutoExtended
            ? VanillaIkAutoAbsoluteMaxChainLength
            : VanillaIkAutoMaxChainLength;
        return Math.Clamp(
            baseLength + _vanillaIkAutoRootExtraBones + _vanillaIkAutoEndExtraBones,
            1,
            VanillaIkAutoAbsoluteMaxChainLength);
    }

    private int FindVanillaIkAutoChainStart(VanillaAnimationDocument document, IReadOnlyList<ShapeElement> path, int selectedIndex, out string stopReason)
    {
        stopReason = "";
        for (int index = selectedIndex - 1; index >= 0; index--)
        {
            string elementName = path[index].Name ?? "";
            if (!string.IsNullOrWhiteSpace(elementName) && ContainsVanillaIkAnchor(document, elementName))
            {
                stopReason = $"{elementName} (pinned anchor)";
                return Math.Min(selectedIndex, index + 1);
            }

            if (!IsVanillaIkStructuralHub(path[index], index == 0, out string reason)) continue;

            string name = string.IsNullOrWhiteSpace(path[index].Name) ? "unnamed hub" : path[index].Name!;
            stopReason = $"{name} ({reason})";
            return Math.Min(selectedIndex, index + 1);
        }

        return Math.Max(0, selectedIndex - 1);
    }

    private int FindVanillaIkHardAnchorStart(VanillaAnimationDocument document, IReadOnlyList<ShapeElement> path, int selectedIndex)
    {
        for (int index = selectedIndex - 1; index >= 0; index--)
        {
            string elementName = path[index].Name ?? "";
            if (!string.IsNullOrWhiteSpace(elementName) && ContainsVanillaIkAnchor(document, elementName))
            {
                return Math.Min(selectedIndex, index + 1);
            }
        }

        return 0;
    }

    private bool IsVanillaIkConservativeChainAllowed(
        VanillaAnimationDocument document,
        VanillaAnimation animation,
        AnimationKeyFrame keyFrame,
        IReadOnlyList<ShapeElement> path,
        int startIndex,
        int selectedIndex,
        IReadOnlyList<string> orderedNames)
    {
        if (orderedNames.Count <= 2) return true;
        if (IsVanillaIkClearLinearPath(path, startIndex, selectedIndex)) return true;

        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        foreach (string elementName in orderedNames)
        {
            if (TryResolveVanillaSymmetryPair(document, elementName, allElements, out _, out VanillaSymmetrySide sourceSide, out _) &&
                sourceSide != VanillaSymmetrySide.Unknown)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVanillaIkClearLinearPath(IReadOnlyList<ShapeElement> path, int startIndex, int selectedIndex)
    {
        for (int index = Math.Max(0, startIndex); index < selectedIndex; index++)
        {
            ShapeElement current = path[index];
            ShapeElement next = path[index + 1];
            ShapeElement[] children = current.Children ?? [];
            ShapeElement[] namedChildren = children
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToArray();
            if (namedChildren.Length <= 1) continue;

            int nextMatches = namedChildren.Count(child => string.Equals(child.Name, next.Name, StringComparison.OrdinalIgnoreCase));
            if (nextMatches != 1) return false;
        }

        return true;
    }

    private void TrimVanillaIkChainAtAnchors(VanillaAnimationDocument document, List<ShapeElement> chainElements)
    {
        for (int index = 0; index < chainElements.Count; index++)
        {
            string elementName = chainElements[index].Name ?? "";
            if (string.IsNullOrWhiteSpace(elementName) || !ContainsVanillaIkAnchor(document, elementName)) continue;

            chainElements.RemoveRange(index, chainElements.Count - index);
            return;
        }
    }

    private static bool IsVanillaIkStructuralHub(ShapeElement element, bool isRoot, out string reason)
    {
        if (isRoot)
        {
            reason = "shape root";
            return true;
        }

        int childCount = element.Children?.Length ?? 0;
        if (childCount >= VanillaIkAutoHubChildThreshold)
        {
            reason = $"hub with {childCount} children";
            return true;
        }

        string normalized = NormalizeVanillaIkStructureName(element.Name);
        if (IsVanillaIkTrunkName(normalized))
        {
            reason = $"trunk name '{element.Name}'";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsVanillaIkTrunkName(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return false;
        if (VanillaIkTrunkNameTokens.Contains(normalizedName, StringComparer.OrdinalIgnoreCase)) return true;
        return normalizedName.Contains("torso", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("spine", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("pelvis", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVanillaIkStructureName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool TryGetVanillaIkLongestChildPath(ShapeElement element, int maxLength, out List<ShapeElement> path, out string note)
    {
        path = [];
        note = "";
        if (maxLength <= 0) return false;

        ShapeElement? current = SelectVanillaIkLongestNamedChild(element, out int childCount);
        if (current == null) return false;
        if (childCount > 1)
        {
            note = $"Chose the longest child path from {element.Name ?? "unnamed"} ({childCount} children).";
        }

        path.Add(current);
        AppendVanillaIkLongestDescendantPath(path, current, maxLength - 1, out string extensionNote);
        if (!string.IsNullOrWhiteSpace(extensionNote))
        {
            note = string.IsNullOrWhiteSpace(note) ? extensionNote : $"{note} {extensionNote}";
        }

        return path.Count > 0;
    }

    private static void AppendVanillaIkLongestDescendantPath(List<ShapeElement> chainElements, ShapeElement current, int maxAdditional, out string note)
    {
        note = "";
        for (int added = 0; added < maxAdditional; added++)
        {
            ShapeElement? next = SelectVanillaIkLongestNamedChild(current, out int childCount);
            if (next == null) return;

            if (childCount > 1 && string.IsNullOrWhiteSpace(note))
            {
                note = $"Extended through the longest child path from {current.Name ?? "unnamed"} ({childCount} children).";
            }

            chainElements.Add(next);
            current = next;
        }
    }

    private static ShapeElement? SelectVanillaIkLongestNamedChild(ShapeElement element, out int childCount)
    {
        childCount = element.Children?.Length ?? 0;
        if (element.Children == null || element.Children.Length == 0) return null;

        return element.Children
            .Where(child => !string.IsNullOrWhiteSpace(child.Name))
            .OrderByDescending(GetVanillaIkNamedSubtreeDepth)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int GetVanillaIkNamedSubtreeDepth(ShapeElement element)
    {
        if (element.Children == null || element.Children.Length == 0) return 1;

        int best = 0;
        foreach (ShapeElement child in element.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Name)) continue;
            best = Math.Max(best, GetVanillaIkNamedSubtreeDepth(child));
        }

        return 1 + best;
    }

    private static bool TryBuildVanillaIkChainNames(IEnumerable<ShapeElement> elements, out string[] names)
    {
        names = elements
            .Select(element => element.Name ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length > 0;
    }

    private static string[] BuildVanillaIkFallbackChainNames(IReadOnlyList<ShapeElement> path, string selectedElementName)
    {
        if (path.Count == 0) return string.IsNullOrWhiteSpace(selectedElementName) ? [] : [selectedElementName.Trim()];

        int selectedIndex = path.Count - 1;
        int startIndex = Math.Max(0, selectedIndex - 1);
        string[] names = path
            .Skip(startIndex)
            .Select(element => element.Name ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length > 0 || string.IsNullOrWhiteSpace(selectedElementName) ? names : [selectedElementName.Trim()];
    }

    private bool TryGetManualVanillaIkChain(Shape? shape, out VanillaIkManualChain chain, out string error, out string warning)
    {
        chain = default;
        error = "";
        warning = "";

        if (_vanillaIkChainElementNames.Count == 0)
        {
            error = "Switch to Manual override, then click body parts or Ctrl+Click elements to build a manual IK chain.";
            return false;
        }

        if (shape?.Elements == null || shape.Elements.Length == 0)
        {
            error = "IK needs a loaded shape hierarchy.";
            return false;
        }

        var nodes = new List<VanillaIkChainNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string selectedName in _vanillaIkChainElementNames)
        {
            string name = selectedName.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(name))
            {
                error = $"IK chain contains {name} more than once.";
                return false;
            }

            if (!TryFindShapeElementPath(shape, name, out List<ShapeElement> path) || path.Count == 0)
            {
                error = $"IK chain element {name} was not found in the shape hierarchy.";
                return false;
            }

            ShapeElement element = path[^1];
            string elementName = string.IsNullOrWhiteSpace(element.Name) ? name : element.Name!;
            string parentName = path.Count > 1 && !string.IsNullOrWhiteSpace(path[^2].Name) ? path[^2].Name! : "";
            nodes.Add(new VanillaIkChainNode(elementName, parentName, path.Count - 1));
        }

        if (nodes.Count == 0)
        {
            error = "Switch to Manual override, then click body parts or Ctrl+Click elements to build a manual IK chain.";
            return false;
        }

        nodes.Sort((left, right) =>
        {
            int depth = left.Depth.CompareTo(right.Depth);
            return depth != 0 ? depth : string.Compare(left.ElementName, right.ElementName, StringComparison.OrdinalIgnoreCase);
        });

        for (int index = 1; index < nodes.Count; index++)
        {
            VanillaIkChainNode previous = nodes[index - 1];
            VanillaIkChainNode current = nodes[index];
            if (!string.Equals(current.ParentElementName, previous.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                error = $"IK chain must be one contiguous parent-to-child path. {current.ElementName} is not a direct child of {previous.ElementName}.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(nodes[0].ParentElementName))
        {
            warning = "Top selected element is a root element; IK can rotate the whole model.";
        }

        string[] orderedNames = nodes.Select(node => node.ElementName).ToArray();
        chain = new VanillaIkManualChain(orderedNames, orderedNames[^1], string.Join(" -> ", orderedNames));
        return true;
    }

    private static bool ContainsElementName(string[] elementNames, string value)
    {
        return !string.IsNullOrWhiteSpace(value) && elementNames.Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
    }

    private void SetVanillaIkTargetFromCurrentEnd(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain)
    {
        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.DistalEndpoint, out VanillaIkCcdCache? cache, out string error) || cache == null)
        {
            _vanillaStatus = error;
            return;
        }

        _vanillaIkTargetX = (float)cache.EndOrigin.X;
        _vanillaIkTargetY = (float)cache.EndOrigin.Y;
        _vanillaIkTargetZ = (float)cache.EndOrigin.Z;
        _vanillaIkHasTarget = true;
        _vanillaStatus = $"IK target set from distal end of {chain.EndElementName}.";
    }

    private void ApplyVanillaIkTarget(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain)
    {
        if (!_vanillaIkHasTarget)
        {
            _vanillaStatus = "IK solve needs a target.";
            return;
        }

        if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.DistalEndpoint, out VanillaIkCcdCache? cache, out string error) || cache == null)
        {
            _vanillaStatus = error;
            return;
        }

        Vec3d target = new(_vanillaIkTargetX, _vanillaIkTargetY, _vanillaIkTargetZ);
        if (!TrySolveVanillaIkToTarget(cache, target, _vanillaIkPreserveDraggedPartRotation, keepHandleLocalTransform: false, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
        {
            _vanillaStatus = solveError;
            return;
        }

        ApplyVanillaIkSolvedElements(keyFrame, chain, solvedElements);
        ApplyVanillaElementEdit(row, entry, keyFrame, chain.ElementNames.ToArray());
        _vanillaStatus = finalDistance <= VanillaIkSolveTolerance
            ? $"Solved IK for {chain.ElementNames.Count} element(s) at frame {keyFrame.Frame}."
            : $"Solved IK best effort for {chain.ElementNames.Count} element(s); remaining distance {finalDistance:0.###}.";
    }

    private void DrawVanillaSymmetryPairSelector(VanillaAnimationDocument document, string selectedElementName, string[] allElements)
    {
        string[] pairOptions = BuildVanillaSymmetryPairOptions(selectedElementName, allElements);
        string manualPair = GetVanillaSymmetryPairOverride(document, selectedElementName);
        int selectedPairIndex = string.IsNullOrWhiteSpace(manualPair)
            ? 0
            : Math.Max(0, Array.FindIndex(pairOptions, option => string.Equals(option, manualPair, StringComparison.OrdinalIgnoreCase)));

        if (selectedPairIndex < 0) selectedPairIndex = 0;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Manual pair##vanilla-symmetry-manual-pair", ref selectedPairIndex, pairOptions, pairOptions.Length))
        {
            if (selectedPairIndex <= 0)
            {
                ClearVanillaSymmetryPairOverride(document, selectedElementName);
                _vanillaStatus = $"Cleared manual symmetry pair for {selectedElementName}.";
            }
            else
            {
                SetVanillaSymmetryPairOverride(document, selectedElementName, pairOptions[selectedPairIndex]);
                _vanillaStatus = $"Manual symmetry pair set: {selectedElementName} <-> {pairOptions[selectedPairIndex]}.";
            }
        }
    }

    private void ApplyVanillaSymmetryAction(VanillaBrowserRow row, VanillaAnimationDocument document, Func<VanillaSymmetryResult> action)
    {
        VanillaSymmetryResult result = action();
        _vanillaStatus = result.Message;
        if (!result.Applied) return;

        MarkVanillaDirty(document);
        RefreshVanillaPreviewAfterEdit(row);
    }

    private VanillaSymmetryResult ApplyVanillaElementEdit(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame sourceKeyFrame, params string[] changedElementNames)
    {
        VanillaSymmetryResult symmetry = PropagateVanillaLiveSymmetry(entry.Document, entry.Animation, sourceKeyFrame, changedElementNames);
        PreserveVanillaSelectedKeyFrame(entry.Animation, sourceKeyFrame);
        MarkVanillaDirty(entry.Document);
        string[] refreshElementNames = symmetry.WrittenElements is { Count: > 0 }
            ? changedElementNames.Concat(symmetry.WrittenElements).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : changedElementNames;
        RefreshVanillaPreviewAfterEdit(row, refreshElementNames);

        if (symmetry.Applied)
        {
            _vanillaStatus = symmetry.Message;
        }

        return symmetry;
    }

    private void PreserveVanillaSelectedKeyFrame(VanillaAnimation animation, AnimationKeyFrame sourceKeyFrame)
    {
        if (animation.KeyFrames == null || animation.KeyFrames.Length == 0) return;

        int previousIndex = _vanillaSelection.KeyFrameIndex;
        int currentIndex = Array.FindIndex(animation.KeyFrames, keyFrame => ReferenceEquals(keyFrame, sourceKeyFrame));
        if (currentIndex < 0) return;

        _vanillaSelection.KeyFrameIndex = currentIndex;
        if (_vanillaViewportGizmoDragAxis != TransformGizmoAxis.None && _vanillaViewportGizmoDragKeyFrameIndex == previousIndex)
        {
            _vanillaViewportGizmoDragKeyFrameIndex = currentIndex;
        }

        if (_vanillaIkDragActive && _vanillaIkDragKeyFrameIndex == previousIndex)
        {
            _vanillaIkDragKeyFrameIndex = currentIndex;
        }
    }

    private VanillaSymmetryResult PropagateVanillaLiveSymmetry(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame sourceKeyFrame, IEnumerable<string> changedElementNames)
    {
        if (!_vanillaLiveSymmetryEnabled || _vanillaLiveSymmetryPropagating)
        {
            return new(false, 0, 0, 0, "");
        }

        if (sourceKeyFrame.Elements == null || sourceKeyFrame.Elements.Count == 0)
        {
            return new(false, 0, 0, 0, "");
        }

        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, sourceKeyFrame);
        if (allElements.Length <= 1)
        {
            return new(false, 0, 0, 0, "");
        }

        var sourceSnapshots = new List<(string Name, AnimationKeyFrameElement Element)>();
        foreach (string sourceName in changedElementNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (sourceKeyFrame.Elements.TryGetValue(sourceName, out AnimationKeyFrameElement? sourceElement) && sourceElement != null)
            {
                sourceSnapshots.Add((sourceName, CloneElement(sourceElement)));
            }
        }

        if (sourceSnapshots.Count == 0)
        {
            return new(false, 0, 0, 0, "");
        }

        int phaseFrames = GetVanillaLiveSymmetryPhaseFrames(animation);
        int targetFrame = _vanillaLiveSymmetryMode == VanillaLiveSymmetryMode.InPlace
            ? sourceKeyFrame.Frame
            : GetVanillaPhaseTargetFrame(animation, sourceKeyFrame.Frame, phaseFrames);
        int written = 0;
        int created = 0;
        int overwritten = 0;
        int skipped = 0;
        List<string> writtenNames = [];

        _vanillaLiveSymmetryPropagating = true;
        try
        {
            foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in sourceSnapshots)
            {
                if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide sourceSide, out _) ||
                    string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase) ||
                    !ShouldVanillaLiveSymmetryPropagateFrom(sourceSide))
                {
                    skipped++;
                    continue;
                }

                AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, targetFrame, out bool createdKeyFrame);
                if (createdKeyFrame) created++;
                targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
                if (targetKeyFrame.Elements.ContainsKey(pairName)) overwritten++;
                targetKeyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
                writtenNames.Add(pairName);
                written++;
            }
        }
        finally
        {
            _vanillaLiveSymmetryPropagating = false;
        }

        if (written > 0 && animation.KeyFrames != null)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, skipped > 0 ? "Live symmetry skipped all changed elements." : "")
            : new(true, written, created, overwritten, $"Live symmetry wrote {written} mirrored pair(s) at frame {targetFrame}; created {created} keyframe(s), overwrote {overwritten} element(s).", writtenNames);
    }

    private bool ShouldVanillaLiveSymmetryPropagateFrom(VanillaSymmetrySide sourceSide)
    {
        return _vanillaLiveSymmetryDriver switch
        {
            VanillaLiveSymmetryDriver.LeftDrivesRight => sourceSide == VanillaSymmetrySide.Left,
            VanillaLiveSymmetryDriver.RightDrivesLeft => sourceSide == VanillaSymmetrySide.Right,
            _ => true
        };
    }

    private static VanillaSymmetryResult MirrorVanillaElementInKeyFrame(AnimationKeyFrame keyFrame, string sourceElementName, string targetElementName, AnimationKeyFrameElement sourceElement)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        bool overwritten = keyFrame.Elements.ContainsKey(targetElementName);
        keyFrame.Elements[targetElementName] = MirrorVanillaElement(sourceElement);
        return new(true, 1, 0, overwritten ? 1 : 0, $"Mirrored {sourceElementName} to {targetElementName} in frame {keyFrame.Frame}.");
    }

    private static VanillaSymmetryResult MirrorVanillaPairToSelected(AnimationKeyFrame keyFrame, string selectedElementName, string pairElementName)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        if (!keyFrame.Elements.TryGetValue(pairElementName, out AnimationKeyFrameElement? pairElement) || pairElement == null)
        {
            return new(false, 0, 0, 0, $"{pairElementName} is not present in frame {keyFrame.Frame}.");
        }

        bool overwritten = keyFrame.Elements.ContainsKey(selectedElementName);
        keyFrame.Elements[selectedElementName] = MirrorVanillaElement(pairElement);
        return new(true, 1, 0, overwritten ? 1 : 0, $"Mirrored {pairElementName} to {selectedElementName} in frame {keyFrame.Frame}.");
    }

    private VanillaSymmetryResult MirrorVanillaSourceSidePairsInKeyFrame(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame keyFrame, VanillaSymmetrySide sourceSide)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        int written = 0;
        int overwritten = 0;

        foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in keyFrame.Elements.ToArray().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide elementSide, out _) ||
                elementSide != sourceSide ||
                string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (keyFrame.Elements.ContainsKey(pairName)) overwritten++;
            keyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
            written++;
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No {sourceSide.ToString().ToLowerInvariant()}-side elements with pairs were found in frame {keyFrame.Frame}.")
            : new(true, written, 0, overwritten, $"Mirrored {written} {sourceSide.ToString().ToLowerInvariant()}-side pair(s) in frame {keyFrame.Frame}; overwrote {overwritten}.");
    }

    private VanillaSymmetryResult FlipVanillaPoseInKeyFrame(VanillaAnimationDocument document, VanillaAnimation animation, AnimationKeyFrame keyFrame)
    {
        if (keyFrame.Elements == null || keyFrame.Elements.Count == 0)
        {
            return new(false, 0, 0, 0, $"Frame {keyFrame.Frame} has no element channels to flip.");
        }

        string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, keyFrame);
        Dictionary<string, AnimationKeyFrameElement> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, AnimationKeyFrameElement element) in keyFrame.Elements)
        {
            snapshot[name] = CloneElement(element);
        }

        HashSet<string> handled = new(StringComparer.OrdinalIgnoreCase);
        int swapped = 0;
        int mirroredInPlace = 0;
        foreach ((string name, AnimationKeyFrameElement element) in snapshot)
        {
            if (handled.Contains(name)) continue;

            if (TryResolveVanillaSymmetryPair(document, name, allElements, out string pairName, out _, out _) &&
                !string.Equals(pairName, name, StringComparison.OrdinalIgnoreCase))
            {
                keyFrame.Elements[pairName] = MirrorVanillaElement(element);
                if (snapshot.TryGetValue(pairName, out AnimationKeyFrameElement? pairElement))
                {
                    keyFrame.Elements[name] = MirrorVanillaElement(pairElement);
                }
                else
                {
                    keyFrame.Elements.Remove(name);
                }

                handled.Add(name);
                handled.Add(pairName);
                swapped++;
            }
            else
            {
                keyFrame.Elements[name] = MirrorVanillaElement(element);
                handled.Add(name);
                mirroredInPlace++;
            }
        }

        return new(true, swapped + mirroredInPlace, 0, 0,
            $"Flipped pose in frame {keyFrame.Frame}: swapped {swapped} pair(s), mirrored {mirroredInPlace} center element(s) in place.");
    }

    private void CreateMirroredVanillaAnimationCopy(VanillaShapeAnimationEntry entry)
    {
        VanillaAnimationDocument document = entry.Document;
        VanillaAnimation source = entry.Animation;
        VanillaAnimation clone = CloneVanillaAnimation(source);
        string baseCode = source.Code ?? source.Name ?? "animation";
        clone.Code = BuildUniqueVanillaAnimationCode(document, baseCode + "-mirrored");
        clone.Name = string.IsNullOrWhiteSpace(source.Name) ? clone.Code : source.Name + " mirrored";

        int flippedFrames = 0;
        foreach (AnimationKeyFrame keyFrame in clone.KeyFrames ?? [])
        {
            if (FlipVanillaPoseInKeyFrame(document, source, keyFrame).Applied) flippedFrames++;
        }

        VanillaShapeAnimationEntry newEntry = new(document, document.ShapeAnimations.Count, clone, null);
        document.ShapeAnimations.Add(newEntry);
        MarkVanillaDirty(document);
        _vanillaIndex.RebuildLinks();
        InvalidateVanillaBrowserRows();
        EnsureVanillaBrowserVisibleRows();
        _vanillaStatus = $"Created mirrored animation '{clone.Code}' ({flippedFrames} keyframe(s) flipped). Find it in the browser.";
    }

    private VanillaSymmetryResult BakeVanillaHalfCycleSymmetry(VanillaAnimationDocument document, VanillaAnimation animation, string sourceElementName, string targetElementName)
    {
        if (animation.QuantityFrames <= 0)
        {
            return new(false, 0, 0, 0, "Half-cycle bake needs a positive animation frame count.");
        }

        animation.KeyFrames ??= [];
        int halfCycleFrames = GetVanillaHalfCycleFrames(animation);
        int written = 0;
        int created = 0;
        int overwritten = 0;

        foreach (AnimationKeyFrame sourceKeyFrame in animation.KeyFrames.ToArray().OrderBy(keyFrame => keyFrame.Frame))
        {
            if (sourceKeyFrame.Elements == null ||
                !sourceKeyFrame.Elements.TryGetValue(sourceElementName, out AnimationKeyFrameElement? sourceElement) ||
                sourceElement == null)
            {
                continue;
            }

            AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, GetVanillaHalfCycleTargetFrame(animation, sourceKeyFrame.Frame, halfCycleFrames), out bool createdKeyFrame);
            if (createdKeyFrame) created++;
            targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
            if (targetKeyFrame.Elements.ContainsKey(targetElementName)) overwritten++;
            targetKeyFrame.Elements[targetElementName] = MirrorVanillaElement(sourceElement);
            written++;
        }

        if (written > 0)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No source keyframes contain {sourceElementName}.")
            : new(true, written, created, overwritten, $"Baked half-cycle symmetry {sourceElementName} -> {targetElementName}: wrote {written}, created {created} keyframe(s), overwrote {overwritten} element(s).");
    }

    private VanillaSymmetryResult BakeVanillaHalfCycleSymmetryForSide(VanillaAnimationDocument document, VanillaAnimation animation, VanillaSymmetrySide sourceSide)
    {
        if (animation.QuantityFrames <= 0)
        {
            return new(false, 0, 0, 0, "Half-cycle bake needs a positive animation frame count.");
        }

        animation.KeyFrames ??= [];
        int halfCycleFrames = GetVanillaHalfCycleFrames(animation);
        int written = 0;
        int created = 0;
        int overwritten = 0;

        foreach (AnimationKeyFrame sourceKeyFrame in animation.KeyFrames.ToArray().OrderBy(keyFrame => keyFrame.Frame))
        {
            if (sourceKeyFrame.Elements == null || sourceKeyFrame.Elements.Count == 0) continue;

            string[] allElements = BuildVanillaSymmetryElementUniverse(document, animation, sourceKeyFrame);
            foreach ((string sourceName, AnimationKeyFrameElement sourceElement) in sourceKeyFrame.Elements.ToArray().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryResolveVanillaSymmetryPair(document, sourceName, allElements, out string pairName, out VanillaSymmetrySide elementSide, out _) ||
                    elementSide != sourceSide ||
                    string.Equals(sourceName, pairName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AnimationKeyFrame targetKeyFrame = GetOrCreateVanillaTargetKeyFrame(animation, GetVanillaHalfCycleTargetFrame(animation, sourceKeyFrame.Frame, halfCycleFrames), out bool createdKeyFrame);
                if (createdKeyFrame) created++;
                targetKeyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
                if (targetKeyFrame.Elements.ContainsKey(pairName)) overwritten++;
                targetKeyFrame.Elements[pairName] = MirrorVanillaElement(sourceElement);
                written++;
            }
        }

        if (written > 0)
        {
            animation.KeyFrames = animation.KeyFrames.OrderBy(keyFrame => keyFrame.Frame).ToArray();
        }

        return written == 0
            ? new(false, 0, 0, 0, $"No {sourceSide.ToString().ToLowerInvariant()}-side source elements with pairs were found.")
            : new(true, written, created, overwritten, $"Baked half-cycle symmetry for {sourceSide.ToString().ToLowerInvariant()} side: wrote {written}, created {created} keyframe(s), overwrote {overwritten} element(s).");
    }

    private bool TryApplyVanillaViewportIkMove(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrameElement desiredElement, NVector3 dragModelDelta)
    {
        if (entry.Animation.KeyFrames == null || entry.Animation.KeyFrames.Length == 0) return false;
        int keyFrameIndex = Math.Clamp(_vanillaSelection.KeyFrameIndex, 0, entry.Animation.KeyFrames.Length - 1);
        AnimationKeyFrame keyFrame = entry.Animation.KeyFrames[keyFrameIndex];

        if (!TryGetActiveVanillaIkChain(entry.Document, entry.Animation, keyFrame, _vanillaSelection.ElementName, out VanillaIkManualChain chain, out string chainError, out _))
        {
            _vanillaStatus = chainError;
            return false;
        }

        string selectedElementName = _vanillaSelection.ElementName;
        if (!_vanillaIkDragActive ||
            _vanillaIkDragCache == null ||
            _vanillaIkDragRowKey != row.Key ||
            _vanillaIkDragKeyFrameIndex != keyFrameIndex ||
            !string.Equals(_vanillaIkDragElementName, selectedElementName, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryCreateVanillaIkCcdCache(row, entry, keyFrame, chain, VanillaIkEffectorMode.GizmoHandle, out VanillaIkCcdCache? cache, out string error) || cache == null)
            {
                _vanillaStatus = error;
                return false;
            }

            _vanillaIkDragActive = true;
            _vanillaIkDragRowKey = row.Key;
            _vanillaIkDragKeyFrameIndex = keyFrameIndex;
            _vanillaIkDragElementName = selectedElementName;
            _vanillaIkDragCache = cache;
        }

        Vec3d target = _vanillaIkLockMoveToDragAxis && dragModelDelta.LengthSquared() > 0.000001f
            ? Add(_vanillaIkDragCache.EndOrigin, new Vec3d(dragModelDelta.X, dragModelDelta.Y, dragModelDelta.Z))
            : GetVanillaIkDesiredEndTarget(_vanillaIkDragCache, desiredElement);
        if (!TrySolveVanillaIkToTarget(_vanillaIkDragCache, target, _vanillaIkPreserveDraggedPartRotation, keepHandleLocalTransform: true, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string solveError))
        {
            _vanillaStatus = solveError;
            return false;
        }

        ApplyVanillaIkSolvedElements(keyFrame, chain, solvedElements);
        ApplyVanillaElementEdit(row, entry, keyFrame, chain.ElementNames.ToArray());
        string targetMode = _vanillaIkLockMoveToDragAxis ? " on drag axis" : "";
        string handleMode = _vanillaIkPreserveDraggedPartRotation ? " with connected handle" : "";
        _vanillaStatus = finalDistance <= VanillaIkSolveTolerance
            ? $"IK Move solved {chain.ElementNames.Count} element(s){targetMode}{handleMode}."
            : $"IK Move solved best effort{targetMode}{handleMode}; remaining distance {finalDistance:0.###}.";
        return true;
    }

    private bool TryCreateVanillaIkCcdCache(VanillaBrowserRow row, VanillaShapeAnimationEntry entry, AnimationKeyFrame keyFrame, VanillaIkManualChain chain, VanillaIkEffectorMode effectorMode, out VanillaIkCcdCache? cache, out string error)
    {
        cache = null;
        error = "";

        VanillaAnimationPreviewScene? scene = EnsureVanillaPreviewScene(row);
        if (scene == null)
        {
            error = "IK needs a loaded vanilla preview scene.";
            return false;
        }

        scene.Scrub(Math.Clamp(keyFrame.Frame, 0, Math.Max(0, scene.QuantityFrames - 1)));

        var infos = new List<VanillaIkPoseInfo>();
        foreach (string elementName in chain.ElementNames)
        {
            if (!TryGetVanillaIkPoseInfo(scene, elementName, out VanillaIkPoseInfo info, out error)) return false;
            infos.Add(info);
        }

        if (infos.Count == 0)
        {
            error = "IK needs at least one chain element.";
            return false;
        }

        VanillaIkPoseInfo endInfo = infos[^1];
        bool allowZeroEndSegment = effectorMode == VanillaIkEffectorMode.GizmoHandle;
        if (!TryGetVanillaIkEffectorModel(endInfo, effectorMode, out Vec3d endOrigin))
        {
            error = $"Could not find an IK effector point for {chain.EndElementName}.";
            return false;
        }

        Vec3d[] jointPositions = new Vec3d[infos.Count + 1];
        for (int index = 0; index < infos.Count; index++)
        {
            jointPositions[index] = infos[index].Origin;
        }
        jointPositions[^1] = endOrigin;

        for (int index = 0; index < jointPositions.Length - 1; index++)
        {
            if (Distance(jointPositions[index], jointPositions[index + 1]) > 0.0001) continue;
            if (allowZeroEndSegment && index == jointPositions.Length - 2 && jointPositions.Length > 2) continue;

            error = $"IK chain segment at {chain.ElementNames[index]} has no usable length.";
            return false;
        }

        TransformGizmoAxes selectedAxes = new(
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(1, 0, 0)), new Vec3d(1, 0, 0)),
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(0, 1, 0)), new Vec3d(0, 1, 0)),
            SafeNormalize(endInfo.WorldRotation.TransformDirection(new Vec3d(0, 0, 1)), new Vec3d(0, 0, 1)));

        AnimationKeyFrameElement[] startElements = chain.ElementNames
            .Select(name => CloneElement(GetVanillaIkElementOrDefault(keyFrame, name)))
            .ToArray();
        AnimationKeyFrameElement selectedStart = CloneElement(GetVanillaIkElementOrDefault(keyFrame, chain.EndElementName));

        cache = new VanillaIkCcdCache(chain, infos.ToArray(), jointPositions, endOrigin, selectedAxes, selectedStart, startElements);
        return true;
    }

    private static bool TryGetVanillaIkEffectorModel(VanillaIkPoseInfo endInfo, VanillaIkEffectorMode mode, out Vec3d origin)
    {
        if (mode == VanillaIkEffectorMode.GizmoHandle && TryGetVanillaGizmoHandlePointModel(endInfo.Pose, out origin))
        {
            return true;
        }

        return TryGetVanillaDistalEndpointModel(endInfo.Pose, endInfo.Origin, out origin);
    }

    private static AnimationKeyFrameElement GetVanillaIkElementOrDefault(AnimationKeyFrame keyFrame, string elementName)
    {
        if (keyFrame.Elements != null &&
            keyFrame.Elements.TryGetValue(elementName, out AnimationKeyFrameElement? element) &&
            element != null)
        {
            return element;
        }

        return new AnimationKeyFrameElement();
    }

    private static Vec3d GetVanillaIkDesiredEndTarget(VanillaIkCcdCache cache, AnimationKeyFrameElement desiredElement)
    {
        double dx = ((desiredElement.OffsetX ?? 0) - (cache.SelectedStartElement.OffsetX ?? 0)) / 16.0;
        double dy = ((desiredElement.OffsetY ?? 0) - (cache.SelectedStartElement.OffsetY ?? 0)) / 16.0;
        double dz = ((desiredElement.OffsetZ ?? 0) - (cache.SelectedStartElement.OffsetZ ?? 0)) / 16.0;

        return Add(cache.EndOrigin, Add(Add(Scale(cache.SelectedAxes.X, dx), Scale(cache.SelectedAxes.Y, dy)), Scale(cache.SelectedAxes.Z, dz)));
    }

    private static bool TrySolveVanillaIkCcdToTarget(VanillaIkCcdCache cache, Vec3d requestedTarget, bool preserveHandleRotation, bool keepHandleLocalTransform, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string error)
    {
        solvedElements = cache.StartElements.Select(CloneElement).ToArray();
        finalDistance = Distance(cache.EndOrigin, requestedTarget);
        error = "";

        int count = cache.BoneInfos.Count;
        if (count == 0)
        {
            error = "IK needs at least one chain element.";
            return false;
        }

        bool solveWithUpstreamDriversOnly = preserveHandleRotation || keepHandleLocalTransform;
        int driverCount = solveWithUpstreamDriversOnly ? count - 1 : count;
        if (driverCount == 0)
        {
            error = keepHandleLocalTransform || preserveHandleRotation
                ? "Locked IK handle needs a parent chain; disable handle rotation lock or add driver bones."
                : "IK needs at least one driver bone.";
            return false;
        }

        Vec3d[] joints = cache.JointPositions.Select(point => new Vec3d(point.X, point.Y, point.Z)).ToArray();
        RigIkMatrix3[] rotations = cache.BoneInfos.Select(info => info.WorldRotation).ToArray();
        Vec3d lockedHandleEndpointOffset = preserveHandleRotation
            ? Sub(cache.EndOrigin, cache.JointPositions[count - 1])
            : new Vec3d();
        double initialDistance = Distance(joints[^1], requestedTarget);

        const int maxIterations = 24;
        const double vectorEpsilon = 0.000001;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            for (int boneIndex = driverCount - 1; boneIndex >= 0; boneIndex--)
            {
                Vec3d origin = joints[boneIndex];
                Vec3d currentVector = Sub(joints[^1], origin);
                Vec3d targetVector = Sub(requestedTarget, origin);
                if (currentVector.LengthSq() < vectorEpsilon || targetVector.LengthSq() < vectorEpsilon) continue;

                RigIkMatrix3 delta = RigIkMatrix3.FromTo(currentVector, targetVector).Orthonormalized();
                for (int jointIndex = boneIndex + 1; jointIndex < joints.Length; jointIndex++)
                {
                    joints[jointIndex] = Add(origin, delta.TransformDirection(Sub(joints[jointIndex], origin)));
                }

                for (int rotationIndex = boneIndex; rotationIndex < (solveWithUpstreamDriversOnly ? driverCount : rotations.Length); rotationIndex++)
                {
                    rotations[rotationIndex] = delta.Mul(rotations[rotationIndex]).Orthonormalized();
                }

                if (preserveHandleRotation && !keepHandleLocalTransform)
                {
                    joints[^1] = Add(joints[count - 1], lockedHandleEndpointOffset);
                }

                if (Distance(joints[^1], requestedTarget) <= VanillaIkSolveTolerance) break;
            }

            if (Distance(joints[^1], requestedTarget) <= VanillaIkSolveTolerance) break;
        }

        finalDistance = Distance(joints[^1], requestedTarget);
        if (finalDistance > VanillaIkSolveTolerance && finalDistance >= initialDistance - VanillaIkSolveImprovementEpsilon)
        {
            error = $"IK target did not improve. Remaining distance {finalDistance:0.###}.";
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            RigIkMatrix3 parentWorld = index > 0 ? rotations[index - 1] : cache.BoneInfos[index].ParentWorldRotation;
            if (keepHandleLocalTransform && index == count - 1)
            {
                solvedElements[index] = CloneElement(cache.StartElements[index]);
                continue;
            }

            RigIkMatrix3 world = preserveHandleRotation && index == count - 1
                ? cache.BoneInfos[index].WorldRotation
                : rotations[index];
            RigIkMatrix3 local = parentWorld.Inverted().Mul(world).Orthonormalized();
            Vec3d euler = Sub(local.ToEulerDegrees(), cache.BoneInfos[index].BaseRotationDegrees);
            solvedElements[index] = WithVanillaIkRotation(cache.StartElements[index], euler);
        }

        return true;
    }

    private bool TrySolveVanillaIkToTarget(VanillaIkCcdCache cache, Vec3d requestedTarget, bool preserveHandleRotation, bool keepHandleLocalTransform, out AnimationKeyFrameElement[] solvedElements, out double finalDistance, out string error)
    {
        return _vanillaIkSolver == VanillaIkSolverKind.Fabrik
            ? TrySolveVanillaIkFabrikToTarget(cache, requestedTarget, preserveHandleRotation, keepHandleLocalTransform, _vanillaIkSwivelDegrees, out solvedElements, out finalDistance, out error)
            : TrySolveVanillaIkCcdToTarget(cache, requestedTarget, preserveHandleRotation, keepHandleLocalTransform, out solvedElements, out finalDistance, out error);
    }

    private static bool TrySolveVanillaIkFabrikToTarget(
        VanillaIkCcdCache cache,
        Vec3d requestedTarget,
        bool preserveHandleRotation,
        bool keepHandleLocalTransform,
        double swivelDegrees,
        out AnimationKeyFrameElement[] solvedElements,
        out double finalDistance,
        out string error)
    {
        solvedElements = cache.StartElements.Select(CloneElement).ToArray();
        finalDistance = Distance(cache.EndOrigin, requestedTarget);
        error = "";

        int count = cache.BoneInfos.Count;
        if (count == 0)
        {
            error = "IK needs at least one chain element.";
            return false;
        }

        bool solveWithUpstreamDriversOnly = preserveHandleRotation || keepHandleLocalTransform;
        int driverCount = solveWithUpstreamDriversOnly ? count - 1 : count;
        if (driverCount == 0)
        {
            error = keepHandleLocalTransform || preserveHandleRotation
                ? "Locked IK handle needs a parent chain; disable handle rotation lock or add driver bones."
                : "IK needs at least one driver bone.";
            return false;
        }

        Vec3d[] original = cache.JointPositions;
        int jointCount = original.Length;
        double initialDistance = Distance(original[^1], requestedTarget);

        // With a world-locked handle the effector offset from the handle joint stays fixed,
        // so the position chain only solves up to the handle joint.
        bool lockEndSegment = preserveHandleRotation && !keepHandleLocalTransform;
        int solveJointCount = lockEndSegment ? jointCount - 1 : jointCount;
        Vec3d lockedEndOffset = lockEndSegment ? Sub(cache.EndOrigin, original[jointCount - 2]) : new Vec3d();
        Vec3d effectiveTarget = lockEndSegment ? Sub(requestedTarget, lockedEndOffset) : requestedTarget;

        if (solveJointCount < 2)
        {
            error = "FABRIK needs at least one usable chain segment.";
            return false;
        }

        Vec3d[] joints = new Vec3d[solveJointCount];
        double[] lengths = new double[solveJointCount - 1];
        double totalLength = 0;
        for (int index = 0; index < solveJointCount; index++)
        {
            joints[index] = new Vec3d(original[index].X, original[index].Y, original[index].Z);
        }
        for (int index = 0; index < solveJointCount - 1; index++)
        {
            lengths[index] = Math.Max(0.000001, Distance(original[index], original[index + 1]));
            totalLength += lengths[index];
        }

        Vec3d root = new(joints[0].X, joints[0].Y, joints[0].Z);
        if (Distance(root, effectiveTarget) >= totalLength)
        {
            Vec3d direction = SafeNormalize(Sub(effectiveTarget, root), new Vec3d(0, 1, 0));
            for (int index = 1; index < solveJointCount; index++)
            {
                joints[index] = Add(joints[index - 1], Scale(direction, lengths[index - 1]));
            }
        }
        else
        {
            const int maxIterations = 32;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                joints[^1] = new Vec3d(effectiveTarget.X, effectiveTarget.Y, effectiveTarget.Z);
                for (int index = solveJointCount - 2; index >= 0; index--)
                {
                    Vec3d direction = SafeNormalize(Sub(joints[index], joints[index + 1]), new Vec3d(0, 1, 0));
                    joints[index] = Add(joints[index + 1], Scale(direction, lengths[index]));
                }

                joints[0] = new Vec3d(root.X, root.Y, root.Z);
                for (int index = 1; index < solveJointCount; index++)
                {
                    Vec3d direction = SafeNormalize(Sub(joints[index], joints[index - 1]), new Vec3d(0, 1, 0));
                    joints[index] = Add(joints[index - 1], Scale(direction, lengths[index - 1]));
                }

                if (Distance(joints[^1], effectiveTarget) <= VanillaIkSolveTolerance) break;
            }
        }

        if (Math.Abs(swivelDegrees) > 0.01 && solveJointCount >= 3)
        {
            Vec3d swivelAxis = Sub(joints[^1], joints[0]);
            if (swivelAxis.LengthSq() > 0.000001)
            {
                RigIkMatrix3 swivel = RigIkMatrix3.FromAxisAngle(swivelAxis, swivelDegrees * GameMath.DEG2RAD);
                for (int index = 1; index < solveJointCount - 1; index++)
                {
                    joints[index] = Add(joints[0], swivel.TransformDirection(Sub(joints[index], joints[0])));
                }
            }
        }

        Vec3d[] fullJoints = new Vec3d[jointCount];
        for (int index = 0; index < solveJointCount; index++)
        {
            fullJoints[index] = joints[index];
        }
        if (lockEndSegment)
        {
            fullJoints[jointCount - 1] = Add(joints[^1], lockedEndOffset);
        }

        finalDistance = Distance(fullJoints[^1], requestedTarget);
        if (finalDistance > VanillaIkSolveTolerance && finalDistance >= initialDistance - VanillaIkSolveImprovementEpsilon && Math.Abs(swivelDegrees) <= 0.01)
        {
            error = $"IK target did not improve. Remaining distance {finalDistance:0.###}.";
            return false;
        }

        RigIkMatrix3[] worldRotations = new RigIkMatrix3[count];
        for (int index = 0; index < count; index++)
        {
            if (preserveHandleRotation && index == count - 1)
            {
                worldRotations[index] = cache.BoneInfos[index].WorldRotation;
                continue;
            }

            Vec3d originalDirection = Sub(original[index + 1], original[index]);
            Vec3d solvedDirection = Sub(fullJoints[index + 1], fullJoints[index]);
            if (originalDirection.LengthSq() < 0.000001 || solvedDirection.LengthSq() < 0.000001)
            {
                worldRotations[index] = index > 0
                    ? worldRotations[index - 1].Mul(cache.BoneInfos[index - 1].WorldRotation.Inverted().Mul(cache.BoneInfos[index].WorldRotation)).Orthonormalized()
                    : cache.BoneInfos[index].WorldRotation;
                continue;
            }

            worldRotations[index] = RigIkMatrix3.FromTo(originalDirection, solvedDirection).Orthonormalized().Mul(cache.BoneInfos[index].WorldRotation).Orthonormalized();
        }

        for (int index = 0; index < count; index++)
        {
            if (keepHandleLocalTransform && index == count - 1)
            {
                solvedElements[index] = CloneElement(cache.StartElements[index]);
                continue;
            }

            RigIkMatrix3 parentWorld = index > 0 ? worldRotations[index - 1] : cache.BoneInfos[index].ParentWorldRotation;
            RigIkMatrix3 local = parentWorld.Inverted().Mul(worldRotations[index]).Orthonormalized();
            Vec3d euler = Sub(local.ToEulerDegrees(), cache.BoneInfos[index].BaseRotationDegrees);
            solvedElements[index] = WithVanillaIkRotation(cache.StartElements[index], euler);
        }

        return true;
    }

    private static AnimationKeyFrameElement WithVanillaIkRotation(AnimationKeyFrameElement source, Vec3d rotation)
    {
        AnimationKeyFrameElement result = CloneElement(source);
        result.RotationX = NormalizeVanillaDegrees(rotation.X);
        result.RotationY = NormalizeVanillaDegrees(rotation.Y);
        result.RotationZ = NormalizeVanillaDegrees(rotation.Z);
        CompleteVanillaRotationGroup(result);
        return result;
    }

    private static void ApplyVanillaIkSolvedElements(AnimationKeyFrame keyFrame, VanillaIkManualChain chain, IReadOnlyList<AnimationKeyFrameElement> solvedElements)
    {
        keyFrame.Elements ??= new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < chain.ElementNames.Count && index < solvedElements.Count; index++)
        {
            keyFrame.Elements[chain.ElementNames[index]] = solvedElements[index];
        }
    }

    private static bool TryGetVanillaIkPoseInfo(VanillaAnimationPreviewScene scene, string elementName, out VanillaIkPoseInfo info, out string error)
    {
        info = default;
        error = "";

        if (!TryFindVanillaPose(scene.Animator.RootPoses, elementName, out ElementPose? pose, out ElementPose? parentPose) || pose?.ForElement == null)
        {
            error = $"Preview pose for {elementName} was not found.";
            return false;
        }

        if (!TryGetVanillaPoseModelOrigin(pose, out Vec3d origin))
        {
            error = $"Could not resolve the model-space origin for {elementName}.";
            return false;
        }

        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf worldMatrix))
        {
            error = $"Could not resolve the model matrix for {elementName}.";
            return false;
        }

        RigIkMatrix3 parentWorldRotation = RigIkMatrix3.Identity;
        if (parentPose != null && TryBuildVanillaPoseModelMatrix(parentPose, out Matrixf parentMatrix))
        {
            parentWorldRotation = RigIkMatrix3.FromMatrixf(parentMatrix).Orthonormalized();
        }

        info = new(
            pose,
            origin,
            RigIkMatrix3.FromMatrixf(worldMatrix).Orthonormalized(),
            parentWorldRotation,
            new Vec3d(pose.ForElement.RotationX, pose.ForElement.RotationY, pose.ForElement.RotationZ));
        return true;
    }

    private static bool TryFindVanillaPose(IEnumerable<ElementPose>? poses, string elementName, out ElementPose? result, out ElementPose? parentResult)
    {
        return TryFindVanillaPose(poses, elementName, default!, hasParent: false, out result, out parentResult);
    }

    private static bool TryFindVanillaPose(IEnumerable<ElementPose>? poses, string elementName, ElementPose parent, bool hasParent, out ElementPose? result, out ElementPose? parentResult)
    {
        result = default!;
        parentResult = default!;
        if (poses == null || string.IsNullOrWhiteSpace(elementName)) return false;

        foreach (ElementPose pose in poses)
        {
            if (string.Equals(pose.ForElement?.Name, elementName, StringComparison.OrdinalIgnoreCase))
            {
                result = pose;
                parentResult = hasParent ? parent : default!;
                return true;
            }

            if (TryFindVanillaPose(pose.ChildElementPoses, elementName, pose, hasParent: true, out result, out parentResult)) return true;
        }

        return false;
    }

    private static bool TryGetVanillaPoseModelOrigin(ElementPose pose, out Vec3d origin)
    {
        origin = new Vec3d();
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf matrix)) return false;

        Vec3f localOrigin = GetElementLocalRotationOrigin(pose);
        Vec4f transformed = matrix.TransformVector(new Vec4f(localOrigin.X, localOrigin.Y, localOrigin.Z, 1f));
        origin = new Vec3d(transformed.X, transformed.Y, transformed.Z);
        return true;
    }

    private static bool TryGetVanillaGizmoHandlePointModel(ElementPose pose, out Vec3d origin)
    {
        origin = new Vec3d();
        if (pose.ForElement == null) return false;
        if (!TryBuildVanillaPoseModelMatrix(pose, out Matrixf matrix)) return false;

        NVector3 local = GetVanillaGizmoLocalPoint(pose);
        Vec4f transformed = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
        origin = new Vec3d(transformed.X, transformed.Y, transformed.Z);
        return true;
    }

    private static bool TryBuildVanillaPoseModelMatrix(ElementPose pose, out Matrixf matrix)
    {
        matrix = new Matrixf();
        if (pose.AnimModelMatrix == null || pose.AnimModelMatrix.Length < 16) return false;

        matrix.Identity();
        matrix.Mul(pose.AnimModelMatrix);
        return true;
    }

    private static bool TryGetVanillaDistalEndpointModel(ElementPose lowerPose, Vec3d jointOrigin, out Vec3d endpoint)
    {
        endpoint = jointOrigin;
        if (lowerPose.ForElement == null) return false;
        if (!TryBuildVanillaPoseModelMatrix(lowerPose, out Matrixf matrix)) return false;

        Vec3f[] localCorners = GetElementLocalBoxCorners(lowerPose.ForElement);
        double best = -1;
        foreach (Vec3f local in localCorners)
        {
            Vec4f transformed = matrix.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
            Vec3d model = new(transformed.X, transformed.Y, transformed.Z);
            double distance = Sub(model, jointOrigin).LengthSq();
            if (distance <= best) continue;

            best = distance;
            endpoint = model;
        }

        return best > 0.000001;
    }

    private static bool TryFindShapeElementPath(Shape? shape, string elementName, out List<ShapeElement> path)
    {
        path = [];
        if (shape?.Elements == null || string.IsNullOrWhiteSpace(elementName)) return false;

        foreach (ShapeElement root in shape.Elements)
        {
            if (TryFindShapeElementPathRecursive(root, elementName, path)) return true;
        }

        path.Clear();
        return false;
    }

    private static bool TryFindShapeElementPathRecursive(ShapeElement current, string elementName, List<ShapeElement> path)
    {
        path.Add(current);
        if (string.Equals(current.Name, elementName, StringComparison.OrdinalIgnoreCase)) return true;

        if (current.Children != null)
        {
            foreach (ShapeElement child in current.Children)
            {
                if (TryFindShapeElementPathRecursive(child, elementName, path)) return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }
}
