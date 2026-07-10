using ImGuiNET;
using InGameDevTools.Utils;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    // Parametric creature generator: assembles a complete, animation-ready box skeleton from
    // high-level parameters (limb counts, segment/joint counts, sizes, angles). It mirrors the
    // Prism helper's pattern - a parameter window with a live wireframe ghost and a single-undo
    // commit - but instead of merging cuboids into one solid it nests parented box segments so
    // every segment doubles as a clean animation joint.
    //
    // Coordinate convention (everything in shape units, 16 = 1 block):
    //   X = left(-)/right(+), Y = down(-)/up(+), Z = back(-)/front(+, head end).
    // The root group sits at the chosen center (From == To == RotationOrigin == center) and is
    // face-less. Every descendant box is expressed RELATIVE to its parent's From, exactly how the
    // renderer composes element transforms (see ModelLocalElementMatrix), so chains curve naturally
    // when their joint rotations are animated.
    private const int ModelCreatureMaxElements = 300;
    private const double ModelCreatureAutoUvMaxTileUnits = 16.0;

    private enum ModelCreatureArchetype
    {
        Quadruped,
        Biped,
        Serpent,
        Hexapod,
        Bird,
        Wolf,
        Dragon,
        Mammoth,
        Bovine,
        Stag,
        Scorpion
    }

    private static readonly string[] ModelCreatureArchetypeLabels =
    [
        "Quadruped",
        "Biped",
        "Serpent",
        "Hexapod / insect",
        "Bird",
        "Wolf (detailed)",
        "Dragon (showcase)",
        "Mammoth (showcase)",
        "Bovine (showcase)",
        "Stag (showcase)",
        "Scorpion (showcase)"
    ];

    private enum ModelCreatureWingStyle
    {
        Feathered,
        Membrane
    }

    private static readonly string[] ModelCreatureWingStyleLabels =
    [
        "Feathered / limb",
        "Membrane (bat / dragon)"
    ];

    // Per-side arm tips, so a creature can be asymmetric (e.g. one blade arm, one normal hand).
    private enum ModelCreatureArmAppendage
    {
        Normal,
        Blade,
        Claw,
        Club,
        Hook,
        None
    }

    private static readonly string[] ModelCreatureArmAppendageLabels =
    [
        "Normal (hand / hands toggle)",
        "Blade",
        "Big claw",
        "Club / mace",
        "Hook",
        "None (bare stump)"
    ];

    private sealed class ModelCreatureParams
    {
        public NVector3 Center = new(8f, 8f, 8f);
        public NVector3 Rotation;
        public string Texture = "";
        public bool AutoTexture;
        public int Seed = 1;

        // Spine / torso
        public int SpineSegments = 4;
        public float SpineLength = 16f;
        public float BodyWidth = 7f;
        public float BodyHeight = 7f;
        public float BodyTaper = 0.85f;

        // Neck + head
        public int NeckSegments = 2;
        public float NeckLength = 6f;
        public float NeckThickness = 4f;
        public float NeckPitch = -22f;
        public float HeadWidth = 5f;
        public float HeadHeight = 5f;
        public float HeadDepth = 6f;
        public bool Snout;
        public float SnoutLength = 3f;
        public float SnoutSize = 3f;

        // Legs (rear/primary limbs)
        public int LegPairs = 2;
        public int LegSegments = 3;
        public float LegLength = 11f;
        public float LegThickness = 3f;
        public float LegSplay = 4f;
        public float LegBend;
        public float FrontLegPos = 0.18f;
        public float RearLegPos = 0.85f;

        // Arms (front/upper limbs)
        public int ArmPairs;
        public int ArmSegments = 3;
        public float ArmLength = 8f;
        public float ArmThickness = 2.5f;
        public float ArmSplay = 8f;
        public float ArmBend;
        public float ArmPos = 0.22f;

        // Asymmetry: independent right/left arm tips (one blade, one hand, ...). Off = symmetric (unchanged).
        public bool AsymmetricArms;
        public int ArmAppendageRight = (int)ModelCreatureArmAppendage.Normal;
        public int ArmAppendageLeft = (int)ModelCreatureArmAppendage.Normal;
        public float ArmAppendageLength = 9f;   // blade / hook reach
        public float ArmAppendageSize = 2.5f;   // blade height / claw / club size

        // Tail
        public int TailSegments = 4;
        public float TailLength = 10f;
        public float TailThickness = 3f;
        public float TailTaper = 0.25f;
        public float TailDroop = 6f;

        // Wings
        public int WingPairs;
        public int WingSegments = 2;
        public float WingSpan = 12f;
        public float WingChord = 6f;
        public float WingThickness = 1.5f;
        public float WingPos = 0.28f;
        public float WingSweep;

        // Head details
        public int Ears;
        public float EarSize = 2f;
        public float EarHeight = 3f;
        public int HornPairs;
        public int HornSegments = 2;
        public float HornLength = 5f;
        public float HornThickness = 1.5f;
        public float HornCurl = 12f;
        public bool Eyes;
        public float EyeSize = 1f;

        // ---- Advanced (defaults chosen so they reproduce the basic-mode geometry exactly) ----

        // Global
        public float UniformScale = 1f;        // scales the whole creature about its center

        // Spine / torso
        public float BodyFrontTaper = 1f;      // cross-section scale at the head end
        public float BodyBulge = 1f;           // mid-spine cross-section multiplier (belly / barrel chest)
        public float SpineCurve;               // deg per joint, + arches the back up, - sags it

        // Neck + head
        public float NeckTaper = 1f;           // neck thickness at the head end vs the base
        public float NeckCurve;                // deg per joint (swan / S curve)
        public float HeadTilt;                 // deg nod of the head about Z
        public float SnoutDroop;               // deg down-turn of the snout/beak
        public float SnoutWidthMul = 1f;       // lateral scale of the snout box
        public float SnoutHeightMul = 1f;      // vertical scale of the snout box (low = flat beak)

        // Legs
        public float LegTaper = 1f;            // distal segment thickness vs the hip
        public float LegForwardLean;           // deg fore/aft lean of the whole leg
        public bool Feet;
        public float FootLength = 4f;
        public float FootWidth = 3f;
        public float FootHeight = 1.5f;

        // Arms
        public float ArmTaper = 1f;
        public float ArmDownAngle;             // deg fore/aft lean of the whole arm
        public bool Hands;
        public float HandLength = 3f;
        public float HandWidth = 2.5f;
        public float HandHeight = 1.5f;

        // Tail
        public float TailBaseAngle;            // deg lift of the tail base
        public float TailWidthMul = 1f;
        public float TailHeightMul = 1f;       // <1 = flat (beaver/fish) tail, >1 = tall fin

        // Wings
        public float WingDihedral;             // deg, + raises both wings (a dihedral)
        public float WingChordTaper = 1f;      // tip chord vs base chord
        public float WingHeight = 0.8f;        // vertical attach point on the body (0..1)

        // Head details
        public float EarAngle = 8f;            // lateral splay of the ears
        public float EarForward = 0.35f;       // fore/aft position on the head (0..1)
        public float HornSplay = 10f;          // lateral splay of the horns
        public float HornForward = 0.7f;       // fore/aft anchor of a single horn pair (0..1)
        public float HornPitch;                // deg base lean of the horns (+ back)
        public float EyeForward = 0.72f;       // fore/aft position on the head (0..1)
        public float EyeHeight = 0.62f;        // vertical position on the head (0..1)

        // Dorsal ridge (plates / spikes / fin running along the back)
        public int DorsalSpikes;
        public float DorsalSpikeHeight = 4f;
        public float DorsalSpikeLength = 2f;   // fore-aft footprint of each plate
        public float DorsalSpikeWidth = 1.5f;
        public float DorsalSpikeAngle = 20f;   // deg sweep back
        public float DorsalSpikeStart = 0.15f; // span along the spine (0 = head end, 1 = tail end)
        public float DorsalSpikeEnd = 0.85f;

        // ---- Organic detail (toward hand-made fidelity; all default to no-ops) ----

        // Limb attachment volumes embedded in the body (shoulders for arms, haunches for legs)
        public bool Shoulders;
        public float ShoulderLength = 6f;     // vertical extent
        public float ShoulderWidth = 5f;      // fore-aft extent
        public float ShoulderThickness = 3f;  // lateral extent
        public float ShoulderEmbed = 0.6f;    // 0 = fully outside the body, 1 = fully inside

        // Per-segment limb zigzag (a natural crouch: thigh back, shank forward, ...)
        public float LegZigzag;
        public float ArmZigzag;

        // Fluffy tail
        public float TailBulge = 1f;          // mid-tail cross-section multiplier

        // Rounded ears + inner ear
        public int EarSegments = 1;           // >1 stacks tapering boxes for a rounded ear
        public float EarTaper = 1f;           // ear tip vs base cross-section
        public bool InnerEars;                // inset inner-ear box

        // Mouth / jaw / teeth
        public bool Mouth;
        public float JawLength = 4f;
        public float JawDrop;                 // deg the jaw hangs open
        public int Fangs;                     // fangs per side (0..6)
        public float FangSize = 0.75f;
        public float FangLength = 1.5f;

        // Fur cheeks (patches on the head sides)
        public bool Cheeks;
        public float CheekSize = 3f;          // vertical extent
        public float CheekLength = 4f;        // fore-aft extent
        public float CheekAngle = 22f;        // deg sweep back

        // Nose tip
        public bool Nose;
        public float NoseSize = 1.5f;

        // ---- High-fidelity detail (toes, eyes, crests, belly, membrane wings, tail fin) ----

        // Toes / claws on feet and hands
        public int Toes;                      // claws per extremity (0..5)
        public float ClawLength = 2.5f;
        public float ClawSize = 0.7f;
        public float ClawSplay = 14f;         // lateral fan (deg)
        public float ClawCurve = -12f;        // downward hook (deg)

        // Layered eyes
        public bool Pupils;
        public float PupilSize = 0.5f;

        // Brow ridge over the eyes
        public bool Brow;
        public float BrowSize = 1.3f;

        // Head crest / frill (swept-back plates along the top of the head)
        public bool Crest;
        public int CrestCount = 4;
        public float CrestHeight = 4f;
        public float CrestLength = 1.5f;
        public float CrestAngle = -55f;       // laid back
        public float CrestStart = 0.1f;       // along the head (0 = back of head, 1 = front)
        public float CrestEnd = 0.75f;

        // Belly / underbody volume
        public bool Belly;
        public float BellyDepth = 3f;         // how far below the spine it bulges
        public float BellySize = 1.05f;       // lateral fraction of the body width

        // Wings: feathered limb vs membrane (dragon/bat) with finger bones + webbing
        public int WingStyle;                 // ModelCreatureWingStyle
        public int WingFingers = 4;
        public float WingMembraneTrail = 9f;  // how far the membrane trails behind each finger

        // Tail fin / fluke at the tail tip
        public bool TailFin;
        public float TailFinHeight = 5f;
        public float TailFinLength = 4f;
        public bool TailFinVertical = true;   // vertical fin (fish/dragon) vs horizontal fluke (whale)

        // ---- Megafauna detail (FotSA: trunk, tusks, hump, dewlap, tail tuft, ear shaping) ----

        // Trunk / proboscis (elephant)
        public bool Trunk;
        public int TrunkSegments = 8;
        public float TrunkLength = 14f;
        public float TrunkThickness = 3f;
        public float TrunkTaper = 0.4f;
        public float TrunkDroop = 11f;        // deg per segment, curls the trunk down

        // Tusks (elephant / mammoth / boar)
        public bool Tusks;
        public int TuskSegments = 3;
        public float TuskLength = 10f;
        public float TuskThickness = 1.8f;
        public float TuskCurve = 15f;         // deg per segment
        public float TuskForward = 0.85f;     // fore/aft anchor on the head

        // Shoulder hump / withers (bison / aurochs / camel)
        public bool Hump;
        public float HumpHeight = 4f;
        public float HumpLength = 8f;
        public float HumpPos = 0.32f;         // along the spine (0 = head end)

        // Dewlap (hanging throat flap - cattle / moose)
        public bool Dewlap;
        public float DewlapDrop = 4f;
        public float DewlapLength = 5f;
        public float DewlapWidth = 3f;

        // Tail tuft (lion / elephant / cow)
        public bool TailTuft;
        public float TailTuftSize = 3f;

        // Ear shaping (flat fanning ears, fore/aft tilt)
        public float EarWidth = 1f;           // lateral flatten/flare (elephant ears)
        public float EarPitch;                // fore/aft tilt (deg, + lays them back)

        // ---- More fauna detail (mane, fins, antennae, plume, shell) ----

        // Neck mane (lion / horse / hyena)
        public bool Mane;
        public float ManeHeight = 3f;
        public float ManeLength = 1.5f;
        public float ManeAngle = -38f;        // swept back

        // Pectoral / side fins (fish, rays)
        public int FinPairs;                  // 0..3
        public float FinSpan = 6f;
        public float FinChord = 4f;
        public float FinAngle = -12f;         // sweep / droop
        public float FinPos = 0.4f;           // along the spine (0 = head end)
        public float FinHeight = 0.3f;        // vertical mount (0 = belly, 1 = back)

        // Antennae (insect feelers)
        public bool Antennae;
        public int AntennaeSegments = 3;
        public float AntennaeLength = 8f;
        public float AntennaeThickness = 0.5f;
        public float AntennaeCurve = 18f;
        public float AntennaeSplay = 16f;

        // Tail plume / feather fan (rooster / peacock / turkey)
        public bool TailPlume;
        public int PlumeCount = 7;
        public float PlumeLength = 10f;
        public float PlumeWidth = 1.5f;
        public float PlumeSpread = 65f;       // lateral fan (deg)
        public float PlumeAngle = 28f;        // upward angle

        // Shell / carapace (turtle / beetle / armadillo)
        public bool Shell;
        public float ShellHeight = 5f;
        public float ShellLength = 0.85f;     // fraction of body length
        public float ShellWidth = 1.2f;       // fraction of body width

        // ---- Studied-creature detail (mined from Wilderlands Rustbound, Darce's Drifters and Pegasus) ----

        // Branching antlers (deer / elk / moose / the shepherd & caretaker drifters): a swept main beam
        // carrying several tines, unlike the plain tapering Horns.
        public int AntlerPairs;
        public int AntlerBeamSegments = 3;
        public float AntlerBeamLength = 9f;
        public float AntlerBeamThickness = 1.4f;
        public int AntlerTines = 3;            // points branching off each beam
        public float AntlerTineLength = 4f;
        public float AntlerSpread = 30f;       // lateral splay of the whole beam (deg)
        public float AntlerSweep = 16f;        // per-segment back-curve of the beam (deg)
        public float AntlerTineSpread = 35f;   // fore/aft fan of the tines off the beam (deg)
        public float AntlerForward = 0.4f;     // fore/aft anchor on the head (0..1)

        // Mandibles / pincers (ants, beetles, spiders - banefly & blightworm): a lateral pair on the head
        // front that gape sideways and hook inward, with optional inner teeth.
        public bool Mandibles;
        public int MandibleSegments = 2;
        public float MandibleLength = 5f;
        public float MandibleThickness = 1f;
        public float MandibleGape = 18f;       // sideways open angle at the base (deg)
        public float MandibleCurve = 22f;      // per-segment inward hook (deg)
        public bool MandibleFangs;             // inner serrations / teeth

        // Compound / extra eyes (insects, spiders, aberrations): small eyes clustered around the main pair.
        public int ExtraEyes;                  // per side (0..8)
        public float ExtraEyeSize = 0.6f;
        public float ExtraEyeSpread = 1.4f;    // how far they scatter around the main eye

        // Tail-tip stinger / spike mace (scorpion, wasp, blightworm, caretaker): a cluster of spikes at the tail end.
        public bool TailStinger;
        public int StingerCount = 3;
        public float StingerLength = 4f;
        public float StingerSize = 1f;
        public float StingerSpread = 24f;      // fan (deg)
        public float StingerCurve;             // up(+)/down(-) hook of the whole cluster (deg)

        // Layered wing feathers (bird / pegasus / angel): flat feather planes trailing off the feathered-wing spar.
        public int WingFeathers;               // feathers per wing (0 = the plain spar)
        public float WingFeatherLength = 7f;
        public float WingFeatherWidth = 2f;

        // Scattered body spikes / quills / shards (porcupine, sea urchin, the spiked drifters, rust constructs).
        public int BodySpikes;                 // total, scattered over the torso (seeded, reproducible)
        public float BodySpikeLength = 4f;
        public float BodySpikeSize = 1f;
        public float BodySpikeSpan = 0.85f;    // fraction of the spine they cover from the front

        // ---- Per-bone limb tuning: independently tweak each arm/leg bone's length, girth and angle ----
        public bool ArmPerBone;
        public List<float> ArmBoneLength = [];  // length multiplier per arm bone (1 = the even default)
        public List<float> ArmBoneThick = [];   // girth multiplier per arm bone
        public List<float> ArmBoneAngle = [];   // extra bend (deg) per arm bone
        public bool LegPerBone;
        public List<float> LegBoneLength = [];
        public List<float> LegBoneThick = [];
        public List<float> LegBoneAngle = [];

        // ---- Joint gap fillers: a knuckle box at each bent chain joint to close the outer-bend gaps ----
        public bool FillJoints;
        public float JointFillScale = 1f;       // knuckle size relative to the joint's cross-section

        // ---- Smoothing: overlapping 45-degree facet boxes round chunky volumes into octagonal prisms ----
        public int Smoothing;                   // 0 = plain boxes, 1-2 = rounded (the seikret beak technique)

        // ---- Wing membrane ridges: raised bone struts dividing the membrane into sections ----
        public bool WingRidges;
        public float WingRidgeSize = 1f;        // ridge thickness relative to the membrane

        // ---- Targeted fine-tuning: per-element overrides keyed by the element's generated name. Lets any single
        // part be sculpted on top of the procedural base without a dedicated parameter, and the tweaks survive
        // regenerate / randomize as long as the named element still exists. ----
        public Dictionary<string, ModelCreatureTweak> Tweaks = new(StringComparer.Ordinal);

        public ModelCreatureParams Clone()
        {
            ModelCreatureParams clone = (ModelCreatureParams)MemberwiseClone();
            clone.ArmBoneLength = [.. ArmBoneLength];
            clone.ArmBoneThick = [.. ArmBoneThick];
            clone.ArmBoneAngle = [.. ArmBoneAngle];
            clone.LegBoneLength = [.. LegBoneLength];
            clone.LegBoneThick = [.. LegBoneThick];
            clone.LegBoneAngle = [.. LegBoneAngle];
            clone.Tweaks = Tweaks.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);
            return clone;
        }
    }

    /// <summary>A targeted override on one generated element (and its subtree): a uniform resize about its pivot,
    /// a positional nudge, an extra rotation, or hiding it outright. Keyed by element name in
    /// <see cref="ModelCreatureParams.Tweaks"/>, so it re-applies every rebuild.</summary>
    private sealed class ModelCreatureTweak
    {
        public float SizeMul = 1f;
        public NVector3 Offset;
        public NVector3 Rotate;
        public bool Hidden;

        public bool IsNeutral => !Hidden && Math.Abs(SizeMul - 1f) < 1e-4f && Offset == NVector3.Zero && Rotate == NVector3.Zero;

        public ModelCreatureTweak Clone() => (ModelCreatureTweak)MemberwiseClone();
    }

    private bool _modelCreatureWindowOpen;
    private bool _modelCreatureAdvanced;
    private int _modelCreatureArchetypeIndex = (int)ModelCreatureArchetype.Quadruped;
    private readonly ModelCreatureParams _modelCreatureParams = new();
    private ModelElementData? _modelCreaturePreviewRoot;
    private string _modelCreaturePreviewError = "";
    private bool _modelCreaturePreviewDirty = true;
    private int _modelCreaturePreviewCount;
    private string _modelCreatureTweakSelected = "";   // the element being fine-tuned (highlighted in the ghost)

    private static double ModelCreatureLerp(double a, double b, double t)
    {
        return a + (b - a) * t;
    }

    private static double[] ModelCreatureRound(double[] vector)
    {
        return [ModelPrimitiveRound(vector[0]), ModelPrimitiveRound(vector[1]), ModelPrimitiveRound(vector[2])];
    }

    // ---- UI ----------------------------------------------------------------

    private void DrawModelCreaturePanel()
    {
        if (!_modelCreatureWindowOpen) return;

        {
            if (_modelDoc == null)
            {
                ImGui.TextDisabled("Open a shape or create a new one first.");
                return;
            }

            bool changed = false;
            ModelCreatureParams p = _modelCreatureParams;

            ImGui.SetNextItemWidth(190f);
            if (ImGui.Combo("Archetype##model-creature-archetype", ref _modelCreatureArchetypeIndex, ModelCreatureArchetypeLabels, ModelCreatureArchetypeLabels.Length))
            {
                ModelApplyCreatureArchetype((ModelCreatureArchetype)_modelCreatureArchetypeIndex);
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Apply preset##model-creature-preset"))
            {
                ModelApplyCreatureArchetype((ModelCreatureArchetype)_modelCreatureArchetypeIndex);
                changed = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Reset all parameters to this archetype's sensible defaults (keeps center, texture and seed).");
            }

            ImGui.SetNextItemWidth(120f);
            changed |= ImGui.InputInt("Seed##model-creature-seed", ref p.Seed);
            ImGui.SameLine();
            if (ImGui.Button("Randomize##model-creature-randomize"))
            {
                p.Seed++;
                ModelRandomizeCreature(p);
                changed = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Advance the seed and jitter every parameter within sensible ranges. A given seed always produces the same creature, so it is reproducible.");
            }

            changed |= ImGui.DragFloat3("Center##model-creature-center", ref p.Center, 0.25f, -256f, 272f, "%.2f");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Body center in shape units (16 = 1 block). Becomes the group element's pivot.");
            }
            changed |= ImGui.DragFloat3("Rotation##model-creature-rotation", ref p.Rotation, 1f, -360f, 360f, "%.1f deg");

            List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
            ImGui.SetNextItemWidth(190f);
            if (ModelFilteredCombo("Texture##model-creature-texture", p.Texture, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "filter texture codes"))
            {
                p.Texture = pickedTexture;
                changed = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(p.AutoTexture
                    ? "Base texture. Body-part codes that don't exist yet are created pointing at this texture's image, so each region renders until you repaint it."
                    : "Texture code applied to every body part.");
            }

            changed |= ImGui.Checkbox("Auto textures by body part##model-creature-autotex", ref p.AutoTexture);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Give each region its own texture code (body, head, eyes, legs, arms, tail, wings) instead of one shared texture. " +
                    "Existing codes with a matching name are reused; missing ones are added on Create, seeded from the base texture above so you can repaint each region.");
            }

            ImGui.Checkbox("Advanced mode##model-creature-advanced", ref _modelCreatureAdvanced);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Reveal the full set of fine-tuning parameters (tapers, curves, feet/hands, dorsal ridge, per-part angles). " +
                    "Off keeps only the essentials so the tool is not overwhelming.");
            }
            if (_modelCreatureAdvanced)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(160f);
                changed |= ImGui.SliderFloat("Overall scale##model-creature-scale", ref p.UniformScale, 0.1f, 8f, "%.2fx");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Uniformly scales the whole creature about its center after it is built.");
            }

            ImGui.Separator();
            changed |= DrawModelCreatureSections(p);

            if (changed) _modelCreaturePreviewDirty = true;
            if (_modelCreaturePreviewDirty)
            {
                _modelCreaturePreviewDirty = false;
                _modelCreaturePreviewRoot = ModelBuildCreature(out _modelCreaturePreviewError);
                _modelCreaturePreviewCount = _modelCreaturePreviewRoot == null ? 0 : ModelCreatureElementCount(_modelCreaturePreviewRoot);
            }

            ImGui.Separator();
            if (!string.IsNullOrEmpty(_modelCreaturePreviewError))
            {
                ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _modelCreaturePreviewError);
            }
            else
            {
                if (ModelIsMeshLibMode)
                {
                    (int vertices, int faces) = ModelGeneratedMeshCounts(_modelCreaturePreviewRoot);
                    ImGui.TextUnformatted($"{_modelCreaturePreviewCount} element(s), {vertices} mesh vertices, {faces} mesh faces / {ModelCreatureMaxElements} max. Green ghost shows the result.");
                }
                else
                {
                    ImGui.TextUnformatted($"{_modelCreaturePreviewCount} element(s) / {ModelCreatureMaxElements} max. Green ghost shows the result.");
                }
            }

            bool canCreate = _modelCreaturePreviewRoot != null && string.IsNullOrEmpty(_modelCreaturePreviewError);
            if (!canCreate) ImGui.BeginDisabled();
            if (ImGui.Button("Create##model-creature-create"))
            {
                ModelCommitCreature();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Add the skeleton to the shape, grouped under a single parent element (one undo step).");
            }
            ImGui.SameLine();
            if (ImGui.Button("Create & animate##model-creature-create-animate"))
            {
                ModelCommitCreature();
                ModelAnimateCurrentShape();
            }
            if (!canCreate) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Add the skeleton, save the shape, and open it in the animation editor's Shapes tab ready to keyframe.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Close##model-creature-close"))
            {
                _modelCreatureWindowOpen = false;
            }
        }
    }

    private bool DrawModelCreatureSections(ModelCreatureParams p)
    {
        bool changed = false;
        bool advanced = _modelCreatureAdvanced;

        if (ImGui.CollapsingHeader("Spine / torso##model-creature-spine", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderInt("Segments##model-creature-spine-seg", ref p.SpineSegments, 1, 16);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertebrae along the spine; each is its own joint so the body can bend.");
            changed |= ImGui.DragFloat("Length##model-creature-spine-len", ref p.SpineLength, 0.25f, 1f, 256f, "%.2f");
            changed |= ImGui.DragFloat("Width##model-creature-body-w", ref p.BodyWidth, 0.25f, 0.5f, 64f, "%.2f");
            changed |= ImGui.DragFloat("Height##model-creature-body-h", ref p.BodyHeight, 0.25f, 0.5f, 64f, "%.2f");
            changed |= ImGui.SliderFloat("Rear taper##model-creature-body-taper", ref p.BodyTaper, 0.2f, 1.5f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cross-section scale at the tail end relative to the head end.");
            if (advanced)
            {
                changed |= ImGui.SliderFloat("Front taper##model-creature-body-ftaper", ref p.BodyFrontTaper, 0.2f, 1.5f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cross-section scale at the head end (1 = full).");
                changed |= ImGui.SliderFloat("Mid bulge##model-creature-body-bulge", ref p.BodyBulge, 0.4f, 2.5f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Swell the middle of the torso (belly / barrel chest). 1 = none.");
                changed |= ImGui.DragFloat("Spine curve##model-creature-body-curve", ref p.SpineCurve, 0.25f, -30f, 30f, "%.1f deg/seg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Per-joint bend along the back. Positive arches it up, negative sags it.");
                changed |= ImGui.Checkbox("Belly / underbody##model-creature-belly", ref p.Belly);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hang a fuller underbody volume beneath the mid-spine for a heavier silhouette.");
                if (p.Belly)
                {
                    changed |= ImGui.DragFloat("Belly depth##model-creature-belly-d", ref p.BellyDepth, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.SliderFloat("Belly width##model-creature-belly-w", ref p.BellySize, 0.3f, 2f, "x%.2f");
                }
                changed |= ImGui.Checkbox("Shoulder hump##model-creature-hump", ref p.Hump);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A raised muscle/fat hump on the upper back over the shoulders (bison / aurochs / camel).");
                if (p.Hump)
                {
                    changed |= ImGui.DragFloat("Hump height##model-creature-hump-h", ref p.HumpHeight, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Hump length##model-creature-hump-l", ref p.HumpLength, 0.25f, 0.5f, 48f, "%.2f");
                    changed |= ImGui.SliderFloat("Hump position##model-creature-hump-pos", ref p.HumpPos, 0f, 1f, "%.2f");
                }
                changed |= ImGui.Checkbox("Dewlap##model-creature-dewlap", ref p.Dewlap);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A flap of skin hanging below the throat (cattle / moose).");
                if (p.Dewlap)
                {
                    changed |= ImGui.DragFloat("Dewlap drop##model-creature-dewlap-d", ref p.DewlapDrop, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Dewlap length##model-creature-dewlap-l", ref p.DewlapLength, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Dewlap width##model-creature-dewlap-w", ref p.DewlapWidth, 0.25f, 0.25f, 24f, "%.2f");
                }
                changed |= ImGui.Checkbox("Shell / carapace##model-creature-shell", ref p.Shell);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A domed shell arching over the back (turtle / beetle / armadillo).");
                if (p.Shell)
                {
                    changed |= ImGui.DragFloat("Shell height##model-creature-shell-h", ref p.ShellHeight, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.SliderFloat("Shell length##model-creature-shell-l", ref p.ShellLength, 0.2f, 1.5f, "%.2f");
                    changed |= ImGui.SliderFloat("Shell width##model-creature-shell-w", ref p.ShellWidth, 0.2f, 2f, "x%.2f");
                }
            }
        }

        if (ImGui.CollapsingHeader("Neck + head##model-creature-head", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderInt("Neck segments##model-creature-neck-seg", ref p.NeckSegments, 0, 8);
            changed |= ImGui.DragFloat("Neck length##model-creature-neck-len", ref p.NeckLength, 0.25f, 0f, 64f, "%.2f");
            changed |= ImGui.DragFloat("Neck thickness##model-creature-neck-th", ref p.NeckThickness, 0.25f, 0.5f, 32f, "%.2f");
            changed |= ImGui.DragFloat("Neck pitch##model-creature-neck-pitch", ref p.NeckPitch, 1f, -120f, 120f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Negative raises the head, positive lowers it (rotation at the neck base).");
            if (advanced)
            {
                changed |= ImGui.SliderFloat("Neck taper##model-creature-neck-taper", ref p.NeckTaper, 0.2f, 1.5f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Neck thickness at the head end vs the base.");
                changed |= ImGui.DragFloat("Neck curve##model-creature-neck-curve", ref p.NeckCurve, 0.25f, -45f, 45f, "%.1f deg/seg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Per-joint neck bend for an S / swan curve.");
            }
            changed |= ImGui.DragFloat("Head width##model-creature-head-w", ref p.HeadWidth, 0.25f, 0.5f, 48f, "%.2f");
            changed |= ImGui.DragFloat("Head height##model-creature-head-h", ref p.HeadHeight, 0.25f, 0.5f, 48f, "%.2f");
            changed |= ImGui.DragFloat("Head depth##model-creature-head-d", ref p.HeadDepth, 0.25f, 0.5f, 48f, "%.2f");
            if (advanced)
            {
                changed |= ImGui.DragFloat("Head tilt##model-creature-head-tilt", ref p.HeadTilt, 0.5f, -75f, 75f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Nod the head up/down relative to the neck.");
            }
            changed |= ImGui.Checkbox("Snout / beak##model-creature-snout", ref p.Snout);
            if (p.Snout)
            {
                changed |= ImGui.DragFloat("Snout length##model-creature-snout-len", ref p.SnoutLength, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Snout size##model-creature-snout-size", ref p.SnoutSize, 0.25f, 0.5f, 32f, "%.2f");
                if (advanced)
                {
                    changed |= ImGui.SliderFloat("Snout width##model-creature-snout-w", ref p.SnoutWidthMul, 0.2f, 3f, "x%.2f");
                    changed |= ImGui.SliderFloat("Snout height##model-creature-snout-h", ref p.SnoutHeightMul, 0.2f, 3f, "x%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Low height = a flat duck bill; high = a tall beak.");
                    changed |= ImGui.DragFloat("Snout droop##model-creature-snout-droop", ref p.SnoutDroop, 0.5f, -60f, 60f, "%.1f deg");
                }
            }

            if (advanced)
            {
                changed |= ImGui.Checkbox("Mouth / jaw##model-creature-mouth", ref p.Mouth);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a hinged lower jaw under the head front, optionally with fangs (upper hang down, lower stand up).");
                if (p.Mouth)
                {
                    changed |= ImGui.DragFloat("Jaw length##model-creature-jaw-len", ref p.JawLength, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Jaw open##model-creature-jaw-drop", ref p.JawDrop, 0.5f, 0f, 70f, "%.1f deg");
                    changed |= ImGui.SliderInt("Fangs / side##model-creature-fangs", ref p.Fangs, 0, 6);
                    if (p.Fangs > 0)
                    {
                        changed |= ImGui.DragFloat("Fang size##model-creature-fang-size", ref p.FangSize, 0.05f, 0.1f, 6f, "%.2f");
                        changed |= ImGui.DragFloat("Fang length##model-creature-fang-len", ref p.FangLength, 0.1f, 0.25f, 8f, "%.2f");
                    }
                }
                changed |= ImGui.Checkbox("Nose tip##model-creature-nose", ref p.Nose);
                if (p.Nose)
                {
                    changed |= ImGui.DragFloat("Nose size##model-creature-nose-size", ref p.NoseSize, 0.1f, 0.25f, 8f, "%.2f");
                }

                changed |= ImGui.Checkbox("Mandibles / pincers##model-creature-mandibles", ref p.Mandibles);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A lateral pair on the head front that gape sideways and hook inward (ants/beetles/spiders - the banefly & blightworm), separate from the vertical jaw.");
                if (p.Mandibles)
                {
                    changed |= ImGui.SliderInt("Mandible segments##model-creature-mandible-seg", ref p.MandibleSegments, 1, 4);
                    changed |= ImGui.DragFloat("Mandible length##model-creature-mandible-len", ref p.MandibleLength, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Mandible thickness##model-creature-mandible-th", ref p.MandibleThickness, 0.1f, 0.25f, 8f, "%.2f");
                    changed |= ImGui.DragFloat("Mandible gape##model-creature-mandible-gape", ref p.MandibleGape, 0.5f, -30f, 60f, "%.1f deg");
                    changed |= ImGui.DragFloat("Mandible hook##model-creature-mandible-curve", ref p.MandibleCurve, 0.5f, -45f, 45f, "%.1f deg");
                    changed |= ImGui.Checkbox("Mandible teeth##model-creature-mandible-fangs", ref p.MandibleFangs);
                }

                changed |= ImGui.Checkbox("Trunk / proboscis##model-creature-trunk", ref p.Trunk);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A long, drooping, tapering multi-segment trunk off the head front (elephant/mammoth/tapir). Each segment is a joint so it can curl.");
                if (p.Trunk)
                {
                    changed |= ImGui.SliderInt("Trunk segments##model-creature-trunk-seg", ref p.TrunkSegments, 1, 16);
                    changed |= ImGui.DragFloat("Trunk length##model-creature-trunk-len", ref p.TrunkLength, 0.25f, 1f, 96f, "%.2f");
                    changed |= ImGui.DragFloat("Trunk thickness##model-creature-trunk-th", ref p.TrunkThickness, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.SliderFloat("Trunk taper##model-creature-trunk-taper", ref p.TrunkTaper, 0.05f, 1f, "%.2f");
                    changed |= ImGui.DragFloat("Trunk droop##model-creature-trunk-droop", ref p.TrunkDroop, 0.5f, -30f, 45f, "%.1f deg/seg");
                }

                changed |= ImGui.Checkbox("Tusks##model-creature-tusks", ref p.Tusks);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A pair of long curved tapering tusks from the lower face (elephant/mammoth/boar/walrus).");
                if (p.Tusks)
                {
                    changed |= ImGui.SliderInt("Tusk segments##model-creature-tusk-seg", ref p.TuskSegments, 1, 8);
                    changed |= ImGui.DragFloat("Tusk length##model-creature-tusk-len", ref p.TuskLength, 0.25f, 1f, 64f, "%.2f");
                    changed |= ImGui.DragFloat("Tusk thickness##model-creature-tusk-th", ref p.TuskThickness, 0.1f, 0.25f, 12f, "%.2f");
                    changed |= ImGui.DragFloat("Tusk curve##model-creature-tusk-curve", ref p.TuskCurve, 0.5f, -45f, 45f, "%.1f deg/seg");
                    changed |= ImGui.SliderFloat("Tusk position##model-creature-tusk-fwd", ref p.TuskForward, 0f, 1f, "%.2f");
                }

                changed |= ImGui.Checkbox("Mane##model-creature-mane", ref p.Mane);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A swept-back plate on each neck segment (lion/horse/hyena). Add neck segments for a denser mane.");
                if (p.Mane)
                {
                    changed |= ImGui.DragFloat("Mane height##model-creature-mane-h", ref p.ManeHeight, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Mane length##model-creature-mane-l", ref p.ManeLength, 0.1f, 0.25f, 16f, "%.2f");
                    changed |= ImGui.DragFloat("Mane sweep##model-creature-mane-angle", ref p.ManeAngle, 0.5f, -90f, 90f, "%.1f deg");
                }

                changed |= ImGui.Checkbox("Antennae##model-creature-antennae", ref p.Antennae);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A pair of thin curving feelers from the head front (insects).");
                if (p.Antennae)
                {
                    changed |= ImGui.SliderInt("Antenna segments##model-creature-ant-seg", ref p.AntennaeSegments, 1, 8);
                    changed |= ImGui.DragFloat("Antenna length##model-creature-ant-len", ref p.AntennaeLength, 0.25f, 0.5f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Antenna thickness##model-creature-ant-th", ref p.AntennaeThickness, 0.05f, 0.1f, 6f, "%.2f");
                    changed |= ImGui.DragFloat("Antenna curve##model-creature-ant-curve", ref p.AntennaeCurve, 0.5f, -45f, 45f, "%.1f deg/seg");
                    changed |= ImGui.DragFloat("Antenna splay##model-creature-ant-splay", ref p.AntennaeSplay, 0.5f, -45f, 60f, "%.1f deg");
                }
            }
        }

        if (ImGui.CollapsingHeader("Legs##model-creature-legs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderInt("Leg pairs##model-creature-leg-pairs", ref p.LegPairs, 0, 6);
            changed |= ImGui.SliderInt("Joints / leg##model-creature-leg-seg", ref p.LegSegments, 1, 6);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Segments per leg (e.g. thigh, shank, foot). Each is a joint.");
            changed |= ImGui.DragFloat("Leg length##model-creature-leg-len", ref p.LegLength, 0.25f, 1f, 96f, "%.2f");
            changed |= ImGui.DragFloat("Leg thickness##model-creature-leg-th", ref p.LegThickness, 0.25f, 0.5f, 24f, "%.2f");
            changed |= ImGui.DragFloat("Splay##model-creature-leg-splay", ref p.LegSplay, 0.5f, -45f, 45f, "%.1f deg");
            changed |= ImGui.DragFloat("Joint bend##model-creature-leg-bend", ref p.LegBend, 0.5f, -45f, 45f, "%.1f deg");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Per-joint bend in the rest pose; small values give digitigrade legs.");
            changed |= ImGui.SliderFloat("Front position##model-creature-leg-front", ref p.FrontLegPos, 0f, 1f, "%.2f");
            changed |= ImGui.SliderFloat("Rear position##model-creature-leg-rear", ref p.RearLegPos, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Where leg pairs attach along the spine (0 = head end, 1 = tail end).");
            if (advanced)
            {
                changed |= ImGui.SliderFloat("Leg taper##model-creature-leg-taper", ref p.LegTaper, 0.2f, 1.5f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Foot-end thickness vs the hip.");
                changed |= ImGui.DragFloat("Forward lean##model-creature-leg-lean", ref p.LegForwardLean, 0.5f, -60f, 60f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lean the whole leg fore (+) or aft (-).");
                changed |= ImGui.DragFloat("Crouch / zigzag##model-creature-leg-zig", ref p.LegZigzag, 0.5f, -60f, 60f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Alternates the bend joint-to-joint (thigh back, shank forward, ...) for a natural standing crouch where every segment sits at a different angle.");
                changed |= ImGui.Checkbox("Feet##model-creature-feet", ref p.Feet);
                if (p.Feet)
                {
                    changed |= ImGui.DragFloat("Foot length##model-creature-foot-len", ref p.FootLength, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Foot width##model-creature-foot-w", ref p.FootWidth, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Foot height##model-creature-foot-h", ref p.FootHeight, 0.25f, 0.25f, 16f, "%.2f");
                }

                changed |= ImGui.Checkbox("Shoulders / haunches##model-creature-shoulders", ref p.Shoulders);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Embed a muscle volume in the body where each limb attaches (shoulders for arms, haunches for legs); the limb then hangs from its lower end, just like the hand-made models.");
                if (p.Shoulders)
                {
                    changed |= ImGui.DragFloat("Shoulder length##model-creature-sh-len", ref p.ShoulderLength, 0.25f, 1f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Shoulder width##model-creature-sh-w", ref p.ShoulderWidth, 0.25f, 1f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Shoulder thickness##model-creature-sh-th", ref p.ShoulderThickness, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.SliderFloat("Shoulder embed##model-creature-sh-embed", ref p.ShoulderEmbed, 0f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("0 = the volume sits outside the body, 1 = fully buried inside it.");
                }

                changed |= ImGui.SliderInt("Toes / claws##model-creature-toes", ref p.Toes, 0, 5);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Articulated claws fanned forward off each foot/hand (or off the limb tip if there are no feet).");
                if (p.Toes > 0)
                {
                    changed |= ImGui.DragFloat("Claw length##model-creature-claw-len", ref p.ClawLength, 0.1f, 0.25f, 16f, "%.2f");
                    changed |= ImGui.DragFloat("Claw size##model-creature-claw-size", ref p.ClawSize, 0.05f, 0.1f, 6f, "%.2f");
                    changed |= ImGui.DragFloat("Claw splay##model-creature-claw-splay", ref p.ClawSplay, 0.5f, -45f, 45f, "%.1f deg");
                    changed |= ImGui.DragFloat("Claw curve##model-creature-claw-curve", ref p.ClawCurve, 0.5f, -60f, 30f, "%.1f deg");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Negative hooks the claws downward.");
                }

                changed |= DrawModelCreaturePerBone("leg", Math.Clamp(p.LegSegments, 1, 6), ref p.LegPerBone, p.LegBoneLength, p.LegBoneThick, p.LegBoneAngle);
            }
        }

        if (ImGui.CollapsingHeader("Arms / front limbs##model-creature-arms"))
        {
            changed |= ImGui.SliderInt("Arm pairs##model-creature-arm-pairs", ref p.ArmPairs, 0, 4);
            changed |= ImGui.SliderInt("Joints / arm##model-creature-arm-seg", ref p.ArmSegments, 1, 6);
            changed |= ImGui.DragFloat("Arm length##model-creature-arm-len", ref p.ArmLength, 0.25f, 1f, 96f, "%.2f");
            changed |= ImGui.DragFloat("Arm thickness##model-creature-arm-th", ref p.ArmThickness, 0.25f, 0.5f, 24f, "%.2f");
            changed |= ImGui.DragFloat("Splay##model-creature-arm-splay", ref p.ArmSplay, 0.5f, -60f, 60f, "%.1f deg");
            changed |= ImGui.DragFloat("Joint bend##model-creature-arm-bend", ref p.ArmBend, 0.5f, -45f, 45f, "%.1f deg");
            changed |= ImGui.SliderFloat("Position##model-creature-arm-pos", ref p.ArmPos, 0f, 1f, "%.2f");
            if (advanced)
            {
                changed |= ImGui.SliderFloat("Arm taper##model-creature-arm-taper", ref p.ArmTaper, 0.2f, 1.5f, "%.2f");
                changed |= ImGui.DragFloat("Down angle##model-creature-arm-down", ref p.ArmDownAngle, 0.5f, -90f, 90f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lean the whole arm fore (+) or aft (-) from the shoulder.");
                changed |= ImGui.DragFloat("Crouch / zigzag##model-creature-arm-zig", ref p.ArmZigzag, 0.5f, -60f, 60f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Alternating per-joint bend (upper back, fore forward, ...) so every arm segment sits at a different angle.");
                changed |= ImGui.Checkbox("Hands##model-creature-hands", ref p.Hands);
                if (p.Hands)
                {
                    changed |= ImGui.DragFloat("Hand length##model-creature-hand-len", ref p.HandLength, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Hand width##model-creature-hand-w", ref p.HandWidth, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Hand height##model-creature-hand-h", ref p.HandHeight, 0.25f, 0.25f, 16f, "%.2f");
                }

                changed |= ImGui.Checkbox("Asymmetric arms##model-creature-asym", ref p.AsymmetricArms);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Give the right and left arms different tips (e.g. one blade arm, one hand). Off = the arms mirror as usual.");
                if (p.AsymmetricArms)
                {
                    ImGui.SetNextItemWidth(190f);
                    changed |= ImGui.Combo("Right arm##model-creature-arm-app-r", ref p.ArmAppendageRight, ModelCreatureArmAppendageLabels, ModelCreatureArmAppendageLabels.Length);
                    ImGui.SetNextItemWidth(190f);
                    changed |= ImGui.Combo("Left arm##model-creature-arm-app-l", ref p.ArmAppendageLeft, ModelCreatureArmAppendageLabels, ModelCreatureArmAppendageLabels.Length);
                    changed |= ImGui.DragFloat("Appendage length##model-creature-arm-app-len", ref p.ArmAppendageLength, 0.25f, 1f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Appendage size##model-creature-arm-app-size", ref p.ArmAppendageSize, 0.1f, 0.25f, 16f, "%.2f");
                }

                changed |= DrawModelCreaturePerBone("arm", Math.Clamp(p.ArmSegments, 1, 6), ref p.ArmPerBone, p.ArmBoneLength, p.ArmBoneThick, p.ArmBoneAngle);
            }
        }

        if (ImGui.CollapsingHeader("Tail##model-creature-tail"))
        {
            changed |= ImGui.SliderInt("Segments##model-creature-tail-seg", ref p.TailSegments, 0, 16);
            changed |= ImGui.DragFloat("Length##model-creature-tail-len", ref p.TailLength, 0.25f, 0f, 128f, "%.2f");
            changed |= ImGui.DragFloat("Base thickness##model-creature-tail-th", ref p.TailThickness, 0.25f, 0.5f, 24f, "%.2f");
            changed |= ImGui.SliderFloat("Tip taper##model-creature-tail-taper", ref p.TailTaper, 0.05f, 1f, "%.2f");
            changed |= ImGui.DragFloat("Droop / segment##model-creature-tail-droop", ref p.TailDroop, 0.5f, -45f, 45f, "%.1f deg");
            if (advanced)
            {
                changed |= ImGui.DragFloat("Base lift##model-creature-tail-base", ref p.TailBaseAngle, 0.5f, -90f, 90f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Raise (+) or lower (-) the tail at its root.");
                changed |= ImGui.SliderFloat("Width##model-creature-tail-w", ref p.TailWidthMul, 0.2f, 3f, "x%.2f");
                changed |= ImGui.SliderFloat("Height##model-creature-tail-h", ref p.TailHeightMul, 0.2f, 3f, "x%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flat (low height) for a beaver/fish paddle, tall for a fin.");
                changed |= ImGui.SliderFloat("Fluff / bulge##model-creature-tail-bulge", ref p.TailBulge, 0.4f, 3f, "x%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Swell the middle of the tail into a fluffy brush (like a wolf's). 1 = none.");
                changed |= ImGui.Checkbox("Tail fin / fluke##model-creature-tail-fin", ref p.TailFin);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A flat fin at the tail tip (fish/dragon, or a horizontal whale fluke).");
                if (p.TailFin)
                {
                    changed |= ImGui.DragFloat("Fin height##model-creature-tail-fin-h", ref p.TailFinHeight, 0.25f, 0.5f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Fin length##model-creature-tail-fin-l", ref p.TailFinLength, 0.25f, 0.5f, 48f, "%.2f");
                    changed |= ImGui.Checkbox("Vertical##model-creature-tail-fin-v", ref p.TailFinVertical);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("On = upright fin (fish/dragon). Off = horizontal fluke (whale).");
                }
                changed |= ImGui.Checkbox("Tail tuft##model-creature-tail-tuft", ref p.TailTuft);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A bushy tuft at the tail tip (lion / elephant / cow).");
                if (p.TailTuft)
                {
                    changed |= ImGui.DragFloat("Tuft size##model-creature-tail-tuft-s", ref p.TailTuftSize, 0.25f, 0.5f, 24f, "%.2f");
                }
                changed |= ImGui.Checkbox("Tail stinger / spikes##model-creature-stinger", ref p.TailStinger);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A cluster of spikes at the tail tip (scorpion / wasp stinger, the blightworm & caretaker maces).");
                if (p.TailStinger)
                {
                    changed |= ImGui.SliderInt("Stinger spikes##model-creature-stinger-count", ref p.StingerCount, 1, 12);
                    changed |= ImGui.DragFloat("Stinger length##model-creature-stinger-len", ref p.StingerLength, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Stinger size##model-creature-stinger-size", ref p.StingerSize, 0.1f, 0.25f, 8f, "%.2f");
                    changed |= ImGui.DragFloat("Stinger fan##model-creature-stinger-spread", ref p.StingerSpread, 0.5f, 0f, 80f, "%.1f deg");
                    changed |= ImGui.DragFloat("Stinger hook##model-creature-stinger-curve", ref p.StingerCurve, 0.5f, -80f, 80f, "%.1f deg");
                }
                changed |= ImGui.Checkbox("Tail plume / fan##model-creature-plume", ref p.TailPlume);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A spray of flat feathers fanning back and up from the rear (rooster / peacock / turkey).");
                if (p.TailPlume)
                {
                    changed |= ImGui.SliderInt("Plume feathers##model-creature-plume-count", ref p.PlumeCount, 1, 16);
                    changed |= ImGui.DragFloat("Plume length##model-creature-plume-len", ref p.PlumeLength, 0.25f, 1f, 64f, "%.2f");
                    changed |= ImGui.DragFloat("Plume width##model-creature-plume-w", ref p.PlumeWidth, 0.1f, 0.25f, 16f, "%.2f");
                    changed |= ImGui.DragFloat("Plume spread##model-creature-plume-spread", ref p.PlumeSpread, 0.5f, 0f, 90f, "%.1f deg");
                    changed |= ImGui.DragFloat("Plume angle##model-creature-plume-angle", ref p.PlumeAngle, 0.5f, -30f, 80f, "%.1f deg");
                }
            }
        }

        if (ImGui.CollapsingHeader("Wings##model-creature-wings"))
        {
            changed |= ImGui.SliderInt("Wing pairs##model-creature-wing-pairs", ref p.WingPairs, 0, 2);
            changed |= ImGui.SliderInt("Segments##model-creature-wing-seg", ref p.WingSegments, 1, 6);
            changed |= ImGui.DragFloat("Span##model-creature-wing-span", ref p.WingSpan, 0.25f, 1f, 128f, "%.2f");
            changed |= ImGui.DragFloat("Chord##model-creature-wing-chord", ref p.WingChord, 0.25f, 0.5f, 64f, "%.2f");
            changed |= ImGui.DragFloat("Thickness##model-creature-wing-th", ref p.WingThickness, 0.25f, 0.25f, 16f, "%.2f");
            changed |= ImGui.SliderFloat("Position##model-creature-wing-pos", ref p.WingPos, 0f, 1f, "%.2f");
            changed |= ImGui.DragFloat("Sweep / segment##model-creature-wing-sweep", ref p.WingSweep, 0.5f, -45f, 45f, "%.1f deg");
            if (advanced)
            {
                changed |= ImGui.DragFloat("Dihedral##model-creature-wing-dihedral", ref p.WingDihedral, 0.5f, -60f, 80f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Raise (+) or lower (-) both wings together at the shoulder.");
                changed |= ImGui.SliderFloat("Chord taper##model-creature-wing-ctaper", ref p.WingChordTaper, 0.1f, 1.5f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wingtip chord vs the root chord (low = pointed wings).");
                changed |= ImGui.SliderFloat("Mount height##model-creature-wing-h", ref p.WingHeight, 0f, 1f, "%.2f");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertical attach point on the body (1 = top of the back).");
                ImGui.SetNextItemWidth(190f);
                changed |= ImGui.Combo("Style##model-creature-wing-style", ref p.WingStyle, ModelCreatureWingStyleLabels, ModelCreatureWingStyleLabels.Length);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Feathered = a tapering limb spar. Membrane = a bat/dragon wing: a leading-edge spar, a fan of finger bones with webbing, and a large membrane reaching back to the torso. The spar flaps with the wing.");
                if ((ModelCreatureWingStyle)p.WingStyle == ModelCreatureWingStyle.Membrane)
                {
                    changed |= ImGui.SliderInt("Fingers##model-creature-wing-fingers", ref p.WingFingers, 2, 6);
                    changed |= ImGui.DragFloat("Membrane trail##model-creature-wing-trail", ref p.WingMembraneTrail, 0.25f, 1f, 48f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("How far the webbing trails behind each finger bone.");
                    changed |= ImGui.Checkbox("Membrane ridges##model-creature-wing-ridges", ref p.WingRidges);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Raised bone struts dividing the membrane into sections, standing proud of the surface (the finger/arm ridges of a real bat/reptile wing).");
                    if (p.WingRidges)
                    {
                        changed |= ImGui.DragFloat("Ridge size##model-creature-wing-ridge-size", ref p.WingRidgeSize, 0.05f, 0.2f, 3f, "x%.2f");
                    }
                }
                else
                {
                    changed |= ImGui.SliderInt("Feathers / wing##model-creature-wing-feathers", ref p.WingFeathers, 0, 24);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lay a row of overlapping flat feathers along the spar (a proper layered bird / pegasus wing). 0 = the plain spar.");
                    if (p.WingFeathers > 0)
                    {
                        changed |= ImGui.DragFloat("Feather length##model-creature-wing-feather-len", ref p.WingFeatherLength, 0.25f, 0.5f, 48f, "%.2f");
                        changed |= ImGui.DragFloat("Feather width##model-creature-wing-feather-w", ref p.WingFeatherWidth, 0.1f, 0.25f, 16f, "%.2f");
                    }
                }

                changed |= ImGui.SliderInt("Side fins##model-creature-fins", ref p.FinPairs, 0, 3);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flat pectoral/ventral fins on the body sides (fish, rays).");
                if (p.FinPairs > 0)
                {
                    changed |= ImGui.DragFloat("Fin span##model-creature-fin-span", ref p.FinSpan, 0.25f, 0.5f, 48f, "%.2f");
                    changed |= ImGui.DragFloat("Fin chord##model-creature-fin-chord", ref p.FinChord, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Fin sweep##model-creature-fin-angle", ref p.FinAngle, 0.5f, -60f, 60f, "%.1f deg");
                    changed |= ImGui.SliderFloat("Fin position##model-creature-fin-pos", ref p.FinPos, 0f, 1f, "%.2f");
                    changed |= ImGui.SliderFloat("Fin mount##model-creature-fin-h", ref p.FinHeight, 0f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertical mount (0 = belly, 1 = back).");
                }
            }
        }

        if (ImGui.CollapsingHeader("Head details##model-creature-details"))
        {
            changed |= ImGui.SliderInt("Ear pairs##model-creature-ears", ref p.Ears, 0, 2);
            if (p.Ears > 0)
            {
                changed |= ImGui.DragFloat("Ear size##model-creature-ear-size", ref p.EarSize, 0.25f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Ear height##model-creature-ear-h", ref p.EarHeight, 0.25f, 0.25f, 24f, "%.2f");
                if (advanced)
                {
                    changed |= ImGui.DragFloat("Ear splay##model-creature-ear-splay", ref p.EarAngle, 0.5f, -45f, 60f, "%.1f deg");
                    changed |= ImGui.SliderFloat("Ear position##model-creature-ear-fwd", ref p.EarForward, 0f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fore/aft placement of the ears on the head.");
                    changed |= ImGui.SliderInt("Ear segments##model-creature-ear-seg", ref p.EarSegments, 1, 4);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stacked tapering boxes that round the ear off (2-3 looks rounded).");
                    changed |= ImGui.SliderFloat("Ear taper##model-creature-ear-taper", ref p.EarTaper, 0.05f, 1.5f, "%.2f");
                    changed |= ImGui.SliderFloat("Ear flare / width##model-creature-ear-width", ref p.EarWidth, 0.2f, 3f, "x%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Widen the ear laterally into a flat fanning panel (elephant).");
                    changed |= ImGui.DragFloat("Ear tilt##model-creature-ear-pitch", ref p.EarPitch, 0.5f, -60f, 60f, "%.1f deg");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tilt the ears fore/aft (positive lays them back).");
                    changed |= ImGui.Checkbox("Inner ear##model-creature-ear-inner", ref p.InnerEars);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Inset a smaller box on the front of each ear (the pink inner ear).");
                }
            }
            changed |= ImGui.SliderInt("Horn pairs##model-creature-horns", ref p.HornPairs, 0, 2);
            if (p.HornPairs > 0)
            {
                changed |= ImGui.SliderInt("Horn segments##model-creature-horn-seg", ref p.HornSegments, 1, 6);
                changed |= ImGui.DragFloat("Horn length##model-creature-horn-len", ref p.HornLength, 0.25f, 0.5f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Horn thickness##model-creature-horn-th", ref p.HornThickness, 0.25f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Horn curl##model-creature-horn-curl", ref p.HornCurl, 0.5f, -45f, 45f, "%.1f deg");
                if (advanced)
                {
                    changed |= ImGui.DragFloat("Horn splay##model-creature-horn-splay", ref p.HornSplay, 0.5f, -45f, 60f, "%.1f deg");
                    changed |= ImGui.DragFloat("Horn pitch##model-creature-horn-pitch", ref p.HornPitch, 0.5f, -90f, 90f, "%.1f deg");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lean the horns back (+) or forward (-).");
                    changed |= ImGui.SliderFloat("Horn position##model-creature-horn-fwd", ref p.HornForward, 0f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fore/aft anchor of a single horn pair.");
                }
            }
            changed |= ImGui.SliderInt("Antler pairs##model-creature-antlers", ref p.AntlerPairs, 0, 2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Branching antlers: a swept beam carrying several tines (deer / elk / moose / the shepherd & deerhorn drifters).");
            if (p.AntlerPairs > 0)
            {
                changed |= ImGui.DragFloat("Beam length##model-creature-antler-len", ref p.AntlerBeamLength, 0.25f, 1f, 48f, "%.2f");
                changed |= ImGui.SliderInt("Tines / beam##model-creature-antler-tines", ref p.AntlerTines, 0, 8);
                changed |= ImGui.DragFloat("Tine length##model-creature-antler-tinelen", ref p.AntlerTineLength, 0.25f, 0.5f, 24f, "%.2f");
                changed |= ImGui.DragFloat("Antler spread##model-creature-antler-spread", ref p.AntlerSpread, 0.5f, -20f, 70f, "%.1f deg");
                if (advanced)
                {
                    changed |= ImGui.SliderInt("Beam segments##model-creature-antler-seg", ref p.AntlerBeamSegments, 1, 6);
                    changed |= ImGui.DragFloat("Beam thickness##model-creature-antler-th", ref p.AntlerBeamThickness, 0.1f, 0.25f, 8f, "%.2f");
                    changed |= ImGui.DragFloat("Beam sweep##model-creature-antler-sweep", ref p.AntlerSweep, 0.5f, -45f, 45f, "%.1f deg");
                    changed |= ImGui.DragFloat("Tine fan##model-creature-antler-tinefan", ref p.AntlerTineSpread, 0.5f, 0f, 80f, "%.1f deg");
                    changed |= ImGui.SliderFloat("Antler position##model-creature-antler-fwd", ref p.AntlerForward, 0f, 1f, "%.2f");
                }
            }
            changed |= ImGui.Checkbox("Eyes##model-creature-eyes", ref p.Eyes);
            if (p.Eyes)
            {
                changed |= ImGui.DragFloat("Eye size##model-creature-eye-size", ref p.EyeSize, 0.1f, 0.25f, 8f, "%.2f");
                if (advanced)
                {
                    changed |= ImGui.SliderFloat("Eye forward##model-creature-eye-fwd", ref p.EyeForward, 0f, 1f, "%.2f");
                    changed |= ImGui.SliderFloat("Eye height##model-creature-eye-h", ref p.EyeHeight, 0f, 1f, "%.2f");
                    changed |= ImGui.Checkbox("Pupils##model-creature-pupils", ref p.Pupils);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("A smaller pupil box proud of each eye's outer face (layered eyes).");
                    if (p.Pupils)
                    {
                        changed |= ImGui.DragFloat("Pupil size##model-creature-pupil-size", ref p.PupilSize, 0.05f, 0.1f, 6f, "%.2f");
                    }
                    changed |= ImGui.SliderInt("Extra eyes / side##model-creature-extraeyes", ref p.ExtraEyes, 0, 8);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("A cluster of smaller eyes around each main eye (compound / arachnid eyes - the banefly & blightworm).");
                    if (p.ExtraEyes > 0)
                    {
                        changed |= ImGui.DragFloat("Extra eye size##model-creature-extraeye-size", ref p.ExtraEyeSize, 0.05f, 0.1f, 4f, "%.2f");
                        changed |= ImGui.SliderFloat("Extra eye spread##model-creature-extraeye-spread", ref p.ExtraEyeSpread, 0.4f, 4f, "%.2f");
                    }
                }
            }

            if (advanced)
            {
                changed |= ImGui.Checkbox("Fur cheeks##model-creature-cheeks", ref p.Cheeks);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fur patches swept back off the sides of the head, like the wolf's cheek ruff.");
                if (p.Cheeks)
                {
                    changed |= ImGui.DragFloat("Cheek length##model-creature-cheek-len", ref p.CheekLength, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Cheek size##model-creature-cheek-size", ref p.CheekSize, 0.25f, 0.5f, 24f, "%.2f");
                    changed |= ImGui.DragFloat("Cheek sweep##model-creature-cheek-angle", ref p.CheekAngle, 0.5f, -45f, 60f, "%.1f deg");
                }

                changed |= ImGui.Checkbox("Brow ridge##model-creature-brow", ref p.Brow);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A heavy ridge over each eye for a predatory face.");
                if (p.Brow)
                {
                    changed |= ImGui.DragFloat("Brow size##model-creature-brow-size", ref p.BrowSize, 0.1f, 0.25f, 8f, "%.2f");
                }

                changed |= ImGui.Checkbox("Head crest / frill##model-creature-crest", ref p.Crest);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("A row of swept-back plates along the top of the head (dragon crest / lizard frill).");
                if (p.Crest)
                {
                    changed |= ImGui.SliderInt("Crest plates##model-creature-crest-count", ref p.CrestCount, 1, 12);
                    changed |= ImGui.DragFloat("Crest height##model-creature-crest-h", ref p.CrestHeight, 0.25f, 0.5f, 32f, "%.2f");
                    changed |= ImGui.DragFloat("Crest length##model-creature-crest-l", ref p.CrestLength, 0.1f, 0.25f, 16f, "%.2f");
                    changed |= ImGui.DragFloat("Crest sweep##model-creature-crest-angle", ref p.CrestAngle, 0.5f, -90f, 90f, "%.1f deg");
                    changed |= ImGui.SliderFloat("Crest start##model-creature-crest-start", ref p.CrestStart, 0f, 1f, "%.2f");
                    changed |= ImGui.SliderFloat("Crest end##model-creature-crest-end", ref p.CrestEnd, 0f, 1f, "%.2f");
                }
            }
        }

        if (ImGui.CollapsingHeader("Dorsal ridge##model-creature-ridge"))
        {
            changed |= ImGui.SliderInt("Plates / spikes##model-creature-ridge-count", ref p.DorsalSpikes, 0, 24);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("A row of spikes/plates/sails running along the top of the back (stegosaurus plates, dragon spines, a sailfin).");
            if (p.DorsalSpikes > 0)
            {
                changed |= ImGui.DragFloat("Height##model-creature-ridge-h", ref p.DorsalSpikeHeight, 0.25f, 0.25f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Sweep##model-creature-ridge-angle", ref p.DorsalSpikeAngle, 0.5f, -80f, 80f, "%.1f deg");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lean the plates back (+) or forward (-).");
                if (advanced)
                {
                    changed |= ImGui.DragFloat("Length##model-creature-ridge-len", ref p.DorsalSpikeLength, 0.25f, 0.25f, 32f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fore-aft footprint of each plate (wide = sail/plates, narrow = spikes).");
                    changed |= ImGui.DragFloat("Width##model-creature-ridge-w", ref p.DorsalSpikeWidth, 0.25f, 0.25f, 24f, "%.2f");
                    changed |= ImGui.SliderFloat("Span start##model-creature-ridge-start", ref p.DorsalSpikeStart, 0f, 1f, "%.2f");
                    changed |= ImGui.SliderFloat("Span end##model-creature-ridge-end", ref p.DorsalSpikeEnd, 0f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Where the ridge starts and ends along the spine (0 = head end, 1 = tail end).");
                }
            }

            changed |= ImGui.SliderInt("Body spikes / quills##model-creature-bodyspikes", ref p.BodySpikes, 0, 60);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Spikes scattered all over the torso, not in a neat row (porcupine, sea urchin, the spiked drifters, rust constructs). Reproducible from the seed.");
            if (p.BodySpikes > 0)
            {
                changed |= ImGui.DragFloat("Spike length##model-creature-bodyspike-len", ref p.BodySpikeLength, 0.25f, 0.25f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Spike size##model-creature-bodyspike-size", ref p.BodySpikeSize, 0.1f, 0.25f, 8f, "%.2f");
                if (advanced)
                {
                    changed |= ImGui.SliderFloat("Coverage##model-creature-bodyspike-span", ref p.BodySpikeSpan, 0.05f, 1f, "%.2f");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fraction of the body length they cover from the head end.");
                }
            }
        }

        if (ImGui.CollapsingHeader("Mesh / smoothing##model-creature-mesh"))
        {
            changed |= ImGui.SliderInt("Round volumes##model-creature-smoothing", ref p.Smoothing, 0, 2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Round the chunky body/head/limb cubes into octagonal (1) or near-round (2) prisms by overlapping a rotated facet box on each, like the hand-made seikret beak. 0 = plain boxes. Adds elements.");

            changed |= ImGui.Checkbox("Fill joint gaps##model-creature-filljoints", ref p.FillJoints);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Drop a knuckle box at every bent joint to close the V-gaps where two bones meet at an angle.");
            if (p.FillJoints)
            {
                changed |= ImGui.SliderFloat("Knuckle size##model-creature-filljoint-size", ref p.JointFillScale, 0.3f, 2.5f, "x%.2f");
            }
        }

        changed |= DrawModelCreatureTweaks(p);

        return changed;
    }

    /// <summary>Targeted fine-tuning: pick any element of the generated model and resize / nudge / rotate / hide
    /// it, on top of the procedural parameters. Keyed by name, so the override sticks across regenerate and
    /// randomize. One generic panel reaches every part, instead of a dedicated slider per feature.</summary>
    private bool DrawModelCreatureTweaks(ModelCreatureParams p)
    {
        if (!ImGui.CollapsingHeader("Fine-tune (targeted)##model-creature-tweaks")) return false;

        bool changed = false;
        ImGui.TextWrapped("Sculpt any single element on top of the procedural base. Tweaks are keyed by element name and survive regenerate / randomize while the part exists.");

        List<string> names = _modelCreaturePreviewRoot == null
            ? []
            : _modelCreaturePreviewRoot.EnumerateSubtree()
                .Where(element => !ReferenceEquals(element, _modelCreaturePreviewRoot) && !string.IsNullOrEmpty(element.Name))
                .Select(element => element.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();

        ImGui.SetNextItemWidth(220f);
        if (ModelFilteredCombo("Element##model-creature-tweak-pick", _modelCreatureTweakSelected, names, out string picked, allowCustom: false, filterHint: "filter element names"))
        {
            _modelCreatureTweakSelected = picked;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick the element to fine-tune; it highlights yellow in the preview.");

        if (!string.IsNullOrEmpty(_modelCreatureTweakSelected))
        {
            ModelCreatureTweak tweak = p.Tweaks.TryGetValue(_modelCreatureTweakSelected, out ModelCreatureTweak? existing)
                ? existing
                : p.Tweaks[_modelCreatureTweakSelected] = new ModelCreatureTweak();

            changed |= ImGui.SliderFloat("Size##model-creature-tweak-size", ref tweak.SizeMul, 0.1f, 5f, "x%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Resize this element and its children about its joint pivot.");
            changed |= ImGui.DragFloat3("Offset##model-creature-tweak-off", ref tweak.Offset, 0.25f, -128f, 128f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Nudge this element (and subtree) in shape units (16 = 1 block).");
            changed |= ImGui.DragFloat3("Rotate##model-creature-tweak-rot", ref tweak.Rotate, 1f, -180f, 180f, "%.1f deg");
            changed |= ImGui.Checkbox("Hide##model-creature-tweak-hide", ref tweak.Hidden);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this element and its children from the model.");
            if (ImGui.Button("Reset element##model-creature-tweak-reset"))
            {
                p.Tweaks.Remove(_modelCreatureTweakSelected);
                changed = true;
            }
        }

        List<string> active = p.Tweaks.Where(entry => !entry.Value.IsNeutral).Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (active.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled($"{active.Count} active tweak(s):");
            foreach (string name in active)
            {
                if (ImGui.SmallButton($"x##model-creature-tweak-del-{name}"))
                {
                    p.Tweaks.Remove(name);
                    if (_modelCreatureTweakSelected == name) _modelCreatureTweakSelected = "";
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.Selectable($"{name}  {ModelCreatureTweakSummary(p.Tweaks[name])}##model-creature-tweak-sel-{name}", _modelCreatureTweakSelected == name))
                {
                    _modelCreatureTweakSelected = name;
                }
            }
        }

        // Keep the dictionary sparse: drop neutral (unedited) tweaks except the one currently selected.
        foreach (string key in p.Tweaks.Where(entry => entry.Key != _modelCreatureTweakSelected && entry.Value.IsNeutral).Select(entry => entry.Key).ToList())
        {
            p.Tweaks.Remove(key);
        }

        return changed;
    }

    private static string ModelCreatureTweakSummary(ModelCreatureTweak tweak)
    {
        if (tweak.Hidden) return "(hidden)";
        List<string> parts = [];
        if (Math.Abs(tweak.SizeMul - 1f) > 1e-4f) parts.Add($"x{tweak.SizeMul:0.##}");
        if (tweak.Offset != NVector3.Zero) parts.Add($"move({tweak.Offset.X:0.#},{tweak.Offset.Y:0.#},{tweak.Offset.Z:0.#})");
        if (tweak.Rotate != NVector3.Zero) parts.Add($"rot({tweak.Rotate.X:0.#},{tweak.Rotate.Y:0.#},{tweak.Rotate.Z:0.#})");
        return string.Join(" ", parts);
    }

    /// <summary>Per-bone limb tuning: a checkbox plus, when on, a length/girth/angle row for each bone. The
    /// override lists are grown/trimmed to the joint count (length and girth default to x1, angle to 0).</summary>
    private bool DrawModelCreaturePerBone(string id, int segCount, ref bool enabled, List<float> len, List<float> thick, List<float> angle)
    {
        bool changed = ImGui.Checkbox($"Per-bone tuning##model-creature-perbone-{id}", ref enabled);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tweak each bone's length, girth and angle individually instead of one even taper.");
        if (!enabled) return changed;

        ModelCreatureResizeList(len, segCount, 1f);
        ModelCreatureResizeList(thick, segCount, 1f);
        ModelCreatureResizeList(angle, segCount, 0f);

        for (int k = 0; k < segCount; k++)
        {
            ImGui.TextDisabled($"Bone {k + 1}");
            float l = len[k], t = thick[k], a = angle[k];
            ImGui.SetNextItemWidth(96f);
            if (ImGui.DragFloat($"Len##model-creature-perbone-{id}-l{k}", ref l, 0.01f, 0.05f, 4f, "x%.2f")) { len[k] = l; changed = true; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(96f);
            if (ImGui.DragFloat($"Girth##model-creature-perbone-{id}-t{k}", ref t, 0.01f, 0.05f, 4f, "x%.2f")) { thick[k] = t; changed = true; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            if (ImGui.DragFloat($"Angle##model-creature-perbone-{id}-a{k}", ref a, 0.5f, -90f, 90f, "%.1f deg")) { angle[k] = a; changed = true; }
        }
        return changed;
    }

    private static void ModelCreatureResizeList(List<float> list, int count, float fill)
    {
        while (list.Count < count) list.Add(fill);
        if (list.Count > count) list.RemoveRange(count, list.Count - count);
    }

    private void ModelApplyCreatureArchetype(ModelCreatureArchetype archetype)
    {
        ModelCreatureParams p = _modelCreatureParams;
        // Preserve placement, texture, workflow toggles and targeted tweaks across preset swaps.
        NVector3 center = p.Center;
        NVector3 rotation = p.Rotation;
        string texture = p.Texture;
        bool autoTexture = p.AutoTexture;
        int seed = p.Seed;
        Dictionary<string, ModelCreatureTweak> tweaks = p.Tweaks;

        ModelCreatureParams defaults = new();
        foreach (System.Reflection.FieldInfo field in typeof(ModelCreatureParams).GetFields())
        {
            field.SetValue(p, field.GetValue(defaults));
        }

        switch (archetype)
        {
            case ModelCreatureArchetype.Quadruped:
                break;
            case ModelCreatureArchetype.Biped:
                p.SpineSegments = 3;
                p.SpineLength = 12f;
                p.BodyHeight = 8f;
                p.LegPairs = 1;
                p.LegSegments = 3;
                p.LegLength = 13f;
                p.RearLegPos = 0.6f;
                p.ArmPairs = 1;
                p.ArmSegments = 3;
                p.ArmPos = 0.25f;
                p.ArmSplay = 12f;
                p.NeckSegments = 1;
                p.NeckPitch = -10f;
                p.TailSegments = 0;
                break;
            case ModelCreatureArchetype.Serpent:
                p.SpineSegments = 12;
                p.SpineLength = 40f;
                p.BodyWidth = 4f;
                p.BodyHeight = 4f;
                p.BodyTaper = 0.5f;
                p.LegPairs = 0;
                p.NeckSegments = 2;
                p.NeckLength = 5f;
                p.NeckThickness = 3.5f;
                p.HeadWidth = 4f;
                p.HeadHeight = 3.5f;
                p.TailSegments = 6;
                p.TailLength = 16f;
                p.TailThickness = 3f;
                p.TailTaper = 0.1f;
                p.TailDroop = 0f;
                break;
            case ModelCreatureArchetype.Hexapod:
                p.SpineSegments = 3;
                p.SpineLength = 12f;
                p.BodyWidth = 5f;
                p.BodyHeight = 4f;
                p.LegPairs = 3;
                p.LegSegments = 2;
                p.LegLength = 8f;
                p.LegThickness = 1.5f;
                p.LegSplay = 20f;
                p.NeckSegments = 1;
                p.NeckLength = 2f;
                p.HeadWidth = 4f;
                p.HeadHeight = 3.5f;
                p.HeadDepth = 4f;
                p.TailSegments = 0;
                p.HornPairs = 1;
                p.HornSegments = 2;
                p.HornLength = 4f;
                p.HornThickness = 0.75f;
                p.HornCurl = -8f;
                break;
            case ModelCreatureArchetype.Bird:
                p.SpineSegments = 3;
                p.SpineLength = 10f;
                p.BodyWidth = 6f;
                p.BodyHeight = 7f;
                p.LegPairs = 1;
                p.LegSegments = 3;
                p.LegLength = 9f;
                p.LegThickness = 1.5f;
                p.RearLegPos = 0.65f;
                p.NeckSegments = 2;
                p.NeckLength = 5f;
                p.NeckThickness = 3f;
                p.NeckPitch = -35f;
                p.HeadWidth = 3.5f;
                p.HeadHeight = 3.5f;
                p.HeadDepth = 3.5f;
                p.Snout = true;
                p.SnoutLength = 3f;
                p.SnoutSize = 1.5f;
                p.WingPairs = 1;
                p.WingSegments = 2;
                p.WingPos = 0.3f;
                p.TailSegments = 2;
                p.TailLength = 7f;
                p.TailThickness = 4f;
                p.TailTaper = 0.6f;
                p.TailDroop = -4f;
                break;
            case ModelCreatureArchetype.Wolf:
                // Showcases the organic-detail features: embedded shoulders/haunches, a crouched multi-segment
                // leg, a snout with a fanged jaw and nose, rounded ears with inner ear, fur cheeks and a fluffy tail.
                p.SpineSegments = 4;
                p.SpineLength = 20f;
                p.BodyWidth = 6f;
                p.BodyHeight = 7f;
                p.BodyTaper = 0.82f;
                p.BodyBulge = 1.15f;
                p.NeckSegments = 1;
                p.NeckLength = 4f;
                p.NeckThickness = 4.5f;
                p.NeckPitch = -18f;
                p.HeadWidth = 4.5f;
                p.HeadHeight = 4.5f;
                p.HeadDepth = 5f;
                p.Snout = true;
                p.SnoutLength = 3.5f;
                p.SnoutSize = 2.5f;
                p.SnoutHeightMul = 0.8f;
                p.Mouth = true;
                p.JawLength = 3.5f;
                p.Fangs = 2;
                p.FangSize = 0.6f;
                p.FangLength = 1.2f;
                p.Nose = true;
                p.NoseSize = 1.4f;
                p.Ears = 1;
                p.EarSize = 1.8f;
                p.EarHeight = 3f;
                p.EarSegments = 2;
                p.EarTaper = 0.5f;
                p.InnerEars = true;
                p.Cheeks = true;
                p.CheekLength = 4f;
                p.CheekSize = 3f;
                p.Eyes = true;
                p.EyeSize = 0.8f;
                p.LegPairs = 2;
                p.LegSegments = 3;
                p.LegLength = 12f;
                p.LegThickness = 2.6f;
                p.LegTaper = 0.7f;
                p.LegZigzag = 18f;
                p.FrontLegPos = 0.22f;
                p.RearLegPos = 0.82f;
                p.Feet = true;
                p.FootLength = 3f;
                p.FootWidth = 2.4f;
                p.Shoulders = true;
                p.ShoulderLength = 6f;
                p.ShoulderWidth = 4.5f;
                p.ShoulderThickness = 2.8f;
                p.ShoulderEmbed = 0.62f;
                p.TailSegments = 5;
                p.TailLength = 13f;
                p.TailThickness = 2.6f;
                p.TailTaper = 0.55f;
                p.TailBulge = 1.6f;
                p.TailDroop = 8f;
                p.TailBaseAngle = -8f;
                break;
            case ModelCreatureArchetype.Dragon:
                // Pulls every high-fidelity feature together: long serpentine neck and tail, four clawed legs with
                // embedded shoulders, a fanged crested head with brow and slit-pupil eyes, a belly, a dorsal ridge
                // and membrane wings.
                p.SpineSegments = 6;
                p.SpineLength = 30f;
                p.BodyWidth = 7f;
                p.BodyHeight = 8f;
                p.BodyTaper = 0.7f;
                p.BodyFrontTaper = 0.85f;
                p.BodyBulge = 1.2f;
                p.NeckSegments = 4;
                p.NeckLength = 17f;
                p.NeckThickness = 4.5f;
                p.NeckPitch = -28f;
                p.NeckTaper = 0.6f;
                p.NeckCurve = -7f;
                p.HeadWidth = 4.5f;
                p.HeadHeight = 4.5f;
                p.HeadDepth = 6f;
                p.Snout = true;
                p.SnoutLength = 4f;
                p.SnoutSize = 3f;
                p.SnoutHeightMul = 0.8f;
                p.Mouth = true;
                p.JawLength = 5f;
                p.Fangs = 3;
                p.FangSize = 0.6f;
                p.FangLength = 1.4f;
                p.Nose = true;
                p.NoseSize = 1.4f;
                p.Eyes = true;
                p.EyeSize = 0.9f;
                p.Pupils = true;
                p.PupilSize = 0.5f;
                p.Brow = true;
                p.Crest = true;
                p.CrestCount = 5;
                p.CrestHeight = 4.5f;
                p.CrestAngle = -55f;
                p.LegPairs = 2;
                p.LegSegments = 4;
                p.LegLength = 17f;
                p.LegThickness = 3f;
                p.LegTaper = 0.6f;
                p.LegZigzag = 20f;
                p.FrontLegPos = 0.3f;
                p.RearLegPos = 0.82f;
                p.Feet = true;
                p.FootLength = 3.5f;
                p.FootWidth = 3f;
                p.Toes = 3;
                p.ClawLength = 3f;
                p.ClawSize = 0.7f;
                p.Shoulders = true;
                p.ShoulderLength = 7f;
                p.ShoulderWidth = 5f;
                p.ShoulderThickness = 3f;
                p.ShoulderEmbed = 0.6f;
                p.Belly = true;
                p.BellyDepth = 3.5f;
                p.BellySize = 1.05f;
                p.WingPairs = 1;
                p.WingSegments = 2;
                p.WingSpan = 28f;
                p.WingChord = 8f;
                p.WingThickness = 1.5f;
                p.WingPos = 0.32f;
                p.WingHeight = 0.95f;
                p.WingStyle = (int)ModelCreatureWingStyle.Membrane;
                p.WingFingers = 4;
                p.WingMembraneTrail = 13f;
                p.TailSegments = 8;
                p.TailLength = 32f;
                p.TailThickness = 4f;
                p.TailTaper = 0.12f;
                p.TailDroop = 4f;
                p.TailBaseAngle = -6f;
                p.DorsalSpikes = 12;
                p.DorsalSpikeHeight = 3f;
                p.DorsalSpikeLength = 1.5f;
                p.DorsalSpikeAngle = 25f;
                p.DorsalSpikeStart = 0.1f;
                p.DorsalSpikeEnd = 0.95f;
                p.TailFin = true;
                p.TailFinHeight = 6f;
                p.TailFinLength = 5f;
                p.TailFinVertical = true;
                break;
            case ModelCreatureArchetype.Mammoth:
                // Megafauna: a bulky humped body on thick columnar legs, a long drooping trunk, curved tusks and
                // big flat ears (FotSA Elephantidae).
                p.SpineSegments = 4;
                p.SpineLength = 22f;
                p.BodyWidth = 10f;
                p.BodyHeight = 12f;
                p.BodyTaper = 0.85f;
                p.BodyBulge = 1.25f;
                p.NeckSegments = 1;
                p.NeckLength = 3f;
                p.NeckThickness = 8f;
                p.NeckPitch = -6f;
                p.HeadWidth = 7f;
                p.HeadHeight = 7f;
                p.HeadDepth = 6f;
                p.Trunk = true;
                p.TrunkSegments = 9;
                p.TrunkLength = 18f;
                p.TrunkThickness = 3.5f;
                p.TrunkTaper = 0.35f;
                p.TrunkDroop = 12f;
                p.Tusks = true;
                p.TuskSegments = 4;
                p.TuskLength = 16f;
                p.TuskThickness = 2.2f;
                p.TuskCurve = 18f;
                p.Ears = 1;
                p.EarSize = 2.5f;
                p.EarHeight = 5f;
                p.EarWidth = 2.4f;
                p.EarPitch = 30f;
                p.Eyes = true;
                p.EyeSize = 0.8f;
                p.Pupils = true;
                p.Hump = true;
                p.HumpHeight = 5f;
                p.HumpLength = 9f;
                p.HumpPos = 0.25f;
                p.LegPairs = 2;
                p.LegSegments = 3;
                p.LegLength = 16f;
                p.LegThickness = 5f;
                p.LegTaper = 0.8f;
                p.FrontLegPos = 0.25f;
                p.RearLegPos = 0.82f;
                p.Feet = true;
                p.FootLength = 4f;
                p.FootWidth = 5f;
                p.Toes = 3;
                p.ClawLength = 1.5f;
                p.ClawSize = 1.3f;
                p.ClawCurve = 0f;
                p.ClawSplay = 22f;
                p.Shoulders = true;
                p.ShoulderLength = 8f;
                p.ShoulderWidth = 6f;
                p.ShoulderThickness = 4f;
                p.TailSegments = 3;
                p.TailLength = 8f;
                p.TailThickness = 1.8f;
                p.TailTaper = 0.6f;
                p.TailDroop = 8f;
                p.TailTuft = true;
                p.TailTuftSize = 2.5f;
                break;
            case ModelCreatureArchetype.Bovine:
                // Cattle / aurochs: humped shoulders, a hanging dewlap, curved horns, cloven hooves and a tufted tail
                // (FotSA Bovinae).
                p.SpineSegments = 4;
                p.SpineLength = 20f;
                p.BodyWidth = 8f;
                p.BodyHeight = 9f;
                p.BodyTaper = 0.8f;
                p.BodyBulge = 1.15f;
                p.NeckSegments = 2;
                p.NeckLength = 6f;
                p.NeckThickness = 6f;
                p.NeckPitch = 8f;
                p.HeadWidth = 5f;
                p.HeadHeight = 5f;
                p.HeadDepth = 6f;
                p.Snout = true;
                p.SnoutLength = 3f;
                p.SnoutSize = 3.5f;
                p.SnoutHeightMul = 0.9f;
                p.Mouth = true;
                p.JawLength = 3f;
                p.Nose = true;
                p.Ears = 1;
                p.EarSize = 1.8f;
                p.EarHeight = 2.5f;
                p.EarWidth = 1.7f;
                p.EarPitch = 12f;
                p.HornPairs = 1;
                p.HornSegments = 3;
                p.HornLength = 11f;
                p.HornThickness = 1.8f;
                p.HornCurl = 18f;
                p.HornSplay = 38f;
                p.HornPitch = -12f;
                p.Eyes = true;
                p.EyeSize = 0.8f;
                p.Pupils = true;
                p.Hump = true;
                p.HumpHeight = 4f;
                p.HumpLength = 8f;
                p.HumpPos = 0.3f;
                p.Dewlap = true;
                p.DewlapDrop = 5f;
                p.DewlapLength = 6f;
                p.DewlapWidth = 3f;
                p.LegPairs = 2;
                p.LegSegments = 3;
                p.LegLength = 13f;
                p.LegThickness = 3f;
                p.LegTaper = 0.7f;
                p.FrontLegPos = 0.25f;
                p.RearLegPos = 0.82f;
                p.Feet = true;
                p.FootLength = 2.5f;
                p.FootWidth = 3f;
                p.Toes = 2;
                p.ClawLength = 1.8f;
                p.ClawSize = 1.3f;
                p.ClawCurve = 0f;
                p.ClawSplay = 8f;
                p.Shoulders = true;
                p.ShoulderLength = 6f;
                p.ShoulderWidth = 5f;
                p.ShoulderThickness = 3f;
                p.TailSegments = 5;
                p.TailLength = 14f;
                p.TailThickness = 1.5f;
                p.TailTaper = 0.5f;
                p.TailDroop = 10f;
                p.TailTuft = true;
                p.TailTuftSize = 2f;
                break;
            case ModelCreatureArchetype.Stag:
                // A deer / elk: slim body on long digitigrade legs, a raised head and big branching antlers
                // (the standout feature mined from the Wilderlands shepherd/caretaker and Darce's deerhorn drifters).
                p.SpineSegments = 4;
                p.SpineLength = 18f;
                p.BodyWidth = 6f;
                p.BodyHeight = 7f;
                p.BodyTaper = 0.85f;
                p.NeckSegments = 2;
                p.NeckLength = 8f;
                p.NeckThickness = 3.5f;
                p.NeckPitch = -42f;
                p.HeadWidth = 4f;
                p.HeadHeight = 4f;
                p.HeadDepth = 6f;
                p.Snout = true;
                p.SnoutLength = 3.5f;
                p.SnoutSize = 2.5f;
                p.SnoutHeightMul = 0.8f;
                p.Nose = true;
                p.NoseSize = 1.2f;
                p.Ears = 1;
                p.EarSize = 1.6f;
                p.EarHeight = 3.5f;
                p.EarTaper = 0.5f;
                p.Eyes = true;
                p.EyeSize = 0.7f;
                p.AntlerPairs = 1;
                p.AntlerBeamSegments = 3;
                p.AntlerBeamLength = 11f;
                p.AntlerBeamThickness = 1.4f;
                p.AntlerTines = 4;
                p.AntlerTineLength = 5f;
                p.AntlerSpread = 34f;
                p.AntlerSweep = 14f;
                p.AntlerTineSpread = 40f;
                p.LegPairs = 2;
                p.LegSegments = 3;
                p.LegLength = 15f;
                p.LegThickness = 2f;
                p.LegTaper = 0.6f;
                p.LegZigzag = 16f;
                p.FrontLegPos = 0.22f;
                p.RearLegPos = 0.82f;
                p.Feet = true;
                p.FootLength = 2.5f;
                p.FootWidth = 1.8f;
                p.Toes = 2;
                p.ClawLength = 1.2f;
                p.ClawSize = 0.9f;
                p.TailSegments = 2;
                p.TailLength = 4f;
                p.TailThickness = 1.5f;
                p.TailTaper = 0.4f;
                p.TailTuft = true;
                p.TailTuftSize = 1.5f;
                break;
            case ModelCreatureArchetype.Scorpion:
                // An arthropod: a low segmented body on six legs, big front pincers (mandibles), a cluster of
                // compound eyes, and a long segmented tail tipped with a stinger (Wilderlands banefly/blightworm).
                p.SpineSegments = 5;
                p.SpineLength = 16f;
                p.BodyWidth = 6f;
                p.BodyHeight = 3.5f;
                p.BodyTaper = 0.7f;
                p.NeckSegments = 1;
                p.NeckLength = 2f;
                p.NeckThickness = 4f;
                p.NeckPitch = 0f;
                p.HeadWidth = 5f;
                p.HeadHeight = 3f;
                p.HeadDepth = 4f;
                p.Mandibles = true;
                p.MandibleSegments = 3;
                p.MandibleLength = 7f;
                p.MandibleThickness = 1.2f;
                p.MandibleGape = 14f;
                p.MandibleCurve = 26f;
                p.MandibleFangs = true;
                p.Eyes = true;
                p.EyeSize = 0.7f;
                p.ExtraEyes = 4;
                p.ExtraEyeSize = 0.4f;
                p.ExtraEyeSpread = 1.6f;
                p.LegPairs = 3;
                p.LegSegments = 3;
                p.LegLength = 9f;
                p.LegThickness = 1.2f;
                p.LegSplay = 24f;
                p.LegZigzag = 22f;
                p.FrontLegPos = 0.2f;
                p.RearLegPos = 0.78f;
                p.TailSegments = 7;
                p.TailLength = 18f;
                p.TailThickness = 2.5f;
                p.TailTaper = 0.45f;
                p.TailDroop = -10f;
                p.TailBaseAngle = 30f;
                p.TailStinger = true;
                p.StingerCount = 3;
                p.StingerLength = 4f;
                p.StingerSize = 1f;
                p.StingerSpread = 18f;
                p.StingerCurve = -28f;
                break;
        }

        p.Center = center;
        p.Rotation = rotation;
        p.Texture = texture;
        p.AutoTexture = autoTexture;
        p.Seed = seed;
        p.Tweaks = tweaks;
    }

    private void ModelRandomizeCreature(ModelCreatureParams p)
    {
        Random r = new(p.Seed);
        float Range(float min, float max) => min + (float)r.NextDouble() * (max - min);

        p.SpineSegments = r.Next(2, 7);
        p.SpineLength = Range(10f, 30f);
        p.BodyWidth = Range(4f, 9f);
        p.BodyHeight = Range(4f, 9f);
        p.BodyTaper = Range(0.5f, 1.1f);

        p.NeckSegments = r.Next(0, 4);
        p.NeckLength = Range(2f, 9f);
        p.NeckThickness = Range(2.5f, 5f);
        p.NeckPitch = Range(-45f, 20f);
        p.HeadWidth = Range(3f, 7f);
        p.HeadHeight = Range(3f, 7f);
        p.HeadDepth = Range(3f, 8f);
        p.Snout = r.NextDouble() < 0.45;
        p.SnoutLength = Range(2f, 5f);
        p.SnoutSize = Range(1.5f, 4f);

        p.LegPairs = r.Next(0, 5);
        p.LegSegments = r.Next(1, 5);
        p.LegLength = Range(6f, 16f);
        p.LegThickness = Range(1.5f, 4.5f);
        p.LegSplay = Range(-5f, 25f);
        p.LegBend = Range(-10f, 15f);

        p.ArmPairs = r.Next(0, 2);
        p.ArmSegments = r.Next(1, 4);
        p.ArmLength = Range(5f, 12f);
        p.ArmThickness = Range(1.5f, 3.5f);

        p.TailSegments = r.Next(0, 9);
        p.TailLength = Range(5f, 18f);
        p.TailThickness = Range(2f, 5f);
        p.TailTaper = Range(0.1f, 0.6f);
        p.TailDroop = Range(-8f, 14f);

        p.WingPairs = r.Next(0, 2);
        p.HornPairs = r.Next(0, 2);
        p.HornSegments = r.Next(1, 4);
        p.HornLength = Range(2f, 8f);
        p.HornCurl = Range(-25f, 25f);
        p.Ears = r.Next(0, 2);
        p.Eyes = r.NextDouble() < 0.6;

        // Advanced flourishes so Randomize explores the full space, not just the basic parameters.
        p.UniformScale = Range(0.7f, 1.5f);
        p.BodyFrontTaper = Range(0.7f, 1.1f);
        p.BodyBulge = Range(0.85f, 1.6f);
        p.SpineCurve = Range(-6f, 9f);
        p.NeckTaper = Range(0.6f, 1.1f);
        p.NeckCurve = Range(-12f, 12f);
        p.HeadTilt = Range(-15f, 10f);
        p.SnoutWidthMul = Range(0.6f, 1.9f);
        p.SnoutHeightMul = Range(0.5f, 1.5f);
        p.SnoutDroop = Range(-10f, 22f);
        p.LegTaper = Range(0.5f, 1.05f);
        p.LegForwardLean = Range(-12f, 12f);
        p.Feet = r.NextDouble() < 0.5;
        p.FootLength = Range(2f, 6f);
        p.ArmTaper = Range(0.6f, 1.05f);
        p.Hands = p.ArmPairs > 0 && r.NextDouble() < 0.5;
        p.TailWidthMul = Range(0.5f, 1.5f);
        p.TailHeightMul = Range(0.4f, 1.7f);
        p.TailBaseAngle = Range(-10f, 28f);
        p.WingDihedral = Range(-10f, 32f);
        p.WingChordTaper = Range(0.4f, 1.05f);
        p.DorsalSpikes = r.NextDouble() < 0.4 ? r.Next(3, 13) : 0;
        p.DorsalSpikeHeight = Range(2f, 7f);
        p.DorsalSpikeLength = Range(1f, 4f);
        p.DorsalSpikeAngle = Range(-5f, 45f);

        // Organic detail flourishes.
        p.Shoulders = r.NextDouble() < 0.55;
        p.ShoulderEmbed = Range(0.45f, 0.8f);
        p.ShoulderLength = Range(4f, 8f);
        p.ShoulderWidth = Range(3f, 6f);
        p.LegZigzag = Range(0f, 25f);
        p.ArmZigzag = Range(0f, 20f);
        p.TailBulge = Range(0.9f, 2.1f);
        p.Mouth = r.NextDouble() < 0.45;
        p.JawDrop = Range(0f, 25f);
        p.Fangs = r.Next(0, 4);
        p.Nose = r.NextDouble() < 0.6;
        p.Cheeks = r.NextDouble() < 0.4;
        p.EarSegments = r.Next(1, 4);
        p.EarTaper = Range(0.3f, 1.05f);
        p.InnerEars = r.NextDouble() < 0.5;

        // High-fidelity detail flourishes.
        p.Toes = r.NextDouble() < 0.5 ? r.Next(3, 5) : 0;
        p.ClawLength = Range(1.5f, 3.5f);
        p.ClawCurve = Range(-25f, 0f);
        p.Pupils = r.NextDouble() < 0.6;
        p.Brow = r.NextDouble() < 0.4;
        p.Crest = r.NextDouble() < 0.35;
        p.CrestCount = r.Next(3, 7);
        p.CrestHeight = Range(2f, 6f);
        p.CrestAngle = Range(-75f, -25f);
        p.Belly = r.NextDouble() < 0.4;
        p.BellyDepth = Range(2f, 5f);
        p.WingStyle = r.NextDouble() < 0.5 ? (int)ModelCreatureWingStyle.Membrane : (int)ModelCreatureWingStyle.Feathered;
        p.WingFingers = r.Next(3, 6);
        p.TailFin = r.NextDouble() < 0.35;
        p.TailFinHeight = Range(3f, 7f);
        p.TailFinVertical = r.NextDouble() < 0.7;

        // Megafauna flourishes.
        p.Trunk = r.NextDouble() < 0.2;
        p.TrunkLength = Range(8f, 18f);
        p.TrunkDroop = Range(6f, 16f);
        p.Tusks = r.NextDouble() < 0.3;
        p.TuskLength = Range(6f, 16f);
        p.TuskCurve = Range(8f, 22f);
        p.Hump = r.NextDouble() < 0.35;
        p.HumpHeight = Range(2f, 6f);
        p.Dewlap = r.NextDouble() < 0.3;
        p.DewlapDrop = Range(2f, 6f);
        p.TailTuft = r.NextDouble() < 0.4;
        p.EarWidth = Range(0.8f, 2.4f);
        p.EarPitch = Range(-10f, 35f);

        p.Mane = r.NextDouble() < 0.3;
        p.ManeHeight = Range(2f, 5f);
        p.FinPairs = r.NextDouble() < 0.25 ? r.Next(1, 4) : 0;
        p.FinSpan = Range(4f, 9f);
        p.Antennae = r.NextDouble() < 0.2;
        p.AntennaeLength = Range(5f, 12f);
        p.TailPlume = r.NextDouble() < 0.25;
        p.PlumeCount = r.Next(5, 11);
        p.Shell = r.NextDouble() < 0.15;
        p.ShellHeight = Range(3f, 7f);

        // Studied-creature detail flourishes.
        p.AntlerPairs = r.NextDouble() < 0.2 ? 1 : 0;
        p.AntlerBeamSegments = r.Next(2, 5);
        p.AntlerBeamLength = Range(7f, 13f);
        p.AntlerTines = r.Next(2, 6);
        p.AntlerTineLength = Range(3f, 6f);
        p.AntlerSpread = Range(20f, 42f);
        p.Mandibles = r.NextDouble() < 0.2;
        p.MandibleLength = Range(3f, 7f);
        p.MandibleGape = Range(8f, 26f);
        p.MandibleCurve = Range(10f, 30f);
        p.MandibleFangs = r.NextDouble() < 0.5;
        p.ExtraEyes = r.NextDouble() < 0.25 ? r.Next(2, 7) : 0;
        p.ExtraEyeSize = Range(0.4f, 0.8f);
        p.TailStinger = r.NextDouble() < 0.2;
        p.StingerCount = r.Next(1, 5);
        p.StingerLength = Range(3f, 6f);
        p.StingerCurve = Range(-30f, 20f);
        p.WingFeathers = p.WingPairs > 0 && r.NextDouble() < 0.5 ? r.Next(4, 12) : 0;
        p.WingFeatherLength = Range(5f, 9f);
        p.BodySpikes = r.NextDouble() < 0.2 ? r.Next(6, 24) : 0;
        p.BodySpikeLength = Range(2f, 6f);
        p.BodySpikeSize = Range(0.6f, 1.6f);
    }

    // ---- Builder -----------------------------------------------------------

    private ModelElementData? ModelBuildCreature(out string error)
    {
        error = "";
        if (_modelDoc == null) return null;

        try
        {
            ModelCreatureParams p = _modelCreatureParams;
            p.Rotation.X = (float)ModelWrapDegrees(p.Rotation.X);
            p.Rotation.Y = (float)ModelWrapDegrees(p.Rotation.Y);
            p.Rotation.Z = (float)ModelWrapDegrees(p.Rotation.Z);

            string texture = string.IsNullOrWhiteSpace(p.Texture)
                ? _modelDoc.Textures.FirstOrDefault()?.Code ?? ""
                : p.Texture;

            // Built head-toward +X internally, but Vintage Story entities face -X (verified against vanilla
            // quadrupeds), so the group carries a base 180 deg yaw. A 180 deg turn about Y keeps the fore-aft
            // axis on X, so the locomotion generator's Z-axis leg swing still reads as forward/backward.
            ModelElementData group = new()
            {
                Name = "Creature",
                From = [p.Center.X, p.Center.Y, p.Center.Z],
                To = [p.Center.X, p.Center.Y, p.Center.Z],
                RotationOrigin = [p.Center.X, p.Center.Y, p.Center.Z],
                RotationX = ModelPrimitiveRound(p.Rotation.X),
                RotationY = ModelPrimitiveRound(ModelWrapDegrees(p.Rotation.Y + 180.0)),
                RotationZ = ModelPrimitiveRound(p.Rotation.Z)
            };

            int spineCount = Math.Clamp(p.SpineSegments, 1, 16);
            double spineLen = Math.Max(1.0, p.SpineLength);
            double segLen = spineLen / spineCount;
            double bodyW = Math.Max(0.5, p.BodyWidth);
            double bodyH = Math.Max(0.5, p.BodyHeight);

            // Spine: chain along +X (the Vintage Story forward axis), centered so x spans [-L/2, +L/2].
            // Lateral (left/right) is Z, up is Y. This matches vanilla entities so the preview frames the
            // creature correctly and one locomotion convention animates it.
            List<double[]> spineSizes = [];
            List<double> spineBends = [];
            for (int k = 0; k < spineCount; k++)
            {
                double t = spineCount <= 1 ? 1.0 : (double)k / (spineCount - 1); // 0 = rear, 1 = front
                double scale = ModelCreatureLerp(p.BodyTaper, p.BodyFrontTaper, t);
                double bulge = ModelCreatureLerp(1.0, Math.Max(0.1, p.BodyBulge), 4.0 * t * (1.0 - t)); // 0 at both ends, peaks mid-spine
                spineSizes.Add([segLen, bodyH * scale * bulge, bodyW * scale * bulge]);
                spineBends.Add(k == 0 ? 0.0 : p.SpineCurve);
            }

            // Spine curve bends each joint about Z (the up/down plane); default 0 keeps the back straight.
            ModelCreatureChain(group, [-spineLen * 0.5, 0.0, 0.0], 0.0, 0.0, 0.0, 0, 1, spineSizes, spineBends, 2, "spine", out _);

            // Collect spine elements rear->front (each currently has only its spine successor as a child).
            List<ModelElementData> spine = [];
            for (ModelElementData? cur = group.Children.Count > 0 ? group.Children[0] : null; cur != null; cur = cur.Children.Count > 0 ? cur.Children[0] : null)
            {
                spine.Add(cur);
            }

            ModelElementData frontVertebra = spine[^1];
            ModelElementData rearVertebra = spine[0];

            ModelBuildCreatureHead(p, frontVertebra);
            ModelBuildCreatureLimbs(p, spine, isArm: false);
            ModelBuildCreatureLimbs(p, spine, isArm: true);
            ModelBuildCreatureTail(p, rearVertebra);
            ModelBuildCreatureWings(p, spine);
            ModelBuildCreatureDorsalSpikes(p, spine);
            ModelBuildCreatureBodySpikes(p, spine);
            ModelBuildCreatureBelly(p, spine, spineLen, bodyW);
            ModelBuildCreatureHump(p, spine);
            ModelBuildCreatureDewlap(p, frontVertebra);
            ModelBuildCreatureFins(p, spine);
            ModelBuildCreatureShell(p, spine, spineLen, bodyW);
            ModelBuildCreatureTailPlume(p, rearVertebra);

            // Post-processes over the finished skeleton: knuckles that close angled joint gaps, then facet
            // boxes that round the chunky volumes. Both snapshot the structural elements first so they don't
            // recurse onto their own additions.
            ModelCreatureFillJoints(p, group);
            if (!ModelIsMeshLibMode)
            {
                ModelCreatureApplySmoothing(p, group);
            }

            // Targeted per-element overrides on top of the procedural base (resize/move/rotate/hide named parts).
            ModelApplyCreatureTweaks(group);

            ModelCreatureScaleSubtree(group, Math.Clamp((double)p.UniformScale, 0.1, 8.0));

            ModelCreatureAssignFaces(group, texture, p.AutoTexture);

            if (ModelIsMeshLibMode)
            {
                ModelAssignGeneratedMeshSpecs(group, element => ModelCreatureGeneratedMeshSpec(element, p.Smoothing));
                if (!ModelMaterializeGeneratedMeshes(group, include: null, out string meshError))
                {
                    error = meshError;
                    return null;
                }
            }

            int count = ModelCreatureElementCount(group);
            if (count == 0)
            {
                error = "The current parameters produce no elements.";
                return null;
            }
            if (count > ModelCreatureMaxElements)
            {
                error = $"Too many elements ({count} > {ModelCreatureMaxElements}). Reduce limb pairs, joints or segments.";
                return null;
            }

            return group;
        }
        catch (Exception exception)
        {
            error = $"Generation failed: {exception.Message}";
            return null;
        }
    }

    private void ModelBuildCreatureHead(ModelCreatureParams p, ModelElementData frontVertebra)
    {
        ModelElementData attachTo = frontVertebra;
        double[] attach = ModelCreatureDistalAttach(frontVertebra, 0, 1);
        // Neck pitch (raise/lower the head) is in the vertical plane that contains the forward axis (X),
        // i.e. a rotation about Z.
        double pitch = 0.0;

        List<ModelElementData> neckChain = [];
        int neckCount = Math.Clamp(p.NeckSegments, 0, 8);
        if (neckCount > 0 && p.NeckLength > 0.01)
        {
            double neckSeg = p.NeckLength / neckCount;
            double th = Math.Max(0.5, p.NeckThickness);
            List<double[]> neckSizes = [];
            List<double> neckBends = [];
            for (int k = 0; k < neckCount; k++)
            {
                double t = neckCount <= 1 ? 0.0 : (double)k / (neckCount - 1);
                double segTh = th * ModelCreatureLerp(1.0, Math.Max(0.1, p.NeckTaper), t);
                neckSizes.Add([neckSeg, segTh, segTh]);
                neckBends.Add(k == 0 ? 0.0 : p.NeckCurve);
            }
            ModelElementData neckFirst = ModelCreatureChain(frontVertebra, attach, 0.0, 0.0, p.NeckPitch, 0, 1, neckSizes, neckBends, 2, "neck", out ModelElementData neckTip);
            // Collect the neck chain (each segment's only child so far is its successor) for the mane.
            for (ModelElementData? cur = neckFirst; cur != null; cur = cur.Children.Count > 0 ? cur.Children[0] : null)
            {
                neckChain.Add(cur);
            }
            attachTo = neckTip;
            attach = ModelCreatureDistalAttach(neckTip, 0, 1);
        }
        else
        {
            pitch = p.NeckPitch;
        }

        double hw = Math.Max(0.5, p.HeadWidth);   // lateral (Z)
        double hh = Math.Max(0.5, p.HeadHeight);  // up (Y)
        double hd = Math.Max(0.5, p.HeadDepth);   // fore-aft (X)
        ModelCreatureChain(attachTo, attach, 0.0, 0.0, pitch + p.HeadTilt, 0, 1, [[hd, hh, hw]], null, 0, "head", out ModelElementData head);

        ModelElementData? snout = null;
        if (p.Snout)
        {
            double ss = Math.Max(0.5, p.SnoutSize);
            double sl = Math.Max(0.5, p.SnoutLength);
            double sh = Math.Max(0.25, ss * p.SnoutHeightMul);  // up (Y)
            double sw = Math.Max(0.25, ss * p.SnoutWidthMul);   // lateral (Z)
            // Attach low on the head front (+X face) so the snout reads as a muzzle/beak; droop turns it down.
            double[] snoutAttach = [head.To[0] - head.From[0], (head.To[1] - head.From[1]) * 0.35, ModelCreatureCenterRel(head, 2)];
            snout = ModelCreatureChain(head, snoutAttach, 0.0, 0.0, p.SnoutDroop, 0, 1, [[sl, sh, sw]], null, 0, "snout", out _);
        }

        ModelBuildCreatureMouth(p, head);
        ModelBuildCreatureMandibles(p, head);
        ModelBuildCreatureNose(p, head, snout);
        ModelBuildCreatureTrunk(p, head);
        ModelBuildCreatureTusks(p, head);
        ModelBuildCreatureAntennae(p, head);
        ModelBuildCreatureCrest(p, head);
        ModelBuildCreatureAntlers(p, head);
        ModelBuildCreatureMane(p, neckChain, head);
        ModelBuildCreatureHeadDetails(p, head);
    }

    private void ModelBuildCreatureAntennae(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Antennae) return;

        int count = Math.Clamp(p.AntennaeSegments, 1, 8);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double segLen = Math.Max(0.5, p.AntennaeLength) / count;
        double baseTh = Math.Max(0.1, p.AntennaeThickness);

        foreach (int side in ModelCreatureSides(2))
        {
            List<double[]> sizes = [];
            List<double> bends = [];
            for (int k = 0; k < count; k++)
            {
                double t = count <= 1 ? 0.0 : (double)k / (count - 1);
                double th = baseTh * ModelCreatureLerp(1.0, 0.5, t);
                sizes.Add([th, segLen, th]);
                bends.Add(k == 0 ? 0.0 : p.AntennaeCurve);
            }
            // Thin feelers from the head front-top, splayed out and curving.
            double[] attach = [headX * 0.85, headY, headZ * 0.5 + side * headZ * 0.25];
            ModelCreatureChain(head, attach, side * -p.AntennaeSplay, 0.0, 0.0, 1, 1, sizes, bends, 2, side > 0 ? "antennaRight" : "antennaLeft", out _);
        }
    }

    private void ModelBuildCreatureMane(ModelCreatureParams p, List<ModelElementData> neckChain, ModelElementData head)
    {
        if (!p.Mane) return;

        // One swept-back plate on top of each neck segment (denser with more neck segments); falls back to the
        // head if the creature has no neck.
        List<ModelElementData> segments = neckChain.Count > 0 ? neckChain : [head];
        double h = Math.Max(0.25, p.ManeHeight);
        double len = Math.Max(0.25, p.ManeLength);
        for (int i = 0; i < segments.Count; i++)
        {
            ModelElementData seg = segments[i];
            double sy = seg.To[1] - seg.From[1];
            double thin = Math.Max(0.25, (seg.To[2] - seg.From[2]) * 0.14);
            double[] attach = [ModelCreatureCenterRel(seg, 0), sy, ModelCreatureCenterRel(seg, 2)];
            ModelCreatureChain(seg, attach, 0.0, 0.0, p.ManeAngle, 1, 1, [[len, h, thin]], null, 0, $"mane{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureTrunk(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Trunk) return;

        int count = Math.Clamp(p.TrunkSegments, 1, 16);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double segLen = Math.Max(0.5, p.TrunkLength) / count;
        double baseTh = Math.Max(0.5, p.TrunkThickness);

        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < count; k++)
        {
            double t = count <= 1 ? 0.0 : (double)k / (count - 1);
            double th = baseTh * ModelCreatureLerp(1.0, Math.Max(0.1, p.TrunkTaper), t);
            sizes.Add([segLen, th, th]);
            bends.Add(k == 0 ? 0.0 : -p.TrunkDroop); // curls downward about Z
        }
        // Grows forward (+X) from the head's lower front, then droops - an elephant proboscis.
        double[] attach = [headX, headY * 0.3, headZ * 0.5];
        ModelCreatureChain(head, attach, 0.0, 0.0, 0.0, 0, 1, sizes, bends, 2, "trunk", out _);
    }

    private void ModelBuildCreatureTusks(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Tusks) return;

        int count = Math.Clamp(p.TuskSegments, 1, 8);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double segLen = Math.Max(0.5, p.TuskLength) / count;
        double baseTh = Math.Max(0.25, p.TuskThickness);

        foreach (int side in ModelCreatureSides(2))
        {
            List<double[]> sizes = [];
            List<double> bends = [];
            for (int k = 0; k < count; k++)
            {
                double t = count <= 1 ? 0.0 : (double)k / (count - 1);
                double th = baseTh * ModelCreatureLerp(1.0, 0.25, t); // taper to a point
                sizes.Add([segLen, th, th]);
                bends.Add(k == 0 ? 0.0 : p.TuskCurve); // curves forward then up
            }
            // Low on the head front, offset laterally; grows forward (+X), curving up.
            double[] attach = [headX * p.TuskForward, headY * 0.18, headZ * 0.5 + side * headZ * 0.25];
            ModelCreatureChain(head, attach, 0.0, 0.0, 0.0, 0, 1, sizes, bends, 2, side > 0 ? "tuskRight" : "tuskLeft", out _);
        }
    }

    private void ModelBuildCreatureCrest(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Crest) return;

        int count = Math.Clamp(p.CrestCount, 1, 12);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double h = Math.Max(0.25, p.CrestHeight);
        double len = Math.Max(0.25, p.CrestLength);
        double w = Math.Max(0.25, headZ * 0.12);
        double start = Math.Clamp(p.CrestStart, 0f, 1f);
        double end = Math.Clamp(p.CrestEnd, 0f, 1f);

        for (int i = 0; i < count; i++)
        {
            double frac = count == 1 ? (start + end) * 0.5 : ModelCreatureLerp(start, end, (double)i / (count - 1));
            double scale = ModelCreatureLerp(0.65, 1.0, Math.Sin(Math.PI * frac)); // taller toward the middle
            // A swept-back flat plate on top of the head, centred laterally.
            double[] attach = [headX * frac, headY, headZ * 0.5];
            ModelCreatureChain(head, attach, 0.0, 0.0, p.CrestAngle, 1, 1, [[len, h * scale, w]], null, 0, $"crest{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureMouth(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Mouth) return;

        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double jl = Math.Max(0.5, p.JawLength);
        double jw = Math.Max(0.5, headZ * 0.7);
        double jh = Math.Max(0.4, headY * 0.2);

        // Lower jaw hinged near the head's lower front, growing forward (+X); JawDrop swings it open about Z.
        double[] jawAttach = [headX * 0.85, headY * 0.16, headZ * 0.5];
        ModelElementData jaw = ModelCreatureChain(head, jawAttach, 0.0, 0.0, -p.JawDrop, 0, 1, [[jl, jh, jw]], null, 0, "jaw", out _);

        int fangs = Math.Clamp(p.Fangs, 0, 6);
        if (fangs == 0) return;

        double fs = Math.Max(0.1, p.FangSize);
        double fl = Math.Max(0.25, p.FangLength);
        double step = fs * 1.6;
        foreach (int side in ModelCreatureSides(2))
        {
            string sideTag = side > 0 ? "R" : "L";
            double upperLat = side > 0 ? headZ * 0.72 : headZ * 0.28;
            double lowerLat = side > 0 ? jw * 0.72 : jw * 0.28;
            for (int i = 0; i < fangs; i++)
            {
                // Upper fangs hang down from the head's front-bottom; lower fangs stand up from the jaw front.
                ModelCreatureBox(head, [headX * 0.92 - i * step, -fl * 0.35, upperLat], [fs, fl, fs], 0.0, 0.0, 0.0, $"fangUp{sideTag}{i + 1}");
                ModelCreatureBox(jaw, [jl * 0.85 - i * step, jh + fl * 0.3, lowerLat], [fs, fl, fs], 0.0, 0.0, 0.0, $"fangLow{sideTag}{i + 1}");
            }
        }
    }

    private void ModelBuildCreatureNose(ModelCreatureParams p, ModelElementData head, ModelElementData? snout)
    {
        if (!p.Nose) return;

        ModelElementData target = snout ?? head;
        double tx = target.To[0] - target.From[0];
        double ty = target.To[1] - target.From[1];
        double tz = target.To[2] - target.From[2];
        double ns = Math.Max(0.25, p.NoseSize);
        // A rounded snout tip on the upper front face.
        double[] center = [tx * 0.96, ty * 0.62, tz * 0.5];
        ModelCreatureBox(target, center, [ns * 0.8, ns, ns * 1.2], 0.0, 0.0, 0.0, "nose");
    }

    private void ModelBuildCreatureHeadDetails(ModelCreatureParams p, ModelElementData head)
    {
        double headX = head.To[0] - head.From[0]; // fore-aft
        double headY = head.To[1] - head.From[1]; // up
        double headZ = head.To[2] - head.From[2]; // lateral

        if (p.Ears > 0)
        {
            double es = Math.Max(0.25, p.EarSize);
            double eh = Math.Max(0.25, p.EarHeight);
            int earSegs = Math.Clamp(p.EarSegments, 1, 4);
            double earSegLen = eh / earSegs;
            double earTaper = Math.Max(0.05, p.EarTaper);
            foreach (int side in ModelCreatureSides(2))
            {
                // On top of the head, offset laterally (Z); splay outward is a tilt about X. Multiple tapering
                // segments round the ear off; the inner-ear box insets the front face.
                double earW = Math.Max(0.1, p.EarWidth);
                List<double[]> earSizes = [];
                for (int k = 0; k < earSegs; k++)
                {
                    double t = earSegs <= 1 ? 0.0 : (double)k / (earSegs - 1);
                    double sc = ModelCreatureLerp(1.0, earTaper, t);
                    earSizes.Add([es * sc, earSegLen, es * sc * earW]); // EarWidth flares the ear out laterally (elephant)
                }
                double[] attach = [headX * p.EarForward, headY, headZ * 0.5 + side * headZ * 0.28];
                string earName = side > 0 ? "earRight" : "earLeft";
                ModelElementData earBase = ModelCreatureChain(head, attach, side * -p.EarAngle, 0.0, p.EarPitch, 1, 1, earSizes, null, 0, earName, out _);

                if (p.InnerEars)
                {
                    ModelCreatureBox(earBase, [es * 0.9, earSegLen * 0.5, es * 0.5], [es * 0.3, earSegLen * 0.7, es * 0.6], 0.0, 0.0, 0.0, $"{earName}Inner");
                }
            }
        }

        if (p.Cheeks)
        {
            double cs = Math.Max(0.5, p.CheekSize);     // vertical (Y)
            double cl = Math.Max(0.5, p.CheekLength);   // fore-aft (X)
            double ct = Math.Max(0.25, headZ * 0.2);    // lateral (Z)
            foreach (int side in ModelCreatureSides(2))
            {
                // Fur patches on the rear-lateral of the head, swept back and flared outward (yaw about Y).
                double zFace = side > 0 ? headZ : 0.0;
                double[] center = [headX * 0.32, headY * 0.45, zFace + side * ct * 0.25];
                ModelCreatureBox(head, center, [cl, cs, ct], 0.0, side * -p.CheekAngle, 0.0, side > 0 ? "cheekRight" : "cheekLeft");
            }
        }

        if (p.HornPairs > 0)
        {
            int hc = Math.Clamp(p.HornSegments, 1, 6);
            double hornSeg = Math.Max(0.5, p.HornLength) / hc;
            double baseTh = Math.Max(0.25, p.HornThickness);
            for (int pair = 0; pair < p.HornPairs; pair++)
            {
                double xfrac = p.HornPairs == 1 ? p.HornForward : ModelCreatureLerp(0.8, 0.3, (double)pair / (p.HornPairs - 1));
                foreach (int side in ModelCreatureSides(2))
                {
                    List<double[]> sizes = [];
                    List<double> bends = [];
                    for (int k = 0; k < hc; k++)
                    {
                        double t = hc <= 1 ? 0.0 : (double)k / (hc - 1);
                        double th = baseTh * ModelCreatureLerp(1.0, 0.4, t);
                        sizes.Add([th, hornSeg, th]);
                        bends.Add(k == 0 ? 0.0 : p.HornCurl);
                    }
                    // Grow up (Y); lateral splay tilts about X, pitch leans about Z at the base, curl bends about Z per joint.
                    double[] attach = [headX * xfrac, headY, headZ * 0.5 + side * headZ * 0.3];
                    ModelCreatureChain(head, attach, side * -p.HornSplay, 0.0, p.HornPitch, 1, 1, sizes, bends, 2, $"horn{(side > 0 ? "Right" : "Left")}{pair + 1}", out _);
                }
            }
        }

        if (p.Eyes)
        {
            double eye = Math.Max(0.25, p.EyeSize);
            double pupil = Math.Max(0.1, p.PupilSize);
            foreach (int side in ModelCreatureSides(2))
            {
                // On the upper front of the head, poking out the lateral (Z) faces.
                double zFace = side > 0 ? headZ : 0.0;
                double[] attach = [headX * p.EyeForward, headY * p.EyeHeight, zFace];
                string eyeName = side > 0 ? "eyeRight" : "eyeLeft";
                ModelElementData eyeEl = ModelCreatureChain(head, attach, 0.0, 0.0, 0.0, 2, side, [[eye, eye, eye]], null, 0, eyeName, out _);
                if (p.Pupils)
                {
                    // A smaller pupil proud of the eye's outer (lateral) face.
                    double ex = eyeEl.To[0] - eyeEl.From[0];
                    double ey = eyeEl.To[1] - eyeEl.From[1];
                    double ez = eyeEl.To[2] - eyeEl.From[2];
                    double[] pc = [ex * 0.55, ey * 0.5, side > 0 ? ez : 0.0];
                    ModelCreatureBox(eyeEl, pc, [pupil, pupil, pupil * 0.6], 0.0, 0.0, 0.0, eyeName + "Pupil");
                }

                if (p.ExtraEyes > 0)
                {
                    // A cluster of smaller eyes scattered around the main one (compound / arachnid eyes).
                    int extra = Math.Clamp(p.ExtraEyes, 0, 8);
                    double small = Math.Max(0.1, p.ExtraEyeSize);
                    double spread = Math.Max(0.2, p.ExtraEyeSpread) * small;
                    for (int e = 0; e < extra; e++)
                    {
                        double ang = extra <= 1 ? 0.0 : 2.0 * Math.PI * e / extra;
                        double ringX = headX * p.EyeForward + Math.Cos(ang) * spread;
                        double ringY = Math.Clamp(headY * p.EyeHeight + Math.Sin(ang) * spread, small, headY - small);
                        double[] exAttach = [ringX, ringY, zFace];
                        ModelCreatureChain(head, exAttach, 0.0, 0.0, 0.0, 2, side, [[small, small, small]], null, 0, $"{eyeName}Extra{e + 1}", out _);
                    }
                }
            }
        }

        if (p.Brow)
        {
            double bs = Math.Max(0.25, p.BrowSize);
            foreach (int side in ModelCreatureSides(2))
            {
                // A ridge over each eye for a heavier, predatory face.
                double zc = side > 0 ? headZ * 0.72 : headZ * 0.28;
                double[] center = [headX * p.EyeForward, headY * Math.Min(0.98, p.EyeHeight + 0.22), zc];
                ModelCreatureBox(head, center, [bs * 1.5, bs * 0.7, headZ * 0.34], 0.0, side * -6.0, side * 9.0, side > 0 ? "browRight" : "browLeft");
            }
        }
    }

    /// <summary>Branching antlers: a swept main beam (like a horn) carrying a fan of tines, mined from the
    /// Wilderlands shepherd/caretaker and Darce's deerhorn drifters. Distinct from the plain tapering Horns.</summary>
    private void ModelBuildCreatureAntlers(ModelCreatureParams p, ModelElementData head)
    {
        if (p.AntlerPairs <= 0) return;

        int beamSegs = Math.Clamp(p.AntlerBeamSegments, 1, 6);
        int tines = Math.Clamp(p.AntlerTines, 0, 8);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double beamSeg = Math.Max(0.5, p.AntlerBeamLength) / beamSegs;
        double baseTh = Math.Max(0.2, p.AntlerBeamThickness);
        double tineLen = Math.Max(0.5, p.AntlerTineLength);

        for (int pair = 0; pair < Math.Clamp(p.AntlerPairs, 0, 2); pair++)
        {
            double xfrac = p.AntlerPairs == 1 ? Math.Clamp((double)p.AntlerForward, 0.0, 1.0) : ModelCreatureLerp(0.6, 0.3, (double)pair / Math.Max(1, p.AntlerPairs - 1));
            foreach (int side in ModelCreatureSides(2))
            {
                List<double[]> sizes = [];
                List<double> bends = [];
                for (int k = 0; k < beamSegs; k++)
                {
                    double t = beamSegs <= 1 ? 0.0 : (double)k / (beamSegs - 1);
                    double th = baseTh * ModelCreatureLerp(1.0, 0.5, t);
                    sizes.Add([th, beamSeg, th]);
                    bends.Add(k == 0 ? 0.0 : p.AntlerSweep); // beam curves back about Z
                }
                // Beam grows up (+Y) off the top of the head, splayed outward (about X).
                double[] attach = [headX * xfrac, headY, headZ * 0.5 + side * headZ * 0.32];
                string beamName = $"antler{(side > 0 ? "Right" : "Left")}{pair + 1}";
                ModelElementData beamFirst = ModelCreatureChain(head, attach, side * -p.AntlerSpread, 0.0, 0.0, 1, 1, sizes, bends, 2, beamName, out _);

                if (tines <= 0) continue;
                List<ModelElementData> beam = [];
                for (ModelElementData? cur = beamFirst; cur != null; cur = cur.Children.Count > 0 ? cur.Children[0] : null)
                {
                    beam.Add(cur);
                }
                for (int ti = 0; ti < tines; ti++)
                {
                    double tf = tines <= 1 ? 0.5 : (double)ti / (tines - 1);
                    int segIndex = Math.Clamp((int)Math.Round(ModelCreatureLerp(0, beam.Count - 1, tf)), 0, beam.Count - 1);
                    ModelElementData seg = beam[segIndex];
                    double segLen = seg.To[1] - seg.From[1];
                    double tl = tineLen * ModelCreatureLerp(1.1, 0.6, tf);
                    // A point branching up off the beam, fanned fore/aft (about Z) so the rack spreads.
                    double fan = (tf - 0.5) * 2.0 * p.AntlerTineSpread;
                    double[] tineAttach = [ModelCreatureCenterRel(seg, 0), segLen * 0.65, ModelCreatureCenterRel(seg, 2)];
                    ModelCreatureChain(seg, tineAttach, side * -10.0, 0.0, fan, 1, 1, [[baseTh * 0.7, tl, baseTh * 0.7]], null, 2, $"{beamName}Tine{ti + 1}", out _);
                }
            }
        }
    }

    /// <summary>Lateral mandibles / pincers on the head front that gape sideways and hook inward (ant/beetle/
    /// spider), mined from the Wilderlands banefly and blightworm. Complements the vertical Mouth/jaw.</summary>
    private void ModelBuildCreatureMandibles(ModelCreatureParams p, ModelElementData head)
    {
        if (!p.Mandibles) return;

        int segs = Math.Clamp(p.MandibleSegments, 1, 4);
        double headX = head.To[0] - head.From[0];
        double headY = head.To[1] - head.From[1];
        double headZ = head.To[2] - head.From[2];
        double segLen = Math.Max(0.5, p.MandibleLength) / segs;
        double baseTh = Math.Max(0.2, p.MandibleThickness);

        foreach (int side in ModelCreatureSides(2))
        {
            List<double[]> sizes = [];
            List<double> bends = [];
            for (int k = 0; k < segs; k++)
            {
                double t = segs <= 1 ? 0.0 : (double)k / (segs - 1);
                double th = baseTh * ModelCreatureLerp(1.0, 0.5, t);
                sizes.Add([segLen, th, th]);
                bends.Add(k == 0 ? 0.0 : -side * p.MandibleCurve); // hook inward toward the midline
            }
            // Pair on the head's lower front (+X), splayed apart by the gape (yaw about Y).
            double[] attach = [headX, headY * 0.18, headZ * 0.5 + side * headZ * 0.3];
            ModelElementData mand = ModelCreatureChain(head, attach, 0.0, side * p.MandibleGape, 0.0, 0, 1, sizes, bends, 1, side > 0 ? "mandibleRight" : "mandibleLeft", out ModelElementData mandTip);

            if (p.MandibleFangs)
            {
                // A couple of inward-facing teeth on the inner edge of the base segment.
                double inner = side > 0 ? 0.0 : baseTh;
                for (int f = 0; f < 2; f++)
                {
                    ModelCreatureBox(mand, [segLen * (0.4 + 0.4 * f), baseTh * 0.5, inner], [baseTh * 0.8, baseTh * 0.5, baseTh * 0.5], 0.0, 0.0, 0.0, $"mandible{(side > 0 ? "Right" : "Left")}Fang{f + 1}");
                }
                _ = mandTip;
            }
        }
    }

    /// <summary>Scatters spikes / quills / metal shards over the torso (porcupine, sea urchin, the spiked
    /// drifters, rust constructs). Seeded so a given seed is reproducible; distinct from the neat Dorsal ridge.</summary>
    private void ModelBuildCreatureBodySpikes(ModelCreatureParams p, List<ModelElementData> spine)
    {
        int count = Math.Clamp(p.BodySpikes, 0, 60);
        if (count <= 0 || spine.Count == 0) return;

        double length = Math.Max(0.25, p.BodySpikeLength);
        double size = Math.Max(0.1, p.BodySpikeSize);
        double span = Math.Clamp((double)p.BodySpikeSpan, 0.05, 1.0);
        Random r = new(p.Seed * 31 + 7);

        for (int i = 0; i < count; i++)
        {
            double frac = span * r.NextDouble();
            ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, frac);
            double vx = vertebra.To[0] - vertebra.From[0];
            double vz = vertebra.To[2] - vertebra.From[2];
            double sl = length * (0.6 + 0.8 * r.NextDouble());
            double sz = size * (0.6 + 0.8 * r.NextDouble());
            // Grow up (+Y) then roll about the fore-aft (X) axis so the spike points in a random radial
            // direction around the body (mostly the upper hemisphere), with a little fore/aft pitch.
            double roll = ModelCreatureLerp(-145.0, 145.0, r.NextDouble());
            double pitch = ModelCreatureLerp(-25.0, 25.0, r.NextDouble());
            double[] attach = [ModelCreatureLerp(vx * 0.2, vx * 0.8, r.NextDouble()), ModelCreatureCenterRel(vertebra, 1), ModelCreatureLerp(vz * 0.25, vz * 0.75, r.NextDouble())];
            ModelCreatureChain(vertebra, attach, roll, 0.0, pitch, 1, 1, [[sz, sl, sz]], null, 2, $"quill{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureLimbs(ModelCreatureParams p, List<ModelElementData> spine, bool isArm)
    {
        int pairs = isArm ? Math.Clamp(p.ArmPairs, 0, 4) : Math.Clamp(p.LegPairs, 0, 6);
        if (pairs <= 0) return;

        int segCount = isArm ? Math.Clamp(p.ArmSegments, 1, 6) : Math.Clamp(p.LegSegments, 1, 6);
        double length = isArm ? Math.Max(1.0, p.ArmLength) : Math.Max(1.0, p.LegLength);
        double thick = isArm ? Math.Max(0.5, p.ArmThickness) : Math.Max(0.5, p.LegThickness);
        double splay = isArm ? p.ArmSplay : p.LegSplay;
        double bend = isArm ? p.ArmBend : p.LegBend;
        double taper = isArm ? Math.Max(0.1, p.ArmTaper) : Math.Max(0.1, p.LegTaper);
        double lean = isArm ? p.ArmDownAngle : p.LegForwardLean;
        double limbSeg = length / segCount;
        string tag = isArm ? "arm" : "leg";

        double zigzag = isArm ? p.ArmZigzag : p.LegZigzag;
        bool perBone = isArm ? p.ArmPerBone : p.LegPerBone;
        List<float> boneLen = isArm ? p.ArmBoneLength : p.LegBoneLength;
        List<float> boneThick = isArm ? p.ArmBoneThick : p.LegBoneThick;
        List<float> boneAngle = isArm ? p.ArmBoneAngle : p.LegBoneAngle;
        bool extremity = isArm ? p.Hands : p.Feet;
        double exLength = isArm ? p.HandLength : p.FootLength;
        double exWidth = isArm ? p.HandWidth : p.FootWidth;
        double exHeight = isArm ? p.HandHeight : p.FootHeight;
        string exTag = isArm ? "hand" : "foot";
        string rootTag = isArm ? "shoulder" : "haunch";

        for (int pair = 0; pair < pairs; pair++)
        {
            double frac;
            if (isArm)
            {
                frac = pairs == 1 ? p.ArmPos : ModelCreatureLerp(p.ArmPos, Math.Min(1.0, p.ArmPos + 0.4), (double)pair / (pairs - 1));
            }
            else
            {
                frac = pairs == 1 ? p.RearLegPos : ModelCreatureLerp(p.FrontLegPos, p.RearLegPos, (double)pair / (pairs - 1));
            }

            ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, frac);
            double vx = vertebra.To[0] - vertebra.From[0]; // fore-aft
            double vz = vertebra.To[2] - vertebra.From[2]; // lateral

            foreach (int side in ModelCreatureSides(2))
            {
                List<double[]> sizes = [];
                List<double> bends = [];
                for (int k = 0; k < segCount; k++)
                {
                    double t = segCount <= 1 ? 0.0 : (double)k / (segCount - 1);
                    double segThick = thick * ModelCreatureLerp(1.0, taper, t);
                    double segLength = limbSeg;
                    // Per-bone tuning multiplies this bone's length/girth and adds an extra bend, so each
                    // arm/leg segment can be sized and angled by hand instead of an even taper.
                    if (perBone)
                    {
                        if (k < boneLen.Count) segLength *= Math.Max(0.05, boneLen[k]);
                        if (k < boneThick.Count) segThick *= Math.Max(0.05, boneThick[k]);
                    }
                    sizes.Add([segThick, segLength, segThick]);
                    // Uniform per-joint bend plus the zigzag (which also angles the first joint) so every
                    // segment can carry a different orientation, like a hand-posed leg.
                    double uniformBend = k == 0 ? 0.0 : bend;
                    double extraBend = perBone && k < boneAngle.Count ? boneAngle[k] : 0.0;
                    bends.Add(uniformBend + zigzag * ModelCreatureZigzagFactor(k) + extraBend);
                }

                string sideTag = side > 0 ? "Right" : "Left";

                // Limbs hang from the body's lower lateral (Z) edge and grow straight down (-Y). With Shoulders on,
                // a shoulder/haunch volume is embedded in the body there and the limb hangs from its lower end.
                ModelElementData limbParent = vertebra;
                double[] attach = [vx * 0.5, 0.0, side > 0 ? vz : 0.0];
                if (p.Shoulders)
                {
                    double shW = Math.Max(0.5, p.ShoulderWidth);     // fore-aft (X)
                    double shH = Math.Max(0.5, p.ShoulderLength);    // vertical (Y)
                    double shT = Math.Max(0.5, p.ShoulderThickness); // lateral (Z)
                    double embed = Math.Clamp((double)p.ShoulderEmbed, 0.0, 1.0);
                    double sign = side > 0 ? 1.0 : -1.0;
                    double surfaceZ = side > 0 ? vz : 0.0;
                    double[] shoulderCenter =
                    [
                        vx * 0.5,
                        shH * 0.35,                              // sits in the lower body, protruding slightly below
                        surfaceZ + sign * shT * (0.5 - embed)    // straddles the body's lateral surface
                    ];
                    limbParent = ModelCreatureBox(vertebra, shoulderCenter, [shW, shH, shT], 0.0, 0.0, 0.0, $"{rootTag}{sideTag}{pair + 1}");
                    attach = [shW * 0.5, 0.0, shT * 0.5];        // limb hangs from the shoulder's bottom-centre
                }

                // Splay tilts the limb outward laterally (about X); the fore/aft lean and per-joint bends are
                // about Z so they complement the walk swing.
                ModelCreatureChain(limbParent, attach, -side * splay, 0.0, lean, 1, -1, sizes, bends, 2, $"{tag}{sideTag}{pair + 1}", out ModelElementData limbTip);

                // Asymmetry: when enabled, each arm can carry a different tip (one blade, one hand, ...).
                ModelCreatureArmAppendage appendage = isArm && p.AsymmetricArms
                    ? (ModelCreatureArmAppendage)Math.Clamp(side > 0 ? p.ArmAppendageRight : p.ArmAppendageLeft, 0, ModelCreatureArmAppendageLabels.Length - 1)
                    : ModelCreatureArmAppendage.Normal;

                if (appendage != ModelCreatureArmAppendage.Normal)
                {
                    ModelBuildCreatureArmAppendage(p, limbTip, appendage, sideTag, pair + 1);
                    continue;
                }

                ModelElementData paw = limbTip;
                if (extremity)
                {
                    // A foot/hand box at the limb tip, growing forward (+X) from the ankle/wrist so the
                    // locomotion generator (which classifies "foot"/"hand" names) treats it as the toe joint.
                    double[] exAttach = [ModelCreatureCenterRel(limbTip, 0), 0.0, ModelCreatureCenterRel(limbTip, 2)];
                    paw = ModelCreatureChain(limbTip, exAttach, 0.0, 0.0, 0.0, 0, 1,
                        [[Math.Max(0.25, exLength), Math.Max(0.25, exHeight), Math.Max(0.25, exWidth)]],
                        null, 0, $"{exTag}{sideTag}{pair + 1}", out _);
                }

                if (p.Toes > 0)
                {
                    ModelBuildCreatureToes(p, paw, exTag, sideTag, pair + 1);
                }
            }
        }
    }

    /// <summary>Builds an asymmetric arm tip - a blade, a big claw, a club, a hook, or nothing (a bare stump) -
    /// at the wrist, growing forward (+X) like a held weapon. Names are "arm*" so they ride the arm region's
    /// texture and the arm's animation.</summary>
    private void ModelBuildCreatureArmAppendage(ModelCreatureParams p, ModelElementData limbTip,
        ModelCreatureArmAppendage appendage, string sideTag, int index)
    {
        double len = Math.Max(0.5, p.ArmAppendageLength);
        double size = Math.Max(0.25, p.ArmAppendageSize);
        // Anchor at the wrist (lower-front of the limb tip), growing forward (+X).
        double[] attach = [ModelCreatureCenterRel(limbTip, 0), 0.0, ModelCreatureCenterRel(limbTip, 2)];
        string suffix = $"{sideTag}{index}";

        switch (appendage)
        {
            case ModelCreatureArmAppendage.None:
                return;

            case ModelCreatureArmAppendage.Blade:
            {
                // A long flat blade tapering to a point: tall (Y), thin (Z), growing forward in three segments.
                double thin = Math.Max(0.15, size * 0.18);
                List<double[]> sizes = [];
                for (int k = 0; k < 3; k++)
                {
                    double t = k / 2.0;
                    sizes.Add([len / 3.0, size * ModelCreatureLerp(1.0, 0.1, t), thin]);
                }
                ModelCreatureChain(limbTip, attach, 0.0, 0.0, 0.0, 0, 1, sizes, null, 2, $"armBlade{suffix}", out _);
                break;
            }

            case ModelCreatureArmAppendage.Claw:
            {
                // A spread of three big curved claws (a pincer/talon hand).
                double cl = len * 0.6;
                double cs = Math.Max(0.2, size * 0.4);
                for (int i = 0; i < 3; i++)
                {
                    double frac = i / 2.0;
                    double yaw = (frac - 0.5) * 2.0 * 26.0;
                    ModelCreatureChain(limbTip, attach, 0.0, yaw, -22.0, 0, 1, [[cl, cs, cs], [cl * 0.7, cs * 0.7, cs * 0.7]], [0.0, -28.0], 2, $"armClaw{suffix}_{i + 1}", out _);
                }
                break;
            }

            case ModelCreatureArmAppendage.Club:
            {
                // A short wrist stub ending in a heavy ball/mace head.
                ModelElementData stub = ModelCreatureChain(limbTip, attach, 0.0, 0.0, 0.0, 0, 1, [[len * 0.45, size * 0.5, size * 0.5]], null, 2, $"armClub{suffix}", out _);
                double[] headCenter = [stub.To[0] - stub.From[0], ModelCreatureCenterRel(stub, 1), ModelCreatureCenterRel(stub, 2)];
                ModelCreatureBox(stub, headCenter, [size * 1.3, size * 1.3, size * 1.3], 0.0, 0.0, 0.0, $"armClub{suffix}Head");
                break;
            }

            case ModelCreatureArmAppendage.Hook:
            {
                // A curved two-segment hook.
                double hs = Math.Max(0.2, size * 0.4);
                ModelCreatureChain(limbTip, attach, 0.0, 0.0, -10.0, 0, 1, [[len * 0.55, hs, hs], [len * 0.5, hs * 0.8, hs * 0.8]], [0.0, -70.0], 2, $"armHook{suffix}", out _);
                break;
            }
        }
    }

    private void ModelBuildCreatureToes(ModelCreatureParams p, ModelElementData paw, string tag, string sideTag, int index)
    {
        int toes = Math.Clamp(p.Toes, 0, 5);
        if (toes <= 0) return;

        double cl = Math.Max(0.25, p.ClawLength);
        double cs = Math.Max(0.1, p.ClawSize);
        double pawX = paw.To[0] - paw.From[0];
        double pawY = paw.To[1] - paw.From[1];
        double pawZ = paw.To[2] - paw.From[2];

        for (int i = 0; i < toes; i++)
        {
            double frac = toes == 1 ? 0.5 : (double)i / (toes - 1);
            double lat = ModelCreatureLerp(pawZ * 0.15, pawZ * 0.85, frac); // spread across the paw width
            double yaw = (frac - 0.5) * 2.0 * p.ClawSplay;
            // Claw grows forward (+X) from the paw's front-bottom; yaw fans it, ClawCurve hooks it down.
            double[] attach = [pawX, pawY * 0.25, lat];
            ModelCreatureChain(paw, attach, 0.0, yaw, p.ClawCurve, 0, 1, [[cl, cs, cs]], null, 0, $"{tag}Claw{sideTag}{index}_{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureTail(ModelCreatureParams p, ModelElementData rearVertebra)
    {
        int count = Math.Clamp(p.TailSegments, 0, 16);
        if (count <= 0 || p.TailLength <= 0.01) return;

        double tailSeg = p.TailLength / count;
        double baseTh = Math.Max(0.5, p.TailThickness);
        double widthMul = Math.Max(0.1, p.TailWidthMul);
        double heightMul = Math.Max(0.1, p.TailHeightMul);
        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < count; k++)
        {
            double t = count <= 1 ? 0.0 : (double)k / (count - 1);
            double th = baseTh * ModelCreatureLerp(1.0, Math.Clamp(p.TailTaper, 0.05, 1.0), t);
            // A mid-tail bulge (0 at both ends, peak at the middle) gives a fluffy brush tail on top of the taper.
            double bulge = ModelCreatureLerp(1.0, Math.Max(0.1, p.TailBulge), 4.0 * t * (1.0 - t));
            sizes.Add([tailSeg, th * heightMul * bulge, th * widthMul * bulge]);
            bends.Add(-p.TailDroop);
        }

        // Attach at the rear face (-X) center and grow backward (-X); a base lift raises the root, droop bends
        // each joint downward (both about Z).
        double[] attach = [0.0, ModelCreatureCenterRel(rearVertebra, 1), ModelCreatureCenterRel(rearVertebra, 2)];
        ModelCreatureChain(rearVertebra, attach, 0.0, 0.0, -p.TailBaseAngle, 0, -1, sizes, bends, 2, "tail", out ModelElementData tailTip);

        if (p.TailFin)
        {
            double fh = Math.Max(0.5, p.TailFinHeight);
            double fl = Math.Max(0.5, p.TailFinLength);
            double thin = Math.Max(0.25, fh * 0.12);
            // A flat fin/fluke extending back (-X) from the tail tip; vertical (fish/dragon) or horizontal (whale).
            double[] center = [-fl * 0.5, ModelCreatureCenterRel(tailTip, 1), ModelCreatureCenterRel(tailTip, 2)];
            double[] size = p.TailFinVertical ? [fl, fh, thin] : [fl, thin, fh];
            ModelCreatureBox(tailTip, center, size, 0.0, 0.0, 0.0, "tailFin");
        }

        if (p.TailTuft)
        {
            double ts = Math.Max(0.5, p.TailTuftSize);
            // A bushy tuft at the very tip (lion / elephant / cow).
            double[] center = [-ts * 0.35, ModelCreatureCenterRel(tailTip, 1), ModelCreatureCenterRel(tailTip, 2)];
            ModelCreatureBox(tailTip, center, [ts, ts, ts], 0.0, 0.0, 0.0, "tailTuft");
        }

        if (p.TailStinger)
        {
            // A cluster of spikes at the tail tip (scorpion / wasp stinger, the blightworm & caretaker maces),
            // growing back (-X), fanned laterally and hooked up/down by the curve.
            int stings = Math.Clamp(p.StingerCount, 1, 12);
            double sl = Math.Max(0.5, p.StingerLength);
            double ss = Math.Max(0.1, p.StingerSize);
            for (int i = 0; i < stings; i++)
            {
                double fanFrac = stings <= 1 ? 0.5 : (double)i / (stings - 1);
                double yaw = (fanFrac - 0.5) * 2.0 * p.StingerSpread;
                double[] stingAttach = [0.0, ModelCreatureCenterRel(tailTip, 1), ModelCreatureCenterRel(tailTip, 2)];
                ModelCreatureChain(tailTip, stingAttach, 0.0, yaw, p.StingerCurve, 0, -1, [[sl, ss, ss]], null, 2, $"stinger{i + 1}", out _);
            }
        }
    }

    private void ModelBuildCreatureWings(ModelCreatureParams p, List<ModelElementData> spine)
    {
        int pairs = Math.Clamp(p.WingPairs, 0, 2);
        if (pairs <= 0) return;

        int segCount = Math.Clamp(p.WingSegments, 1, 6);
        double span = Math.Max(1.0, p.WingSpan);
        double chord = Math.Max(0.5, p.WingChord);
        double th = Math.Max(0.25, p.WingThickness);
        double wingSeg = span / segCount;

        for (int pair = 0; pair < pairs; pair++)
        {
            double frac = pairs == 1 ? p.WingPos : ModelCreatureLerp(p.WingPos, Math.Min(1.0, p.WingPos + 0.3), (double)pair / (pairs - 1));
            ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, frac);
            double vx = vertebra.To[0] - vertebra.From[0]; // fore-aft
            double vh = vertebra.To[1] - vertebra.From[1]; // up
            double vz = vertebra.To[2] - vertebra.From[2]; // lateral

            bool membrane = (ModelCreatureWingStyle)p.WingStyle == ModelCreatureWingStyle.Membrane;
            foreach (int side in ModelCreatureSides(2))
            {
                // Attach on the body's lateral (Z) side and grow outward (±Z). The dihedral tilts both wings
                // up/down together (mirrored sign about the fore-aft X axis).
                double[] attach = [vx * 0.5, vh * Math.Clamp((double)p.WingHeight, 0.0, 1.0), side > 0 ? vz : 0.0];
                string wingName = $"wing{(side > 0 ? "Right" : "Left")}{pair + 1}";

                if (membrane)
                {
                    ModelBuildCreatureMembraneWing(p, vertebra, side, attach, span, chord, th, wingName);
                    continue;
                }

                List<double[]> sizes = [];
                List<double> bends = [];
                for (int k = 0; k < segCount; k++)
                {
                    double t = segCount <= 1 ? 0.0 : (double)k / (segCount - 1);
                    double segChord = chord * ModelCreatureLerp(1.0, Math.Max(0.05, p.WingChordTaper), t);
                    sizes.Add([segChord, th, wingSeg]);
                    bends.Add(k == 0 ? 0.0 : side * p.WingSweep);
                }
                // Feathered / limb style: a single chord-tapered spar; sweep yaws each segment about Y.
                ModelElementData sparFirst = ModelCreatureChain(vertebra, attach, -side * p.WingDihedral, 0.0, 0.0, 2, side, sizes, bends, 1, wingName, out _);
                if (p.WingFeathers > 0)
                {
                    ModelBuildCreatureWingFeathers(p, sparFirst, side, wingName);
                }
            }
        }
    }

    /// <summary>Lays a row of overlapping flat feather planes along the feathered-wing spar (the pegasus
    /// primaries/secondaries), trailing back (-X) and fanning toward the tip. Turns the plain spar into a
    /// proper layered wing.</summary>
    private void ModelBuildCreatureWingFeathers(ModelCreatureParams p, ModelElementData sparFirst, int side, string wingName)
    {
        int feathers = Math.Clamp(p.WingFeathers, 1, 24);
        double fl = Math.Max(0.5, p.WingFeatherLength);
        double fw = Math.Max(0.25, p.WingFeatherWidth);
        double thin = Math.Max(0.08, fw * 0.12);

        List<ModelElementData> spar = [];
        for (ModelElementData? cur = sparFirst; cur != null; cur = cur.Children.Count > 0 ? cur.Children[0] : null)
        {
            spar.Add(cur);
        }

        for (int i = 0; i < feathers; i++)
        {
            double t = feathers <= 1 ? 0.5 : (double)i / (feathers - 1);
            int segIndex = Math.Clamp((int)Math.Floor(t * spar.Count), 0, spar.Count - 1);
            ModelElementData seg = spar[segIndex];
            double len = fl * ModelCreatureLerp(0.8, 1.15, t);   // longer primaries toward the tip
            double yaw = side * ModelCreatureLerp(8.0, 48.0, t); // fan back toward the tip
            // Trails back (-X) off the spar's trailing edge; the spar's lateral axis is Z.
            double[] attach = [0.0, ModelCreatureCenterRel(seg, 1), ModelCreatureCenterRel(seg, 2)];
            ModelCreatureChain(seg, attach, 0.0, yaw, 0.0, 0, -1, [[len, thin, fw]], null, 0, $"{wingName}Feather{i + 1}", out _);
        }
    }

    /// <summary>
    /// Membrane (bat / dragon) wing: a segmented leading-edge spar growing out from the torso, membrane panels
    /// attached along each spar segment, and finger bones rooted along the spar. This keeps the wing connected
    /// to the body while making the Wing Segments slider visibly change the structure.
    /// The spar is named "...Spar" (NOT "...Arm"): the locomotion generator skips anything whose name contains
    /// "arm" as a limb before it ever checks for wings, so a "...Arm" spar never gets the wing flap.
    /// </summary>
    private void ModelBuildCreatureMembraneWing(ModelCreatureParams p, ModelElementData vertebra, int side,
        double[] attach, double span, double chord, double th, string name)
    {
        int fingers = Math.Clamp(p.WingFingers, 2, 6);
        int segments = Math.Clamp(p.WingSegments, 1, 6);
        double trail = Math.Max(1.0, p.WingMembraneTrail);
        double armTh = Math.Max(0.4, th * 1.4);
        double membraneThin = Math.Max(0.1, th * 0.4);
        double segLen = span / segments;

        List<double[]> sparSizes = [];
        List<double> sparBends = [];
        for (int k = 0; k < segments; k++)
        {
            double t = segments <= 1 ? 0.0 : (double)k / (segments - 1);
            double taper = ModelCreatureLerp(1.0, Math.Max(0.18, p.WingChordTaper), t);
            sparSizes.Add([Math.Max(th * 1.2, chord * 0.42 * taper), armTh * ModelCreatureLerp(1.0, 0.55, t), segLen]);
            sparBends.Add(k == 0 ? 0.0 : side * p.WingSweep);
        }

        ModelElementData sparFirst = ModelCreatureChain(vertebra, attach, -side * p.WingDihedral, 0.0, 0.0, 2, side, sparSizes, sparBends, 1, $"{name}Spar", out _);

        List<ModelElementData> spar = [];
        for (ModelElementData? current = sparFirst; current != null && spar.Count < segments; current = current.Children.Count > 0 ? current.Children[0] : null)
        {
            spar.Add(current);
        }

        for (int k = 0; k < spar.Count; k++)
        {
            ModelElementData segment = spar[k];
            double t = spar.Count <= 1 ? 0.0 : (double)k / (spar.Count - 1);
            double panelTrail = Math.Max(chord * ModelCreatureLerp(1.1, 0.45, t), trail * ModelCreatureLerp(1.0, 0.35, t));
            double panelSpan = Math.Max(0.25, segment.SizeZ + Math.Min(th, segLen * 0.18));
            double[] panelCenter =
            [
                ModelCreatureCenterRel(segment, 0) - panelTrail * 0.46,
                ModelCreatureCenterRel(segment, 1),
                ModelCreatureCenterRel(segment, 2)
            ];
            ModelCreatureBox(segment, panelCenter, [panelTrail, membraneThin, panelSpan], 0.0, 0.0, 0.0, $"{name}Membrane{k + 1}");

            if (p.WingRidges)
            {
                // A raised bone strut along this panel's leading edge, standing proud of the membrane so the
                // sections read as separate panels (the finger/arm ridges of a real bat/reptile wing).
                double ridgeHeight = membraneThin + Math.Max(0.15, th * 0.55) * Math.Clamp((double)p.WingRidgeSize, 0.2, 3.0);
                double ridgeThin = Math.Max(0.12, th * 0.3);
                double[] ridgeCenter = [ModelCreatureCenterRel(segment, 0) - panelTrail * 0.46, ModelCreatureCenterRel(segment, 1), Math.Min(ridgeThin, segment.SizeZ * 0.5)];
                ModelCreatureBox(segment, ridgeCenter, [panelTrail, ridgeHeight, ridgeThin], 0.0, 0.0, 0.0, $"{name}Ridge{k + 1}");
            }
        }

        for (int i = 0; i < fingers; i++)
        {
            double frac = fingers == 1 ? 0.0 : (double)i / (fingers - 1);
            int anchorIndex = Math.Clamp((int)Math.Round(ModelCreatureLerp(Math.Max(0, spar.Count - 2), spar.Count - 1, frac)), 0, spar.Count - 1);
            ModelElementData anchor = spar[anchorIndex];
            double sweep = side * ModelCreatureLerp(-12.0, 62.0, frac);
            double fl = span * ModelCreatureLerp(0.54, 0.24, frac);
            double fingerTh = Math.Max(0.25, th * ModelCreatureLerp(0.95, 0.55, frac));
            double[] fingerAttach = ModelCreatureDistalAttach(anchor, 2, side);
            ModelCreatureChain(anchor, fingerAttach, 0.0, sweep, 0.0, 2, side, [[chord * 0.14, fingerTh, fl]], null, 0, $"{name}Finger{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureBelly(ModelCreatureParams p, List<ModelElementData> spine, double spineLen, double bodyW)
    {
        if (!p.Belly || spine.Count == 0) return;

        ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, 0.5);
        double depth = Math.Max(0.5, p.BellyDepth);
        double width = Math.Max(0.5, bodyW * Math.Clamp((double)p.BellySize, 0.2, 2.0));
        // An underbody volume hung beneath the mid-spine, spanning most of the body length, for a fuller silhouette.
        double[] center = [ModelCreatureCenterRel(vertebra, 0), -depth * 0.35, ModelCreatureCenterRel(vertebra, 2)];
        ModelCreatureBox(vertebra, center, [spineLen * 0.55, depth, width], 0.0, 0.0, 0.0, "belly");
    }

    private void ModelBuildCreatureFins(ModelCreatureParams p, List<ModelElementData> spine)
    {
        int pairs = Math.Clamp(p.FinPairs, 0, 3);
        if (pairs <= 0 || spine.Count == 0) return;

        double span = Math.Max(0.5, p.FinSpan);
        double chord = Math.Max(0.5, p.FinChord);
        double thin = Math.Max(0.1, chord * 0.08);
        for (int pair = 0; pair < pairs; pair++)
        {
            double frac = pairs == 1 ? p.FinPos : ModelCreatureLerp(p.FinPos, Math.Min(1.0, p.FinPos + 0.4), (double)pair / (pairs - 1));
            ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, frac);
            double vx = vertebra.To[0] - vertebra.From[0];
            double vh = vertebra.To[1] - vertebra.From[1];
            double vz = vertebra.To[2] - vertebra.From[2];
            foreach (int side in ModelCreatureSides(2))
            {
                // A flat fin growing outward (±Z), thin vertically, swept back about Y.
                double[] attach = [vx * 0.5, vh * Math.Clamp((double)p.FinHeight, 0.0, 1.0), side > 0 ? vz : 0.0];
                ModelCreatureChain(vertebra, attach, 0.0, side * p.FinAngle, 0.0, 2, side, [[chord, thin, span]], null, 0, $"fin{(side > 0 ? "Right" : "Left")}{pair + 1}", out _);
            }
        }
    }

    private void ModelBuildCreatureShell(ModelCreatureParams p, List<ModelElementData> spine, double spineLen, double bodyW)
    {
        if (!p.Shell || spine.Count == 0) return;

        ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, 0.5);
        double vh = vertebra.To[1] - vertebra.From[1];
        double h = Math.Max(0.5, p.ShellHeight);
        double len = Math.Max(1.0, spineLen * Math.Clamp((double)p.ShellLength, 0.2, 1.5));
        double w = Math.Max(0.5, bodyW * Math.Clamp((double)p.ShellWidth, 0.2, 2.0));
        // A domed carapace arching over the back (turtle / beetle / armadillo).
        double[] center = [ModelCreatureCenterRel(vertebra, 0), vh * 0.6 + h * 0.4, ModelCreatureCenterRel(vertebra, 2)];
        ModelCreatureBox(vertebra, center, [len, h, w], 0.0, 0.0, 0.0, "shell");
    }

    private void ModelBuildCreatureTailPlume(ModelCreatureParams p, ModelElementData rearVertebra)
    {
        if (!p.TailPlume) return;

        int count = Math.Clamp(p.PlumeCount, 1, 16);
        double len = Math.Max(0.5, p.PlumeLength);
        double w = Math.Max(0.25, p.PlumeWidth);
        double thin = Math.Max(0.1, w * 0.18);
        for (int i = 0; i < count; i++)
        {
            double frac = count == 1 ? 0.5 : (double)i / (count - 1);
            double yaw = (frac - 0.5) * 2.0 * p.PlumeSpread;       // lateral fan
            // A spray of flat feathers from the rear, growing back (-X), tilted up and fanned sideways.
            double[] attach = [0.0, ModelCreatureCenterRel(rearVertebra, 1), ModelCreatureCenterRel(rearVertebra, 2)];
            ModelCreatureChain(rearVertebra, attach, 0.0, yaw, -p.PlumeAngle, 0, -1, [[len, w, thin]], null, 0, $"plume{i + 1}", out _);
        }
    }

    private void ModelBuildCreatureHump(ModelCreatureParams p, List<ModelElementData> spine)
    {
        if (!p.Hump || spine.Count == 0) return;

        ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, Math.Clamp((double)p.HumpPos, 0.0, 1.0));
        double vh = vertebra.To[1] - vertebra.From[1];
        double vz = vertebra.To[2] - vertebra.From[2];
        double h = Math.Max(0.5, p.HumpHeight);
        double len = Math.Max(0.5, p.HumpLength);
        // A raised muscle/fat hump on top of the shoulders (bison / aurochs / camel).
        double[] center = [ModelCreatureCenterRel(vertebra, 0), vh + h * 0.4, ModelCreatureCenterRel(vertebra, 2)];
        ModelCreatureBox(vertebra, center, [len, h, vz * 0.85], 0.0, 0.0, 0.0, "hump");
    }

    private void ModelBuildCreatureDewlap(ModelCreatureParams p, ModelElementData frontVertebra)
    {
        if (!p.Dewlap) return;

        double vx = frontVertebra.To[0] - frontVertebra.From[0];
        double drop = Math.Max(0.5, p.DewlapDrop);
        double len = Math.Max(0.5, p.DewlapLength);
        double w = Math.Max(0.25, p.DewlapWidth);
        // A flat flap hanging below the throat (front-bottom of the chest).
        double[] center = [vx * 0.9, -drop * 0.4, ModelCreatureCenterRel(frontVertebra, 2)];
        ModelCreatureBox(frontVertebra, center, [len, drop, w], 0.0, 0.0, 0.0, "dewlap");
    }

    private void ModelBuildCreatureDorsalSpikes(ModelCreatureParams p, List<ModelElementData> spine)
    {
        int count = Math.Clamp(p.DorsalSpikes, 0, 24);
        if (count <= 0) return;

        double height = Math.Max(0.25, p.DorsalSpikeHeight);
        double length = Math.Max(0.25, p.DorsalSpikeLength);
        double width = Math.Max(0.25, p.DorsalSpikeWidth);
        double start = Math.Clamp(p.DorsalSpikeStart, 0f, 1f);
        double end = Math.Clamp(p.DorsalSpikeEnd, 0f, 1f);

        for (int i = 0; i < count; i++)
        {
            double frac = count <= 1 ? (start + end) * 0.5 : ModelCreatureLerp(start, end, (double)i / (count - 1));
            ModelElementData vertebra = ModelCreatureVertebraForFraction(spine, frac);
            // On top of the chosen vertebra (Y+), centered fore-aft and laterally; grow straight up, swept by angle about Z.
            double[] attach = [ModelCreatureCenterRel(vertebra, 0), vertebra.To[1] - vertebra.From[1], ModelCreatureCenterRel(vertebra, 2)];
            ModelCreatureChain(vertebra, attach, 0.0, 0.0, p.DorsalSpikeAngle, 1, 1, [[length, height, width]], null, 0, $"spike{i + 1}", out _);
        }
    }

    /// <summary>Closes the gaps where chain segments meet at an angle by dropping a "knuckle" box at each bent
    /// joint, sized to the cross-section and sitting on the shared pivot so it overlaps both bones (the way
    /// hand modelers hide joint notches). Skips thin details (feathers, claws, membranes) and straight joints.</summary>
    private void ModelCreatureFillJoints(ModelCreatureParams p, ModelElementData group)
    {
        if (!p.FillJoints) return;

        double scale = Math.Clamp((double)p.JointFillScale, 0.3, 2.5);
        int n = 0;
        foreach (ModelElementData el in group.EnumerateSubtree().ToList())
        {
            ModelElementData? parent = el.Parent;
            if (parent == null || el.RotationOrigin is not { Length: >= 3 } origin) continue;
            if (parent.SizeX <= 0.01 || parent.SizeY <= 0.01 || parent.SizeZ <= 0.01) continue; // root / faceless
            double minDim = Math.Min(el.SizeX, Math.Min(el.SizeY, el.SizeZ));
            if (minDim < 0.8) continue; // skip thin detail (feathers, claws, membranes, antennae)
            double bend = Math.Abs(el.RotationX) + Math.Abs(el.RotationY) + Math.Abs(el.RotationZ);
            if (bend < 3.0) continue;   // a straight joint leaves no gap

            // A cube sized to the bone's cross-section (its two smaller dims) at the shared pivot.
            double[] dims = [el.SizeX, el.SizeY, el.SizeZ];
            Array.Sort(dims);
            double side = (dims[0] + dims[1]) * 0.5 * scale;
            ModelCreatureBox(parent, [origin[0], origin[1], origin[2]], [side, side, side], 0.0, 0.0, 0.0, $"joint{++n}");
        }
    }

    /// <summary>Rounds the chunky, roughly-square volumes (body, head, neck, limb bones) by adding an
    /// overlapping facet box rotated about the long axis, turning each cube into an octagonal prism - the same
    /// "many overlapping angled boxes" trick the hand-made seikret beak uses. Skips planes and thin rods.</summary>
    private void ModelCreatureApplySmoothing(ModelCreatureParams p, ModelElementData group)
    {
        int level = Math.Clamp(p.Smoothing, 0, 2);
        if (level <= 0) return;

        // Facet angles: 45 deg makes an octagon; level 2 adds 22.5/67.5 for a near-round 16-gon.
        double[] angles = level >= 2 ? [45.0, 22.5, 67.5] : [45.0];
        int n = 0;
        foreach (ModelElementData el in group.EnumerateSubtree().ToList())
        {
            if (el.SizeX <= 0.01 || el.SizeY <= 0.01 || el.SizeZ <= 0.01) continue;
            double sx = el.SizeX, sy = el.SizeY, sz = el.SizeZ;
            int longAxis = sx >= sy && sx >= sz ? 0 : sy >= sz ? 1 : 2;
            double crossA = longAxis == 0 ? sy : sx;
            double crossB = longAxis == 2 ? sy : sz;
            double crossMin = Math.Min(crossA, crossB);
            double crossMax = Math.Max(crossA, crossB);
            if (crossMin < 1.0 || crossMax > crossMin * 2.6) continue; // too thin, or a flat plane

            double[] center = [sx * 0.5, sy * 0.5, sz * 0.5];
            foreach (double angle in angles)
            {
                // The rotated facet's cross-section is scaled to ~1/sqrt2 so its corners reach the original
                // faces and its faces cut the original corners, producing the rounded silhouette.
                double[] facet = new double[3];
                facet[longAxis] = longAxis == 0 ? sx : longAxis == 1 ? sy : sz;
                facet[longAxis == 0 ? 1 : 0] = crossA * 0.72;
                facet[longAxis == 2 ? 1 : 2] = crossB * 0.72;
                double rx = longAxis == 0 ? angle : 0.0;
                double ry = longAxis == 1 ? angle : 0.0;
                double rz = longAxis == 2 ? angle : 0.0;
                ModelCreatureBox(el, center, facet, rx, ry, rz, $"{el.Name}Round{++n}");
            }
        }
    }

    /// <summary>
    /// Maps the animation-ready boxes to semantic MeshLib surfaces. The original From/To bounds and pivots
    /// remain the compatibility contract; only the renderable surface changes. Chain elements expose their
    /// growth axis through a pivot on one end face, while centred detail boxes use their longest dimension.
    /// </summary>
    private static ModelGeneratedMeshSpec ModelCreatureGeneratedMeshSpec(ModelElementData element, int smoothing)
    {
        (int axis, int sign) = ModelCreatureGeneratedAxis(element);
        int level = Math.Clamp(smoothing, 0, 2);
        int loftSides = level switch { 0 => 4, 1 => 8, _ => 16 };
        int curvedSides = level switch { 0 => 8, 1 => 12, _ => 16 };
        int curvedLayers = level switch { 0 => 4, 1 => 6, _ => 8 };
        string name = element.Name;

        if (ModelCreatureNameStartsWithAny(name, "wing") && name.Contains("Membrane", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Membrane, axis, sign);
        }

        if (ModelCreatureNameStartsWithAny(name, "ear", "crest", "fin", "feather", "mane", "dewlap", "plume") ||
            name.Contains("Feather", StringComparison.Ordinal) || name.Contains("Blade", StringComparison.Ordinal) ||
            name.Contains("Fin", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Leaf, axis, sign);
        }

        if (ModelCreatureNameStartsWithAny(name, "horn", "tusk", "claw", "fang", "quill", "stinger", "spike") ||
            name.Contains("Fang", StringComparison.Ordinal) || name.Contains("Tine", StringComparison.Ordinal) ||
            name.Contains("Claw", StringComparison.Ordinal) || name.Contains("Hook", StringComparison.Ordinal) ||
            name.Contains("Stinger", StringComparison.Ordinal) || name.Contains("Quill", StringComparison.Ordinal) ||
            name.Contains("Spike", StringComparison.Ordinal) || name.Contains("Tusk", StringComparison.Ordinal) ||
            name.Contains("Horn", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Cone, axis, sign, loftSides);
        }

        if (name.StartsWith("shell", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Dome, axis: 1, sign: 1, sides: curvedSides, layers: curvedLayers);
        }

        if (ModelCreatureNameStartsWithAny(name, "head", "joint", "eye", "shoulder", "haunch", "belly", "hump", "cheek", "nose") ||
            name.Contains("ClubHead", StringComparison.Ordinal) || name.Contains("Tuft", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Ellipsoid, axis, sign, curvedSides, curvedLayers);
        }

        if (ModelCreatureNameStartsWithAny(name, "spine", "neck", "leg", "foot", "arm", "hand", "tail", "trunk", "antenna", "antler") ||
            name.StartsWith("wing", StringComparison.Ordinal))
        {
            double endScale = ModelCreatureNameStartsWithAny(name, "tail", "trunk", "antenna", "antler") ? 0.72d : 1d;
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis, sign, loftSides, startScale: 1d, endScale: endScale);
        }

        if (ModelCreatureNameStartsWithAny(name, "jaw", "snout", "brow", "mouth"))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Wedge, axis, sign);
        }

        return ModelGeneratedSpec(ModelGeneratedMeshKind.ChamferedBox, axis, sign);
    }

    private static (int Axis, int Sign) ModelCreatureGeneratedAxis(ModelElementData element)
    {
        if (element.RotationOrigin is { Length: >= 3 } origin)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                double size = element.To[axis] - element.From[axis];
                double tolerance = Math.Max(0.00001d, size * 0.00001d);
                if (Math.Abs(origin[axis] - element.From[axis]) <= tolerance) return (axis, 1);
                if (Math.Abs(origin[axis] - element.To[axis]) <= tolerance) return (axis, -1);
            }
        }

        return (ModelGeneratedLongestAxis(element), 1);
    }

    private static bool ModelCreatureNameStartsWithAny(string name, params string[] prefixes)
    {
        return prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Applies the targeted per-element tweaks: hides flagged elements (and their subtree), then resizes /
    /// nudges / rotates the named elements that remain. Keyed by name so the same tweaks re-apply every rebuild.</summary>
    private void ModelApplyCreatureTweaks(ModelElementData group)
    {
        Dictionary<string, ModelCreatureTweak> tweaks = _modelCreatureParams.Tweaks;
        if (tweaks.Count == 0) return;

        // Pass 1: remove hidden elements (and, with them, their subtree).
        foreach (ModelElementData element in group.EnumerateSubtree().ToList())
        {
            if (ReferenceEquals(element, group)) continue;
            if (tweaks.TryGetValue(element.Name, out ModelCreatureTweak? tw) && tw is { Hidden: true })
            {
                element.Parent?.Children.Remove(element);
            }
        }

        // Pass 2: transform the surviving named elements.
        foreach (ModelElementData element in group.EnumerateSubtree().ToList())
        {
            if (ReferenceEquals(element, group)) continue;
            if (!tweaks.TryGetValue(element.Name, out ModelCreatureTweak? tw) || tw == null || tw.IsNeutral) continue;

            double sizeMul = Math.Clamp((double)tw.SizeMul, 0.05, 20.0);
            if (Math.Abs(sizeMul - 1.0) > 1e-4) ModelCreatureScaleElement(element, sizeMul);

            if (tw.Offset != NVector3.Zero)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    double d = axis == 0 ? tw.Offset.X : axis == 1 ? tw.Offset.Y : tw.Offset.Z;
                    element.From[axis] = ModelPrimitiveRound(element.From[axis] + d);
                    element.To[axis] = ModelPrimitiveRound(element.To[axis] + d);
                    if (element.RotationOrigin is { Length: >= 3 } origin) origin[axis] = ModelPrimitiveRound(origin[axis] + d);
                }
            }

            if (tw.Rotate != NVector3.Zero)
            {
                element.RotationX = ModelPrimitiveRound(ModelWrapDegrees(element.RotationX + tw.Rotate.X));
                element.RotationY = ModelPrimitiveRound(ModelWrapDegrees(element.RotationY + tw.Rotate.Y));
                element.RotationZ = ModelPrimitiveRound(ModelWrapDegrees(element.RotationZ + tw.Rotate.Z));
            }
        }
    }

    /// <summary>Uniformly scales one element's box about its pivot and its whole subtree by the factor. Uniform
    /// scale commutes with the joints' rotations, so a part stays attached at its pivot and just changes size.</summary>
    private static void ModelCreatureScaleElement(ModelElementData element, double factor)
    {
        double[] pivot = element.RotationOrigin is { Length: >= 3 } origin
            ? [origin[0], origin[1], origin[2]]
            : [(element.From[0] + element.To[0]) * 0.5, (element.From[1] + element.To[1]) * 0.5, (element.From[2] + element.To[2]) * 0.5];

        for (int axis = 0; axis < 3; axis++)
        {
            element.From[axis] = ModelPrimitiveRound(pivot[axis] + (element.From[axis] - pivot[axis]) * factor);
            element.To[axis] = ModelPrimitiveRound(pivot[axis] + (element.To[axis] - pivot[axis]) * factor);
        }
        foreach (ModelElementData node in element.EnumerateSubtree())
        {
            if (ReferenceEquals(node, element)) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                node.From[axis] = ModelPrimitiveRound(node.From[axis] * factor);
                node.To[axis] = ModelPrimitiveRound(node.To[axis] * factor);
                if (node.RotationOrigin is { Length: >= 3 } o) o[axis] = ModelPrimitiveRound(o[axis] * factor);
            }
        }
    }

    /// <summary>Uniformly scales every descendant box about the group origin (all coordinates are relative
    /// offsets, so a single multiply scales the whole hierarchy). The face-less root is left untouched.</summary>
    private static void ModelCreatureScaleSubtree(ModelElementData root, double scale)
    {
        if (Math.Abs(scale - 1.0) < 1e-6) return;

        foreach (ModelElementData node in root.EnumerateSubtree())
        {
            if (ReferenceEquals(node, root)) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                node.From[axis] = ModelPrimitiveRound(node.From[axis] * scale);
                node.To[axis] = ModelPrimitiveRound(node.To[axis] * scale);
                if (node.RotationOrigin is { Length: >= 3 } origin)
                {
                    origin[axis] = ModelPrimitiveRound(origin[axis] * scale);
                }
            }
        }
    }

    // ---- Builder helpers ---------------------------------------------------

    /// <summary>
    /// Builds a nested chain of box segments growing along a principal axis. seg0 is parented to
    /// <paramref name="parent"/> and attaches at <paramref name="attachRel"/> (coordinates relative
    /// to the parent's From) carrying the base aim rotation; each later segment is a child of the
    /// previous, pivoting at the shared joint so per-segment bends curve the chain.
    /// </summary>
    private ModelElementData ModelCreatureChain(ModelElementData parent, double[] attachRel,
        double rotX, double rotY, double rotZ, int axis, int sign,
        List<double[]> sizes, List<double>? bends, int bendAxis, string namePrefix, out ModelElementData tip)
    {
        int ca = axis == 0 ? 1 : 0;
        int cb = axis == 2 ? 1 : 2;

        ModelElementData? prev = null;
        ModelElementData? first = null;
        for (int k = 0; k < sizes.Count; k++)
        {
            double[] size = sizes[k];
            double[] origin = new double[3];
            if (k == 0)
            {
                origin[0] = attachRel[0];
                origin[1] = attachRel[1];
                origin[2] = attachRel[2];
            }
            else
            {
                double distal = sign > 0 ? prev!.To[axis] : prev!.From[axis];
                origin[axis] = distal - prev!.From[axis];
                origin[ca] = (prev!.From[ca] + prev!.To[ca]) * 0.5 - prev!.From[ca];
                origin[cb] = (prev!.From[cb] + prev!.To[cb]) * 0.5 - prev!.From[cb];
            }

            double[] from = new double[3];
            double[] to = new double[3];
            from[axis] = origin[axis] + (sign > 0 ? 0.0 : -size[axis]);
            to[axis] = from[axis] + size[axis];
            from[ca] = origin[ca] - size[ca] * 0.5;
            to[ca] = origin[ca] + size[ca] * 0.5;
            from[cb] = origin[cb] - size[cb] * 0.5;
            to[cb] = origin[cb] + size[cb] * 0.5;

            ModelElementData element = new()
            {
                Name = sizes.Count == 1 ? namePrefix : $"{namePrefix}{k + 1}",
                From = ModelCreatureRound(from),
                To = ModelCreatureRound(to),
                RotationOrigin = ModelCreatureRound(origin),
                Parent = k == 0 ? parent : prev
            };

            double rx = k == 0 ? rotX : 0.0;
            double ry = k == 0 ? rotY : 0.0;
            double rz = k == 0 ? rotZ : 0.0;
            double bend = bends != null && k < bends.Count ? bends[k] : 0.0;
            if (bendAxis == 0) rx += bend;
            else if (bendAxis == 1) ry += bend;
            else rz += bend;
            element.RotationX = ModelPrimitiveRound(ModelWrapDegrees(rx));
            element.RotationY = ModelPrimitiveRound(ModelWrapDegrees(ry));
            element.RotationZ = ModelPrimitiveRound(ModelWrapDegrees(rz));

            (k == 0 ? parent : prev!).Children.Add(element);
            first ??= element;
            prev = element;
        }

        tip = prev!;
        return first!;
    }

    /// <summary>Creates one box centred at <paramref name="centerRel"/> (relative to the parent's From, the same
    /// space the chain uses) with the given size and rotation, parents it, and returns it. Used for the detail
    /// volumes - shoulders, jaw, cheeks, nose - that don't grow along a single axis.</summary>
    private ModelElementData ModelCreatureBox(ModelElementData parent, double[] centerRel, double[] size,
        double rotX, double rotY, double rotZ, string name)
    {
        double[] from = [centerRel[0] - size[0] * 0.5, centerRel[1] - size[1] * 0.5, centerRel[2] - size[2] * 0.5];
        double[] to = [centerRel[0] + size[0] * 0.5, centerRel[1] + size[1] * 0.5, centerRel[2] + size[2] * 0.5];
        ModelElementData element = new()
        {
            Name = name,
            From = ModelCreatureRound(from),
            To = ModelCreatureRound(to),
            RotationOrigin = ModelCreatureRound(centerRel),
            RotationX = ModelPrimitiveRound(ModelWrapDegrees(rotX)),
            RotationY = ModelPrimitiveRound(ModelWrapDegrees(rotY)),
            RotationZ = ModelPrimitiveRound(ModelWrapDegrees(rotZ)),
            Parent = parent
        };
        parent.Children.Add(element);
        return element;
    }

    /// <summary>Alternating, decaying bend weight per limb segment so the joints zigzag into a natural crouch
    /// (first joint one way, the next the other) instead of curling uniformly.</summary>
    private static double ModelCreatureZigzagFactor(int segmentIndex)
    {
        return (segmentIndex % 2 == 0 ? 1.0 : -1.3) * Math.Pow(0.72, segmentIndex);
    }

    /// <summary>Attach point (relative to the box's From) at the center of its distal face along (axis, sign).</summary>
    private static double[] ModelCreatureDistalAttach(ModelElementData box, int axis, int sign)
    {
        int ca = axis == 0 ? 1 : 0;
        int cb = axis == 2 ? 1 : 2;
        double[] attach = new double[3];
        attach[axis] = sign > 0 ? box.To[axis] - box.From[axis] : 0.0;
        attach[ca] = (box.To[ca] - box.From[ca]) * 0.5;
        attach[cb] = (box.To[cb] - box.From[cb]) * 0.5;
        return attach;
    }

    /// <summary>Center of the box on one axis, expressed relative to its From.</summary>
    private static double ModelCreatureCenterRel(ModelElementData box, int axis)
    {
        return (box.To[axis] - box.From[axis]) * 0.5;
    }

    private static IEnumerable<int> ModelCreatureSides(int count)
    {
        // Right then left; both are built explicitly so nested limbs mirror across the body center
        // without relying on coordinate negation in a translated parent frame.
        if (count >= 1) yield return 1;
        if (count >= 2) yield return -1;
    }

    private static ModelElementData ModelCreatureVertebraForFraction(List<ModelElementData> spine, double fraction)
    {
        // fraction: 0 = head/front end, 1 = tail/rear end. spine is ordered rear -> front.
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        int frontIndex = spine.Count - 1;
        double target = ModelCreatureLerp(frontIndex, 0.0, fraction);
        int index = (int)Math.Round(target);
        return spine[Math.Clamp(index, 0, spine.Count - 1)];
    }

    private void ModelCreatureAssignFaces(ModelElementData element, string texture, bool autoByPart)
    {
        // With auto-by-part on, every region resolves to its own texture code (body, head, eye, leg,
        // arm, tail, wing). Resolution is cached so each category hits the document's texture list once;
        // the codes themselves are materialized as texture entries later, at commit (see
        // ModelCreatureEnsureRegionTextures), keeping this build pass free of document side effects.
        Dictionary<string, string>? regionCodes = autoByPart ? new(StringComparer.Ordinal) : null;

        foreach (ModelElementData node in element.EnumerateSubtree())
        {
            if (ReferenceEquals(node, element)) continue;
            if (node.SizeX <= 0.0001 || node.SizeY <= 0.0001 || node.SizeZ <= 0.0001) continue;

            string code = texture;
            if (regionCodes != null)
            {
                string category = ModelCreaturePartCategory(node.Name);
                if (!regionCodes.TryGetValue(category, out string? resolved))
                {
                    resolved = ModelCreatureResolveRegionCode(category);
                    regionCodes[category] = resolved;
                }
                code = resolved;
            }

            for (int face = 0; face < 6; face++)
            {
                node.Faces[face] = new ModelFaceData { Texture = code };
                ModelCreatureAutoUvFace(node, face);
            }
        }
    }

    private static void ModelCreatureAutoUvFace(ModelElementData element, int faceIndex)
    {
        ModelFaceData? face = element.Faces[faceIndex];
        if (face == null) return;

        (double width, double height) = faceIndex switch
        {
            0 or 2 => (element.SizeX, element.SizeY),
            1 or 3 => (element.SizeZ, element.SizeY),
            _ => (element.SizeX, element.SizeZ)
        };
        face.Uv[0] = 0f;
        face.Uv[1] = 0f;
        face.Uv[2] = (float)Math.Clamp(Math.Max(0.0, width), 0.0, ModelCreatureAutoUvMaxTileUnits);
        face.Uv[3] = (float)Math.Clamp(Math.Max(0.0, height), 0.0, ModelCreatureAutoUvMaxTileUnits);
    }

    /// <summary>Maps a generated element name (e.g. "spine2", "legRight1", "head") to a coarse body region.</summary>
    private static string ModelCreaturePartCategory(string name)
    {
        if (name.StartsWith("spine", StringComparison.Ordinal) || name.StartsWith("neck", StringComparison.Ordinal) ||
            name.StartsWith("shoulder", StringComparison.Ordinal) || name.StartsWith("haunch", StringComparison.Ordinal)) return "body";
        if (name.StartsWith("eye", StringComparison.Ordinal)) return "eye";
        if (name.StartsWith("head", StringComparison.Ordinal) || name.StartsWith("snout", StringComparison.Ordinal) ||
            name.StartsWith("ear", StringComparison.Ordinal) || name.StartsWith("horn", StringComparison.Ordinal) ||
            name.StartsWith("jaw", StringComparison.Ordinal) || name.StartsWith("fang", StringComparison.Ordinal) ||
            name.StartsWith("cheek", StringComparison.Ordinal) || name.StartsWith("nose", StringComparison.Ordinal) ||
            name.StartsWith("brow", StringComparison.Ordinal) || name.StartsWith("crest", StringComparison.Ordinal) ||
            name.StartsWith("trunk", StringComparison.Ordinal) || name.StartsWith("tusk", StringComparison.Ordinal) ||
            name.StartsWith("antenna", StringComparison.Ordinal) || name.StartsWith("antler", StringComparison.Ordinal) ||
            name.StartsWith("mandible", StringComparison.Ordinal)) return "head";
        if (name.StartsWith("leg", StringComparison.Ordinal) || name.StartsWith("foot", StringComparison.Ordinal)) return "leg";
        if (name.StartsWith("arm", StringComparison.Ordinal) || name.StartsWith("hand", StringComparison.Ordinal)) return "arm";
        if (name.StartsWith("tail", StringComparison.Ordinal) || name.StartsWith("plume", StringComparison.Ordinal) ||
            name.StartsWith("stinger", StringComparison.Ordinal)) return "tail";
        if (name.StartsWith("wing", StringComparison.Ordinal) || name.StartsWith("fin", StringComparison.Ordinal)) return "wing";
        if (name.StartsWith("quill", StringComparison.Ordinal)) return "body";
        return "body";
    }

    /// <summary>
    /// Texture code to use for a body region: an existing code that matches the region name (singular or
    /// simple plural, case-insensitive) is reused; otherwise the bare category name is returned and gets
    /// created as a fresh texture entry on commit.
    /// </summary>
    private string ModelCreatureResolveRegionCode(string category)
    {
        if (_modelDoc == null) return category;

        foreach (ModelTextureEntry texture in _modelDoc.Textures)
        {
            if (texture.Code.Equals(category, StringComparison.OrdinalIgnoreCase) ||
                texture.Code.Equals(category + "s", StringComparison.OrdinalIgnoreCase))
            {
                return texture.Code;
            }
        }

        return category;
    }

    /// <summary>
    /// Adds a texture entry for every per-region code referenced by the committed creature that the shape
    /// doesn't already define. New entries point at the base texture's image so each region renders
    /// immediately; the user repaints them afterwards.
    /// </summary>
    private void ModelCreatureEnsureRegionTextures(ModelElementData root, string baseCode)
    {
        if (_modelDoc == null) return;

        string basePath = _modelDoc.Textures.FirstOrDefault(texture => string.Equals(texture.Code, baseCode, StringComparison.Ordinal))?.Path
            ?? _modelDoc.Textures.FirstOrDefault()?.Path
            ?? "";

        HashSet<string> existing = new(_modelDoc.Textures.Select(texture => texture.Code), StringComparer.Ordinal);
        IEnumerable<string> referencedCodes = root.EnumerateSubtree().Any(node => node.NonCuboid?.Editable == true)
            ? ModelGeneratedTextureCodes(root)
            : root.EnumerateSubtree()
                .SelectMany(node => node.Faces)
                .Where(face => face != null)
                .Select(face => face!.Texture)
                .Where(code => !string.IsNullOrEmpty(code))
                .Distinct(StringComparer.Ordinal);
        foreach (string code in referencedCodes)
        {
            if (existing.Add(code))
            {
                _modelDoc.Textures.Add(new ModelTextureEntry { Code = code, Path = basePath });
            }
        }
    }

    private static int ModelCreatureElementCount(ModelElementData root)
    {
        return root.EnumerateSubtree().Count() - 1;
    }

    private void ModelCommitCreature()
    {
        if (_modelDoc == null || _modelCreaturePreviewRoot == null) return;

        ModelBeginEdit();
        ModelElementData root = _modelCreaturePreviewRoot.CloneSubtree();
        root.Name = ModelGenerateElementName(root.Name);
        ModelPrefixCreatureNames(root);
        _modelDoc.Roots.Add(root);

        int texturesBefore = _modelDoc.Textures.Count;
        if (_modelCreatureParams.AutoTexture)
        {
            string baseCode = string.IsNullOrWhiteSpace(_modelCreatureParams.Texture)
                ? _modelDoc.Textures.FirstOrDefault()?.Code ?? ""
                : _modelCreatureParams.Texture;
            ModelCreatureEnsureRegionTextures(root, baseCode);
        }
        int texturesAdded = _modelDoc.Textures.Count - texturesBefore;

        ModelSelectElement(root);
        ModelMarkChanged();
        ModelEndEdit("Add creature");
        _modelStatus = texturesAdded > 0
            ? $"Added {root.Name} ({ModelCreatureElementCount(root)} elements, {texturesAdded} texture slot(s))."
            : $"Added {root.Name} ({ModelCreatureElementCount(root)} elements).";
    }

    private static void ModelPrefixCreatureNames(ModelElementData root)
    {
        string baseName = root.Name;
        foreach (ModelElementData node in root.EnumerateSubtree())
        {
            if (ReferenceEquals(node, root)) continue;
            node.Name = $"{baseName}_{node.Name}";
        }
    }

    private void DrawModelCreatureGhost(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (!_modelCreatureWindowOpen || _modelCreaturePreviewRoot == null || !string.IsNullOrEmpty(_modelCreaturePreviewError))
        {
            return;
        }

        uint ghost = ImGui.ColorConvertFloat4ToU32(new NVector4(0.4f, 0.95f, 0.55f, 0.7f));
        uint highlight = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 0.85f, 0.15f, 0.95f));
        foreach (ModelElementData element in _modelCreaturePreviewRoot.EnumerateSubtree())
        {
            if (element.SizeX <= 0.0001 || element.SizeY <= 0.0001 || element.SizeZ <= 0.0001) continue;
            bool selected = !string.IsNullOrEmpty(_modelCreatureTweakSelected)
                && string.Equals(element.Name, _modelCreatureTweakSelected, StringComparison.Ordinal);
            if (element.NonCuboid?.Editable == true)
            {
                DrawModelMeshWireOverlay(drawList, camera, element, selected ? highlight : ghost, selected ? 2.2f : 1.2f, drawVertices: false);
                continue;
            }
            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            foreach ((int a, int b) in ModelBoxEdges)
            {
                DrawModelViewportLine(drawList, camera, corners[a], corners[b], selected ? highlight : ghost, selected ? 2.2f : 1.2f);
            }
        }
    }
}
