using ImGuiNET;
using System.Text;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;
using VanillaAnimation = Vintagestory.API.Common.Animation;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    // Procedural animation generator: a parameter-rich tool (like the model editor's Prism helper / Creature
    // generator) that builds a keyframed animation for the current shape. Two modes share one sampler:
    //   - Oscillation: generic per-element sine/wave channels on any rotation/offset/stretch axis.
    //   - Locomotion: gait presets that classify joints (legs/arms/tail/spine/head/wings) by name and emit the
    //     same wave channels with semantically-chosen axes and phases.
    // Both expand to a flat list of per-element channels, then sample evenly across the loop into keyframes.
    private enum VanillaGenMode { Oscillation, Locomotion, Pose, Overlay }
    private enum VanillaGenTargetScope { All, NameFilter, SelectedSubtree }
    // Sine..Noise are the oscillation waveforms (Noise is a seamless seeded pseudo-noise for idle fidgets);
    // Stance and SwingBump are the duty-shaped gait curves the locomotion engine uses (a foot that dwells in
    // stance then swings, and a flex that fires only during swing).
    private enum VanillaGenWave { Sine, Cosine, Triangle, Sawtooth, Square, Noise, Stance, SwingBump }
    private enum VanillaGenChannelTarget { RotationX, RotationY, RotationZ, OffsetX, OffsetY, OffsetZ, StretchX, StretchY, StretchZ }
    private enum VanillaGenGait { Walk, Trot, Gallop, Idle, Swim, Fly, Pace, Bound, Stalk, Crawl, Climb, Charge }
    // Pose actions: semantic one-shot or held poses applied through the rig (no periodic motion).
    private enum VanillaGenAction
    {
        Sit, Lie, Sleep, Crouch, Rear, Beg, PlayBow, Stretch, Pounce, Eat, Graze,
        LookUp, LookDown, LookLeft, LookRight, Cower, Collapse, Flinch,
        // Vocalizations (Task 22), expanded attacks (Task 26), foraging/grooming (Task 29), death/wounded (Task 27).
        Howl, Roar, Call, Bite, Swipe, Kick, Gore, Stomp, TailWhip,
        Sniff, Peck, Dig, Lick, Scratch, Loaf, Drink, TrunkCurl, Death, WoundedRest
    }
    // How the in/out transition of a one-shot pose is shaped (Task 30).
    private enum VanillaGenPoseEnvelope { Smooth, Anticipate, Overshoot, Bounce }
    // Single-frame additive steering / pitch / bank overlays (Task 5).
    private enum VanillaGenOverlay { TurnLeft, TurnRight, PitchUp, PitchDown, BankLeft, BankRight, LeanLeft, LeanRight }
    // Mirror of EnumAnimationBlendMode for the meta-snippet export (Task 6); Auto picks a sensible default per mode.
    private enum VanillaGenBlend { Auto, Add, Average, AddAverage }
    // End-of-animation / activity-stopped handling (Task 4); Auto derives from the mode + Loop flag.
    private enum VanillaGenEndHandling { Auto, Repeat, Stop, Hold, EaseOut }
    private enum VanillaGenStopHandling { Auto, EaseOut, Rewind, Stop, PlayTillEnd }

    private static readonly string[] VanillaGenModeLabels = ["Oscillation", "Locomotion", "Pose / action", "Additive overlay"];
    private static readonly string[] VanillaGenScopeLabels = ["All elements", "Name filter", "Selected + subtree"];
    private static readonly string[] VanillaGenWaveLabels = ["Sine", "Cosine", "Triangle", "Sawtooth", "Square", "Noise"];
    private static readonly string[] VanillaGenChannelTargetLabels =
        ["Rotation X", "Rotation Y", "Rotation Z", "Offset X", "Offset Y", "Offset Z", "Stretch X", "Stretch Y", "Stretch Z"];
    private static readonly string[] VanillaGenGaitLabels = ["Walk", "Trot", "Gallop", "Idle", "Swim", "Fly", "Pace", "Bound", "Stalk", "Crawl", "Climb", "Charge"];
    private static readonly string[] VanillaGenActionLabels =
        ["Sit", "Lie down", "Sleep", "Crouch", "Rear up", "Beg", "Play bow", "Stretch", "Pounce / attack", "Eat", "Graze",
         "Look up", "Look down", "Look left", "Look right", "Cower", "Collapse / die", "Flinch / hurt",
         "Howl", "Roar", "Call / vocalize", "Bite", "Swipe / slap", "Kick", "Gore / headbutt", "Stomp", "Tail whip",
         "Sniff", "Peck", "Dig / forage", "Lick / groom", "Scratch", "Loaf", "Drink", "Trunk curl", "Death", "Wounded rest"];
    private static readonly string[] VanillaGenPoseEnvelopeLabels = ["Smooth", "Anticipate", "Overshoot", "Bounce / settle"];
    private static readonly string[] VanillaGenOverlayLabels =
        ["Turn left", "Turn right", "Pitch up", "Pitch down", "Bank left", "Bank right", "Lean left", "Lean right"];
    private static readonly string[] VanillaGenBlendLabels = ["Auto (by mode)", "Add", "Average", "AddAverage"];
    private static readonly string[] VanillaGenEndHandlingLabels = ["Auto", "Repeat", "Stop", "Hold last", "Ease out"];
    private static readonly string[] VanillaGenStopHandlingLabels = ["Auto", "Ease out", "Rewind", "Stop", "Play till end"];

    private sealed class VanillaGenChannel
    {
        public bool Enabled = true;
        public VanillaGenChannelTarget Target = VanillaGenChannelTarget.RotationZ;
        public VanillaGenWave Wave = VanillaGenWave.Sine;
        public float Amplitude = 15f;
        public int Frequency = 1;
        public float Phase;
        public float Bias;
    }

    private sealed class VanillaGenParams
    {
        public VanillaGenMode Mode = VanillaGenMode.Oscillation;
        public string Code = "gen-anim";
        public string Name = "";
        public int Frames = 30;
        public int SampleCount = 12;
        public bool Loop = true;
        public bool EaseSpeed = true;
        public bool OverwriteSelected;

        public VanillaGenTargetScope Scope = VanillaGenTargetScope.All;
        public string IncludeFilter = "";
        public string ExcludeFilter = "";

        // Oscillation
        public List<VanillaGenChannel> Channels = [new VanillaGenChannel()];
        public float PhasePerElement;
        public float SymmetryPhase = 180f;

        // Locomotion
        public VanillaGenGait Gait = VanillaGenGait.Walk;
        public float LegStride = 32f;
        public float StanceRatio = 0.62f;
        public float KneeFlex = 22f;
        public float AnkleBend = 10f;
        public bool KneeFlip;
        public float ArmSwing = 24f;
        public float BodyBob = 0.7f;
        public float BodyRoll = 4f;
        public float SpineBend = 8f;
        public float TailSway = 14f;
        public float TailWave = 45f;
        public float HeadBob = 4f;
        public float WingFlap;          // 0 by default: wings only flap on a flight gait (or when set by hand)
        public int WingBeats = 1;
        public float Asymmetry = 0.12f;

        // ---- Advanced (defaults are no-ops, so basic mode is byte-identical) ----

        // Global
        public float Intensity = 1f;        // master multiplier on every channel's amplitude
        public float GlobalPhase;           // deg, shifts the whole loop in time
        public bool Reverse;                // play the loop backwards
        public int RandomSeed = 1;          // seed for the oscillation jitter

        // Oscillation extras
        public float Jitter;                // 0..1 random phase per element (organic desync)
        public float AmplitudeJitter;       // 0..1 random amplitude variance per element
        public float AmplitudeGradient;     // -1..1 amplitude ramp across targets (base -> tip emphasis)
        public float Sharpness = 1f;        // waveform shaping: >1 snappier, <1 softer

        // Locomotion extras
        public float FootLift;              // vertical toe lift during the swing (1/16 units)
        public float BodyPitch;             // fore/aft rock of the torso (deg)
        public float BodySway;              // lateral body sway / waddle (1/16 units)
        public float HeadYaw;               // head looks left/right as it moves (deg)
        public float HeadStabilize;         // 0..1 gaze stabilization: damp the head bob and counter the body's pitch/roll so the head holds level
        public float NeckBob;               // slow neck nod (deg)
        public float Breathing;             // torso breathing pulse (StretchY fraction)
        public int BreathRate = 1;          // breaths per loop
        public float EarFlop;               // ear / antenna flap (deg)

        // Locomotion extras (mod-grade realism; defaults are no-ops)
        public float BodySurge;             // Task 7: fore-aft body lunge (Origin OffsetX, 1/16 units) for gallop/bound
        public float SpineFlex;             // Task 8: sagittal back arch/extend (rotationZ) for asymmetric gaits
        public float FootReach;             // Task 10: fore-aft foot reach during swing (1/16 units)
        public float TailBob;               // Task 12: vertical (rotationZ) component mixed into the tail wave (deg)
        public float TailTaper;             // Task 12: 0..1 amplitude decay toward the tail tip
        public bool Backward;               // Task 16: step backwards (stride/surge reversed, foot lift kept correct)
        public float EarsBack;              // Task 11: pin ears back (deg bias) - aggressive/running mood
        public float MouthOpen;             // Task 11: hold the jaw open (deg bias) - pant/snarl
        public float TailSet;               // Task 11: tail carriage bias (deg; + raised, - tucked)
        public float BodyTilt;              // Tasks 13/14: static torso pitch (deg; + nose-up climb, - nose-down charge)
        public float LegTuck;               // Task 20: fold the legs up during flight (0..1)
        public float FlightBob;             // Task 21: vertical body bob synced to the wingbeat (1/16 units)
        public float NeckCurve;             // Task 21: hold the neck in an S-curve (deg per segment, alternating)
        public float WingChainLag;          // Task 17: phase lag (deg) added per wing segment outward (tip billow)
        public float SecondaryJiggle;       // Task 34: passive lagged wobble on loose elements (crest/fur/dewlap...)
        public float Squash;                // Task 33: 0..1 squash&stretch coupled to the body bob low point

        // ---- Cutting-edge: physics-based secondary motion + a biomechanical speed model (defaults are no-ops) ----
        public bool JigglePhysics;          // spring-damper follow-through on loose parts (tail/ear/crest/fin/...)
        public float Floppiness = 0.5f;     // 0 = stiff & snappy, 1 = floppy & laggy (lower natural frequency)
        public float JiggleBounce = 0.35f;  // 0 = critically damped (no overshoot), 1 = springy overshoot
        public float Speed = 1f;            // biomechanical speed: coherently scales cadence/stride/duty/bob (1 = preset baseline)

        public bool FootLock;               // ground-contact IK: the stance foot stays planted (no foot-slide)
        public float FootLockHeight = 0.92f;// rest foot drop as a fraction of leg length (slightly bent so the IK is solvable)
        public float FootLockReach = 1f;    // multiplier on the planted-foot fore-aft travel

        // Runtime-only (not a user parameter, not serialized): the element->parent map for coupled-chain jiggle
        // physics, populated from the shape when the document is available.
        public Dictionary<string, string>? HierarchyParents;

        // Pose / action
        public VanillaGenAction Action = VanillaGenAction.Sit;
        public float PoseStrength = 1f;     // scales the whole pose
        public bool ReturnToRest;           // off = hold the pose (sit/lie); on = rest -> pose -> rest (one-shot)
        public bool PoseEase = true;        // smoothstep the transition
        public float PoseHold = 0.4f;       // fraction of the loop spent at the held pose (ReturnToRest only)
        public float PoseSettle;            // residual idle sway/breath layered on the held pose (0..1)
        public VanillaGenPoseEnvelope PoseEnvelope = VanillaGenPoseEnvelope.Smooth; // Task 30
        public bool WeightShift;            // Task 31: counter-shift the body toward the support side for one-limb gestures
        public bool PoseTransition;         // Task 28: build From->To instead of Rest->To
        public VanillaGenAction FromAction = VanillaGenAction.Sit; // Task 28: the starting pose for a transition

        // Overlay (Task 5)
        public VanillaGenOverlay Overlay = VanillaGenOverlay.TurnLeft;
        public float OverlayAmount = 20f;   // total bend distributed down the spine/neck/tail chain (deg)

        // Output handling (Tasks 3/4/35) and meta export (Tasks 6/32)
        public bool AutoRotShortest = true; // Task 3: flag rotation channels that span >180 deg for shortest-path lerp
        public bool OptimizeKeyFrames;      // Tasks 1/2: prune collinear / redundant per-channel keyframes
        public float OptimizeTolerance = 0.25f;
        public VanillaGenEndHandling OnEnd = VanillaGenEndHandling.Auto;
        public VanillaGenStopHandling OnStop = VanillaGenStopHandling.Auto;
        public VanillaGenBlend MetaBlend = VanillaGenBlend.Auto;
        public float MetaWeight = 1f;
        public float MetaEaseIn = 10f;
        public float MetaEaseOut = 10f;
        public float MetaAnimSpeed = 1f;
        public bool MetaMulWalkSpeed;
        public bool MetaSupressDefault;
        public string MetaTrigger = "";     // e.g. "walk", "dead", or "default"
        public bool EmitFootstepSounds;     // Task 32: footstep AnimationSounds at the computed plant frames
        public string FootstepSound = "game:creature/wolf/footsteps/dirt/footstep-wolf-dirt*";
    }

    private readonly record struct VanillaGenElementChannel(
        string Element, VanillaGenChannelTarget Field, VanillaGenWave Wave, double Amplitude, int Frequency, double PhaseDeg, double Bias, double Shape = 0.0, double Sharpness = 1.0)
    {
        // When set, this channel's value is this function of the cyclic position (used by the foot-lock IK to
        // emit per-phase joint angles); the wave/amplitude/bias/intensity path is bypassed.
        public System.Func<double, double>? Curve { get; init; }
    }

    private bool _vanillaAnimationGeneratorWindowOpen;
    private bool _vanillaGenAdvanced;
    private bool _vanillaGenLiveUpdate = true;
    private bool _vanillaGenApplyingLiveUpdate;
    private string _vanillaGenLiveFingerprint = "";
    private readonly VanillaGenParams _vanillaGenParams = new();
    private string _vanillaGenLastAnimationCode = "";

    // ---- Floating overlay --------------------------------------------------

    private void DrawVanillaAnimationGeneratorPanel()
    {
        if (!_vanillaAnimationGeneratorWindowOpen) return;

        bool open = true;
        if (BeginDevToolsFloatingTool("Procedural animation generator###vanilla-gen-overlay", ref open, new NVector2(480f, 600f)))
        {
            VanillaAnimationDocument? document = GetVanillaTargetShapeDocument();
            if (document?.Shape == null)
            {
                ImGui.TextDisabled("Select or load a shape (Shapes tab) to generate animations for it.");
            }
            else
            {
                DrawVanillaAnimationGeneratorBody(document);
            }
        }
        ImGui.End();

        if (!open)
        {
            _vanillaAnimationGeneratorWindowOpen = false;
        }
    }

    private void DrawVanillaAnimationGeneratorBody(VanillaAnimationDocument document)
    {
        VanillaGenParams p = _vanillaGenParams;

        int modeIndex = (int)p.Mode;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Mode##vanilla-gen-mode", ref modeIndex, VanillaGenModeLabels, VanillaGenModeLabels.Length))
        {
            p.Mode = (VanillaGenMode)modeIndex;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Advanced##vanilla-gen-advanced", ref _vanillaGenAdvanced);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reveal the full set of fine-tuning parameters (intensity, phase, jitter, waveform sharpness, " +
                "and locomotion secondary motion). Off keeps only the essentials.");
        }

        DrawVanillaGenGlobalControls(p);
        ImGui.Separator();
        DrawVanillaGenTargetingControls(p, document);
        ImGui.Separator();
        switch (p.Mode)
        {
            case VanillaGenMode.Oscillation:
                DrawVanillaGenOscillationControls(p);
                break;
            case VanillaGenMode.Locomotion:
                DrawVanillaGenLocomotionControls(p);
                break;
            case VanillaGenMode.Overlay:
                DrawVanillaGenOverlayControls(p);
                break;
            default:
                DrawVanillaGenPoseControls(p);
                break;
        }

        ImGui.Separator();
        DrawVanillaGenOutputControls(p);
        ImGui.Separator();
        int targetCount = ResolveVanillaGenTargets(document, p).Count;
        ImGui.TextUnformatted($"{targetCount} target element(s) matched.");
        if (targetCount == 0)
        {
            ImGui.TextColored(new NVector4(1f, 0.72f, 0.32f, 1f), "Adjust targeting - nothing matched.");
        }

        bool canGenerate = targetCount > 0;
        if (!canGenerate) ImGui.BeginDisabled();
        if (ImGui.Button("Generate##vanilla-gen-create"))
        {
            GenerateVanillaGeneratedAnimation(document, regenerateInPlace: false);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(p.OverwriteSelected
                ? "Overwrite the selected animation's keyframes (or create a new one if none is selected)."
                : "Create a new animation, select it and play it in the preview.");
        }
        ImGui.SameLine();
        bool canRegen = !string.IsNullOrWhiteSpace(_vanillaGenLastAnimationCode);
        if (!canRegen) ImGui.BeginDisabled();
        if (ImGui.Button("Regenerate##vanilla-gen-regen"))
        {
            GenerateVanillaGeneratedAnimation(document, regenerateInPlace: true);
        }
        if (!canRegen) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Rewrite the last generated animation ('{_vanillaGenLastAnimationCode}') in place with the current parameters.");
        }
        if (!canGenerate) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.Checkbox("Live update while playing##vanilla-gen-live", ref _vanillaGenLiveUpdate);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When the last generated animation is selected and playing, slider edits rewrite it in place without rebuilding the preview mesh.");
        }

        UpdateVanillaGenLivePreview(document, p, canGenerate, targetCount);

        if (!string.IsNullOrWhiteSpace(_vanillaStatus))
        {
            ImGui.TextWrapped(_vanillaStatus);
        }
    }

    private void DrawVanillaGenGlobalControls(VanillaGenParams p)
    {
        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("Code##vanilla-gen-code", ref p.Code, 120);
        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("Name##vanilla-gen-name", "(defaults to code)", ref p.Name, 120);

        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Frames##vanilla-gen-frames", ref p.Frames))
        {
            p.Frames = Math.Clamp(p.Frames, 1, 10000);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderInt("Keyframes##vanilla-gen-samples", ref p.SampleCount, 2, 60))
        {
            p.SampleCount = Math.Clamp(p.SampleCount, 2, 240);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("How many keyframes to sample across the loop. More = smoother curves (the game interpolates linearly between keyframes).");
        }

        ImGui.Checkbox("Loop##vanilla-gen-loop", ref p.Loop);
        ImGui.SameLine();
        ImGui.Checkbox("Ease speed##vanilla-gen-ease", ref p.EaseSpeed);
        ImGui.SameLine();
        ImGui.Checkbox("Overwrite selected##vanilla-gen-overwrite", ref p.OverwriteSelected);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("On: Generate rewrites the currently selected animation. Off: Generate spawns a new animation.");
        }

        ImGui.SetNextItemWidth(150f);
        ImGui.SliderFloat("Intensity##vanilla-gen-intensity", ref p.Intensity, 0f, 3f, "x%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Master multiplier on every channel's amplitude (a quick 'energy' dial). 1 = as authored.");

        if (_vanillaGenAdvanced)
        {
            ImGui.SetNextItemWidth(150f);
            ImGui.DragFloat("Global phase##vanilla-gen-gphase", ref p.GlobalPhase, 1f, -360f, 360f, "%.0f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shift the whole loop in time. Handy for offsetting a copy of an animation.");
            ImGui.SameLine();
            ImGui.Checkbox("Reverse##vanilla-gen-reverse", ref p.Reverse);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play the generated loop backwards.");
            ImGui.SetNextItemWidth(120f);
            ImGui.InputInt("Random seed##vanilla-gen-seed", ref p.RandomSeed);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Seed for the oscillation jitter. A given seed always produces the same result.");
        }
    }

    private void DrawVanillaGenTargetingControls(VanillaGenParams p, VanillaAnimationDocument document)
    {
        ImGui.SeparatorText("Targets");
        int scopeIndex = (int)p.Scope;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Scope##vanilla-gen-scope", ref scopeIndex, VanillaGenScopeLabels, VanillaGenScopeLabels.Length))
        {
            p.Scope = (VanillaGenTargetScope)scopeIndex;
        }

        if (p.Scope == VanillaGenTargetScope.SelectedSubtree)
        {
            string selected = string.IsNullOrWhiteSpace(_vanillaSelection.ElementName) ? "(none selected)" : _vanillaSelection.ElementName;
            ImGui.TextDisabled($"Selected element: {selected}");
        }

        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("Include##vanilla-gen-include", "e.g. leg*, tail*", ref p.IncludeFilter, 160);
        ImGui.SetNextItemWidth(180f);
        ImGui.InputTextWithHint("Exclude##vanilla-gen-exclude", "e.g. *tip", ref p.ExcludeFilter, 160);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Comma-separated patterns. '*' and '?' are wildcards; plain text matches as a substring.");
        }
    }

    private void DrawVanillaGenOscillationControls(VanillaGenParams p)
    {
        ImGui.SeparatorText("Wave channels");
        for (int i = 0; i < p.Channels.Count; i++)
        {
            VanillaGenChannel channel = p.Channels[i];
            ImGui.PushID(i);
            ImGui.Checkbox("##vanilla-gen-ch-enabled", ref channel.Enabled);
            ImGui.SameLine();
            int targetIndex = (int)channel.Target;
            ImGui.SetNextItemWidth(96f);
            if (ImGui.Combo("##vanilla-gen-ch-target", ref targetIndex, VanillaGenChannelTargetLabels, VanillaGenChannelTargetLabels.Length))
            {
                channel.Target = (VanillaGenChannelTarget)targetIndex;
            }
            ImGui.SameLine();
            int waveIndex = (int)channel.Wave;
            ImGui.SetNextItemWidth(84f);
            if (ImGui.Combo("##vanilla-gen-ch-wave", ref waveIndex, VanillaGenWaveLabels, VanillaGenWaveLabels.Length))
            {
                channel.Wave = (VanillaGenWave)waveIndex;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("x##vanilla-gen-ch-remove") && p.Channels.Count > 1)
            {
                p.Channels.RemoveAt(i);
                ImGui.PopID();
                i--;
                continue;
            }

            ImGui.SetNextItemWidth(96f);
            ImGui.DragFloat("Amp##vanilla-gen-ch-amp", ref channel.Amplitude, 0.5f, -360f, 360f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            if (ImGui.InputInt("Cyc##vanilla-gen-ch-freq", ref channel.Frequency))
            {
                channel.Frequency = Math.Clamp(channel.Frequency, 1, 64);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Cycles per loop (integer keeps the loop seamless).");
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(96f);
            ImGui.DragFloat("Phase##vanilla-gen-ch-phase", ref channel.Phase, 1f, -360f, 360f, "%.0f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.DragFloat("Bias##vanilla-gen-ch-bias", ref channel.Bias, 0.25f, -360f, 360f, "%.2f");

            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.Button("Add channel##vanilla-gen-ch-add") && p.Channels.Count < 8)
        {
            p.Channels.Add(new VanillaGenChannel());
        }

        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("Phase per element##vanilla-gen-phasespread", ref p.PhasePerElement, 1f, -180f, 180f, "%.0f deg");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Extra phase added per target (in tree order) - makes travelling waves down a chain like a tail or spine.");
        }
        ImGui.SetNextItemWidth(150f);
        ImGui.DragFloat("Symmetry phase##vanilla-gen-symphase", ref p.SymmetryPhase, 1f, -180f, 180f, "%.0f deg");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Phase added to right-side elements (auto-detected) so left/right limbs move out of phase.");
        }

        if (_vanillaGenAdvanced)
        {
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("Amplitude ramp##vanilla-gen-ampgrad", ref p.AmplitudeGradient, -1f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scales amplitude across the targets in tree order. Positive emphasises the later elements (tips), negative the roots.");
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("Phase jitter##vanilla-gen-jitter", ref p.Jitter, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Random per-element phase offset (uses the seed above) for organic, non-uniform motion.");
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("Amplitude jitter##vanilla-gen-ampjit", ref p.AmplitudeJitter, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Random per-element amplitude variance so elements don't all move by exactly the same amount.");
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("Sharpness##vanilla-gen-sharpness", ref p.Sharpness, 0.2f, 5f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waveform shaping. >1 snaps toward the extremes (punchier), <1 rounds it off (gentler). 1 = unchanged.");
        }
    }

    private void DrawVanillaGenLocomotionControls(VanillaGenParams p)
    {
        ImGui.SeparatorText("Gait");
        int gaitIndex = (int)p.Gait;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("Gait##vanilla-gen-gait", ref gaitIndex, VanillaGenGaitLabels, VanillaGenGaitLabels.Length))
        {
            p.Gait = (VanillaGenGait)gaitIndex;
            ApplyVanillaGaitPreset(p);
        }
        ImGui.SameLine();
        if (ImGui.Button("Apply preset##vanilla-gen-gait-apply"))
        {
            ApplyVanillaGaitPreset(p);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Reset the locomotion parameters to sensible defaults for this gait.");
        }

        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat("Speed##vanilla-gen-speed", ref p.Speed, 0.3f, 3f, "%.2fx") && p.Speed > 0f)
        {
            ApplyVanillaGaitPreset(p);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Biomechanical speed: one knob rescales the gait coherently - faster takes a quicker cadence, a longer stride, a lower stance (duty) factor, a bigger bob and a forward lean. Re-applies the preset.");
        }

        ImGui.SeparatorText("Legs");
        ImGui.DragFloat("Stride##vanilla-gen-legstride", ref p.LegStride, 0.5f, 0f, 90f, "%.1f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fore-aft swing of the whole leg from the hip.");
        ImGui.SliderFloat("Stance ratio##vanilla-gen-stance", ref p.StanceRatio, 0.3f, 0.85f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fraction of the cycle the foot is planted (pushing back). Higher = more of a slow walk, less of a wave.");
        ImGui.DragFloat("Knee flex##vanilla-gen-kneeflex", ref p.KneeFlex, 0.5f, 0f, 90f, "%.1f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How much the knee bends during the swing only (not during stance). This is what lifts the foot.");
        ImGui.DragFloat("Ankle bend##vanilla-gen-anklebend", ref p.AnkleBend, 0.5f, 0f, 60f, "%.1f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Counter-bend of the foot/lower joints so the foot stays oriented through the step.");
        ImGui.Checkbox("Flip knee direction##vanilla-gen-kneeflip", ref p.KneeFlip);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flip the knee bend direction if it bends the wrong way for this rig.");

        ImGui.Checkbox("Foot lock (IK)##vanilla-gen-footlock", ref p.FootLock);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ground-contact inverse kinematics: solve the hip + knee so the stance foot stays planted (constant height, constant backward sweep) instead of arcing - zero foot-slide, the gold standard of procedural locomotion. Needs two-bone legs; tune with Flip knee. Best on legs that rest roughly straight down.");
        if (p.FootLock)
        {
            ImGui.SliderFloat("Foot height##vanilla-gen-footlockheight", ref p.FootLockHeight, 0.6f, 0.99f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Resting foot drop as a fraction of leg length (lower = more bent knees / crouch).");
            ImGui.SliderFloat("Foot reach##vanilla-gen-footlockreach", ref p.FootLockReach, 0.2f, 2f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("How far the planted foot travels fore-aft (scales the stride distance).");
        }

        ImGui.SeparatorText("Body");
        ImGui.DragFloat("Bob##vanilla-gen-bodybob", ref p.BodyBob, 0.05f, 0f, 8f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertical body bounce in 1/16-block units (twice per stride).");
        ImGui.DragFloat("Roll##vanilla-gen-bodyroll", ref p.BodyRoll, 0.5f, 0f, 45f, "%.1f deg");
        ImGui.DragFloat("Spine bend##vanilla-gen-spinebend", ref p.SpineBend, 0.5f, 0f, 60f, "%.1f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Horizontal coil travelling along the spine, so the torso is not bilaterally rigid.");
        ImGui.DragFloat("Head bob##vanilla-gen-headbob", ref p.HeadBob, 0.5f, 0f, 45f, "%.1f deg");

        ImGui.SeparatorText("Tail / arms / wings");
        ImGui.DragFloat("Arm swing##vanilla-gen-armswing", ref p.ArmSwing, 0.5f, 0f, 90f, "%.1f deg");
        ImGui.DragFloat("Tail sway##vanilla-gen-tailsway", ref p.TailSway, 0.5f, 0f, 90f, "%.1f deg");
        ImGui.DragFloat("Tail wave##vanilla-gen-tailwave", ref p.TailWave, 1f, 0f, 180f, "%.0f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Phase per tail segment - higher values give a more snake-like travelling wave.");
        ImGui.DragFloat("Wing flap##vanilla-gen-wingflap", ref p.WingFlap, 0.5f, 0f, 90f, "%.1f deg");
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Wing beats##vanilla-gen-wingbeats", ref p.WingBeats))
        {
            p.WingBeats = Math.Clamp(p.WingBeats, 1, 16);
        }

        if (_vanillaGenAdvanced)
        {
            ImGui.SeparatorText("Secondary motion");
            ImGui.DragFloat("Foot lift##vanilla-gen-footlift", ref p.FootLift, 0.05f, 0f, 12f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertical lift of the toe during the swing (1/16-block units) for clearer steps on top of the knee flex.");
            ImGui.DragFloat("Body pitch##vanilla-gen-bodypitch", ref p.BodyPitch, 0.5f, 0f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fore/aft rock of the torso each step.");
            ImGui.DragFloat("Body sway##vanilla-gen-bodysway", ref p.BodySway, 0.05f, 0f, 8f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lateral waddle of the torso (1/16-block units).");
            ImGui.DragFloat("Head yaw##vanilla-gen-headyaw", ref p.HeadYaw, 0.5f, 0f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Head turns left/right as it moves.");
            ImGui.SliderFloat("Gaze stabilize##vanilla-gen-headstab", ref p.HeadStabilize, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hold the head level: damps its own bob and counters the body's pitch/roll so the gaze stays steady (stalking, aiming, birds of prey). 0 = off.");
            ImGui.DragFloat("Neck bob##vanilla-gen-neckbob", ref p.NeckBob, 0.5f, 0f, 45f, "%.1f deg");
            ImGui.DragFloat("Ear flop##vanilla-gen-earflop", ref p.EarFlop, 0.5f, 0f, 60f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Floppy ears / antennae (any element named 'ear'), beating in sync.");
            ImGui.DragFloat("Breathing##vanilla-gen-breath", ref p.Breathing, 0.01f, 0f, 1f, "%.3f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Slow torso breathing pulse (vertical stretch). Great for idle gaits.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt("Rate##vanilla-gen-breathrate", ref p.BreathRate))
            {
                p.BreathRate = Math.Clamp(p.BreathRate, 1, 16);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Breaths per loop.");

            ImGui.SeparatorText("Body dynamics (mod-grade)");
            ImGui.DragFloat("Body surge##vanilla-gen-bodysurge", ref p.BodySurge, 0.1f, 0f, 12f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 7: fore-aft lunge of the whole body (1/16 units) - the gallop/bound leap.");
            ImGui.DragFloat("Spine flex##vanilla-gen-spineflex", ref p.SpineFlex, 0.5f, 0f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 8: sagittal back arch/extend (the gallop hump), distinct from the lateral spine bend.");
            ImGui.DragFloat("Foot reach##vanilla-gen-footreach", ref p.FootReach, 0.05f, 0f, 8f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 10: how far the foot reaches forward during the swing (1/16 units).");
            ImGui.DragFloat("Body tilt##vanilla-gen-bodytilt", ref p.BodyTilt, 0.5f, -45f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Static torso pitch: + nose-up (climb), - nose-down (charge/stalk).");
            ImGui.DragFloat("Squash##vanilla-gen-squash", ref p.Squash, 0.02f, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 33: vertical squash on the body-bob low point (weighty footfalls).");

            ImGui.SeparatorText("Tail / ears / mood");
            ImGui.DragFloat("Tail bob##vanilla-gen-tailbob", ref p.TailBob, 0.5f, 0f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 12: vertical (rotationZ) bounce mixed into the tail's lateral wave.");
            ImGui.SliderFloat("Tail taper##vanilla-gen-tailtaper", ref p.TailTaper, 0f, 0.95f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 12: how much the wave amplitude decays toward the tail tip.");
            ImGui.DragFloat("Tail set##vanilla-gen-tailset", ref p.TailSet, 0.5f, -60f, 60f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 11: tail carriage bias (+ raised, - tucked).");
            ImGui.DragFloat("Ears back##vanilla-gen-earsback", ref p.EarsBack, 0.5f, 0f, 90f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 11: pin the ears back (running / aggressive mood).");
            ImGui.DragFloat("Mouth open##vanilla-gen-mouthopen", ref p.MouthOpen, 0.5f, 0f, 70f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 11: hold the jaw open; large values add a pant cycle.");
            ImGui.DragFloat("Secondary jiggle##vanilla-gen-jiggle", ref p.SecondaryJiggle, 0.02f, 0f, 2f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 34: passive lagged wobble on loose elements (crest/fur/dewlap/wattle/feather/fin/mane).");

            ImGui.SeparatorText("Soft-body physics");
            ImGui.Checkbox("Jiggle physics##vanilla-gen-jigglephys", ref p.JigglePhysics);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Run a real damped-spring follow-through over the keyframes for loose parts (tail/ear/antenna/crest/fin/plume/floof). They lag, overshoot and settle like soft tissue instead of snapping to their keyed pose. Works in any mode.");
            if (p.JigglePhysics)
            {
                ImGui.SliderFloat("Floppiness##vanilla-gen-floppy", ref p.Floppiness, 0f, 1f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Soft tissue mass/looseness: 0 = stiff and snappy, 1 = floppy with a long lag.");
                ImGui.SliderFloat("Bounce##vanilla-gen-jigglebounce", ref p.JiggleBounce, 0f, 1f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Damping: 0 = settles cleanly with no overshoot, 1 = springy, overshoots and wobbles before settling.");
            }

            ImGui.SeparatorText("Flight");
            ImGui.SliderFloat("Leg tuck##vanilla-gen-legtuck", ref p.LegTuck, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 20: fold the legs up and hold them (used in flight).");
            ImGui.DragFloat("Flight bob##vanilla-gen-flightbob", ref p.FlightBob, 0.1f, 0f, 8f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 21: vertical body bob synced to the wingbeat (1/16 units).");
            ImGui.DragFloat("Neck curve##vanilla-gen-neckcurve", ref p.NeckCurve, 0.5f, 0f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 21: hold a multi-segment neck in an S-curve (alternating per segment).");
            ImGui.DragFloat("Wing chain lag##vanilla-gen-wingchainlag", ref p.WingChainLag, 1f, 0f, 120f, "%.0f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 17: phase lag added per wing segment outward, so the membrane billows and follows through.");
        }

        ImGui.SeparatorText("Variation");
        ImGui.SliderFloat("Asymmetry##vanilla-gen-asym", ref p.Asymmetry, 0f, 1f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tiny left/right timing variation so the two sides are never a perfect mirror.");
        ImGui.SameLine();
        ImGui.Checkbox("Backward##vanilla-gen-backward", ref p.Backward);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 16: step backward - the stride reverses but the foot still lifts correctly through the swing.");
    }

    private void DrawVanillaGenPoseControls(VanillaGenParams p)
    {
        ImGui.SeparatorText("Pose / action");
        int actionIndex = (int)p.Action;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Action##vanilla-gen-action", ref actionIndex, VanillaGenActionLabels, VanillaGenActionLabels.Length))
        {
            p.Action = (VanillaGenAction)actionIndex;
            p.ReturnToRest = VanillaActionIsTransient(p.Action); // sensible default: gestures return, postures hold
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Builds a semantic pose by folding/raising the legs, spine, head, tail and jaw through the rig. Works on any entity whose parts are named conventionally (leg/arm/haunch/spine/head/neck/tail/jaw).");

        ImGui.SetNextItemWidth(200f);
        ImGui.SliderFloat("Strength##vanilla-gen-posestrength", ref p.PoseStrength, 0f, 1.5f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scales the whole pose. 1 = full, lower for a subtler version.");

        ImGui.Checkbox("Return to rest##vanilla-gen-poseret", ref p.ReturnToRest);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("On: a one-shot gesture (rest -> pose -> rest), e.g. pounce/flinch. Off: hold the pose, e.g. sit/lie/sleep.");
        ImGui.SameLine();
        ImGui.Checkbox("Ease##vanilla-gen-poseease", ref p.PoseEase);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Smoothstep the in/out transition instead of a linear ramp.");

        // Task 28: build a transition between two poses instead of rest -> pose -> rest.
        ImGui.Checkbox("Transition from##vanilla-gen-posetrans", ref p.PoseTransition);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 28: animate FROM another pose into this action (e.g. downed -> stand) instead of from rest.");
        if (p.PoseTransition)
        {
            ImGui.SameLine();
            int fromIndex = (int)p.FromAction;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.Combo("##vanilla-gen-fromaction", ref fromIndex, VanillaGenActionLabels, VanillaGenActionLabels.Length))
            {
                p.FromAction = (VanillaGenAction)fromIndex;
            }
        }

        if (_vanillaGenAdvanced)
        {
            int envIndex = (int)p.PoseEnvelope;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Envelope##vanilla-gen-poseenv", ref envIndex, VanillaGenPoseEnvelopeLabels, VanillaGenPoseEnvelopeLabels.Length))
            {
                p.PoseEnvelope = (VanillaGenPoseEnvelope)envIndex;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 30: shape the one-shot transition - anticipate (wind back first), overshoot (past the target then settle), or bounce.");
            ImGui.Checkbox("Weight shift##vanilla-gen-weightshift", ref p.WeightShift);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 31: shift the body onto the support side when a single limb is lifted (scratch/beg/howl).");
        }

        if (p.ReturnToRest)
        {
            ImGui.SetNextItemWidth(200f);
            ImGui.SliderFloat("Hold##vanilla-gen-posehold", ref p.PoseHold, 0f, 0.9f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fraction of the loop spent holding the pose at full before it returns to rest.");
        }
        else
        {
            ImGui.SetNextItemWidth(200f);
            ImGui.SliderFloat("Settle / idle##vanilla-gen-posesettle", ref p.PoseSettle, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds a faint breathing pulse to the held pose so it isn't perfectly frozen.");
        }
    }

    private void DrawVanillaGenOverlayControls(VanillaGenParams p)
    {
        ImGui.SeparatorText("Additive overlay (Task 5)");
        ImGui.TextWrapped("Builds a 1-frame pose that bends only the spine/neck/tail. Blend it additively over a gait " +
            "(steering / pitch / bank). Use 'Copy entity meta' below for the matching blend wiring.");
        int overlayIndex = (int)p.Overlay;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Overlay##vanilla-gen-overlay", ref overlayIndex, VanillaGenOverlayLabels, VanillaGenOverlayLabels.Length))
        {
            p.Overlay = (VanillaGenOverlay)overlayIndex;
        }
        ImGui.SetNextItemWidth(200f);
        ImGui.DragFloat("Amount##vanilla-gen-overlayamt", ref p.OverlayAmount, 0.5f, 0f, 90f, "%.1f deg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Total bend distributed down the chain.");
    }

    // Output handling (Tasks 1/2/3/4) and the entity-meta snippet (Tasks 6/32).
    private void DrawVanillaGenOutputControls(VanillaGenParams p)
    {
        if (!_vanillaGenAdvanced) return;

        ImGui.SeparatorText("Output");
        int endIndex = (int)p.OnEnd;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("On end##vanilla-gen-onend", ref endIndex, VanillaGenEndHandlingLabels, VanillaGenEndHandlingLabels.Length))
        {
            p.OnEnd = (VanillaGenEndHandling)endIndex;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 4: what happens at the last frame. Auto: loop cycles, stop gestures, hold death.");
        ImGui.SameLine();
        int stopIndex = (int)p.OnStop;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("On stopped##vanilla-gen-onstop", ref stopIndex, VanillaGenStopHandlingLabels, VanillaGenStopHandlingLabels.Length))
        {
            p.OnStop = (VanillaGenStopHandling)stopIndex;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 4: what happens when the activity stops. Auto eases cycles out and plays gestures to the end.");

        ImGui.Checkbox("Shortest-path rotation##vanilla-gen-rotshortest", ref p.AutoRotShortest);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 3: flag rotation channels that span >180 deg so the game lerps the short way (no backwards spins).");
        ImGui.SameLine();
        ImGui.Checkbox("Optimize keyframes##vanilla-gen-optimize", ref p.OptimizeKeyFrames);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tasks 1/2: drop redundant per-channel keyframe values that lie on a straight line (smaller, sparser output).");
        if (p.OptimizeKeyFrames)
        {
            ImGui.SetNextItemWidth(150f);
            ImGui.SliderFloat("Tolerance##vanilla-gen-opttol", ref p.OptimizeTolerance, 0.01f, 2f, "%.2f");
        }

        if (ImGui.TreeNode("Entity meta snippet##vanilla-gen-meta"))
        {
            int blendIndex = (int)p.MetaBlend;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.Combo("Blend mode##vanilla-gen-metablend", ref blendIndex, VanillaGenBlendLabels, VanillaGenBlendLabels.Length))
            {
                p.MetaBlend = (VanillaGenBlend)blendIndex;
            }
            ImGui.SetNextItemWidth(120f);
            ImGui.DragFloat("Weight##vanilla-gen-metaweight", ref p.MetaWeight, 0.1f, 0f, 100f, "%.2f");
            ImGui.SetNextItemWidth(120f);
            ImGui.DragFloat("Ease in##vanilla-gen-metaeasein", ref p.MetaEaseIn, 0.5f, 0f, 100f, "%.1f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ImGui.DragFloat("Ease out##vanilla-gen-metaeaseout", ref p.MetaEaseOut, 0.5f, 0f, 100f, "%.1f");
            ImGui.SetNextItemWidth(120f);
            ImGui.DragFloat("Anim speed##vanilla-gen-metaspeed", ref p.MetaAnimSpeed, 0.05f, 0f, 8f, "%.2f");
            ImGui.SameLine();
            ImGui.Checkbox("x walk speed##vanilla-gen-metamulwalk", ref p.MetaMulWalkSpeed);
            ImGui.Checkbox("Supress default##vanilla-gen-metasupress", ref p.MetaSupressDefault);
            ImGui.SetNextItemWidth(150f);
            ImGui.InputTextWithHint("Trigger##vanilla-gen-metatrigger", "walk / dead / default", ref p.MetaTrigger, 64);
            ImGui.Checkbox("Footstep sounds##vanilla-gen-metafoot", ref p.EmitFootstepSounds);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Task 32: emit AnimationSounds at the computed foot-plant frames.");
            if (p.EmitFootstepSounds)
            {
                ImGui.SetNextItemWidth(280f);
                ImGui.InputText("Sound##vanilla-gen-footsound", ref p.FootstepSound, 160);
            }
            if (ImGui.Button("Copy entity meta##vanilla-gen-metacopy"))
            {
                ImGui.SetClipboardText(BuildVanillaMetaSnippet(GetVanillaTargetShapeDocument(), p));
                _vanillaStatus = "Animation generator: entity meta snippet copied to clipboard.";
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy a ready-to-paste AnimationMetaData block for the entity JSON.");
            ImGui.TreePop();
        }
    }

    private static bool VanillaActionIsTransient(VanillaGenAction action) => action switch
    {
        VanillaGenAction.Pounce or VanillaGenAction.Flinch or VanillaGenAction.Stretch or VanillaGenAction.PlayBow
            or VanillaGenAction.Bite or VanillaGenAction.Swipe or VanillaGenAction.Kick or VanillaGenAction.Gore
            or VanillaGenAction.Stomp or VanillaGenAction.TailWhip or VanillaGenAction.Peck or VanillaGenAction.Scratch
            or VanillaGenAction.Howl or VanillaGenAction.Roar or VanillaGenAction.Call or VanillaGenAction.Sniff
            or VanillaGenAction.Death => true,
        _ => false
    };

    private static void ApplyVanillaGaitPreset(VanillaGenParams p)
    {
        // Every preset starts the whole body STILL, then turns on only the motions that gait actually uses. So a
        // part never moves unless this gait asks it to - e.g. wings hold their rest pose on the ground and only
        // beat when the gait flies (set below in Fly). This is what stops walk/swim/stalk from flapping wings.
        p.KneeFlip = false;
        p.Asymmetry = 0.12f;
        p.TailWave = 45f;
        p.WingFlap = 0f;        // wings still unless the gait flies
        p.WingBeats = 1;
        p.WingChainLag = 0f;    // flight-only; reset so it never leaks onto a ground gait
        // Secondary-motion defaults; individual gaits tune them below.
        p.FootLift = 2f;
        p.BodyPitch = 2f;
        p.BodySway = 0f;
        p.HeadYaw = 0f;
        p.NeckBob = 0f;
        p.Breathing = 0f;
        p.BreathRate = 1;
        p.EarFlop = 0f;
        // Mod-grade extras reset to no-op; gaits that benefit opt in below.
        p.BodySurge = 0f;
        p.SpineFlex = 0f;
        p.FootReach = 0f;
        p.TailBob = 0f;
        p.TailTaper = 0.4f;     // a gentle tip taper reads better than a rigid wag and is harmless
        p.EarsBack = 0f;
        p.MouthOpen = 0f;
        p.TailSet = 0f;
        p.BodyTilt = 0f;
        p.LegTuck = 0f;
        p.FlightBob = 0f;
        p.NeckCurve = 0f;
        p.Squash = 0f;
        p.HeadStabilize = 0f;
        switch (p.Gait)
        {
            case VanillaGenGait.Walk:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (30, 32f, 0.65f, 24f, 10f, 22f, 0.7f, 4f, 8f, 12f, 3f);
                (p.FootLift, p.BodyPitch, p.BodySway, p.HeadYaw) = (1.5f, 2f, 0.6f, 4f);
                break;
            case VanillaGenGait.Trot:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (22, 42f, 0.5f, 32f, 12f, 28f, 1.0f, 5f, 10f, 16f, 5f);
                (p.FootLift, p.BodyPitch, p.BodySway) = (2.5f, 3f, 0.4f);
                break;
            case VanillaGenGait.Gallop:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (16, 58f, 0.4f, 42f, 16f, 38f, 1.8f, 7f, 18f, 24f, 8f);
                (p.FootLift, p.BodyPitch, p.BodySway) = (4f, 6f, 0.5f);
                // Gallop: the body lunges fore-aft (Task 7) and the spine arches in the sagittal plane (Task 8).
                (p.BodySurge, p.SpineFlex, p.FootReach, p.EarsBack, p.MouthOpen, p.TailSet, p.Squash) = (3.5f, 16f, 2f, 24f, 18f, 20f, 0.4f);
                break;
            case VanillaGenGait.Idle:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (60, 3f, 0.6f, 3f, 1f, 2f, 0.3f, 1f, 3f, 8f, 3f);
                (p.FootLift, p.BodyPitch, p.Breathing, p.BreathRate, p.HeadYaw, p.EarFlop) = (0f, 0f, 0.06f, 1, 3f, 4f);
                break;
            case VanillaGenGait.Swim:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (34, 16f, 0.5f, 12f, 6f, 26f, 0.2f, 3f, 18f, 34f, 6f);
                p.TailWave = 80f;
                (p.FootLift, p.BodyPitch, p.BodySway) = (0f, 0f, 1.5f);
                break;
            case VanillaGenGait.Fly:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (24, 8f, 0.5f, 6f, 3f, 10f, 0.4f, 3f, 6f, 12f, 4f);
                p.WingFlap = 45f;
                (p.FootLift, p.BodyPitch) = (0f, 0f);
                // Flight: tuck the legs (Task 20), bob with the wingbeat and hold the neck in an S (Task 21),
                // and let the wing tips lag for a membrane billow (Task 17).
                (p.LegTuck, p.FlightBob, p.NeckCurve, p.WingChainLag, p.TailWave) = (1f, 1.5f, 8f, 40f, 60f);
                break;
            case VanillaGenGait.Pace:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (26, 36f, 0.55f, 26f, 11f, 24f, 0.6f, 9f, 8f, 12f, 3f);
                (p.FootLift, p.BodyPitch, p.BodySway) = (2f, 2f, 1.4f);  // pronounced lateral sway (camel/giraffe)
                break;
            case VanillaGenGait.Bound:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (14, 60f, 0.35f, 46f, 18f, 40f, 2.4f, 4f, 24f, 26f, 9f);
                (p.FootLift, p.BodyPitch) = (5f, 10f);                   // rabbit / squirrel leaping bound
                (p.BodySurge, p.SpineFlex, p.FootReach, p.EarsBack, p.Squash) = (4.5f, 22f, 2.5f, 18f, 0.5f);
                break;
            case VanillaGenGait.Stalk:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (44, 22f, 0.72f, 18f, 8f, 14f, 0.25f, 2f, 7f, 8f, 2f);
                (p.FootLift, p.BodyPitch) = (1f, 1f);                    // slow, low, deliberate
                (p.BodyTilt, p.TailSet) = (-6f, -8f);                    // crouched, head and tail low
                p.HeadStabilize = 0.7f;                                  // eyes locked on the prey
                break;
            case VanillaGenGait.Crawl:
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (46, 20f, 0.7f, 16f, 7f, 16f, 0.2f, 2f, 11f, 10f, 2f);
                (p.FootLift, p.BodyPitch) = (0.5f, 1f);
                break;
            case VanillaGenGait.Climb:
                // Task 13: vertical ascent - big reach + knee flex, body pitched up, almost no bob.
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (34, 40f, 0.6f, 40f, 14f, 40f, 0.2f, 1f, 6f, 8f, 2f);
                (p.FootLift, p.BodyPitch, p.FootReach, p.BodyTilt) = (5f, 1f, 4f, 22f);
                break;
            case VanillaGenGait.Charge:
                // Task 14: a committed, faster gallop with the head/horns lowered.
                (p.Frames, p.LegStride, p.StanceRatio, p.KneeFlex, p.AnkleBend, p.ArmSwing, p.BodyBob, p.BodyRoll, p.SpineBend, p.TailSway, p.HeadBob) = (14, 64f, 0.38f, 44f, 16f, 40f, 1.6f, 6f, 18f, 20f, 6f);
                (p.FootLift, p.BodyPitch, p.BodySurge, p.SpineFlex, p.FootReach) = (4f, 5f, 4f, 16f, 2f);
                (p.EarsBack, p.MouthOpen, p.BodyTilt, p.TailSet) = (28f, 22f, -14f, 14f);
                break;
        }

        ApplyVanillaSpeedScaling(p);
    }

    /// <summary>Biomechanical speed model: one knob coherently rescales the gait the preset just laid down.
    /// Faster animals take a quicker cadence (fewer frames per loop), a longer stride, a LOWER duty factor
    /// (less of the cycle spent in stance - the defining mark of a fast gait), a bigger bob/knee flex and a
    /// slight forward lean. 1.0 leaves the preset untouched; idle is exempt.</summary>
    private static void ApplyVanillaSpeedScaling(VanillaGenParams p)
    {
        double speed = Math.Clamp((double)p.Speed, 0.2, 4.0);
        if (Math.Abs(speed - 1.0) < 1e-3 || p.Gait == VanillaGenGait.Idle) return;

        p.Frames = Math.Clamp((int)Math.Round(p.Frames / Math.Sqrt(speed)), 4, 240);
        p.LegStride *= (float)Math.Pow(speed, 0.55);
        p.ArmSwing *= (float)Math.Pow(speed, 0.55);
        p.StanceRatio = (float)Math.Clamp(p.StanceRatio * Math.Pow(speed, -0.32), 0.28, 0.85);
        p.KneeFlex *= (float)Math.Pow(speed, 0.30);
        p.AnkleBend *= (float)Math.Pow(speed, 0.30);
        p.FootLift *= (float)speed;
        p.BodyBob *= (float)Math.Pow(speed, 0.8);
        p.BodyRoll *= (float)Math.Pow(speed, 0.4);
        if (speed > 1.0) p.BodyTilt -= (float)(4.0 * (speed - 1.0)); // lean into the run
    }

    // ---- Generation core ---------------------------------------------------

    private List<string> ResolveVanillaGenTargets(VanillaAnimationDocument document, VanillaGenParams p)
    {
        Shape? shape = document.Shape;
        if (shape == null) return [];

        List<string> ordered = BuildVanillaShapeDfsOrder(shape);
        IEnumerable<string> baseSet;
        if (p.Scope == VanillaGenTargetScope.SelectedSubtree)
        {
            if (string.IsNullOrWhiteSpace(_vanillaSelection.ElementName)) return [];
            ShapeElement? element = FindVanillaShapeElement(shape, _vanillaSelection.ElementName);
            if (element == null) return [];
            HashSet<string> subtree = new(GetShapeElementNamesRecursive(element), StringComparer.OrdinalIgnoreCase);
            baseSet = ordered.Where(subtree.Contains);
        }
        else
        {
            baseSet = ordered;
        }

        List<Regex> include = BuildVanillaGenGlobs(p.IncludeFilter);
        List<Regex> exclude = BuildVanillaGenGlobs(p.ExcludeFilter);

        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in baseSet)
        {
            if (!seen.Add(name)) continue;
            bool explicitTargeting = p.Scope != VanillaGenTargetScope.All || include.Count > 0;
            if (!explicitTargeting && IsVanillaGenInheritedDetail(name.ToLowerInvariant())) continue;
            if (include.Count > 0 && !include.Any(glob => glob.IsMatch(name))) continue;
            if (exclude.Count > 0 && exclude.Any(glob => glob.IsMatch(name))) continue;
            result.Add(name);
        }

        return result;
    }

    private List<VanillaGenElementChannel> BuildVanillaGenElementChannels(VanillaAnimationDocument document, List<string> targets, VanillaGenParams p)
    {
        if (p.Mode == VanillaGenMode.Locomotion)
        {
            return BuildVanillaLocomotionChannels(document, targets, p);
        }

        List<VanillaGenElementChannel> result = [];
        string[] allElements = GetSortedShapeElementNames(document);
        int count = targets.Count;
        double sharpness = Math.Max(0.1, p.Sharpness);
        for (int index = 0; index < count; index++)
        {
            string name = targets[index];
            TryResolveVanillaSymmetryPair(document, name, allElements, out _, out VanillaSymmetrySide side, out _);
            double elementPhase = p.PhasePerElement * index + (side == VanillaSymmetrySide.Right ? p.SymmetryPhase : 0.0);

            // Deterministic per-element jitter (organic desync) and a tip/base amplitude ramp.
            double phaseJitter = p.Jitter > 0f ? (VanillaGenHash01(name, p.RandomSeed) - 0.5) * 2.0 * p.Jitter * 180.0 : 0.0;
            double ampJitter = p.AmplitudeJitter > 0f ? 1.0 + (VanillaGenHash01(name, p.RandomSeed * 7919 + 1) - 0.5) * 2.0 * p.AmplitudeJitter : 1.0;
            double gradient = count > 1 ? 1.0 + p.AmplitudeGradient * ((double)index / (count - 1)) : 1.0;
            double ampMul = Math.Max(0.0, ampJitter * gradient);

            // The noise waveform reads its per-element seed from the Shape slot (unused by the other waves).
            double noiseSeed = p.RandomSeed * 101 + index;
            foreach (VanillaGenChannel channel in p.Channels)
            {
                if (!channel.Enabled) continue;
                result.Add(new VanillaGenElementChannel(
                    name, channel.Target, channel.Wave, channel.Amplitude * ampMul, Math.Max(1, channel.Frequency),
                    channel.Phase + elementPhase + phaseJitter, channel.Bias, noiseSeed, sharpness));
            }
        }

        return result;
    }

    /// <summary>Deterministic [0,1) hash of an element name and seed - used for reproducible per-element jitter.</summary>
    private static double VanillaGenHash01(string text, int seed)
    {
        unchecked
        {
            uint hash = 2166136261u ^ (uint)seed;
            foreach (char c in text)
            {
                hash = (hash ^ c) * 16777619u;
            }
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            hash ^= hash >> 15;
            return (hash & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private sealed class VanillaLocoLeg
    {
        public List<string> Segments = [];
        public List<double> SegmentLengths = []; // bone lengths (shape units), hip->foot, for the foot-lock IK
        public int Side;        // 0 = left, 1 = right
        public bool SideKnown;
        public int Row;         // 0 = front-most
        public double PosX;
        public double PosZ;
        public bool HasPos;
    }

    private sealed class VanillaLocoRig
    {
        public List<VanillaLocoLeg> Legs = [];
        public List<VanillaLocoLeg> Arms = [];
        public int LegRowCount = 1;
        public int ArmRowCount = 1;
    }

    // A walk is not a flat left/right mirror: each foot follows a real gait phase, the hip swings with a
    // duty-shaped stride (planted stance, quick swing), and the knee/ankle flex ONLY during the swing so they
    // bend correctly instead of the whole leg curling. Legs/arms are extracted as joint chains from the
    // hierarchy and placed by their real world position; the body, spine, tail, head and wings add secondary
    // motion so the rig is never bilaterally identical.
    private List<VanillaGenElementChannel> BuildVanillaLocomotionChannels(VanillaAnimationDocument document, List<string> targets, VanillaGenParams p)
    {
        List<VanillaGenElementChannel> result = [];
        HashSet<string> targetSet = new(targets, StringComparer.OrdinalIgnoreCase);
        VanillaLocoRig rig = BuildVanillaLocomotionRig(document, targetSet);
        double duty = Math.Clamp(p.StanceRatio, 0.1f, 0.9f);
        string[] allElements = GetSortedShapeElementNames(document);

        EmitVanillaLegChannels(result, rig.Legs, rig.LegRowCount, p, duty, isArm: false);
        EmitVanillaLegChannels(result, rig.Arms, rig.ArmRowCount, p, duty, isArm: true);

        Dictionary<string, int> wingDepth = p.WingChainLag > 0.01 ? BuildVanillaWingDepths(document) : new();
        int tailIndex = 0;
        int spineIndex = 0;
        int neckIndex = 0;
        foreach (string name in targets)
        {
            string lower = name.ToLowerInvariant();
            if (IsVanillaLocoLeg(lower) || IsVanillaLocoArm(lower)) continue;
            // Generated surface/detail geometry rides its parent bone. Giving it its own channel double-transforms
            // it, which looks like a duplicate shape clipping through the animated rig.
            if (IsVanillaGenInheritedDetail(lower)) continue;

            if (lower.Contains("wing"))
            {
                // Wings only beat when the gait actually flies (WingFlap > 0). On a ground/water gait the wings
                // are a folded flight organ - they hold their rest pose, they do NOT flap with the stride.
                if (p.WingFlap > 0.01)
                {
                    // Wings beat up and down together (a mirror image), not in alternation. The two wings are
                    // mirror images across the body's lateral axis, so an identical flap rotation lifts them in
                    // OPPOSITE vertical directions; the right side is negated so the pair stays in sync.
                    double flapSign = VanillaMirroredFlapSign(document, name, allElements);
                    // Task 17: distal wing segments lag the proximal beat, with a decaying amplitude, so the
                    // membrane billows and follows through instead of moving as one rigid plank.
                    int depth = wingDepth.TryGetValue(name, out int d) ? d : 0;
                    double lag = p.WingChainLag * depth;
                    double billowFalloff = Math.Pow(0.8, depth);
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationX, VanillaGenWave.Sine, p.WingFlap * flapSign * billowFalloff, Math.Max(1, p.WingBeats), lag, 0.0));
                }
            }
            else if (lower.Contains("tail"))
            {
                // Task 12: the tail is a tapering travelling wave on Y (sway) with an optional Z (bob) mix and a
                // carriage bias (TailSet), its amplitude decaying toward the tip.
                double taper = p.TailTaper > 0.001 ? Math.Pow(1.0 - Math.Clamp(p.TailTaper, 0f, 0.95f), tailIndex) : 1.0;
                double tailPhase = 90.0 + p.TailWave * tailIndex;
                double setBias = tailIndex == 0 ? p.TailSet : 0.0;
                result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationY, VanillaGenWave.Sine, p.TailSway * taper, 1, tailPhase, 0.0));
                if (p.TailBob > 0.01 || Math.Abs(setBias) > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, p.TailBob * taper, 2, tailPhase, setBias));
                }
                tailIndex++;
            }
            else if (lower.Contains("jaw") || lower.Contains("mandible"))
            {
                // Task 11: hold the mouth open (pant / snarl), with a faint pant cycle on aggressive gaits.
                if (Math.Abs(p.MouthOpen) > 0.01)
                {
                    double pant = p.MouthOpen > 12f ? p.MouthOpen * 0.18 : 0.0;
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, pant, 4, 0.0, -p.MouthOpen));
                }
            }
            else if (lower.Contains("head"))
            {
                // Gaze stabilization: a stabilized head bobs less of its own and counters the body's inherited
                // pitch/roll (emitted on the root spine), so the head holds level - the hallmark of a steady
                // predator/prey gaze. The head is down-chain of the spine, so an equal-and-opposite rotation
                // cancels the body's at the head.
                double stab = Math.Clamp(p.HeadStabilize, 0.0, 1.0);
                double headBob = p.HeadBob * (1.0 - 0.7 * stab);
                result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, headBob, 2, 180.0, VanillaHeadTiltBias(p)));
                if (p.HeadYaw > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationY, VanillaGenWave.Sine, p.HeadYaw, 1, 90.0, 0.0));
                }
                if (stab > 0.001 && p.BodyPitch > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, -p.BodyPitch * stab, 2, 90.0, 0.0));
                }
                if (stab > 0.001 && p.BodyRoll > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationX, VanillaGenWave.Sine, -p.BodyRoll * stab, 1, 0.0, 0.0));
                }
            }
            else if (lower.Contains("neck"))
            {
                // Task 21/24: distribute a held S-curve (alternating sign) down a multi-segment neck.
                double curveBias = p.NeckCurve > 0.01 ? p.NeckCurve * (neckIndex % 2 == 0 ? 1.0 : -1.0) : 0.0;
                curveBias += VanillaHeadTiltBias(p) * 0.4;
                result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, p.HeadBob * 0.5, 2, 180.0, curveBias));
                if (p.NeckBob > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, p.NeckBob, 1, 150.0, 0.0));
                }
                neckIndex++;
            }
            else if (lower.Contains("ear") || lower.Contains("antenn"))
            {
                // Task 11: pin the ears back as a constant bias (running / aggressive mood).
                double earSign = VanillaMirroredFlapSign(document, name, allElements);
                if (p.EarFlop > 0.01)
                {
                    // Ears/antennae flop together (mirror image), like the wings.
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationX, VanillaGenWave.Sine, p.EarFlop * earSign, 2, 0.0, 0.0));
                }
                if (p.EarsBack > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 0.0, 1, 0.0, p.EarsBack));
                }
            }
            else if (lower.Contains("spine") || lower.Contains("body") || lower.Contains("torso") || lower.Contains("chest"))
            {
                // Bob/roll/pitch/sway/breath only on the root segment (parented offsets compound down the chain);
                // the lateral coil travels along every segment so the torso flexes like a real spine.
                if (spineIndex == 0 && p.BodyBob > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.OffsetY, VanillaGenWave.Sine, p.BodyBob, 2, 0.0, 0.0));
                }
                if (spineIndex == 0 && p.BodyRoll > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationX, VanillaGenWave.Sine, p.BodyRoll, 1, 0.0, 0.0));
                }
                // Body pitch oscillation plus a static torso tilt (Task 13 climb nose-up / Task 14 charge nose-down).
                if (spineIndex == 0 && (p.BodyPitch > 0.01 || Math.Abs(p.BodyTilt) > 0.01))
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, p.BodyPitch, 2, 90.0, p.BodyTilt));
                }
                if (spineIndex == 0 && p.BodySway > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.OffsetZ, VanillaGenWave.Sine, p.BodySway, 1, 0.0, 0.0));
                }
                // Task 7: fore-aft body surge (gallop/bound/charge lunge). Task 21: vertical bob synced to the wingbeat.
                if (spineIndex == 0 && p.BodySurge > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.OffsetX, VanillaGenWave.Sine, p.BodySurge * (p.Backward ? -1.0 : 1.0), 1, 0.0, 0.0));
                }
                if (spineIndex == 0 && p.FlightBob > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.OffsetY, VanillaGenWave.Sine, p.FlightBob, Math.Max(1, p.WingBeats), 90.0, 0.0));
                }
                if (spineIndex == 0 && p.Breathing > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.StretchY, VanillaGenWave.Sine, p.Breathing, Math.Max(1, p.BreathRate), 0.0, 0.0));
                }
                // Task 33: a subtle vertical squash that dips on the body-bob low point (weighty footfalls).
                if (spineIndex == 0 && p.Squash > 0.001 && p.BodyBob > 0.001)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.StretchY, VanillaGenWave.Sine, -0.04 * p.Squash, 2, 90.0, 0.0));
                }
                if (p.SpineBend > 0.01)
                {
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationY, VanillaGenWave.Sine, p.SpineBend, 1, 30.0 * spineIndex, 0.0));
                }
                // Task 8: sagittal flex (back rounds then extends) on rotationZ - the gallop/bound back arch.
                if (p.SpineFlex > 0.01)
                {
                    int flexBeats = p.Gait is VanillaGenGait.Bound ? 2 : 1;
                    result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, p.SpineFlex, flexBeats, 20.0 * spineIndex, 0.0));
                }
                spineIndex++;
            }
            else if (p.SecondaryJiggle > 0.01 && IsVanillaLooseElement(lower))
            {
                // Task 34: loose elements (crest/fur/dewlap/wattle/feather/fin/frill/mane) wobble with a lagged
                // follow of the body's motion - a soft passive jiggle.
                double jiggleSign = VanillaMirroredFlapSign(document, name, allElements);
                result.Add(new VanillaGenElementChannel(name, VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 6.0 * p.SecondaryJiggle * jiggleSign, 2, 220.0, 0.0));
            }
        }

        return result;
    }

    /// <summary>A static head/neck pitch bias derived from the gait's torso tilt (head leads the body: charge
    /// drops it, climb/rear lifts it).</summary>
    private static double VanillaHeadTiltBias(VanillaGenParams p)
    {
        return p.Gait switch
        {
            VanillaGenGait.Charge => -10.0,
            VanillaGenGait.Climb => 8.0,
            _ => 0.0
        };
    }

    private static bool IsVanillaLooseElement(string lower)
    {
        return lower.Contains("crest") || lower.Contains("frill") || lower.Contains("dewlap") || lower.Contains("fur")
            || lower.Contains("wattle") || lower.Contains("feather") || lower.Contains("fin") || lower.Contains("mane")
            || lower.Contains("tuft") || lower.Contains("whisker");
    }

    /// <summary>Decorative geometry the creature generator adds as children of a real bone - rounding facets
    /// (<c>{part}Round{n}</c>), joint-gap knuckles (<c>joint{n}</c>) and scattered quills (<c>quill{n}</c>).
    /// These inherit their parent's motion already, so the locomotion classifier must not give them their own
    /// channel (and a facet named after a spine/head/tail bone would otherwise corrupt the phase sequence).</summary>
    private static bool IsVanillaGenDecorativeDetail(string lower)
    {
        return Regex.IsMatch(lower, @"round\d+$") || Regex.IsMatch(lower, @"^joint\d+$") || Regex.IsMatch(lower, @"^quill\d+$");
    }

    private static bool IsVanillaGenInheritedDetail(string lower)
    {
        return IsVanillaGenDecorativeDetail(lower)
            || lower.Contains("membrane", StringComparison.Ordinal)
            || lower.Contains("web", StringComparison.Ordinal);
    }

    /// <summary>element -&gt; parent-element name map from the shape hierarchy, for coupled-chain jiggle physics.</summary>
    private static Dictionary<string, string> BuildVanillaParentMap(VanillaAnimationDocument document)
    {
        Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase);
        Shape? shape = document.Shape;
        if (shape == null) return parents;

        void Visit(ShapeElement element)
        {
            string? name = element.Name;
            foreach (ShapeElement child in element.Children ?? [])
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(child.Name)) parents[child.Name!] = name!;
                Visit(child);
            }
        }
        foreach (ShapeElement root in shape.Elements ?? []) Visit(root);
        return parents;
    }

    /// <summary>Depth of each wing element within its wing chain (0 = the proximal-most wing segment), used to
    /// lag the membrane billow (Task 17). Walks the shape hierarchy counting consecutive "wing" ancestors.</summary>
    private Dictionary<string, int> BuildVanillaWingDepths(VanillaAnimationDocument document)
    {
        Dictionary<string, int> depths = new(StringComparer.OrdinalIgnoreCase);
        Shape? shape = document.Shape;
        if (shape == null) return depths;

        void Visit(ShapeElement element, int wingDepth)
        {
            string lower = (element.Name ?? "").ToLowerInvariant();
            bool isWing = lower.Contains("wing");
            int depthHere = isWing ? wingDepth : 0;
            if (isWing && !string.IsNullOrWhiteSpace(element.Name)) depths[element.Name] = depthHere;
            foreach (ShapeElement child in element.Children ?? [])
            {
                Visit(child, isWing ? wingDepth + 1 : 0);
            }
        }
        foreach (ShapeElement root in shape.Elements ?? [])
        {
            Visit(root, 0);
        }
        return depths;
    }

    private static void EmitVanillaLegChannels(List<VanillaGenElementChannel> result, List<VanillaLocoLeg> legs, int rowCount, VanillaGenParams p, double duty, bool isArm)
    {
        foreach (VanillaLocoLeg leg in legs)
        {
            if (leg.Segments.Count == 0) continue;

            double kneeSign = (leg.Row % 2 == 0 ? -1.0 : 1.0) * (p.KneeFlip ? -1.0 : 1.0);

            // Task 20: in flight (or any gait with LegTuck) the legs are folded up and held, not striding.
            if (p.LegTuck > 0.001)
            {
                double tuck = p.LegTuck;
                result.Add(new VanillaGenElementChannel(leg.Segments[0], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 0.0, 1, 0.0, 35.0 * tuck * kneeSign));
                if (leg.Segments.Count >= 2)
                    result.Add(new VanillaGenElementChannel(leg.Segments[1], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 0.0, 1, 0.0, -70.0 * tuck * kneeSign));
                for (int depth = 2; depth < leg.Segments.Count; depth++)
                    result.Add(new VanillaGenElementChannel(leg.Segments[depth], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 0.0, 1, 0.0, 40.0 * tuck * kneeSign));
                continue;
            }

            double fraction = GaitPhaseFraction(p.Gait, leg.Row, rowCount, leg.Side);
            if (isArm) fraction = (fraction + 0.5) % 1.0;             // arms counter-swing the legs
            fraction += p.Asymmetry * (leg.Side == 1 ? 0.04 : -0.04); // break the perfect mirror
            double phase = fraction * 360.0;

            // Task 16: stepping backwards reverses the fore-aft swing but keeps the swing-phase foot lift correct.
            double strideSign = p.Backward ? -1.0 : 1.0;
            double stride = (isArm ? p.ArmSwing : p.LegStride) * strideSign;
            double kneeFlex = (isArm ? p.KneeFlex * 0.6 : p.KneeFlex) * kneeSign;

            // Ground-contact IK foot-locking: solve the hip + knee so the stance foot stays planted (constant
            // height, constant backward sweep) instead of arcing - i.e. zero foot-slide. Needs two real bone
            // lengths; skips arms.
            if (p.FootLock && !isArm && leg.Segments.Count >= 2
                && leg.SegmentLengths.Count >= 2 && leg.SegmentLengths[0] > 0.01 && leg.SegmentLengths[1] > 0.01)
            {
                EmitVanillaFootLockChannels(result, leg, p, duty, phase, strideSign);
                continue;
            }

            // Hip: duty-shaped fore-aft stride.
            result.Add(new VanillaGenElementChannel(leg.Segments[0], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Stance, stride, 1, phase, 0.0, duty));

            // Knee: flexes only during the swing.
            if (leg.Segments.Count >= 2)
            {
                result.Add(new VanillaGenElementChannel(leg.Segments[1], VanillaGenChannelTarget.RotationZ, VanillaGenWave.SwingBump, kneeFlex, 1, phase, 0.0, duty));
            }

            // Ankle / lower joints: smaller counter-flex so the foot stays oriented.
            for (int depth = 2; depth < leg.Segments.Count; depth++)
            {
                double ankle = -p.AnkleBend * kneeSign * Math.Pow(0.6, depth - 2);
                result.Add(new VanillaGenElementChannel(leg.Segments[depth], VanillaGenChannelTarget.RotationZ, VanillaGenWave.SwingBump, ankle, 1, phase, 0.0, duty));
            }

            // Foot lift: raise the toe vertically during the swing for a clearer step (legs only).
            if (!isArm && p.FootLift > 0.001)
            {
                result.Add(new VanillaGenElementChannel(leg.Segments[^1], VanillaGenChannelTarget.OffsetY, VanillaGenWave.SwingBump, p.FootLift, 1, phase, 0.0, duty));
            }

            // Task 10: the foot also reaches forward as it swings (offsetX), not just bobbing vertically.
            if (!isArm && p.FootReach > 0.001)
            {
                result.Add(new VanillaGenElementChannel(leg.Segments[^1], VanillaGenChannelTarget.OffsetX, VanillaGenWave.SwingBump, -p.FootReach * strideSign, 1, phase, 0.0, duty));
            }
        }
    }

    /// <summary>Foot-lock: drives the hip + knee with per-phase IK so the foot follows a planted path (a straight,
    /// constant-height, constant-speed backward sweep during stance; a lifted arc during swing). The result is a
    /// gait with no foot-slide - the gold standard of procedural locomotion. Emits Curve channels (the value is the
    /// absolute joint angle); extra distal segments are left to follow.</summary>
    private static void EmitVanillaFootLockChannels(List<VanillaGenElementChannel> result, VanillaLocoLeg leg, VanillaGenParams p, double duty, double phaseDeg, double strideSign)
    {
        double l1 = leg.SegmentLengths[0];
        double l2 = leg.SegmentLengths[1];
        double legLength = l1 + l2;
        double stand = Math.Clamp((double)p.FootLockHeight, 0.5, 0.99) * legLength;
        double strideRad = Math.Clamp((double)p.LegStride, 0.0, 80.0) * 0.5 * (Math.PI / 180.0);
        double halfReach = Math.Min(0.45 * legLength, legLength * Math.Sin(strideRad) * Math.Max(0.1, (double)p.FootLockReach));
        int kneeSign = p.KneeFlip ? 1 : -1;

        (double hip, double knee) Solve(double cyclePos)
        {
            double local = cyclePos - Math.Floor(cyclePos); // the channel's PhaseDeg already offsets this per leg
            double footX, footY;
            if (local < duty)
            {
                double s = duty <= 0.0 ? 0.0 : local / duty;        // stance: foot forward -> back at ground height
                footX = halfReach * (1.0 - 2.0 * s) * strideSign;
                footY = -stand;
            }
            else
            {
                double s = (local - duty) / Math.Max(1e-6, 1.0 - duty); // swing: back -> forward, lifted
                footX = halfReach * (2.0 * s - 1.0) * strideSign;
                footY = -stand + 0.13 * legLength * Math.Sin(Math.PI * s);
            }
            return SolveVanillaLegIk(footX, footY, l1, l2, kneeSign);
        }

        result.Add(new VanillaGenElementChannel(leg.Segments[0], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 1.0, 1, phaseDeg, 0.0) { Curve = c => Solve(c).hip });
        result.Add(new VanillaGenElementChannel(leg.Segments[1], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 1.0, 1, phaseDeg, 0.0) { Curve = c => Solve(c).knee });

        // A third segment (foot/toe) is held flat to the ground by cancelling the accumulated hip+knee rotation,
        // so the sole stays level through the plant instead of the foot pitching and dragging its toe.
        if (leg.Segments.Count >= 3)
        {
            result.Add(new VanillaGenElementChannel(leg.Segments[2], VanillaGenChannelTarget.RotationZ, VanillaGenWave.Sine, 1.0, 1, phaseDeg, 0.0)
            {
                Curve = c => { (double hip, double knee) = Solve(c); return -(hip + knee); }
            });
        }
    }

    /// <summary>Closed-form 2-bone IK in the leg's sagittal swing plane (origin = hip, -Y = straight down,
    /// +X = forward). Returns the hip and knee rotationZ angles (deg) that place the foot exactly at (footX, footY).
    /// The hip is set by the law of cosines; the knee is then aimed straight at the foot, so FK is exact for any
    /// reachable target (the distance is clamped into the reachable annulus). kneeSign picks the elbow side.</summary>
    private static (double hip, double knee) SolveVanillaLegIk(double footX, double footY, double l1, double l2, int kneeSign)
    {
        double d = Math.Clamp(Math.Sqrt(footX * footX + footY * footY), Math.Abs(l1 - l2) + 1e-4, (l1 + l2) * 0.999);
        double phi = Math.Atan2(footX, -footY);                                  // target angle from straight-down
        double cosBeta = Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2.0 * l1 * d), -1.0, 1.0);
        double hip = phi - kneeSign * Math.Acos(cosBeta);
        // Aim the lower bone from the knee straight at the foot (|knee->foot| == l2 by construction).
        double kneeX = footX - l1 * Math.Sin(hip);
        double kneeY = footY + l1 * Math.Cos(hip);
        double knee = Math.Atan2(Math.Sin(Math.Atan2(kneeX, -kneeY) - hip), Math.Cos(Math.Atan2(kneeX, -kneeY) - hip));
        return (hip * (180.0 / Math.PI), knee * (180.0 / Math.PI));
    }

    /// <summary>Forward kinematics of the 2-bone leg, used to verify the IK (and in tests). Mirror of the convention
    /// in <see cref="SolveVanillaLegIk"/>.</summary>
    private static (double x, double y) ForwardKinematicsLeg(double hipDeg, double kneeDeg, double l1, double l2)
    {
        double h = hipDeg * (Math.PI / 180.0);
        double k = kneeDeg * (Math.PI / 180.0);
        return (l1 * Math.Sin(h) + l2 * Math.Sin(h + k), -(l1 * Math.Cos(h) + l2 * Math.Cos(h + k)));
    }

    /// <summary>Top-down pass that builds true world positions (no reliance on cached transforms) and extracts
    /// leg/arm joint chains from the hierarchy: a maximal parent-&gt;child run of leg (or arm) elements, ordered
    /// hip-&gt;foot.</summary>
    private VanillaLocoRig BuildVanillaLocomotionRig(VanillaAnimationDocument document, HashSet<string> targetSet)
    {
        VanillaLocoRig rig = new();
        Shape? shape = document.Shape;
        if (shape == null) return rig;

        Dictionary<string, Vec3d> worldPos = new(StringComparer.OrdinalIgnoreCase);
        float[] identity = Mat4f.Create();
        Mat4f.Identity(identity);
        foreach (ShapeElement root in shape.Elements ?? [])
        {
            TraverseVanillaRig(root, identity, null, "", rig, worldPos, targetSet);
        }

        rig.LegRowCount = FinalizeVanillaLimbGroup(document, rig.Legs, worldPos);
        rig.ArmRowCount = FinalizeVanillaLimbGroup(document, rig.Arms, worldPos);

        // Bone lengths (each segment's longest box extent, in shape units) for the foot-lock IK.
        Dictionary<string, double> lengths = new(StringComparer.OrdinalIgnoreCase);
        void MeasureLengths(ShapeElement element)
        {
            if (!string.IsNullOrWhiteSpace(element.Name) && element.From is { Length: >= 3 } f && element.To is { Length: >= 3 } t)
            {
                lengths[element.Name!] = Math.Max(Math.Abs(t[0] - f[0]), Math.Max(Math.Abs(t[1] - f[1]), Math.Abs(t[2] - f[2])));
            }
            foreach (ShapeElement child in element.Children ?? []) MeasureLengths(child);
        }
        foreach (ShapeElement root in shape.Elements ?? []) MeasureLengths(root);
        foreach (VanillaLocoLeg leg in rig.Legs.Concat(rig.Arms))
        {
            leg.SegmentLengths = leg.Segments.Select(s => lengths.TryGetValue(s, out double len) ? len : 0.0).ToList();
        }
        return rig;
    }

    private static void TraverseVanillaRig(ShapeElement element, float[] parentWorld, VanillaLocoLeg? chain, string chainKind, VanillaLocoRig rig, Dictionary<string, Vec3d> worldPos, HashSet<string> targetSet)
    {
        string name = element.Name ?? "";
        double[] joint = VanillaElementJointLocal(element);
        if (!string.IsNullOrWhiteSpace(name))
        {
            worldPos[name] = TransformVanillaPoint(parentWorld, joint[0], joint[1], joint[2]);
        }

        string lower = name.ToLowerInvariant();
        bool inTarget = string.IsNullOrWhiteSpace(name) || targetSet.Contains(name);
        string kind = inTarget && IsVanillaLocoLeg(lower) ? "leg" : inTarget && IsVanillaLocoArm(lower) ? "arm" : "";

        VanillaLocoLeg? childChain = null;
        string childKind = "";
        if (kind.Length > 0)
        {
            if (chain != null && string.Equals(chainKind, kind, StringComparison.Ordinal))
            {
                chain.Segments.Add(name);
                childChain = chain;
            }
            else
            {
                VanillaLocoLeg created = new();
                created.Segments.Add(name);
                (kind == "leg" ? rig.Legs : rig.Arms).Add(created);
                childChain = created;
            }
            childKind = kind;
        }

        float[] local = element.GetLocalTransformMatrix(0, Mat4f.Create());
        float[] world = Mat4f.Mul(Mat4f.Create(), parentWorld, local);
        foreach (ShapeElement child in element.Children ?? [])
        {
            TraverseVanillaRig(child, world, childChain, childKind, rig, worldPos, targetSet);
        }
    }

    private static double[] VanillaElementJointLocal(ShapeElement element)
    {
        if (element.RotationOrigin is { Length: >= 3 } origin)
        {
            return [origin[0] / 16.0, origin[1] / 16.0, origin[2] / 16.0];
        }
        if (element.From is { Length: >= 3 } from && element.To is { Length: >= 3 } to)
        {
            return [(from[0] + to[0]) / 32.0, (from[1] + to[1]) / 32.0, (from[2] + to[2]) / 32.0];
        }
        return [0.0, 0.0, 0.0];
    }

    private int FinalizeVanillaLimbGroup(VanillaAnimationDocument document, List<VanillaLocoLeg> legs, Dictionary<string, Vec3d> worldPos)
    {
        if (legs.Count == 0) return 1;
        string[] allElements = GetSortedShapeElementNames(document);

        foreach (VanillaLocoLeg leg in legs)
        {
            string hip = leg.Segments[0];
            TryResolveVanillaSymmetryPair(document, hip, allElements, out _, out VanillaSymmetrySide side, out _);
            leg.SideKnown = side != VanillaSymmetrySide.Unknown;
            leg.Side = side == VanillaSymmetrySide.Right ? 1 : 0;
            if (worldPos.TryGetValue(hip, out Vec3d? pos) && pos != null)
            {
                leg.PosX = pos.X;
                leg.PosZ = pos.Z;
                leg.HasPos = true;
            }
        }

        int forwardAxis = DetermineVanillaForwardAxis(legs);
        int lateralAxis = forwardAxis == 0 ? 2 : 0;

        List<VanillaLocoLeg> positioned = legs.Where(leg => leg.HasPos).ToList();
        if (positioned.Count > 0)
        {
            double lateralCenter = positioned.Average(leg => lateralAxis == 0 ? leg.PosX : leg.PosZ);
            foreach (VanillaLocoLeg leg in legs.Where(leg => !leg.SideKnown && leg.HasPos))
            {
                double lateral = lateralAxis == 0 ? leg.PosX : leg.PosZ;
                leg.Side = lateral >= lateralCenter ? 1 : 0;
            }
        }

        return ClusterVanillaLegRows(legs, forwardAxis);
    }

    /// <summary>Forward axis (0=X, 2=Z): the horizontal axis the two symmetry sides do NOT separate along; falls
    /// back to the larger leg spread when sides are unknown.</summary>
    private static int DetermineVanillaForwardAxis(List<VanillaLocoLeg> legs)
    {
        List<VanillaLocoLeg> known = legs.Where(leg => leg.SideKnown && leg.HasPos).ToList();
        if (known.Any(leg => leg.Side == 0) && known.Any(leg => leg.Side == 1))
        {
            double mx0 = known.Where(leg => leg.Side == 0).Average(leg => leg.PosX);
            double mx1 = known.Where(leg => leg.Side == 1).Average(leg => leg.PosX);
            double mz0 = known.Where(leg => leg.Side == 0).Average(leg => leg.PosZ);
            double mz1 = known.Where(leg => leg.Side == 1).Average(leg => leg.PosZ);
            return Math.Abs(mz0 - mz1) > Math.Abs(mx0 - mx1) ? 0 : 2;
        }

        List<VanillaLocoLeg> positioned = legs.Where(leg => leg.HasPos).ToList();
        if (positioned.Count == 0) return 0;
        double spanX = positioned.Max(leg => leg.PosX) - positioned.Min(leg => leg.PosX);
        double spanZ = positioned.Max(leg => leg.PosZ) - positioned.Min(leg => leg.PosZ);
        return spanX >= spanZ ? 0 : 2;
    }

    private static int ClusterVanillaLegRows(List<VanillaLocoLeg> legs, int forwardAxis)
    {
        List<VanillaLocoLeg> positioned = legs.Where(leg => leg.HasPos).ToList();
        if (positioned.Count == 0)
        {
            foreach (VanillaLocoLeg leg in legs) leg.Row = 0;
            return 1;
        }

        double Forward(VanillaLocoLeg leg) => forwardAxis == 0 ? leg.PosX : leg.PosZ;
        double min = positioned.Min(Forward);
        double max = positioned.Max(Forward);
        double span = max - min;
        if (span < 0.05)
        {
            foreach (VanillaLocoLeg leg in legs) leg.Row = 0;
            return 1;
        }

        double tolerance = Math.Max(0.05, span * 0.3);
        List<VanillaLocoLeg> sorted = positioned.OrderBy(Forward).ToList();
        int row = 0;
        double anchor = Forward(sorted[0]);
        sorted[0].Row = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Forward(sorted[i]) - anchor > tolerance)
            {
                row++;
                anchor = Forward(sorted[i]);
            }
            sorted[i].Row = row;
        }

        foreach (VanillaLocoLeg leg in legs.Where(leg => !leg.HasPos)) leg.Row = 0;
        return row + 1;
    }

    /// <summary>
    /// Sign for a flap-style rotation (about the fore-aft axis, e.g. a wing beat) so a left/right pair moves
    /// as a true mirror image - both sides up and down together - instead of alternating. The right side is
    /// negated because the same rotation about the body's forward axis lifts mirrored elements in opposite
    /// directions. Center or unpaired elements return +1 (unchanged).
    /// </summary>
    private double VanillaMirroredFlapSign(VanillaAnimationDocument document, string name, string[] allElements)
    {
        TryResolveVanillaSymmetryPair(document, name, allElements, out _, out VanillaSymmetrySide side, out _);
        return side == VanillaSymmetrySide.Right ? -1.0 : 1.0;
    }

    private static bool IsVanillaLocoLeg(string lower)
    {
        return lower.Contains("leg") || lower.Contains("foot") || lower.Contains("thigh") || lower.Contains("shank") || lower.Contains("paw");
    }

    private static bool IsVanillaLocoArm(string lower)
    {
        return !IsVanillaLocoLeg(lower) && (lower.Contains("arm") || lower.Contains("hand"));
    }

    /// <summary>Phase (fraction of the cycle, 0..1) for a foot given the gait, its row (0=front) and side (0=left,
    /// 1=right). One row is a biped (left/right antiphase); two rows are quadruped gaits; three or more rows use an
    /// alternating-tripod insect gait.</summary>
    private static double GaitPhaseFraction(VanillaGenGait gait, int row, int rowCount, int sideIndex)
    {
        if (rowCount <= 1)
        {
            // Biped: a bound/gallop/pace plants both feet together (a hop); otherwise they alternate.
            return gait is VanillaGenGait.Bound or VanillaGenGait.Gallop or VanillaGenGait.Pace ? 0.0 : 0.5 * sideIndex;
        }

        if (rowCount >= 3)
        {
            // Many-legged creatures (hexapods, the scorpion, centipedes). A 6-legged insect walks with an
            // alternating tripod; a longer body (4+ rows = a myriapod) ripples with a metachronal wave.
            bool myriapod = rowCount >= 4;
            return gait switch
            {
                VanillaGenGait.Pace => 0.5 * sideIndex,                                  // each side moves as a unit
                VanillaGenGait.Bound or VanillaGenGait.Gallop or VanillaGenGait.Charge   // a body wave, both sides ~together
                    => ((double)row / rowCount + 0.06 * sideIndex) % 1.0,
                VanillaGenGait.Walk or VanillaGenGait.Crawl or VanillaGenGait.Stalk or VanillaGenGait.Climb when myriapod
                    => ((double)row / rowCount + 0.5 * sideIndex) % 1.0,                 // metachronal ripple down the body
                _ => 0.5 * ((sideIndex + row) % 2)                                       // alternating tripod (insect / fast)
            };
        }

        // Quadruped: the classic two-row gaits.
        return gait switch
        {
            VanillaGenGait.Walk => ((2 * sideIndex + 3 * row) % 4) / 4.0,   // 4-beat diagonal sequence
            VanillaGenGait.Trot => 0.5 * ((sideIndex + row) % 2),           // diagonal pairs together
            VanillaGenGait.Gallop => 0.5 * row + 0.08 * sideIndex,          // front pair, then back pair
            VanillaGenGait.Charge => 0.5 * row + 0.08 * sideIndex,          // gallop sequence (Task 14)
            VanillaGenGait.Bound => 0.5 * row,                              // both feet of a row land together
            VanillaGenGait.Pace => 0.5 * sideIndex,                         // same-side legs swing together (camel/giraffe)
            VanillaGenGait.Stalk => ((2 * sideIndex + 3 * row) % 4) / 4.0,  // walk sequence, slowed by the preset
            VanillaGenGait.Crawl => ((2 * sideIndex + 3 * row) % 4) / 4.0,
            VanillaGenGait.Climb => ((2 * sideIndex + 3 * row) % 4) / 4.0,  // walk sequence up a wall (Task 13)
            _ => 0.5 * sideIndex                                            // idle / swim / fly
        };
    }

    // ---- Pose / action mode ------------------------------------------------

    // Builds a single full-strength target pose by folding/raising the rig's limbs, spine, head, tail and jaw.
    // Classification is by hierarchy (leg/arm joint chains via the locomotion rig) and by name (spine/head/etc.),
    // so the same action works on any conventionally-named entity. The keyframe sampler then eases this pose in
    // (and optionally back out).
    private Dictionary<string, AnimationKeyFrameElement> BuildVanillaPose(VanillaAnimationDocument document, List<string> targets, VanillaGenParams p)
    {
        Dictionary<string, AnimationKeyFrameElement> pose = new(StringComparer.OrdinalIgnoreCase);
        double s = Math.Max(0.0, p.PoseStrength);

        void Rot(string? el, double x, double y, double z)
        {
            if (string.IsNullOrEmpty(el)) return;
            if (!pose.TryGetValue(el, out AnimationKeyFrameElement? e)) pose[el] = e = new AnimationKeyFrameElement();
            e.RotationX = (e.RotationX ?? 0.0) + x * s;
            e.RotationY = (e.RotationY ?? 0.0) + y * s;
            e.RotationZ = (e.RotationZ ?? 0.0) + z * s;
        }
        void Off(string? el, double x, double y, double z)
        {
            if (string.IsNullOrEmpty(el)) return;
            if (!pose.TryGetValue(el, out AnimationKeyFrameElement? e)) pose[el] = e = new AnimationKeyFrameElement();
            e.OffsetX = (e.OffsetX ?? 0.0) + x * s;
            e.OffsetY = (e.OffsetY ?? 0.0) + y * s;
            e.OffsetZ = (e.OffsetZ ?? 0.0) + z * s;
        }
        // Fold a set of limbs about Z; anglesZ is indexed hip, knee, ankle, toe (extra segments reuse the last).
        void Fold(List<VanillaLocoLeg> limbs, double[] anglesZ)
        {
            foreach (VanillaLocoLeg limb in limbs)
            {
                for (int k = 0; k < limb.Segments.Count; k++)
                {
                    Rot(limb.Segments[k], 0.0, 0.0, anglesZ[Math.Min(k, anglesZ.Length - 1)]);
                }
            }
        }

        VanillaLocoRig rig = BuildVanillaLocomotionRig(document, new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase));
        List<VanillaLocoLeg> front = [];
        List<VanillaLocoLeg> rear = [];
        if (rig.Arms.Count > 0)
        {
            front.AddRange(rig.Arms);
            rear.AddRange(rig.Legs);
        }
        else
        {
            int maxRow = rig.Legs.Count > 0 ? rig.Legs.Max(l => l.Row) : 0;
            foreach (VanillaLocoLeg leg in rig.Legs)
            {
                if (maxRow > 0 && leg.Row == 0) front.Add(leg);
                else rear.Add(leg);
            }
        }

        List<string> spine = [];
        List<string> tail = [];
        List<string> neckChain = [];
        List<string> trunk = [];
        string? head = null;
        string? jaw = null;
        foreach (string name in targets)
        {
            string l = name.ToLowerInvariant();
            if (IsVanillaLocoLeg(l) || IsVanillaLocoArm(l)) continue;
            if (l.Contains("jaw") || l.Contains("mandible")) jaw ??= name;
            else if (l.Contains("trunk") || l.Contains("proboscis") || l.Contains("tongue") || l.Contains("tentacle")) trunk.Add(name);
            else if (l.Contains("head")) head ??= name;
            else if (l.Contains("neck")) neckChain.Add(name);
            else if (l.Contains("tail")) tail.Add(name);
            else if (IsVanillaPoseSpine(l)) spine.Add(name);
        }
        string? bodyRoot = spine.Count > 0 ? spine[0] : head; // chain root carries the whole-body offset/tilt
        void TailRot(double x, double y, double z)
        {
            foreach (string t in tail) Rot(t, x, y, z);
        }
        // Task 24: distribute a neck bend down a multi-segment neck (tip-heavy) so long necks curve smoothly;
        // for a single-segment neck this is identical to rotating that one element.
        void NeckRot(double x, double y, double z)
        {
            int n = neckChain.Count;
            if (n == 0) return;
            double wsum = 0;
            for (int i = 0; i < n; i++) wsum += i + 1;
            for (int i = 0; i < n; i++)
            {
                double w = (i + 1) / wsum;
                Rot(neckChain[i], x * w, y * w, z * w);
            }
        }
        // Task 23: progressive curl down a prehensile chain (trunk/tongue/tentacle) - the tip coils tightest.
        void TrunkCurl(double perSegment, double lateral = 0.0)
        {
            double acc = 0;
            for (int i = 0; i < trunk.Count; i++)
            {
                acc += perSegment;
                Rot(trunk[i], 0, lateral, acc);
            }
        }
        // Task 31: shift the body onto the support side when a single limb is lifted. side 0 = left (+Z), 1 = right (-Z).
        void WeightShiftTo(int supportSide)
        {
            if (!p.WeightShift) return;
            Off(bodyRoot, 0, 0, supportSide == 0 ? 2.0 : -2.0);
            Rot(bodyRoot, supportSide == 0 ? 3.0 : -3.0, 0, 0);
        }
        // Fold only one limb of a group (the first), for one-sided gestures (scratch/swipe/kick).
        void FoldOne(List<VanillaLocoLeg> limbs, double[] anglesZ, out int liftedSide)
        {
            liftedSide = -1;
            if (limbs.Count == 0) return;
            VanillaLocoLeg limb = limbs[0];
            liftedSide = limb.Side;
            for (int k = 0; k < limb.Segments.Count; k++)
            {
                Rot(limb.Segments[k], 0.0, 0.0, anglesZ[Math.Min(k, anglesZ.Length - 1)]);
            }
        }

        switch (p.Action)
        {
            case VanillaGenAction.Sit:
                Off(bodyRoot, 0, -6, 0); Rot(bodyRoot, 0, 0, -10);
                Fold(rear, [90, -35, 80, 80]); Fold(front, [12, 0, 0, 0]);
                NeckRot(0, 0, 6); Rot(head, 0, 0, 12); Rot(jaw, 0, 0, -4); TailRot(0, 60, 20);
                break;
            case VanillaGenAction.Lie:
                Off(bodyRoot, 0, -9, 0);
                Fold(rear, [95, -55, 90, 90]); Fold(front, [-85, 55, 0, 0]);
                NeckRot(0, 0, -8); Rot(head, 0, 0, -6); TailRot(0, 40, 10);
                break;
            case VanillaGenAction.Sleep:
                Off(bodyRoot, 0, -9, 0);
                Fold(rear, [95, -55, 90, 90]); Fold(front, [-85, 55, 0, 0]);
                NeckRot(0, 35, -12); Rot(head, 0, 40, -10); TailRot(0, 70, 10);
                break;
            case VanillaGenAction.Crouch:
                Off(bodyRoot, 0, -3, 0);
                Fold(rear, [32, -22, 16, 16]); Fold(front, [22, -16, 10, 10]);
                NeckRot(0, 0, -6); Rot(head, 0, 0, -8);
                break;
            case VanillaGenAction.Rear:
                Rot(bodyRoot, 0, 0, 48);
                Fold(front, [55, -75, 0, 0]); Fold(rear, [-12, 12, 0, 0]);
                NeckRot(0, 0, 6); Rot(head, 0, 0, 10);
                break;
            case VanillaGenAction.Beg:
                Rot(bodyRoot, 0, 0, 36);
                Fold(front, [60, -85, 0, 0]);
                NeckRot(0, 0, 6); Rot(head, 0, 0, 8); Rot(jaw, 0, 0, -6);
                break;
            case VanillaGenAction.PlayBow:
                Rot(bodyRoot, 0, 0, -28);
                Fold(front, [70, -15, 0, 0]); Fold(rear, [-10, 6, 0, 0]);
                NeckRot(0, 0, -10); Rot(head, 0, 0, -6); TailRot(0, 0, 35);
                break;
            case VanillaGenAction.Stretch:
                Rot(bodyRoot, 0, 0, -22);
                Fold(front, [80, -10, 0, 0]);
                NeckRot(0, 0, 14); Rot(head, 0, 0, 10); TailRot(0, 0, 40);
                break;
            case VanillaGenAction.Pounce:
                Off(bodyRoot, 4, 1, 0); Rot(bodyRoot, 0, 0, 6);
                Fold(front, [-35, 25, 0, 0]); Fold(rear, [40, -25, 0, 0]);
                NeckRot(0, 0, -10); Rot(head, 0, 0, -18); Rot(jaw, 0, 0, -34);
                break;
            case VanillaGenAction.Eat:
                NeckRot(0, 0, -26); Rot(head, 0, 0, -30); Rot(jaw, 0, 0, -12);
                break;
            case VanillaGenAction.Graze:
                Off(bodyRoot, 0, -2, 0); NeckRot(0, 0, -38); Rot(head, 0, 0, -42); Rot(jaw, 0, 0, -8);
                break;
            case VanillaGenAction.LookUp:
                NeckRot(0, 0, 18); Rot(head, 0, 0, 30);
                break;
            case VanillaGenAction.LookDown:
                NeckRot(0, 0, -18); Rot(head, 0, 0, -30);
                break;
            case VanillaGenAction.LookLeft:
                NeckRot(0, 22, 0); Rot(head, 0, 45, 0);
                break;
            case VanillaGenAction.LookRight:
                NeckRot(0, -22, 0); Rot(head, 0, -45, 0);
                break;
            case VanillaGenAction.Cower:
                Off(bodyRoot, 0, -4, 0);
                Fold(rear, [42, -26, 20, 20]); Fold(front, [42, -26, 20, 20]);
                NeckRot(0, 0, -22); Rot(head, 0, 0, -26); TailRot(0, 0, -45);
                break;
            case VanillaGenAction.Collapse:
                Off(bodyRoot, 0, -5, 0); Rot(bodyRoot, 85, 0, 0);
                Fold(rear, [22, -12, 0, 0]); Fold(front, [22, -12, 0, 0]);
                NeckRot(0, 0, -16); Rot(head, 0, 0, -20);
                break;
            case VanillaGenAction.Flinch:
                Off(bodyRoot, -3, -1, 0); Rot(bodyRoot, 0, 0, -14);
                Fold(front, [18, -10, 0, 0]); Fold(rear, [18, -10, 0, 0]);
                NeckRot(0, 0, 12); Rot(head, 0, 0, 16);
                break;

            // ---- Vocalizations (Task 22) ----
            case VanillaGenAction.Howl:
                Off(bodyRoot, 0, 1, 0);
                NeckRot(0, 0, 34); Rot(head, 0, 0, 40); Rot(jaw, 0, 0, -42); TailRot(0, 0, 12);
                WeightShiftTo(0);
                break;
            case VanillaGenAction.Roar:
                Rot(bodyRoot, 0, 0, 6);
                NeckRot(0, 0, 14); Rot(head, 0, 0, 12); Rot(jaw, 0, 0, -46);
                foreach (string c in spine) Rot(c, 0, 0, 2); // chest swells forward
                break;
            case VanillaGenAction.Call:
                NeckRot(0, 0, 22); Rot(head, 0, 0, 20); Rot(jaw, 0, 0, -24);
                break;

            // ---- Attacks (Task 26) ----
            case VanillaGenAction.Bite:
                Off(bodyRoot, 6, 0, 0); NeckRot(0, 0, -18); Rot(head, 0, 0, -22); Rot(jaw, 0, 0, -40);
                break;
            case VanillaGenAction.Swipe:
            {
                FoldOne(front, [-70, 40, 0, 0], out int supportL);
                Rot(bodyRoot, supportL == 0 ? -8 : 8, 0, 6);
                NeckRot(0, supportL == 0 ? -10 : 10, 0); Rot(head, 0, supportL == 0 ? -14 : 14, 0);
                WeightShiftTo(supportL == 0 ? 1 : 0);
                break;
            }
            case VanillaGenAction.Kick:
            {
                FoldOne(rear, [-60, 70, 0, 0], out int supportK);
                Rot(bodyRoot, 0, 0, 12);
                WeightShiftTo(supportK == 0 ? 1 : 0);
                break;
            }
            case VanillaGenAction.Gore:
                Off(bodyRoot, 5, -1, 0); Rot(bodyRoot, 0, 0, -10);
                NeckRot(0, 0, -22); Rot(head, 0, 0, -30); Fold(front, [10, -6, 0, 0]);
                break;
            case VanillaGenAction.Stomp:
                Fold(front, [60, -40, 0, 0]); Off(bodyRoot, -2, 2, 0); Rot(bodyRoot, 0, 0, 10);
                break;
            case VanillaGenAction.TailWhip:
                TailRot(0, 70, 0); Rot(bodyRoot, 0, 0, 8);
                break;

            // ---- Foraging / grooming (Task 29) ----
            case VanillaGenAction.Sniff:
                Off(bodyRoot, 1, -1, 0); NeckRot(0, 0, -20); Rot(head, 0, 0, -22); Rot(jaw, 0, 0, -6);
                break;
            case VanillaGenAction.Peck:
                NeckRot(0, 0, -40); Rot(head, 0, 0, -46); Rot(jaw, 0, 0, -10); Off(bodyRoot, 1, -1, 0);
                break;
            case VanillaGenAction.Dig:
                Off(bodyRoot, 0, -2, 0); Fold(front, [50, -30, 20, 20]);
                NeckRot(0, 0, -24); Rot(head, 0, 0, -28);
                break;
            case VanillaGenAction.Lick:
                NeckRot(0, 22, -20); Rot(head, 0, 30, -26); Rot(jaw, 0, 0, -14); TailRot(0, 0, 10);
                break;
            case VanillaGenAction.Scratch:
            {
                FoldOne(rear, [80, -90, 30, 30], out int supportS);
                NeckRot(0, supportS == 0 ? 16 : -16, -10); Rot(head, 0, supportS == 0 ? 22 : -22, -12);
                WeightShiftTo(supportS == 0 ? 1 : 0);
                break;
            }
            case VanillaGenAction.Loaf:
                Off(bodyRoot, 0, -5, 0);
                Fold(rear, [95, -60, 95, 95]); Fold(front, [70, -70, 0, 0]); // paws tucked under
                NeckRot(0, 0, 4); Rot(head, 0, 0, 4); TailRot(0, 50, 0);
                break;
            case VanillaGenAction.Drink:
                Off(bodyRoot, 0, -3, 0); NeckRot(0, 0, -42); Rot(head, 0, 0, -46); Rot(jaw, 0, 0, -6);
                break;
            case VanillaGenAction.TrunkCurl:
                // Progressive curl of the trunk up toward the mouth, with a touch of lateral drift.
                TrunkCurl(9.0, lateral: 3.0); NeckRot(0, 0, -6); Rot(head, 0, 0, -8);
                break;

            // ---- Death / wounded (Task 27) ----
            case VanillaGenAction.Death:
                Off(bodyRoot, 0, -7, 0); Rot(bodyRoot, 92, 0, 6);   // collapse onto the side
                Fold(rear, [28, -16, 0, 0]); Fold(front, [-24, 14, 0, 0]);
                NeckRot(0, 0, -18); Rot(head, 0, 0, -24); TailRot(0, 0, -10);
                break;
            case VanillaGenAction.WoundedRest:
                Off(bodyRoot, 0, -8, 0); Rot(bodyRoot, 78, 0, 4);
                Fold(rear, [60, -40, 40, 40]); Fold(front, [-50, 40, 0, 0]);
                NeckRot(0, 10, -14); Rot(head, 0, 14, -18);
                break;
        }

        return pose;
    }

    private static bool IsVanillaPoseSpine(string lower)
    {
        return lower.Contains("spine") || lower.Contains("body") || lower.Contains("torso") || lower.Contains("chest")
            || lower.Contains("rear") || lower.Contains("midsection") || lower.Contains("abdomen") || lower.Contains("thorax")
            || lower.Contains("pelvis") || lower.Contains("root") || lower.Contains("origin");
    }

    private static AnimationKeyFrame[] BuildVanillaPoseKeyFrames(VanillaGenParams p, Dictionary<string, AnimationKeyFrameElement> pose)
    {
        int frames = Math.Clamp(p.Frames, 1, 10000);
        int samples = Math.Clamp(p.SampleCount, 2, 240);
        bool ease = p.PoseEase;

        SortedSet<int> frameSet = new() { 0, frames - 1 };
        if (p.ReturnToRest)
        {
            double hold = Math.Clamp(p.PoseHold, 0f, 0.9f);
            double t1 = (1.0 - hold) * 0.5;
            double t2 = t1 + hold;
            frameSet.Add(Math.Clamp((int)Math.Round(t1 * frames), 0, frames - 1));
            frameSet.Add(Math.Clamp((int)Math.Round(t2 * frames), 0, frames - 1));
        }
        for (int i = 0; i < samples; i++)
        {
            frameSet.Add(Math.Clamp((int)Math.Round((double)i * frames / samples), 0, frames - 1));
        }

        List<AnimationKeyFrame> keyFrames = [];
        foreach (int frame in frameSet)
        {
            double envelope = VanillaPoseEnvelope((double)frame / frames, p, ease);
            AnimationKeyFrame keyFrame = new()
            {
                Frame = frame,
                Elements = new Dictionary<string, AnimationKeyFrameElement>(StringComparer.OrdinalIgnoreCase)
            };
            foreach ((string element, AnimationKeyFrameElement src) in pose)
            {
                keyFrame.Elements[element] = ScaleVanillaKeyElement(src, envelope);
            }
            keyFrames.Add(keyFrame);
        }

        return keyFrames.ToArray();
    }

    private static double VanillaPoseEnvelope(double u, VanillaGenParams p, bool ease)
    {
        if (!p.ReturnToRest)
        {
            // Held pose; a faint breathing pulse keeps it from being perfectly frozen.
            return 1.0 + p.PoseSettle * 0.05 * Math.Sin(2.0 * Math.PI * u);
        }

        double hold = Math.Clamp(p.PoseHold, 0f, 0.9f);
        double t1 = (1.0 - hold) * 0.5;
        double t2 = t1 + hold;
        if (u <= t1)
        {
            double rampIn = t1 <= 0.0 ? 1.0 : u / t1;
            return VanillaEnvelopeShape(rampIn, p.PoseEnvelope, ease); // Task 30
        }
        if (u <= t2) return 1.0;
        double rampOut = (1.0 - t2) <= 0.0 ? 1.0 : (u - t2) / (1.0 - t2);
        return 1.0 - (ease ? Smoothstep(rampOut) : rampOut);
    }

    private static double Smoothstep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    /// <summary>Task 30: shape a 0..1 ramp. Anticipate winds back below 0 first; Overshoot passes 1 then settles;
    /// Bounce settles with a damped bounce. Endpoints stay 0 and 1 so a transition still starts/ends cleanly.</summary>
    private static double VanillaEnvelopeShape(double r, VanillaGenPoseEnvelope env, bool ease)
    {
        r = Math.Clamp(r, 0.0, 1.0);
        switch (env)
        {
            case VanillaGenPoseEnvelope.Anticipate:
            {
                const double c = 1.70158;
                return r * r * ((c + 1.0) * r - c);          // easeInBack
            }
            case VanillaGenPoseEnvelope.Overshoot:
            {
                const double c1 = 1.70158, c3 = c1 + 1.0;
                double x = r - 1.0;
                return 1.0 + c3 * x * x * x + c1 * x * x;     // easeOutBack
            }
            case VanillaGenPoseEnvelope.Bounce:
                return VanillaBounceOut(r);
            default:
                return ease ? Smoothstep(r) : r;
        }
    }

    private static double VanillaBounceOut(double x)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (x < 1.0 / d1) return n1 * x * x;
        if (x < 2.0 / d1) { x -= 1.5 / d1; return n1 * x * x + 0.75; }
        if (x < 2.5 / d1) { x -= 2.25 / d1; return n1 * x * x + 0.9375; }
        x -= 2.625 / d1;
        return n1 * x * x + 0.984375;
    }

    /// <summary>Task 28: keyframes for a one-way From -> To pose transition, eased with the chosen envelope.</summary>
    private static AnimationKeyFrame[] BuildVanillaTransitionKeyFrames(VanillaGenParams p, Dictionary<string, AnimationKeyFrameElement> from, Dictionary<string, AnimationKeyFrameElement> to)
    {
        int frames = Math.Clamp(p.Frames, 1, 10000);
        int samples = Math.Clamp(p.SampleCount, 2, 240);
        int lastFrame = Math.Max(1, frames - 1);

        HashSet<string> elements = new(from.Keys, StringComparer.OrdinalIgnoreCase);
        elements.UnionWith(to.Keys);

        SortedSet<int> frameSet = [0, lastFrame];
        for (int i = 0; i < samples; i++) frameSet.Add(Math.Clamp((int)Math.Round((double)i * frames / samples), 0, lastFrame));

        List<AnimationKeyFrame> keyFrames = [];
        foreach (int frame in frameSet)
        {
            double u = (double)frame / lastFrame;
            double t = VanillaEnvelopeShape(u, p.PoseEnvelope, p.PoseEase);
            AnimationKeyFrame keyFrame = new()
            {
                Frame = frame,
                Elements = new Dictionary<string, AnimationKeyFrameElement>(StringComparer.OrdinalIgnoreCase)
            };
            foreach (string el in elements)
            {
                from.TryGetValue(el, out AnimationKeyFrameElement? a);
                to.TryGetValue(el, out AnimationKeyFrameElement? b);
                keyFrame.Elements[el] = LerpVanillaKeyElement(a, b, t);
            }
            keyFrames.Add(keyFrame);
        }
        return keyFrames.ToArray();
    }

    private static AnimationKeyFrameElement LerpVanillaKeyElement(AnimationKeyFrameElement? a, AnimationKeyFrameElement? b, double t)
    {
        AnimationKeyFrameElement dst = new();
        foreach (VanillaGenChannelTarget axis in VanillaGenAllTargets)
        {
            double? av = a == null ? null : GetVanillaGenChannelValue(a, axis);
            double? bv = b == null ? null : GetVanillaGenChannelValue(b, axis);
            if (av == null && bv == null) continue;
            double rest = axis is VanillaGenChannelTarget.StretchX or VanillaGenChannelTarget.StretchY or VanillaGenChannelTarget.StretchZ ? 1.0 : 0.0;
            SetVanillaGenChannelValue(dst, axis, (av ?? rest) + ((bv ?? rest) - (av ?? rest)) * t);
        }
        return dst;
    }

    // ---- Overlay mode (Task 5) ---------------------------------------------

    // Builds a single-frame partial pose that bends only the spine/neck/tail/head so it can be blended additively
    // over a running gait (steering, pitching, banking). The matching entity-meta - near-zero weight + scoped
    // elementBlendMode=addAverage - is produced by BuildVanillaMetaSnippet; this method just makes the pose.
    private Dictionary<string, AnimationKeyFrameElement> BuildVanillaOverlayPose(VanillaAnimationDocument document, List<string> targets, VanillaGenParams p)
    {
        Dictionary<string, AnimationKeyFrameElement> pose = new(StringComparer.OrdinalIgnoreCase);
        List<string> spine = [];
        List<string> neck = [];
        List<string> tail = [];
        string? head = null;
        foreach (string name in targets)
        {
            string l = name.ToLowerInvariant();
            if (IsVanillaLocoLeg(l) || IsVanillaLocoArm(l) || l.Contains("wing") || l.Contains("jaw")) continue;
            if (l.Contains("head")) head ??= name;
            else if (l.Contains("neck")) neck.Add(name);
            else if (l.Contains("tail")) tail.Add(name);
            else if (IsVanillaPoseSpine(l)) spine.Add(name);
        }

        void Add(string? el, VanillaGenChannelTarget axis, double v)
        {
            if (string.IsNullOrEmpty(el)) return;
            if (!pose.TryGetValue(el, out AnimationKeyFrameElement? e)) pose[el] = e = new AnimationKeyFrameElement();
            SetVanillaGenChannelValue(e, axis, (GetVanillaGenChannelValue(e, axis) ?? 0.0) + v);
        }
        // Distribute a total bend down a chain so it reads as a smooth curve, weighted toward the moving end.
        void Curve(List<string> chain, VanillaGenChannelTarget axis, double total, bool tipHeavy)
        {
            int n = chain.Count;
            if (n == 0) return;
            double weightSum = 0;
            for (int i = 0; i < n; i++) weightSum += tipHeavy ? (i + 1) : (n - i);
            for (int i = 0; i < n; i++)
            {
                double w = tipHeavy ? (i + 1) : (n - i);
                Add(chain[i], axis, total * w / weightSum);
            }
        }

        double amt = p.OverlayAmount;
        switch (p.Overlay)
        {
            case VanillaGenOverlay.TurnLeft:
            case VanillaGenOverlay.TurnRight:
            {
                double s = p.Overlay == VanillaGenOverlay.TurnLeft ? 1.0 : -1.0;
                Curve(spine, VanillaGenChannelTarget.RotationY, amt * 0.5 * s, tipHeavy: true);
                Curve(neck, VanillaGenChannelTarget.RotationY, amt * 0.8 * s, tipHeavy: true);
                Add(head, VanillaGenChannelTarget.RotationY, amt * s);
                Curve(tail, VanillaGenChannelTarget.RotationY, -amt * 0.7 * s, tipHeavy: true); // tail trails opposite
                break;
            }
            case VanillaGenOverlay.PitchUp:
            case VanillaGenOverlay.PitchDown:
            {
                double s = p.Overlay == VanillaGenOverlay.PitchUp ? 1.0 : -1.0;
                Curve(neck, VanillaGenChannelTarget.RotationZ, amt * 0.8 * s, tipHeavy: true);
                Add(head, VanillaGenChannelTarget.RotationZ, amt * s);
                Curve(tail, VanillaGenChannelTarget.RotationZ, amt * 0.5 * s, tipHeavy: true);
                break;
            }
            case VanillaGenOverlay.BankLeft:
            case VanillaGenOverlay.BankRight:
            {
                double s = p.Overlay == VanillaGenOverlay.BankLeft ? 1.0 : -1.0;
                Curve(spine, VanillaGenChannelTarget.RotationX, amt * s, tipHeavy: false);
                Curve(tail, VanillaGenChannelTarget.RotationX, amt * 0.6 * s, tipHeavy: true);
                break;
            }
            default: // LeanLeft / LeanRight
            {
                double s = p.Overlay == VanillaGenOverlay.LeanLeft ? 1.0 : -1.0;
                string? root = spine.Count > 0 ? spine[0] : head;
                Add(root, VanillaGenChannelTarget.OffsetZ, amt * 0.15 * s);   // 1/16 units lateral lean
                Add(root, VanillaGenChannelTarget.RotationX, amt * 0.4 * s);
                break;
            }
        }
        return pose;
    }

    private static AnimationKeyFrame[] BuildVanillaOverlayKeyFrames(Dictionary<string, AnimationKeyFrameElement> pose)
    {
        // A single keyframe at frame 0; the game holds it and the entity meta blends it additively.
        AnimationKeyFrame keyFrame = new()
        {
            Frame = 0,
            Elements = new Dictionary<string, AnimationKeyFrameElement>(pose, StringComparer.OrdinalIgnoreCase)
        };
        return [keyFrame];
    }

    // ---- Entity meta snippet (Tasks 6 / 32) --------------------------------

    /// <summary>Builds a ready-to-paste AnimationMetaData JSON block for the entity file, matching the generated
    /// animation's role: gaits get walk-speed coupling + footstep sounds at the plant frames, overlays get a
    /// near-zero weight with addAverage element blends scoped to the bent elements, gestures get AddAverage.</summary>
    private string BuildVanillaMetaSnippet(VanillaAnimationDocument? document, VanillaGenParams p)
    {
        string code = !string.IsNullOrWhiteSpace(_vanillaGenLastAnimationCode)
            ? _vanillaGenLastAnimationCode
            : document != null ? BuildUniqueVanillaAnimationCode(document, p.Code)
            : string.IsNullOrWhiteSpace(p.Code) ? "gen-anim" : p.Code.Trim();

        string blend = ResolveVanillaMetaBlend(p);
        bool overlay = p.Mode == VanillaGenMode.Overlay;
        bool gait = p.Mode == VanillaGenMode.Locomotion;

        List<string> lines = [];
        lines.Add("{");
        lines.Add($"\t\"code\": \"{code}\",");
        lines.Add($"\t\"animation\": \"{code}\",");
        lines.Add($"\t\"blendMode\": \"{blend}\",");
        // Overlays ride at ~zero base weight so only their scoped (addAverage) elements show through the gait.
        float weight = overlay ? 0.01f : p.MetaWeight;
        lines.Add($"\t\"weight\": {weight.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        lines.Add($"\t\"easeInSpeed\": {p.MetaEaseIn.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        lines.Add($"\t\"easeOutSpeed\": {p.MetaEaseOut.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        if (Math.Abs(p.MetaAnimSpeed - 1f) > 0.001f)
            lines.Add($"\t\"animationSpeed\": {p.MetaAnimSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        if (p.MetaMulWalkSpeed || gait)
            lines.Add("\t\"multiplyAnimationSpeedWithMovementSpeed\": true,");
        if (p.MetaSupressDefault || gait)
            lines.Add("\t\"supressDefaultAnimation\": true,");

        string trig = (p.MetaTrigger ?? "").Trim();
        if (!string.IsNullOrEmpty(trig))
        {
            if (string.Equals(trig, "default", StringComparison.OrdinalIgnoreCase))
                lines.Add("\t\"triggeredBy\": { \"defaultAnim\": true },");
            else
                lines.Add($"\t\"triggeredBy\": {{ \"onControls\": [ \"{trig}\" ] }},");
        }

        if (overlay && document != null)
        {
            // Scope the additive bend to exactly the elements the overlay touches.
            List<string> targets = ResolveVanillaGenTargets(document, p);
            Dictionary<string, AnimationKeyFrameElement> pose = BuildVanillaOverlayPose(document, targets, p);
            if (pose.Count > 0)
            {
                lines.Add("\t\"elementWeight\": {");
                lines.Add(string.Join(",\n", pose.Keys.Select(k => $"\t\t\"{k}\": 1")));
                lines.Add("\t},");
                lines.Add("\t\"elementBlendMode\": {");
                lines.Add(string.Join(",\n", pose.Keys.Select(k => $"\t\t\"{k}\": \"AddAverage\"")));
                lines.Add("\t},");
            }
        }

        if (p.EmitFootstepSounds && gait)
        {
            List<int> frames = VanillaFootstepFrames(p);
            if (frames.Count > 0)
            {
                lines.Add("\t\"animationSounds\": [");
                lines.Add(string.Join(",\n", frames.Select(f =>
                    $"\t\t{{ \"frame\": {f}, \"range\": 12, \"location\": \"{p.FootstepSound}\", \"pitch\": {{ \"avg\": 1, \"var\": 0.15 }}, \"volume\": {{ \"avg\": 1 }} }}")));
                lines.Add("\t],");
            }
        }

        // Trim the trailing comma on the last property line.
        int last = lines.Count - 1;
        if (lines[last].EndsWith(",")) lines[last] = lines[last][..^1];
        lines.Add("}");
        return string.Join("\n", lines);
    }

    private static string ResolveVanillaMetaBlend(VanillaGenParams p)
    {
        if (p.MetaBlend != VanillaGenBlend.Auto)
        {
            return p.MetaBlend switch
            {
                VanillaGenBlend.Add => "Add",
                VanillaGenBlend.Average => "Average",
                _ => "AddAverage"
            };
        }
        // Overlays/gestures layer additively; full-body gaits and held postures average.
        if (p.Mode == VanillaGenMode.Overlay) return "Average";
        if (p.Mode == VanillaGenMode.Pose) return p.ReturnToRest ? "AddAverage" : "Average";
        if (p.Mode == VanillaGenMode.Oscillation) return "AddAverage";
        return "Average";
    }

    /// <summary>Approximate foot-plant frames (start of stance) for the current gait across a standard
    /// four-foot layout - used to place footstep sounds (Task 32).</summary>
    private static List<int> VanillaFootstepFrames(VanillaGenParams p)
    {
        int frames = Math.Clamp(p.Frames, 1, 10000);
        SortedSet<int> set = [];
        for (int row = 0; row < 2; row++)
        {
            for (int side = 0; side < 2; side++)
            {
                double fraction = GaitPhaseFraction(p.Gait, row, 2, side);
                int frame = ((int)Math.Round(fraction * frames)) % frames;
                if (frame < 0) frame += frames;
                set.Add(frame);
            }
        }
        return set.ToList();
    }

    private static AnimationKeyFrameElement ScaleVanillaKeyElement(AnimationKeyFrameElement src, double factor)
    {
        AnimationKeyFrameElement dst = new();
        if (src.RotationX.HasValue) dst.RotationX = src.RotationX.Value * factor;
        if (src.RotationY.HasValue) dst.RotationY = src.RotationY.Value * factor;
        if (src.RotationZ.HasValue) dst.RotationZ = src.RotationZ.Value * factor;
        if (src.OffsetX.HasValue) dst.OffsetX = src.OffsetX.Value * factor;
        if (src.OffsetY.HasValue) dst.OffsetY = src.OffsetY.Value * factor;
        if (src.OffsetZ.HasValue) dst.OffsetZ = src.OffsetZ.Value * factor;
        if (src.StretchX.HasValue) dst.StretchX = src.StretchX.Value * factor;
        if (src.StretchY.HasValue) dst.StretchY = src.StretchY.Value * factor;
        if (src.StretchZ.HasValue) dst.StretchZ = src.StretchZ.Value * factor;
        return dst;
    }

    private static AnimationKeyFrame[] BuildVanillaGenKeyFrames(VanillaGenParams p, List<VanillaGenElementChannel> channels)
    {
        int frames = Math.Clamp(p.Frames, 1, 10000);
        int samples = Math.Clamp(p.SampleCount, 2, 240);

        List<int> sampleFrames = [];
        for (int i = 0; i < samples; i++)
        {
            int frame = (int)Math.Round((double)i * frames / samples);
            if (frame >= frames) frame = frames - 1;
            if (sampleFrames.Count == 0 || sampleFrames[^1] != frame) sampleFrames.Add(frame);
        }
        if (sampleFrames.Count == 0 || sampleFrames[0] != 0) sampleFrames.Insert(0, 0);

        Dictionary<string, List<VanillaGenElementChannel>> byElement = new(StringComparer.OrdinalIgnoreCase);
        foreach (VanillaGenElementChannel channel in channels)
        {
            if (!byElement.TryGetValue(channel.Element, out List<VanillaGenElementChannel>? list))
            {
                byElement[channel.Element] = list = [];
            }
            list.Add(channel);
        }

        List<AnimationKeyFrame> keyFrames = [];
        foreach (int frame in sampleFrames)
        {
            AnimationKeyFrame keyFrame = new()
            {
                Frame = frame,
                Elements = new Dictionary<string, AnimationKeyFrameElement>(StringComparer.OrdinalIgnoreCase)
            };
            double tNorm = (double)frame / frames;
            if (p.Reverse) tNorm = 1.0 - tNorm;
            foreach ((string element, List<VanillaGenElementChannel> list) in byElement)
            {
                AnimationKeyFrameElement keyElement = new();
                foreach (VanillaGenElementChannel channel in list)
                {
                    double cyclePos = channel.Frequency * tNorm + (channel.PhaseDeg + p.GlobalPhase) / 360.0;
                    double value;
                    if (channel.Curve != null)
                    {
                        // Foot-lock IK: the curve owns its value (an absolute joint angle), so amplitude/bias/intensity don't apply.
                        value = channel.Curve(cyclePos);
                    }
                    else
                    {
                        double raw = ApplyVanillaGenSharpness(EvalVanillaGenWave(channel.Wave, cyclePos, channel.Shape), channel.Sharpness);
                        value = channel.Bias + p.Intensity * channel.Amplitude * raw; // Intensity scales the oscillation, not the bias
                    }
                    AddVanillaGenChannelValue(keyElement, channel.Field, value);
                }
                keyFrame.Elements[element] = keyElement;
            }
            keyFrames.Add(keyFrame);
        }

        AnimationKeyFrame[] built = keyFrames.ToArray();
        if (p.JigglePhysics) ApplyVanillaSecondaryDynamics(built, frames, p);
        if (p.AutoRotShortest) ApplyVanillaRotShortest(built);
        if (p.OptimizeKeyFrames) built = OptimizeVanillaKeyFrames(built, frames, Math.Max(0.001, p.OptimizeTolerance));
        return built;
    }

    private static bool IsVanillaJiggleElement(string lower)
    {
        return IsVanillaLooseElement(lower) || lower.Contains("tail") || lower.Contains("ear") || lower.Contains("antenn")
            || lower.Contains("stinger") || lower.Contains("plume") || lower.Contains("dewlap") || lower.Contains("floof");
    }

    /// <summary>Cutting-edge physics layer: a damped-spring follow-through over the sampled keyframes for loose
    /// parts (tails, ears, crests, fins, plumes...). Each driven rotation/offset axis is the target of a 2nd-order
    /// spring (omega from Floppiness, damping from Bounce); the steady-state periodic response lags and overshoots
    /// so the part trails and settles like real soft tissue. The loop stays seamless because the response of a
    /// periodic drive is periodic. When the element hierarchy is known, the springs are COUPLED down each chain:
    /// a child is dragged by how far its parent already lagged, so the lag accumulates and the tip whips.</summary>
    private static void ApplyVanillaSecondaryDynamics(AnimationKeyFrame[] keyFrames, int frames, VanillaGenParams p)
    {
        if (keyFrames.Length < 3 || frames < 4) return;

        double floppy = Math.Clamp((double)p.Floppiness, 0.0, 1.0);
        double bounce = Math.Clamp((double)p.JiggleBounce, 0.0, 1.0);
        double omega = 2.0 * Math.PI * (5.0 + (1.4 - 5.0) * floppy); // natural frequency: stiff -> floppy
        double zeta = 1.0 + (0.18 - 1.0) * bounce;                   // critically damped -> springy
        Dictionary<string, string>? parents = p.HierarchyParents;
        const double coupling = 0.6;                                 // how strongly a child is dragged by its parent's lag

        // Jiggle elements present, ordered parent-before-child so a coupled child can read its parent's response.
        List<string> jiggle = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationKeyFrame kf in keyFrames)
            foreach (string el in kf.Elements.Keys)
                if (seen.Add(el) && IsVanillaJiggleElement(el.ToLowerInvariant())) jiggle.Add(el);
        if (jiggle.Count == 0) return;
        HashSet<string> jiggleSet = new(jiggle, StringComparer.OrdinalIgnoreCase);

        int Depth(string el)
        {
            int d = 0;
            string cur = el;
            HashSet<string> guard = new(StringComparer.OrdinalIgnoreCase);
            while (parents != null && parents.TryGetValue(cur, out string? par) && par != null && jiggleSet.Contains(par) && guard.Add(cur))
            {
                d++;
                cur = par;
            }
            return d;
        }
        jiggle.Sort((a, b) => Depth(a).CompareTo(Depth(b)));

        foreach (VanillaGenChannelTarget axis in VanillaGenAllTargets)
        {
            if (axis is VanillaGenChannelTarget.StretchX or VanillaGenChannelTarget.StretchY or VanillaGenChannelTarget.StretchZ) continue;

            Dictionary<string, double[]> targetOf = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double[]> responseOf = new(StringComparer.OrdinalIgnoreCase);

            // Keyed (dense, cyclic) target curve for every jiggle element that actually drives this axis.
            foreach (string el in jiggle)
            {
                List<(int frame, double value)> series = [];
                foreach (AnimationKeyFrame kf in keyFrames)
                {
                    if (kf.Elements.TryGetValue(el, out AnimationKeyFrameElement? e) && e != null && GetVanillaGenChannelValue(e, axis) is double v)
                    {
                        series.Add((kf.Frame, v));
                    }
                }
                if (series.Count < 3) continue;
                if (series.Max(s => s.value) - series.Min(s => s.value) < 0.05) continue; // effectively constant
                targetOf[el] = BuildVanillaCyclicTarget(series, frames);
            }
            if (targetOf.Count == 0) continue;

            // Solve in parent-first order; a coupled child's drive is dragged by its parent's lag (response - keyed).
            foreach (string el in jiggle)
            {
                if (!targetOf.TryGetValue(el, out double[]? target)) continue;
                double[] drive = target;
                if (parents != null && parents.TryGetValue(el, out string? par) && par != null
                    && responseOf.TryGetValue(par, out double[]? parResp) && targetOf.TryGetValue(par, out double[]? parTgt))
                {
                    drive = new double[frames];
                    for (int f = 0; f < frames; f++) drive[f] = target[f] + coupling * (parResp[f] - parTgt[f]);
                }
                responseOf[el] = SolveVanillaSpring(drive, frames, omega, zeta);
            }

            // Write the spring responses back where the axis was keyed.
            foreach ((string el, double[] response) in responseOf)
            {
                foreach (AnimationKeyFrame kf in keyFrames)
                {
                    if (kf.Elements.TryGetValue(el, out AnimationKeyFrameElement? e) && e != null && GetVanillaGenChannelValue(e, axis) != null)
                    {
                        SetVanillaGenChannelValue(e, axis, response[Math.Clamp(kf.Frame, 0, frames - 1)]);
                    }
                }
            }
        }
    }

    /// <summary>Cyclic linear interpolation of a sparse (frame, value) series onto every frame of the loop.</summary>
    private static double[] BuildVanillaCyclicTarget(List<(int frame, double value)> series, int frames)
    {
        double[] target = new double[frames];
        int n = series.Count;
        for (int f = 0; f < frames; f++)
        {
            int next = 0;
            while (next < n && series[next].frame < f) next++;
            (int frame, double value) hiPt = next < n ? series[next] : (series[0].frame + frames, series[0].value);
            (int frame, double value) loPt = next > 0 ? series[next - 1] : (series[n - 1].frame - frames, series[n - 1].value);
            double span = hiPt.frame - loPt.frame;
            target[f] = span <= 0 ? loPt.value : loPt.value + (hiPt.value - loPt.value) * ((f - loPt.frame) / span);
        }
        return target;
    }

    /// <summary>Integrates a damped spring x'' = w^2 (target - x) - 2 z w x' over a cyclic per-frame target until it
    /// reaches its steady-state periodic orbit, then returns the response at every frame. Sub-steps keep the
    /// explicit integration stable for stiff springs / coarse frame counts.</summary>
    private static double[] SolveVanillaSpring(double[] target, int frames, double omega, double zeta)
    {
        double dt = 1.0 / frames; // one loop spans 1.0 time unit
        int subSteps = Math.Max(1, (int)Math.Ceiling(omega * dt / 0.2));
        double subDt = dt / subSteps;
        double x = target[0];
        double v = 0.0;
        double[] response = new double[frames];
        // A few warm-up loops let the transient decay so the recorded loop is the steady-state orbit.
        for (int loop = 0; loop < 10; loop++)
        {
            bool record = loop == 9;
            for (int f = 0; f < frames; f++)
            {
                double tgt = target[f];
                for (int s = 0; s < subSteps; s++)
                {
                    double a = omega * omega * (tgt - x) - 2.0 * zeta * omega * v;
                    v += a * subDt;
                    x += v * subDt;
                }
                if (record) response[f] = x;
            }
        }
        return response;
    }

    private static readonly VanillaGenChannelTarget[] VanillaGenAllTargets =
    [
        VanillaGenChannelTarget.RotationX, VanillaGenChannelTarget.RotationY, VanillaGenChannelTarget.RotationZ,
        VanillaGenChannelTarget.OffsetX, VanillaGenChannelTarget.OffsetY, VanillaGenChannelTarget.OffsetZ,
        VanillaGenChannelTarget.StretchX, VanillaGenChannelTarget.StretchY, VanillaGenChannelTarget.StretchZ
    ];

    private static double? GetVanillaGenChannelValue(AnimationKeyFrameElement e, VanillaGenChannelTarget t) => t switch
    {
        VanillaGenChannelTarget.RotationX => e.RotationX,
        VanillaGenChannelTarget.RotationY => e.RotationY,
        VanillaGenChannelTarget.RotationZ => e.RotationZ,
        VanillaGenChannelTarget.OffsetX => e.OffsetX,
        VanillaGenChannelTarget.OffsetY => e.OffsetY,
        VanillaGenChannelTarget.OffsetZ => e.OffsetZ,
        VanillaGenChannelTarget.StretchX => e.StretchX,
        VanillaGenChannelTarget.StretchY => e.StretchY,
        _ => e.StretchZ
    };

    private static void SetVanillaGenChannelValue(AnimationKeyFrameElement e, VanillaGenChannelTarget t, double? v)
    {
        switch (t)
        {
            case VanillaGenChannelTarget.RotationX: e.RotationX = v; break;
            case VanillaGenChannelTarget.RotationY: e.RotationY = v; break;
            case VanillaGenChannelTarget.RotationZ: e.RotationZ = v; break;
            case VanillaGenChannelTarget.OffsetX: e.OffsetX = v; break;
            case VanillaGenChannelTarget.OffsetY: e.OffsetY = v; break;
            case VanillaGenChannelTarget.OffsetZ: e.OffsetZ = v; break;
            case VanillaGenChannelTarget.StretchX: e.StretchX = v; break;
            case VanillaGenChannelTarget.StretchY: e.StretchY = v; break;
            default: e.StretchZ = v; break;
        }
    }

    /// <summary>Task 3: when a rotation channel's value spans more than 180 deg across the loop, flag it so the
    /// game interpolates the short way around (otherwise it spins the long way / backwards).</summary>
    private static void ApplyVanillaRotShortest(AnimationKeyFrame[] keyFrames)
    {
        if (keyFrames.Length == 0) return;
        HashSet<string> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationKeyFrame kf in keyFrames)
            foreach (string el in kf.Elements.Keys) elements.Add(el);

        foreach (string element in elements)
        {
            foreach ((VanillaGenChannelTarget axis, int flag) in new[]
            {
                (VanillaGenChannelTarget.RotationX, 0), (VanillaGenChannelTarget.RotationY, 1), (VanillaGenChannelTarget.RotationZ, 2)
            })
            {
                double min = double.MaxValue, max = double.MinValue;
                bool any = false;
                foreach (AnimationKeyFrame kf in keyFrames)
                {
                    if (!kf.Elements.TryGetValue(element, out AnimationKeyFrameElement? e) || e == null) continue;
                    double? v = GetVanillaGenChannelValue(e, axis);
                    if (v == null) continue;
                    any = true;
                    min = Math.Min(min, v.Value);
                    max = Math.Max(max, v.Value);
                }
                if (!any || max - min <= 180.0) continue;
                foreach (AnimationKeyFrame kf in keyFrames)
                {
                    if (!kf.Elements.TryGetValue(element, out AnimationKeyFrameElement? e) || e == null) continue;
                    if (flag == 0) e.RotShortestDistanceX = true;
                    else if (flag == 1) e.RotShortestDistanceY = true;
                    else e.RotShortestDistanceZ = true;
                }
            }
        }
    }

    /// <summary>Tasks 1/2: drop per-channel keyframe values that lie on the straight line between their kept
    /// neighbours (within tolerance), exploiting the game's independent per-flag keyframe seeking. Keyframe
    /// endpoints are always kept so the loop stays seamless; emptied elements / keyframes are removed.</summary>
    private static AnimationKeyFrame[] OptimizeVanillaKeyFrames(AnimationKeyFrame[] keyFrames, int frames, double tolerance)
    {
        if (keyFrames.Length <= 2) return keyFrames;

        HashSet<string> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationKeyFrame kf in keyFrames)
            foreach (string el in kf.Elements.Keys) elements.Add(el);

        foreach (string element in elements)
        {
            foreach (VanillaGenChannelTarget axis in VanillaGenAllTargets)
            {
                // (keyframe index, frame, value) for every keyframe that sets this channel on this element.
                List<(int idx, double f, double v)> series = [];
                for (int i = 0; i < keyFrames.Length; i++)
                {
                    if (!keyFrames[i].Elements.TryGetValue(element, out AnimationKeyFrameElement? e) || e == null) continue;
                    double? v = GetVanillaGenChannelValue(e, axis);
                    if (v != null) series.Add((i, keyFrames[i].Frame, v.Value));
                }
                if (series.Count <= 2) continue;

                // Local collinearity test against immediate neighbours; endpoints always survive.
                for (int k = 1; k < series.Count - 1; k++)
                {
                    (int idx, double f, double v) = series[k];
                    (_, double fp, double vp) = series[k - 1];
                    (_, double fn, double vn) = series[k + 1];
                    double span = fn - fp;
                    double interp = span <= 0 ? vp : vp + (vn - vp) * ((f - fp) / span);
                    if (Math.Abs(v - interp) <= tolerance)
                    {
                        SetVanillaGenChannelValue(keyFrames[idx].Elements[element], axis, null);
                    }
                }
            }
        }

        // Drop emptied elements, then keyframes that became empty (except the first and last so the loop is bounded).
        List<AnimationKeyFrame> result = [];
        for (int i = 0; i < keyFrames.Length; i++)
        {
            AnimationKeyFrame kf = keyFrames[i];
            foreach (string el in kf.Elements.Keys.ToList())
            {
                if (!kf.Elements[el].AnySet) kf.Elements.Remove(el);
            }
            bool boundary = i == 0 || i == keyFrames.Length - 1;
            if (boundary || kf.Elements.Count > 0) result.Add(kf);
        }
        return result.ToArray();
    }

    /// <summary>Warps a wave value in [-1,1] toward (sharpness &gt; 1) or away from (sharpness &lt; 1) the extremes,
    /// keeping the sign. 1 is a no-op. Locomotion channels pass 1 so their duty-shaped curves are untouched.</summary>
    private static double ApplyVanillaGenSharpness(double value, double sharpness)
    {
        if (Math.Abs(sharpness - 1.0) < 1e-3) return value;
        double exponent = 1.0 / Math.Clamp(sharpness, 0.1, 8.0);
        return Math.Sign(value) * Math.Pow(Math.Abs(value), exponent);
    }

    private static double EvalVanillaGenWave(VanillaGenWave wave, double cyclePos, double shape)
    {
        double t = cyclePos - Math.Floor(cyclePos);
        return wave switch
        {
            VanillaGenWave.Sine => Math.Sin(2.0 * Math.PI * t),
            VanillaGenWave.Cosine => Math.Cos(2.0 * Math.PI * t),
            VanillaGenWave.Triangle => t < 0.25 ? 4.0 * t : t < 0.75 ? 2.0 - 4.0 * t : 4.0 * t - 4.0,
            VanillaGenWave.Sawtooth => 2.0 * t - 1.0,
            VanillaGenWave.Square => t < 0.5 ? 1.0 : -1.0,
            VanillaGenWave.Noise => EvalVanillaGenNoise(t, shape),
            VanillaGenWave.Stance => EvalVanillaGenStance(t, shape),
            VanillaGenWave.SwingBump => EvalVanillaGenSwingBump(t, shape),
            _ => 0.0
        };
    }

    /// <summary>Seamless pseudo-noise in [-1,1]: a sum of integer harmonics with seeded phases, so it loops
    /// cleanly while reading as an irregular organic wobble (idle fidgets). <paramref name="seed"/> rides the
    /// channel's Shape slot so each element wobbles differently yet reproducibly.</summary>
    private static double EvalVanillaGenNoise(double t, double seed)
    {
        double value = 0.0;
        double amp = 1.0;
        double norm = 0.0;
        unchecked
        {
            uint s = (uint)(int)seed * 2654435761u + 1u;
            for (int k = 1; k <= 4; k++)
            {
                s = s * 1664525u + 1013904223u;
                double phase = (s & 0xFFFFu) / 65536.0;
                value += amp * Math.Sin(2.0 * Math.PI * (k * t + phase));
                norm += amp;
                amp *= 0.55;
            }
        }
        return norm > 0.0 ? value / norm : 0.0;
    }

    /// <summary>Duty-shaped fore-aft stride: the foot starts forward, is pushed slowly back across the stance
    /// fraction (planted feel), then swings quickly forward across the rest of the cycle. Returns [-1, 1].</summary>
    private static double EvalVanillaGenStance(double t, double duty)
    {
        duty = Math.Clamp(duty <= 0.0 ? 0.5 : duty, 0.1, 0.9);
        if (t < duty) return 1.0 - 2.0 * (t / duty);     // stance: linear forward -> back
        double u = (t - duty) / (1.0 - duty);
        return -Math.Cos(Math.PI * u);                   // swing: smooth back -> forward
    }

    /// <summary>One-sided flex that is zero during stance and a single smooth arch (0 -> 1 -> 0) during the swing
    /// phase. Drives the knee/ankle so they only bend while the foot is in the air. Returns [0, 1].</summary>
    private static double EvalVanillaGenSwingBump(double t, double duty)
    {
        duty = Math.Clamp(duty <= 0.0 ? 0.5 : duty, 0.1, 0.9);
        if (t < duty) return 0.0;
        double u = (t - duty) / (1.0 - duty);
        return Math.Sin(Math.PI * u);
    }

    private static void AddVanillaGenChannelValue(AnimationKeyFrameElement element, VanillaGenChannelTarget field, double value)
    {
        switch (field)
        {
            case VanillaGenChannelTarget.RotationX: element.RotationX = (element.RotationX ?? 0.0) + value; break;
            case VanillaGenChannelTarget.RotationY: element.RotationY = (element.RotationY ?? 0.0) + value; break;
            case VanillaGenChannelTarget.RotationZ: element.RotationZ = (element.RotationZ ?? 0.0) + value; break;
            case VanillaGenChannelTarget.OffsetX: element.OffsetX = (element.OffsetX ?? 0.0) + value; break;
            case VanillaGenChannelTarget.OffsetY: element.OffsetY = (element.OffsetY ?? 0.0) + value; break;
            case VanillaGenChannelTarget.OffsetZ: element.OffsetZ = (element.OffsetZ ?? 0.0) + value; break;
            case VanillaGenChannelTarget.StretchX: element.StretchX = (element.StretchX ?? 1.0) + value; break;
            case VanillaGenChannelTarget.StretchY: element.StretchY = (element.StretchY ?? 1.0) + value; break;
            default: element.StretchZ = (element.StretchZ ?? 1.0) + value; break;
        }
    }

    private static List<Regex> BuildVanillaGenGlobs(string filter)
    {
        List<Regex> globs = [];
        if (string.IsNullOrWhiteSpace(filter)) return globs;

        foreach (string raw in filter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string pattern = raw.Contains('*') || raw.Contains('?')
                ? "^" + Regex.Escape(raw).Replace("\\*", ".*").Replace("\\?", ".") + "$"
                : Regex.Escape(raw);
            globs.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        return globs;
    }

    private static List<string> BuildVanillaShapeDfsOrder(Shape shape)
    {
        List<string> order = [];
        foreach (ShapeElement root in shape.Elements ?? [])
        {
            VisitVanillaShapeElement(root, order);
        }
        return order;
    }

    private static void VisitVanillaShapeElement(ShapeElement element, List<string> order)
    {
        if (!string.IsNullOrWhiteSpace(element.Name)) order.Add(element.Name);
        foreach (ShapeElement child in element.Children ?? [])
        {
            VisitVanillaShapeElement(child, order);
        }
    }

    private static ShapeElement? FindVanillaShapeElement(Shape shape, string name)
    {
        foreach (ShapeElement root in shape.Elements ?? [])
        {
            ShapeElement? found = FindVanillaShapeElementRecursive(root, name);
            if (found != null) return found;
        }
        return null;
    }

    private static ShapeElement? FindVanillaShapeElementRecursive(ShapeElement element, string name)
    {
        if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase)) return element;
        foreach (ShapeElement child in element.Children ?? [])
        {
            ShapeElement? found = FindVanillaShapeElementRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // ---- Commit ------------------------------------------------------------

    private void GenerateVanillaGeneratedAnimation(VanillaAnimationDocument document, bool regenerateInPlace, bool livePreviewUpdate = false)
    {
        if (document.Shape == null) return;

        VanillaGenParams p = _vanillaGenParams;
        List<string> targets = ResolveVanillaGenTargets(document, p);
        if (targets.Count == 0)
        {
            _vanillaStatus = "Animation generator: no target elements matched.";
            return;
        }

        AnimationKeyFrame[] keyFrames;
        if (p.Mode == VanillaGenMode.Pose)
        {
            if (p.PoseTransition)
            {
                // Task 28: animate FROM another pose into the chosen action (e.g. downed -> stand).
                VanillaGenAction savedAction = p.Action;
                p.Action = p.FromAction;
                Dictionary<string, AnimationKeyFrameElement> fromPose = BuildVanillaPose(document, targets, p);
                p.Action = savedAction;
                Dictionary<string, AnimationKeyFrameElement> toPose = BuildVanillaPose(document, targets, p);
                if (fromPose.Count == 0 && toPose.Count == 0)
                {
                    _vanillaStatus = "Animation generator: the transition matched no rig parts.";
                    return;
                }
                keyFrames = BuildVanillaTransitionKeyFrames(p, fromPose, toPose);
            }
            else
            {
                Dictionary<string, AnimationKeyFrameElement> pose = BuildVanillaPose(document, targets, p);
                if (pose.Count == 0)
                {
                    _vanillaStatus = "Animation generator: the pose matched no rig parts (need named legs/spine/head/etc.).";
                    return;
                }
                keyFrames = BuildVanillaPoseKeyFrames(p, pose);
            }
        }
        else if (p.Mode == VanillaGenMode.Overlay)
        {
            Dictionary<string, AnimationKeyFrameElement> overlay = BuildVanillaOverlayPose(document, targets, p);
            if (overlay.Count == 0)
            {
                _vanillaStatus = "Animation generator: the overlay matched no spine/neck/tail elements.";
                return;
            }
            keyFrames = BuildVanillaOverlayKeyFrames(overlay);
        }
        else
        {
            List<VanillaGenElementChannel> channels = BuildVanillaGenElementChannels(document, targets, p);
            if (channels.Count == 0)
            {
                _vanillaStatus = "Animation generator: no active channels to apply.";
                return;
            }
            // Hand the element hierarchy to the keyframe builder so coupled-chain jiggle physics can drag each
            // loose segment by its parent's lag (a real tail/neck whip). Null in the test path = independent springs.
            p.HierarchyParents = p.JigglePhysics ? BuildVanillaParentMap(document) : null;
            keyFrames = BuildVanillaGenKeyFrames(p, channels);
        }

        VanillaShapeAnimationEntry? overwriteEntry = null;
        if (regenerateInPlace && !string.IsNullOrWhiteSpace(_vanillaGenLastAnimationCode))
        {
            overwriteEntry = document.ShapeAnimations.FirstOrDefault(entry =>
                string.Equals(entry.Animation.Code, _vanillaGenLastAnimationCode, StringComparison.OrdinalIgnoreCase));
        }
        else if (p.OverwriteSelected)
        {
            VanillaBrowserRow? selectedRow = FindVanillaBrowserRow(_vanillaSelection.RowKey);
            if (selectedRow?.ShapeAnimation != null && ReferenceEquals(selectedRow.ShapeAnimation.Document, document))
            {
                overwriteEntry = selectedRow.ShapeAnimation;
            }
        }

        // Task 35: inherit the per-animation Version (rotation convention) from sibling animations so generated
        // clips match hand-made ones on the same shape, instead of hardcoding 0.
        int siblingVersion = document.ShapeAnimations
            .Where(e => overwriteEntry == null || !ReferenceEquals(e, overwriteEntry))
            .Select(e => e.Animation.Version)
            .GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault() ?? 0;

        if (overwriteEntry != null)
        {
            if (!livePreviewUpdate)
            {
                CommitPendingVanillaHistory();
            }
            string code = overwriteEntry.Animation.Code ?? overwriteEntry.Animation.Name ?? "generated";
            PopulateVanillaGenAnimation(overwriteEntry.Animation, p, code, keyFrames, overwriteEntry.Animation.Version);
            _vanillaGenLastAnimationCode = code;
            MarkVanillaDirty(document);

            InvalidateVanillaBrowserRows();
            EnsureVanillaBrowserVisibleRows();
            VanillaBrowserRow row = BuildVanillaBrowserRow(overwriteEntry);
            bool wasPlaying = _vanillaPreviewScene?.Key == row.Key && _vanillaPreviewScene.Playing;
            SelectVanillaRow(row);
            RefreshVanillaPreviewAfterEdit(row);
            if ((wasPlaying || livePreviewUpdate || p.Loop) && _vanillaPreviewScene?.Key == row.Key)
            {
                _vanillaPreviewScene.Play();
            }
            _vanillaStatus = livePreviewUpdate
                ? $"Animation generator: live-updated '{code}' ({keyFrames.Length} keyframes over {targets.Count} element(s))."
                : $"Animation generator: updated '{code}' ({keyFrames.Length} keyframes over {targets.Count} element(s)).";
            return;
        }

        string newCode = BuildUniqueVanillaAnimationCode(document, p.Code);
        VanillaAnimation animation = new();
        PopulateVanillaGenAnimation(animation, p, newCode, keyFrames, siblingVersion);
        VanillaShapeAnimationEntry shapeEntry = new(document, document.ShapeAnimations.Count, animation, null);
        document.ShapeAnimations.Add(shapeEntry);
        _vanillaGenLastAnimationCode = newCode;
        MarkVanillaDirty(document);
        SelectAndPreviewVanillaShapeAnimation(newCode, shapeEntry);
        if (p.Loop && _vanillaPreviewScene?.Key == _vanillaSelection.RowKey)
        {
            _vanillaPreviewScene.Play();
        }
        _vanillaStatus = $"Animation generator: created '{newCode}' ({keyFrames.Length} keyframes over {targets.Count} element(s)).";
    }

    private void UpdateVanillaGenLivePreview(VanillaAnimationDocument document, VanillaGenParams p, bool canGenerate, int targetCount)
    {
        string fingerprint = BuildVanillaGenLiveFingerprint(p, targetCount);
        if (!_vanillaGenLiveUpdate || !canGenerate || _vanillaGenApplyingLiveUpdate || string.IsNullOrWhiteSpace(_vanillaGenLastAnimationCode))
        {
            _vanillaGenLiveFingerprint = fingerprint;
            return;
        }

        VanillaBrowserRow? selectedRow = FindVanillaBrowserRow(_vanillaSelection.RowKey);
        bool selectedLastGenerated = selectedRow?.ShapeAnimation != null
            && ReferenceEquals(selectedRow.ShapeAnimation.Document, document)
            && string.Equals(selectedRow.ShapeAnimation.Animation.Code ?? selectedRow.ShapeAnimation.Animation.Name, _vanillaGenLastAnimationCode, StringComparison.OrdinalIgnoreCase);
        if (!selectedLastGenerated || _vanillaPreviewScene?.Key != selectedRow!.Key || !_vanillaPreviewScene.Playing)
        {
            _vanillaGenLiveFingerprint = fingerprint;
            return;
        }

        if (string.IsNullOrEmpty(_vanillaGenLiveFingerprint))
        {
            _vanillaGenLiveFingerprint = fingerprint;
            return;
        }

        if (string.Equals(_vanillaGenLiveFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _vanillaGenLiveFingerprint = fingerprint;
        _vanillaGenApplyingLiveUpdate = true;
        try
        {
            GenerateVanillaGeneratedAnimation(document, regenerateInPlace: true, livePreviewUpdate: true);
        }
        finally
        {
            _vanillaGenApplyingLiveUpdate = false;
        }
    }

    private static string BuildVanillaGenLiveFingerprint(VanillaGenParams p, int targetCount)
    {
        StringBuilder builder = new();
        builder.Append(targetCount).Append('|');
        AppendVanillaGenFingerprintValue(builder, p);
        return builder.ToString();
    }

    private static void AppendVanillaGenFingerprintValue(StringBuilder builder, object? value)
    {
        if (value == null)
        {
            builder.Append("<null>");
            return;
        }

        Type type = value.GetType();
        if (value is string text)
        {
            builder.Append('"').Append(text).Append('"');
            return;
        }

        if (type.IsPrimitive || type.IsEnum || value is decimal)
        {
            builder.Append(value);
            return;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            builder.Append('[');
            foreach (object? item in enumerable)
            {
                AppendVanillaGenFingerprintValue(builder, item);
                builder.Append(',');
            }
            builder.Append(']');
            return;
        }

        builder.Append(type.Name).Append('{');
        foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (string.Equals(field.Name, nameof(VanillaGenParams.HierarchyParents), StringComparison.Ordinal)) continue;
            builder.Append(field.Name).Append('=');
            AppendVanillaGenFingerprintValue(builder, field.GetValue(value));
            builder.Append(';');
        }
        builder.Append('}');
    }

    private static void PopulateVanillaGenAnimation(VanillaAnimation animation, VanillaGenParams p, string code, AnimationKeyFrame[] keyFrames, int version)
    {
        animation.Code = code;
        animation.Name = string.IsNullOrWhiteSpace(p.Name) ? code : p.Name.Trim();
        animation.QuantityFrames = p.Mode == VanillaGenMode.Overlay ? 1 : Math.Clamp(p.Frames, 1, 10000);
        animation.Version = version;
        animation.EaseAnimationSpeed = p.EaseSpeed;
        animation.OnActivityStopped = ResolveVanillaStopHandling(p);
        animation.OnAnimationEnd = ResolveVanillaEndHandling(p);
        animation.KeyFrames = keyFrames;
    }

    /// <summary>Task 4: resolve the loop-end handling. Auto loops cycles, stops one-shot gestures, and holds death.</summary>
    private static EnumEntityAnimationEndHandling ResolveVanillaEndHandling(VanillaGenParams p)
    {
        if (p.OnEnd != VanillaGenEndHandling.Auto)
        {
            return p.OnEnd switch
            {
                VanillaGenEndHandling.Repeat => EnumEntityAnimationEndHandling.Repeat,
                VanillaGenEndHandling.Stop => EnumEntityAnimationEndHandling.Stop,
                VanillaGenEndHandling.Hold => EnumEntityAnimationEndHandling.Hold,
                _ => EnumEntityAnimationEndHandling.EaseOut
            };
        }
        if (p.Mode == VanillaGenMode.Overlay) return EnumEntityAnimationEndHandling.Hold; // 1-frame held overlay
        if (p.Mode == VanillaGenMode.Pose && p.ReturnToRest)
        {
            return p.Action is VanillaGenAction.Death or VanillaGenAction.Collapse
                ? EnumEntityAnimationEndHandling.Hold
                : EnumEntityAnimationEndHandling.Stop;
        }
        return p.Loop ? EnumEntityAnimationEndHandling.Repeat : EnumEntityAnimationEndHandling.Stop;
    }

    /// <summary>Task 4: resolve the activity-stopped handling. Auto eases out cycles (smoother than Rewind) and
    /// lets one-shot gestures play to the end.</summary>
    private static EnumEntityActivityStoppedHandling ResolveVanillaStopHandling(VanillaGenParams p)
    {
        if (p.OnStop != VanillaGenStopHandling.Auto)
        {
            return p.OnStop switch
            {
                VanillaGenStopHandling.EaseOut => EnumEntityActivityStoppedHandling.EaseOut,
                VanillaGenStopHandling.Rewind => EnumEntityActivityStoppedHandling.Rewind,
                VanillaGenStopHandling.Stop => EnumEntityActivityStoppedHandling.Stop,
                _ => EnumEntityActivityStoppedHandling.PlayTillEnd
            };
        }
        if (p.Mode == VanillaGenMode.Pose && p.ReturnToRest) return EnumEntityActivityStoppedHandling.PlayTillEnd;
        return EnumEntityActivityStoppedHandling.EaseOut;
    }
}
