using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

// Parametric tool / weapon generator. Like the creature and clothing generators it assembles a complete,
// ready-to-texture box shape from high-level parameters and shows a live wireframe ghost with a single-undo
// commit, but the subject is a HELD item: a haft/grip running along +X with a working head at the far end.
//
// The parameter set was mined from real Vintage Story weapon/tool shapes - the base game tools (axe, pickaxe,
// hammer, shovel, sword, spear, club, bow), the Armory / Vanilla Armory / Dark Souls Armory mods, the Alpha
// Weapon Pack, and Mitsuyo's Japanese gear (tachi, naginata, yari). Every one of those is the same recipe:
// a grip (optionally wrapped) + a guard/collar + a pommel/butt + a head whose form depends on the weapon
// family (blade, axe bit, hammer block, flanged mace, spear/polearm head, pick, spade, hoe, scythe, bow).
// The generator exposes each of those as its own parameter group so any of them - and many combinations
// vanilla never shipped - is reachable, with ~45 archetype presets wiring sensible defaults.
//
// Coordinate frame (16 units = 1 block), built in the item's own space (the in-hand transform orients it):
//   X = length: the butt/pommel sits near -X, the working tip points to +X.
//   Y = the broad axis: a blade's edge-to-spine width, a crossguard's spread, an axe bit's drop.
//   Z = thickness: the narrow axis. Curved blades (sabre, katana, khopesh, scythe) bend about Z, in the X-Y plane.
public sealed partial class DebugWindowManager
{
    private const int WeaponMaxElements = 650;

    private enum WeaponHeadType
    {
        Blade, Axe, Hammer, Mace, Spear, Pick, Spade, Hoe, Scythe, Bow, Staff
    }

    private static readonly string[] WeaponHeadTypeLabels =
    [
        "Blade (sword / dagger / sabre)",
        "Axe bit",
        "Hammer / maul",
        "Mace / flanged head",
        "Spear / polearm",
        "Pick",
        "Spade / shovel",
        "Hoe / adze",
        "Scythe / sickle",
        "Bow",
        "Staff / blunt (none)"
    ];

    private enum WeaponGuardStyle { None, Crossguard, Quillons, Disc, Basket, Knuckle }

    private static readonly string[] WeaponGuardStyleLabels =
        ["None", "Crossguard (straight bar)", "Quillons (curved)", "Disc / tsuba (round plate)", "Basket (curved bars)", "Knuckle bow"];

    private enum WeaponPommelStyle { None, Ball, Wheel, Cap, Faceted, ScentStopper, Ring }

    private static readonly string[] WeaponPommelStyleLabels =
        ["None", "Ball", "Wheel / disc", "Cap", "Faceted", "Scent stopper", "Ring"];

    private enum WeaponBladeTip { Point, Clip, Tanto, Spatula, Rounded, Spear }

    private static readonly string[] WeaponBladeTipLabels =
        ["Point (symmetric)", "Clip (bowie)", "Tanto (angled)", "Spatula (flat)", "Rounded", "Spear point"];

    private enum WeaponAxeBack { None, Spike, Hammer, Pick }

    private static readonly string[] WeaponAxeBackLabels = ["None", "Spike", "Hammer poll", "Back pick"];

    private enum WeaponHammerBack { Flat, Spike, CrossPeen, Claw }

    private static readonly string[] WeaponHammerBackLabels = ["Flat poll", "Spike", "Cross peen", "Claw"];

    private enum WeaponMaceHead { Ball, Cylinder, Box, Cross }

    private static readonly string[] WeaponMaceHeadLabels = ["Ball", "Cylinder / drum", "Box", "Cross / star"];

    private enum WeaponFlangeType { Flange, Stud, Spike, Knob }

    private static readonly string[] WeaponFlangeTypeLabels = ["Flange (blade)", "Stud (pyramid)", "Spike", "Knob"];

    private enum WeaponSpearHead { Leaf, Triangle, Diamond, Needle, Barbed, Lance }

    private static readonly string[] WeaponSpearHeadLabels = ["Leaf", "Triangle", "Diamond", "Needle", "Barbed", "Lance / cone"];

    private enum WeaponPickBack { None, Spike, Adze, Hammer }

    private static readonly string[] WeaponPickBackLabels = ["None", "Spike", "Adze (flat blade)", "Hammer"];

    private enum WeaponBowStyle { Straight, Recurve, Longbow }

    private static readonly string[] WeaponBowStyleLabels = ["Straight / flatbow", "Recurve", "Longbow (D-stave)"];

    private enum WeaponBladeSection { Flat, SingleBevel, DoubleBevel, Diamond, Hollow }

    private static readonly string[] WeaponBladeSectionLabels =
        ["Flat / slab", "Single bevel (chisel)", "Double bevel (lens)", "Diamond / midrib", "Hollow ground"];

    private enum WeaponBladeSpine { Plain, FalseEdge, Rounded, Sharpened }

    private static readonly string[] WeaponBladeSpineLabels = ["Plain", "False edge (swedge)", "Rounded", "Fully sharpened"];

    private enum WeaponGripSection { Round, Oval, Octagon, Square, Ribbed }

    private static readonly string[] WeaponGripSectionLabels = ["Round", "Oval", "Octagon", "Square", "Ribbed / fluted"];

    private enum WeaponEdgeProfile { Straight, Convex, Concave }

    private static readonly string[] WeaponEdgeProfileLabels = ["Straight", "Convex (bellied)", "Concave (recurved)"];

    private enum WeaponFaceShape { Flat, Rounded, Octagon, Waffle }

    private static readonly string[] WeaponFaceShapeLabels = ["Flat", "Rounded", "Octagon", "Waffle (textured)"];

    private enum WeaponArchetype
    {
        Sword, Greatsword, Dagger, Knife, Rapier, Sabre, Katana, Khopesh, Gladius, Falchion, Machete, Scimitar,
        Spear, Pike, Javelin, Trident, Naginata, Glaive, Halberd, Partisan,
        Axe, Battleaxe, Bardiche, BeardedAxe, DoubleAxe, Hatchet,
        Warhammer, Maul, Mace, Morningstar, FlangedMace, Club, Quarterstaff,
        Pickaxe, WarPick, Mattock, Shovel, Hoe, Scythe, Sickle, SmithHammer, Saw,
        Shortbow, Longbow, Recurvebow
    }

    private static readonly string[] WeaponArchetypeLabels =
    [
        "Sword (arming)", "Greatsword / claymore", "Dagger", "Knife", "Rapier", "Sabre", "Katana / tachi", "Khopesh",
        "Gladius", "Falchion / cleaver", "Machete", "Scimitar",
        "Spear", "Pike", "Javelin", "Trident", "Naginata", "Glaive", "Halberd", "Partisan",
        "Axe", "Battle axe", "Bardiche", "Bearded axe", "Double axe", "Hatchet",
        "War hammer", "Maul", "Mace", "Morning star", "Flanged mace", "Club", "Quarterstaff",
        "Pickaxe", "War pick", "Mattock", "Shovel / spade", "Hoe", "Scythe", "Sickle", "Smithing hammer", "Saw",
        "Shortbow", "Longbow", "Recurve bow"
    ];

    private sealed class WeaponParams
    {
        // Identity / item JSON
        public string Code = "customsword";
        public string Name = "Custom Sword";
        public string Texture = "";        // metal / primary
        public string HandleTexture = "";  // wood / leather grip
        public string AccentTexture = "";  // fuller, wrap, gem, binding
        public bool AutoTexture = true;    // split parts across metal/handle/accent codes
        public int Seed = 1;

        // Global placement
        public NVector3 Center = new(8f, 8f, 8f);
        public NVector3 Rotation;
        public float UniformScale = 1f;

        // ---- Grip / haft -------------------------------------------------------
        public float GripLength = 5f;
        public float GripWidth = 1.4f;     // Y
        public float GripDepth = 1.1f;     // Z
        public float GripTaper = 1f;       // butt cross-section vs guard end
        public int GripSection = (int)WeaponGripSection.Oval;
        public float GripSwell;            // deg of mid-grip waisting / bulge (0 = straight)
        public int WrapRings;              // raised cord rings along the grip
        public float WrapSize = 0.35f;
        public bool GripCollar;            // a metal ferrule where the grip meets the guard
        public int Langets;                // metal strips running onto the head (polearms)
        public float LangetLength = 4f;

        // ---- Guard -------------------------------------------------------------
        public int GuardStyle = (int)WeaponGuardStyle.Crossguard;
        public float GuardWidth = 6f;      // total Y spread
        public float GuardDepth = 1.3f;    // Z
        public float GuardThickness = 1f;  // X
        public float GuardForward;         // X offset of the guard toward the blade
        public float QuillonDroop = 18f;   // deg the quillon arms angle toward the blade
        public bool GuardCup;              // a cupped plate (rapier / smallsword) facing the blade
        public int BasketBars = 3;

        // ---- Pommel ------------------------------------------------------------
        public int PommelStyle = (int)WeaponPommelStyle.Ball;
        public float PommelSize = 1.8f;
        public float PommelLength = 1.6f;  // X extent
        public bool PommelPeen;            // a small peen button on the butt end

        // ---- Head selector -----------------------------------------------------
        public int HeadType = (int)WeaponHeadType.Blade;

        // ---- Blade -------------------------------------------------------------
        public int BladeSegments = 4;
        public float BladeLength = 22f;
        public float BladeBaseWidth = 2.6f;  // Y at the guard
        public float BladeTipWidth = 0.5f;   // Y at the tip (width taper)
        public float BladeThickness = 0.5f;  // Z spine thickness at the guard
        public float BladeTipThickness = 0.3f; // Z spine thickness at the tip (distal taper)
        public float BladeCurve;             // deg per segment about Z (sabre / katana / khopesh)
        public int BladeTip = (int)WeaponBladeTip.Point;
        public float BladeTipLength = 2.5f;
        public bool BladeDoubleEdge;         // symmetric edges vs single-edged (offset edge)
        public bool BladeFuller;             // recessed central groove
        public float BladeFullerWidth = 0.3f;// fraction of blade width
        public bool BladeMidrib;             // raised central ridge / diamond section
        public int BladeSerrations;          // saw teeth along the spine
        public bool Ricasso;                 // thicker unsharpened base section

        // Cross-section / grind: the spine stays thick and the cutting edge(s) thin out through bevel facets.
        public int BladeSection = (int)WeaponBladeSection.SingleBevel;
        public bool BladeBevels = true;      // add the angled bevel facets at the edge(s)
        public float BladeBevelWidth = 0.5f; // fraction of the blade width the grind covers
        public float BladeBevelAngle = 20f;  // grind angle (deg)
        public float BladeEdgeThickness = 0.35f; // edge thickness as a fraction of the spine thickness
        public int BladeSpine = (int)WeaponBladeSpine.Plain;
        public float BladeSpineFraction = 0.55f; // how far up the spine a false edge starts (0..1)

        // ---- Axe ---------------------------------------------------------------
        public float AxeBitWidth = 7f;       // Y, edge height
        public float AxeBitDepth = 5f;       // X, how far the bit reaches forward
        public float AxeBitThickness = 1.2f; // Z
        public float AxeBeard = 2.5f;        // extra drop of the lower horn
        public float AxeTopHorn;             // extra rise of the upper horn
        public float AxeSweep = 10f;         // deg edge curvature
        public int AxeEdgeProfile = (int)WeaponEdgeProfile.Straight;
        public bool AxeCheeks;               // reinforced cheeks either side of the eye
        public bool AxeDoubleBit;            // a second bit on the back
        public int AxeBack = (int)WeaponAxeBack.None;
        public float AxeBackLength = 4f;
        public float AxeHeadPos = 0.92f;     // where on the haft the head sits (0..1)
        public bool AxeEye;                  // a raised eye/socket where the haft passes

        // ---- Hammer ------------------------------------------------------------
        public float HammerHeadLength = 4f;  // X
        public float HammerHeadWidth = 3.5f; // Y
        public float HammerHeadHeight = 3.5f;// Z
        public float HammerFaceBevel = 0.4f;
        public int HammerFaceShape = (int)WeaponFaceShape.Flat;
        public int HammerBack = (int)WeaponHammerBack.Flat;
        public float HammerBackLength = 3f;
        public bool HammerReinforced;
        public bool HammerCheeks;            // side cheek plates

        // ---- Mace --------------------------------------------------------------
        public int MaceHead = (int)WeaponMaceHead.Ball;
        public float MaceHeadSize = 3.5f;
        public int FlangeCount = 6;
        public int FlangeType = (int)WeaponFlangeType.Flange;
        public float FlangeLength = 1.8f;
        public float FlangeWidth = 1.2f;
        public int FlangeRows = 1;
        public float FlangeTwist;            // deg of spiral between rows
        public bool MaceNeck;                // a collar between the haft and the head
        public bool MaceTopSpike;
        public float MaceTopSpikeLength = 3f;

        // ---- Spear / polearm ---------------------------------------------------
        public float SocketLength = 3f;
        public float SpearHeadLength = 7f;
        public float SpearHeadWidth = 2.5f;
        public float SpearHeadThickness = 0.7f;
        public int SpearHead = (int)WeaponSpearHead.Leaf;
        public bool SpearMidrib = true;
        public bool SpearWings;              // side lugs (boar spear / partisan)
        public float SpearWingLength = 2.5f;
        public int SpearProngs = 1;          // 1 = spear, 3 = trident / ranseur
        public float SpearProngSpread = 2.2f;
        public bool SpearFuller;             // a groove down the spearhead
        public int SocketRings;              // reinforcing bands around the socket
        public bool SpearBackHook;           // halberd rear hook / spike
        public float SpearBackHookLength = 5f;
        public bool SpearSideAxe;            // halberd / voulge side axe blade
        public bool SpearCurvedBlade;        // naginata / glaive: a curved blade instead of a straight head

        // ---- Pick --------------------------------------------------------------
        public float PickSpikeLength = 7f;
        public float PickSpikeCurve = 16f;   // deg downward hook
        public int PickSpikeCount = 1;       // 1 = war pick, 2 = pickaxe (opposing)
        public float PickHeadWidth = 1.4f;
        public int PickBack = (int)WeaponPickBack.None;
        public float PickBackLength = 4f;

        // ---- Spade / shovel ----------------------------------------------------
        public float SpadeLength = 6f;       // X
        public float SpadeWidth = 6f;        // Y
        public float SpadeThickness = 0.6f;  // Z
        public float SpadeCurl = 12f;        // deg the scoop curls
        public bool SpadePointed;            // pointed (spade) vs flat (shovel)
        public bool SpadeStep;               // foot ledge at the top

        // ---- Hoe / adze --------------------------------------------------------
        public float HoeBladeWidth = 5f;     // Y
        public float HoeBladeLength = 3.5f;  // how far it projects (X)
        public float HoeBladeDrop = 70f;     // deg perpendicular bend
        public int HoeProngs = 1;            // 1 = hoe, 3 = cultivator
        public float HoeProngSpread = 1.8f;

        // ---- Scythe / sickle ---------------------------------------------------
        public int ScytheSegments = 5;
        public float ScytheLength = 16f;
        public float ScytheWidth = 2.2f;
        public float ScytheCurve = 14f;      // deg per segment
        public float ScytheMount = 80f;      // deg the blade mounts off the snath
        public float ScytheThickness = 0.4f;

        // ---- Bow ---------------------------------------------------------------
        public int BowStyle = (int)WeaponBowStyle.Recurve;
        public int BowLimbSegments = 4;
        public float BowLimbLength = 14f;    // per limb
        public float BowLimbThickness = 1f;
        public float BowLimbWidth = 1.6f;
        public float BowRecurve = 10f;       // deg per segment
        public float BowLimbTaper = 0.5f;    // tip width / thickness vs the riser
        public float BowRiserLength = 4f;
        public bool BowString = true;
        public bool BowGripWrap = true;
        public bool BowArrowRest;            // a small rest shelf above the grip

        // ---- Staff / blunt -----------------------------------------------------
        public bool StaffEndKnobs;
        public float StaffKnobSize = 1.8f;
        public bool ClubHead;                // flared, optionally studded striking end
        public float ClubHeadSize = 3f;
        public float ClubHeadLength = 5f;
        public int ClubStuds;

        // ---- Item JSON ---------------------------------------------------------
        public string ToolType = "sword";
        public float AttackPower = 5f;
        public float AttackRange = 1.5f;
        public int Durability = 800;
        public int ToolTier = 3;
        public float MiningSpeed;
        public string DamageType = "SlashingAttack";
    }

    private bool _weaponWindowOpen;
    private bool _weaponAdvanced;
    private int _weaponArchetypeIndex = (int)WeaponArchetype.Sword;
    private readonly WeaponParams _weaponParams = new();
    private ModelElementData? _weaponPreviewRoot;
    private string _weaponPreviewError = "";
    private bool _weaponPreviewDirty = true;
    private int _weaponPreviewCount;
    private string _weaponStatus = "";

    // ---- Geometry helpers --------------------------------------------------

    private static double WeaponLerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>One centred box (size + rotation) parented under <paramref name="parent"/>, expressed in the
    /// parent's From space - the same convention the creature builder uses.</summary>
    private ModelElementData WeaponBox(ModelElementData parent, double cx, double cy, double cz,
        double sx, double sy, double sz, double rx, double ry, double rz, string name)
    {
        return ModelCreatureBox(parent, [cx, cy, cz], [sx, sy, sz], rx, ry, rz, name);
    }

    // ---- Build entry -------------------------------------------------------

    private ModelElementData? ModelBuildWeapon(out string error)
    {
        error = "";
        if (_modelDoc == null) return null;

        try
        {
            WeaponParams p = _weaponParams;
            p.Rotation.X = (float)ModelWrapDegrees(p.Rotation.X);
            p.Rotation.Y = (float)ModelWrapDegrees(p.Rotation.Y);
            p.Rotation.Z = (float)ModelWrapDegrees(p.Rotation.Z);

            ModelElementData group = new()
            {
                Name = "Weapon",
                From = [p.Center.X, p.Center.Y, p.Center.Z],
                To = [p.Center.X, p.Center.Y, p.Center.Z],
                RotationOrigin = [p.Center.X, p.Center.Y, p.Center.Z],
                RotationX = ModelPrimitiveRound(p.Rotation.X),
                RotationY = ModelPrimitiveRound(p.Rotation.Y),
                RotationZ = ModelPrimitiveRound(p.Rotation.Z)
            };

            WeaponHeadType head = (WeaponHeadType)Math.Clamp(p.HeadType, 0, WeaponHeadTypeLabels.Length - 1);

            // Grip spans [-gripLen, 0]; the guard sits at x = 0; the head grows from there toward +X.
            double gripLen = Math.Max(0.5, p.GripLength);
            double headX = Math.Max(0.0, p.GuardThickness) * 0.5;

            ModelWeaponBuildGrip(p, group, gripLen);
            ModelWeaponBuildGuard(p, group, gripLen);
            ModelWeaponBuildPommel(p, group, gripLen);

            switch (head)
            {
                case WeaponHeadType.Blade: ModelWeaponBuildBlade(p, group, headX); break;
                case WeaponHeadType.Axe: ModelWeaponBuildAxe(p, group, gripLen); break;
                case WeaponHeadType.Hammer: ModelWeaponBuildHammer(p, group, headX); break;
                case WeaponHeadType.Mace: ModelWeaponBuildMace(p, group, headX); break;
                case WeaponHeadType.Spear: ModelWeaponBuildSpear(p, group, headX); break;
                case WeaponHeadType.Pick: ModelWeaponBuildPick(p, group, headX); break;
                case WeaponHeadType.Spade: ModelWeaponBuildSpade(p, group, headX); break;
                case WeaponHeadType.Hoe: ModelWeaponBuildHoe(p, group, headX); break;
                case WeaponHeadType.Scythe: ModelWeaponBuildScythe(p, group, headX); break;
                case WeaponHeadType.Bow: ModelWeaponBuildBow(p, group); break;
                case WeaponHeadType.Staff: ModelWeaponBuildStaff(p, group, gripLen); break;
            }

            ModelCreatureScaleSubtree(group, Math.Clamp((double)p.UniformScale, 0.1, 8.0));

            string metal = ModelWeaponResolveTexture(p.Texture, "metal");
            string handle = ModelWeaponResolveTexture(p.HandleTexture, p.AutoTexture ? "handle" : metal, metal);
            string accent = ModelWeaponResolveTexture(p.AccentTexture, p.AutoTexture ? "accent" : metal, metal);
            ModelWeaponAssignFaces(group, metal, handle, accent);

            if (ModelIsMeshLibMode)
            {
                ModelAssignGeneratedMeshSpecs(group, ModelWeaponGeneratedMeshSpec);
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
            if (count > WeaponMaxElements)
            {
                error = $"Too many elements ({count} > {WeaponMaxElements}). Reduce segment / flange / wrap counts.";
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

    private ModelGeneratedMeshSpec ModelWeaponGeneratedMeshSpec(ModelElementData element)
    {
        string name = element.Name.ToLowerInvariant();
        int longAxis = ModelGeneratedLongestAxis(element);
        int shortAxis = ModelWeaponSmallestAxis(element);

        if (name.Contains("gem", StringComparison.Ordinal) || name.Contains("jewel", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Jewel);
        }
        if (name.StartsWith("flange", StringComparison.Ordinal))
        {
            return (WeaponFlangeType)Math.Clamp(_weaponParams.FlangeType, 0, WeaponFlangeTypeLabels.Length - 1) switch
            {
                WeaponFlangeType.Flange => ModelGeneratedSpec(ModelGeneratedMeshKind.Leaf, axis: longAxis),
                WeaponFlangeType.Stud => ModelGeneratedSpec(ModelGeneratedMeshKind.Dome, axis: longAxis, sides: 12, layers: 4),
                WeaponFlangeType.Spike => ModelGeneratedSpec(ModelGeneratedMeshKind.Cone, axis: longAxis, sides: 8),
                _ => ModelGeneratedSpec(ModelGeneratedMeshKind.Ellipsoid, axis: longAxis, sides: 12, layers: 6)
            };
        }
        if (name.Contains("tip", StringComparison.Ordinal) || name.Contains("spike", StringComparison.Ordinal) ||
            name.Contains("prong", StringComparison.Ordinal) || name.Contains("serr", StringComparison.Ordinal) ||
            name.Contains("barb", StringComparison.Ordinal) || name is "pickback" or "axebackpick")
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Cone, axis: longAxis, sides: 8);
        }
        if (name.Contains("bladebevel", StringComparison.Ordinal) || name.Contains("bladeswedge", StringComparison.Ordinal) ||
            name.Contains("edge", StringComparison.Ordinal) || name.Contains("adze", StringComparison.Ordinal) ||
            name.Contains("claw", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Wedge, axis: longAxis);
        }
        if (name.Contains("bladespine", StringComparison.Ordinal) || name.Contains("fuller", StringComparison.Ordinal) ||
            name.Contains("midrib", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: longAxis, sides: 8, endScale: 0.82d);
        }
        if (ModelWeaponIsNumberedPart(name, "blade") || ModelWeaponIsNumberedPart(name, "polearmblade") ||
            ModelWeaponIsNumberedPart(name, "scytheblade"))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.ExtrudedContour, axis: longAxis, startScale: 0.82d, endScale: 0.68d);
        }
        if (name.Contains("spearhead", StringComparison.Ordinal) || name.Contains("axebit", StringComparison.Ordinal) ||
            name.Contains("axebeard", StringComparison.Ordinal) || name.Contains("axetophorn", StringComparison.Ordinal) ||
            name.Contains("spadescoop", StringComparison.Ordinal) || name.Contains("hoeblade", StringComparison.Ordinal) ||
            name.Contains("spearaxe", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.ExtrudedContour, axis: longAxis, startScale: 0.62d, endScale: 0.42d);
        }
        if (name.Contains("guardcup", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Dome, axis: shortAxis, sides: 12, layers: 6);
        }
        if (name.Contains("disc", StringComparison.Ordinal) || name.Contains("wheel", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: shortAxis, sides: 12);
        }
        if (name.Contains("pommelring", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Ring, axis: longAxis, sides: 12);
        }
        if (name == "pommel")
        {
            return (WeaponPommelStyle)Math.Clamp(_weaponParams.PommelStyle, 0, WeaponPommelStyleLabels.Length - 1) switch
            {
                WeaponPommelStyle.Wheel => ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: shortAxis, sides: 12),
                WeaponPommelStyle.Cap => ModelGeneratedSpec(ModelGeneratedMeshKind.Dome, axis: 0, sign: -1, sides: 12, layers: 6),
                WeaponPommelStyle.Faceted => ModelGeneratedSpec(ModelGeneratedMeshKind.Jewel),
                WeaponPommelStyle.ScentStopper => ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: longAxis, sides: 8, endScale: 0.76d),
                _ => ModelGeneratedSpec(ModelGeneratedMeshKind.Ellipsoid, axis: longAxis, sides: 12, layers: 6)
            };
        }
        if (name == "pommelfacet")
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Jewel);
        }
        if (name is "pommelcap" or "pommelpeen")
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Dome, axis: 0, sign: -1, sides: 12, layers: 6);
        }
        if (name == "macehead")
        {
            return (WeaponMaceHead)Math.Clamp(_weaponParams.MaceHead, 0, WeaponMaceHeadLabels.Length - 1) switch
            {
                WeaponMaceHead.Ball => ModelGeneratedSpec(ModelGeneratedMeshKind.Ellipsoid, axis: longAxis, sides: 12, layers: 6),
                WeaponMaceHead.Cylinder => ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: longAxis, sides: 12),
                WeaponMaceHead.Box => ModelGeneratedSpec(ModelGeneratedMeshKind.ChamferedBox),
                _ => ModelGeneratedSpec(ModelGeneratedMeshKind.Jewel)
            };
        }
        if (name.Contains("ball", StringComparison.Ordinal) || name.Contains("knob", StringComparison.Ordinal) ||
            name.Contains("round", StringComparison.Ordinal) ||
            name.StartsWith("pommel", StringComparison.Ordinal) || name.Contains("staffknob", StringComparison.Ordinal))
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Ellipsoid, axis: longAxis, sides: 12, layers: 6);
        }
        if (name.StartsWith("grip", StringComparison.Ordinal) || name.StartsWith("wrap", StringComparison.Ordinal) ||
            name.StartsWith("shaft", StringComparison.Ordinal) || name.StartsWith("haft", StringComparison.Ordinal) ||
            name.StartsWith("socket", StringComparison.Ordinal) || name.StartsWith("staff", StringComparison.Ordinal) ||
            name.StartsWith("club", StringComparison.Ordinal) || name.StartsWith("bowlimb", StringComparison.Ordinal) ||
            name.StartsWith("bowstring", StringComparison.Ordinal) || name.Contains("basketbar", StringComparison.Ordinal) ||
            name.Contains("quillon", StringComparison.Ordinal) || name.Contains("langet", StringComparison.Ordinal) ||
            name.Contains("collar", StringComparison.Ordinal) || name.Contains("rib", StringComparison.Ordinal))
        {
            double endScale = name.StartsWith("grip", StringComparison.Ordinal) || name.StartsWith("shaft", StringComparison.Ordinal) ||
                name.StartsWith("haft", StringComparison.Ordinal) || name.StartsWith("bowlimb", StringComparison.Ordinal)
                ? 0.82d
                : 1d;
            return ModelGeneratedSpec(ModelGeneratedMeshKind.Tube, axis: longAxis, sides: 8, endScale: endScale);
        }
        if (name is "guard" or "hammerhead" or "hammerface" or "axehead" or "pickhead")
        {
            return ModelGeneratedSpec(ModelGeneratedMeshKind.ChamferedBox);
        }
        return ModelGeneratedSpec(ModelGeneratedMeshKind.ChamferedBox);
    }

    private static bool ModelWeaponIsNumberedPart(string name, string prefix)
    {
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && char.IsDigit(name[prefix.Length]);
    }

    private static int ModelWeaponSmallestAxis(ModelElementData element)
    {
        double[] size = [element.SizeX, element.SizeY, element.SizeZ];
        return size[0] <= size[1] && size[0] <= size[2] ? 0 : size[1] <= size[2] ? 1 : 2;
    }

    // ---- Grip / guard / pommel --------------------------------------------

    private void ModelWeaponBuildGrip(WeaponParams p, ModelElementData group, double gripLen)
    {
        double taper = Math.Clamp((double)p.GripTaper, 0.2, 2.5);
        double w = Math.Max(0.2, p.GripWidth);
        double d = Math.Max(0.2, p.GripDepth);
        WeaponGripSection sect = (WeaponGripSection)Math.Clamp(p.GripSection, 0, WeaponGripSectionLabels.Length - 1);

        // An oval grip is wider one way than the other; round/octagon are square in section + chamfered.
        double sw = sect == WeaponGripSection.Square ? w : w;
        double sd = sect == WeaponGripSection.Round || sect == WeaponGripSection.Octagon ? Math.Min(w, d) : d;
        ModelElementData grip = WeaponBox(group, -gripLen * 0.5, 0.0, 0.0, gripLen, sw * taper, sd * taper, 0.0, 0.0, 0.0, "grip");
        if (Math.Abs(taper - 1.0) > 0.01)
        {
            WeaponBox(grip, gripLen * 0.5 - gripLen * 0.25, 0.0, 0.0, gripLen * 0.5, sw, sd, 0.0, 0.0, 0.0, "gripGuardEnd");
        }

        // Cross-section detail: octagon/round add a 45deg chamfer box; ribbed adds raised flutes.
        if (sect == WeaponGripSection.Octagon || sect == WeaponGripSection.Round)
        {
            WeaponBox(grip, gripLen * 0.5, 0.0, 0.0, gripLen * 0.96, sw * 0.74, sd * 0.74, 45.0, 0.0, 0.0, "gripChamfer");
        }
        else if (sect == WeaponGripSection.Ribbed)
        {
            for (int i = 0; i < 4; i++)
            {
                double ang = 45.0 + i * 90.0;
                double ry = Math.Cos(ang * Math.PI / 180.0) * sw * 0.5;
                double rz = Math.Sin(ang * Math.PI / 180.0) * sd * 0.5;
                WeaponBox(grip, gripLen * 0.5, ry, rz, gripLen * 0.92, sw * 0.22, sd * 0.22, 0.0, 0.0, 0.0, $"gripRib{i + 1}");
            }
        }

        // Mid-grip swell / waisting: a bulged or pinched band in the middle.
        if (Math.Abs(p.GripSwell) > 0.5)
        {
            double swell = 1.0 + p.GripSwell / 100.0;
            WeaponBox(grip, gripLen * 0.5, 0.0, 0.0, gripLen * 0.45, sw * swell, sd * swell, 0.0, 0.0, 0.0, "gripSwell");
        }

        if (p.GripCollar)
        {
            WeaponBox(grip, gripLen - 0.3, 0.0, 0.0, 0.7, sw + 0.4, sd + 0.4, 0.0, 0.0, 0.0, "gripCollar");
        }

        // Raised cord/leather wrap rings spaced along the grip.
        int rings = Math.Clamp(p.WrapRings, 0, 16);
        for (int i = 0; i < rings; i++)
        {
            double t = rings == 1 ? 0.5 : (double)i / (rings - 1);
            double x = WeaponLerp(-gripLen + 0.4, -0.6, t) - (-gripLen * 0.5); // relative to grip From-centred box
            double ring = Math.Max(0.1, p.WrapSize);
            WeaponBox(grip, x, 0.0, 0.0, Math.Max(0.4, gripLen / (rings + 2)), sw + ring, sd + ring, 0.0, 0.0, 0.0, $"wrap{i + 1}");
        }
    }

    private void ModelWeaponBuildGuard(WeaponParams p, ModelElementData group, double gripLen)
    {
        WeaponGuardStyle style = (WeaponGuardStyle)Math.Clamp(p.GuardStyle, 0, WeaponGuardStyleLabels.Length - 1);
        if (style == WeaponGuardStyle.None) return;

        double width = Math.Max(0.3, p.GuardWidth);
        double depth = Math.Max(0.3, p.GuardDepth);
        double thick = Math.Max(0.2, p.GuardThickness);
        double gx = p.GuardForward;

        switch (style)
        {
            case WeaponGuardStyle.Crossguard:
                WeaponBox(group, gx, 0.0, 0.0, thick, width, depth, 0.0, 0.0, 0.0, "guard");
                break;

            case WeaponGuardStyle.Quillons:
            {
                WeaponBox(group, gx, 0.0, 0.0, thick, Math.Max(0.6, width * 0.3), depth, 0.0, 0.0, 0.0, "guard");
                double arm = width * 0.5;
                double droop = p.QuillonDroop;
                // Two arms angled toward the blade about Z (the X-Y plane), one up one down.
                WeaponBox(group, gx, arm * 0.5, 0.0, thick, arm, depth * 0.8, 0.0, 0.0, droop, "quillonUp");
                WeaponBox(group, gx, -arm * 0.5, 0.0, thick, arm, depth * 0.8, 0.0, 0.0, -droop, "quillonDown");
                break;
            }

            case WeaponGuardStyle.Disc:
            {
                // Round-ish tsuba: a plate spread in Y and Z, with corner facets to read as a disc.
                WeaponBox(group, gx, 0.0, 0.0, thick, width, width, 0.0, 0.0, 0.0, "guard");
                WeaponBox(group, gx, 0.0, 0.0, thick * 0.9, width * 1.06, width * 0.7, 0.0, 0.0, 0.0, "guardDisc1");
                WeaponBox(group, gx, 0.0, 0.0, thick * 0.9, width * 0.7, width * 1.06, 0.0, 0.0, 0.0, "guardDisc2");
                break;
            }

            case WeaponGuardStyle.Basket:
            {
                WeaponBox(group, gx, 0.0, 0.0, thick, Math.Max(0.6, width * 0.35), depth, 0.0, 0.0, 0.0, "guard");
                int bars = Math.Clamp(p.BasketBars, 1, 6);
                double span = gripLen * 0.85;
                for (int i = 0; i < bars; i++)
                {
                    double ang = bars == 1 ? 0.0 : WeaponLerp(-55.0, 55.0, (double)i / (bars - 1));
                    // Bars sweep back from the guard toward the pommel on the knuckle side (+Z front of hand).
                    WeaponBox(group, gx - span * 0.5, width * 0.18, depth * 0.55, span, 0.4, 0.4, 0.0, ang, 0.0, $"basketBar{i + 1}");
                }
                break;
            }

            case WeaponGuardStyle.Knuckle:
            {
                WeaponBox(group, gx, 0.0, 0.0, thick, Math.Max(0.6, width * 0.4), depth, 0.0, 0.0, 0.0, "guard");
                double span = gripLen * 0.9;
                WeaponBox(group, gx, width * 0.5, depth * 0.4, thick, 0.5, depth * 0.9, 0.0, 0.0, 0.0, "knuckle1");
                WeaponBox(group, gx - span * 0.5, width * 0.45, depth * 0.7, span, 0.5, 0.5, 0.0, 0.0, 0.0, "knuckle2");
                WeaponBox(group, gx - span, width * 0.2, depth * 0.4, 0.5, width * 0.5, 0.5, 0.0, 0.0, 0.0, "knuckle3");
                break;
            }
        }

        // Cupped hilt plate (rapier / smallsword) facing the blade.
        if (p.GuardCup && style != WeaponGuardStyle.Disc)
        {
            double cup = width * 0.55;
            WeaponBox(group, gx + thick * 0.6, 0.0, 0.0, thick * 0.5, cup, cup, 0.0, 0.0, 0.0, "guardCup");
            WeaponBox(group, gx + thick * 0.6, 0.0, 0.0, thick * 0.5, cup * 0.7, cup * 1.05, 0.0, 0.0, 0.0, "guardCupRim1");
            WeaponBox(group, gx + thick * 0.6, 0.0, 0.0, thick * 0.5, cup * 1.05, cup * 0.7, 0.0, 0.0, 0.0, "guardCupRim2");
        }
    }

    private void ModelWeaponBuildPommel(WeaponParams p, ModelElementData group, double gripLen)
    {
        WeaponPommelStyle style = (WeaponPommelStyle)Math.Clamp(p.PommelStyle, 0, WeaponPommelStyleLabels.Length - 1);
        if (style == WeaponPommelStyle.None) return;

        double size = Math.Max(0.3, p.PommelSize);
        double len = Math.Max(0.3, p.PommelLength);
        double x = -gripLen - len * 0.5 + 0.3;

        switch (style)
        {
            case WeaponPommelStyle.Ball:
                WeaponBox(group, x, 0.0, 0.0, len, size, size, 0.0, 0.0, 0.0, "pommel");
                WeaponBox(group, x, 0.0, 0.0, len * 0.8, size * 1.08, size * 0.7, 0.0, 0.0, 0.0, "pommelRound1");
                WeaponBox(group, x, 0.0, 0.0, len * 0.8, size * 0.7, size * 1.08, 0.0, 0.0, 0.0, "pommelRound2");
                break;
            case WeaponPommelStyle.Wheel:
                WeaponBox(group, x, 0.0, 0.0, len * 0.7, size * 1.3, size * 1.3, 0.0, 0.0, 0.0, "pommel");
                break;
            case WeaponPommelStyle.Cap:
                WeaponBox(group, x, 0.0, 0.0, len, size, size, 0.0, 0.0, 0.0, "pommel");
                break;
            case WeaponPommelStyle.Faceted:
                WeaponBox(group, x, 0.0, 0.0, len, size, size, 0.0, 0.0, 0.0, "pommel");
                WeaponBox(group, x, 0.0, 0.0, len * 0.9, size, size, 0.0, 45.0, 0.0, "pommelFacet");
                break;
            case WeaponPommelStyle.ScentStopper:
                WeaponBox(group, x + len * 0.2, 0.0, 0.0, len * 0.6, size * 0.7, size * 0.7, 0.0, 0.0, 0.0, "pommel");
                WeaponBox(group, x - len * 0.4, 0.0, 0.0, len, size, size, 0.0, 0.0, 0.0, "pommelCap");
                break;
            case WeaponPommelStyle.Ring:
                WeaponBox(group, x, size * 0.45, 0.0, len * 0.6, size * 0.5, size * 0.5, 0.0, 0.0, 0.0, "pommelRing1");
                WeaponBox(group, x, -size * 0.45, 0.0, len * 0.6, size * 0.5, size * 0.5, 0.0, 0.0, 0.0, "pommelRing2");
                WeaponBox(group, x, 0.0, size * 0.45, len * 0.6, size * 0.5, size * 0.5, 0.0, 0.0, 0.0, "pommelRing3");
                WeaponBox(group, x, 0.0, -size * 0.45, len * 0.6, size * 0.5, size * 0.5, 0.0, 0.0, 0.0, "pommelRing4");
                break;
        }

        // A peen button capping the tang on the butt end.
        if (p.PommelPeen)
        {
            WeaponBox(group, x - len * 0.55, 0.0, 0.0, len * 0.45, size * 0.4, size * 0.4, 0.0, 0.0, 0.0, "pommelPeen");
        }

        // Langets: metal strips running from the guard up onto the head (common on polearms).
        int langets = Math.Clamp(p.Langets, 0, 4);
        for (int i = 0; i < langets; i++)
        {
            double ang = i * (360.0 / Math.Max(1, langets));
            double ly = Math.Cos(ang * Math.PI / 180.0) * (p.GripWidth * 0.5 + 0.1);
            double lz = Math.Sin(ang * Math.PI / 180.0) * (p.GripDepth * 0.5 + 0.1);
            WeaponBox(group, p.LangetLength * 0.5, ly, lz, Math.Max(1.0, p.LangetLength), 0.4, 0.4, 0.0, 0.0, 0.0, $"langet{i + 1}");
        }
    }

    // ---- Blade -------------------------------------------------------------

    private ModelElementData ModelWeaponBuildBlade(WeaponParams p, ModelElementData group, double headX)
    {
        int segs = Math.Clamp(p.BladeSegments, 1, 12);
        double len = Math.Max(1.0, p.BladeLength);
        double segLen = len / segs;
        double baseW = Math.Max(0.3, p.BladeBaseWidth);
        double tipW = Math.Max(0.1, Math.Min(p.BladeTipWidth, p.BladeBaseWidth));
        double baseTh = Math.Max(0.1, p.BladeThickness);
        double tipTh = Math.Max(0.08, Math.Min(p.BladeTipThickness, p.BladeThickness));
        WeaponBladeSection section = (WeaponBladeSection)Math.Clamp(p.BladeSection, 0, WeaponBladeSectionLabels.Length - 1);

        // The chain segment box is the thin CORE of the blade; the thick spine ridge and the angled bevel
        // facets are added per-segment so the cross-section - and therefore the visible thickness - grades
        // from a thick spine down to a thin cutting edge.
        double edgeFrac = section == WeaponBladeSection.Flat ? 1.0 : Math.Clamp((double)p.BladeEdgeThickness, 0.15, 1.0);

        List<double[]> sizes = [];
        List<double> bends = [];
        List<double> segThick = [];
        for (int k = 0; k < segs; k++)
        {
            double t = segs <= 1 ? 1.0 : (double)k / (segs - 1);
            double w = WeaponLerp(baseW, tipW, t);
            double th = WeaponLerp(baseTh, tipTh, t);
            segThick.Add(th);
            sizes.Add([segLen, w, th * edgeFrac]);
            bends.Add(k == 0 ? 0.0 : p.BladeCurve);
        }
        ModelCreatureChain(group, [headX, 0.0, 0.0], 0.0, 0.0, 0.0, 0, 1, sizes, bends, 2, "blade", out ModelElementData tip);

        // Walk to the tip element and collect the chain for detailing.
        List<ModelElementData> chain = [];
        for (ModelElementData? cur = group.Children.FirstOrDefault(c => c.Name.StartsWith("blade", StringComparison.Ordinal));
             cur != null; cur = cur.Children.FirstOrDefault(c => c.Name.StartsWith("blade", StringComparison.Ordinal)))
        {
            chain.Add(cur);
        }
        ModelElementData first = chain.Count > 0 ? chain[0] : tip;

        if (section != WeaponBladeSection.Flat)
        {
            for (int k = 0; k < chain.Count; k++)
            {
                ModelWeaponBladeCrossSection(p, chain[k], chain[k].SizeX, chain[k].SizeY, segThick[k], section, k, chain.Count);
            }
        }

        // Ricasso: a short thicker, full-width base section just ahead of the guard.
        if (p.Ricasso)
        {
            WeaponBox(first, segLen * 0.4, 0.0, 0.0, segLen * 0.8, baseW * 1.05, baseTh * 1.6, 0.0, 0.0, 0.0, "ricasso");
        }

        // Fuller: a recessed central groove (accent texture) spanning most of the blade body.
        if (p.BladeFuller)
        {
            double fw = Math.Clamp((double)p.BladeFullerWidth, 0.1, 0.8) * baseW;
            WeaponBox(first, len * 0.42, 0.0, 0.0, len * 0.7, fw, baseTh * 0.5, 0.0, 0.0, 0.0, "fuller");
        }

        // Midrib: an extra raised diamond ridge along the centre (on top of the cross-section).
        if (p.BladeMidrib)
        {
            WeaponBox(first, len * 0.42, 0.0, 0.0, len * 0.75, baseW * 0.22, baseTh * 1.8, 0.0, 0.0, 0.0, "midrib");
        }

        // Serrations: saw teeth along the spine of the first segment.
        int serr = Math.Clamp(p.BladeSerrations, 0, 14);
        for (int i = 0; i < serr; i++)
        {
            double sx = WeaponLerp(segLen * 0.4, len * 0.85, serr == 1 ? 0.5 : (double)i / (serr - 1));
            WeaponBox(first, sx, baseW * 0.5, 0.0, 0.5, baseW * 0.35, baseTh, 0.0, 0.0, 35.0, $"serr{i + 1}");
        }

        ModelWeaponBuildBladeTip(p, tip, segLen, tipW, segThick[^1]);
        return tip;
    }

    /// <summary>Adds the thick spine ridge and the angled bevel facets to one blade segment so its cross-section
    /// reads as a real grind (chisel / lens / diamond / hollow) instead of a flat slab. Coordinates are relative
    /// to the segment's From corner, where (0,0,0) = back-bottom-edge and (segLen, w, coreTh) = front-top-spine.</summary>
    private void ModelWeaponBladeCrossSection(WeaponParams p, ModelElementData seg, double segLen, double w, double fullTh,
        WeaponBladeSection section, int segIndex, int segCount)
    {
        double coreTh = seg.SizeZ;
        double zMid = coreTh * 0.5;
        double bevelFrac = Math.Clamp((double)p.BladeBevelWidth, 0.1, 0.9);
        double bevelBand = bevelFrac * w * 0.5;   // height of each bevel band (per edge)
        bool doubleEdged = section is WeaponBladeSection.DoubleBevel or WeaponBladeSection.Diamond or WeaponBladeSection.Hollow || p.BladeDoubleEdge;

        // Thick spine ridge: centred for a symmetric (double) grind, up at the spine for a single bevel.
        double ridgeW = section == WeaponBladeSection.Diamond ? w * 0.34 : w * (1.0 - bevelFrac);
        double ridgeTh = section == WeaponBladeSection.Diamond ? fullTh * 1.15 : fullTh;
        double ridgeY = doubleEdged ? w * 0.5 : (w - ridgeW * 0.5);
        WeaponBox(seg, segLen * 0.5, ridgeY, zMid, segLen, Math.Max(0.1, ridgeW), ridgeTh, 0.0, 0.0, 0.0, $"bladeSpine{segIndex + 1}");

        if (!p.BladeBevels) return;
        double angle = (double)p.BladeBevelAngle * (section == WeaponBladeSection.Hollow ? 1.4 : 1.0);
        // Kept within the thin core so the spine ridge stays the clear thickest point (a true bevel read).
        double facetTh = Math.Max(0.06, coreTh * 0.5);
        double zOff = coreTh * 0.18;

        // Primary cutting edge (bottom, low Y): two facet planes pitched into a V.
        WeaponBox(seg, segLen * 0.5, bevelBand * 0.5, zMid + zOff, segLen, bevelBand, facetTh, angle, 0.0, 0.0, $"bladeBevel{segIndex + 1}A");
        WeaponBox(seg, segLen * 0.5, bevelBand * 0.5, zMid - zOff, segLen, bevelBand, facetTh, -angle, 0.0, 0.0, $"bladeBevel{segIndex + 1}B");

        double spineFrac = segCount > 1 ? (double)segIndex / (segCount - 1) : 1.0;
        bool topEdge = doubleEdged
            || (WeaponBladeSpine)p.BladeSpine == WeaponBladeSpine.Sharpened
            || ((WeaponBladeSpine)p.BladeSpine == WeaponBladeSpine.FalseEdge && spineFrac >= Math.Clamp((double)p.BladeSpineFraction, 0.0, 1.0));
        if (topEdge)
        {
            WeaponBox(seg, segLen * 0.5, w - bevelBand * 0.5, zMid + zOff, segLen, bevelBand, facetTh, -angle, 0.0, 0.0, $"bladeSwedge{segIndex + 1}A");
            WeaponBox(seg, segLen * 0.5, w - bevelBand * 0.5, zMid - zOff, segLen, bevelBand, facetTh, angle, 0.0, 0.0, $"bladeSwedge{segIndex + 1}B");
        }
    }

    private void ModelWeaponBuildBladeTip(WeaponParams p, ModelElementData tip, double segLen, double tipW, double thick)
    {
        WeaponBladeTip style = (WeaponBladeTip)Math.Clamp(p.BladeTip, 0, WeaponBladeTipLabels.Length - 1);
        double attachX = tip.To[0] - tip.From[0];
        double tipLen = Math.Max(0.4, p.BladeTipLength);

        switch (style)
        {
            case WeaponBladeTip.Point:
                WeaponBox(tip, attachX + tipLen * 0.4, 0.0, 0.0, tipLen, tipW * 0.6, thick, 0.0, 0.0, 0.0, "bladeTip");
                WeaponBox(tip, attachX + tipLen * 0.85, 0.0, 0.0, tipLen * 0.6, tipW * 0.6, thick, 0.0, 0.0, 45.0, "bladeTipPoint");
                break;
            case WeaponBladeTip.Clip:
                WeaponBox(tip, attachX + tipLen * 0.4, -tipW * 0.1, 0.0, tipLen, tipW * 0.7, thick, 0.0, 0.0, -22.0, "bladeTip");
                WeaponBox(tip, attachX + tipLen * 0.9, tipW * 0.2, 0.0, tipLen * 0.7, tipW * 0.5, thick, 0.0, 0.0, 30.0, "bladeTipClip");
                break;
            case WeaponBladeTip.Tanto:
                WeaponBox(tip, attachX + tipLen * 0.45, 0.0, 0.0, tipLen, tipW * 0.8, thick, 0.0, 0.0, 32.0, "bladeTip");
                break;
            case WeaponBladeTip.Spatula:
                WeaponBox(tip, attachX + tipLen * 0.4, 0.0, 0.0, tipLen * 0.7, tipW, thick, 0.0, 0.0, 0.0, "bladeTip");
                break;
            case WeaponBladeTip.Rounded:
                WeaponBox(tip, attachX + tipLen * 0.3, 0.0, 0.0, tipLen * 0.6, tipW * 0.75, thick, 0.0, 0.0, 0.0, "bladeTip");
                WeaponBox(tip, attachX + tipLen * 0.5, 0.0, 0.0, tipLen * 0.5, tipW * 0.55, thick, 0.0, 0.0, 45.0, "bladeTipRound");
                break;
            case WeaponBladeTip.Spear:
                WeaponBox(tip, attachX + tipLen * 0.45, 0.0, 0.0, tipLen, tipW * 0.7, thick * 1.4, 0.0, 0.0, 0.0, "bladeTip");
                WeaponBox(tip, attachX + tipLen, 0.0, 0.0, tipLen * 0.6, tipW * 0.6, thick * 1.4, 0.0, 0.0, 45.0, "bladeTipPoint");
                break;
        }
    }

    // ---- Axe ---------------------------------------------------------------

    private void ModelWeaponBuildAxe(WeaponParams p, ModelElementData group, double gripLen)
    {
        double bitW = Math.Max(1.0, p.AxeBitWidth);
        double bitDepth = Math.Max(1.0, p.AxeBitDepth);
        double bitThick = Math.Max(0.3, p.AxeBitThickness);
        // The head sits at a fraction of the haft, measured from the butt (-gripLen) toward the front (0).
        double headX = WeaponLerp(-gripLen, 0.0, Math.Clamp((double)p.AxeHeadPos, 0.0, 1.0));

        if (p.AxeEye)
        {
            WeaponBox(group, headX, 0.0, 0.0, 2.0, p.GripWidth + 1.5, p.GripDepth + 1.5, 0.0, 0.0, 0.0, "axeEye");
        }
        if (p.AxeCheeks)
        {
            WeaponBox(group, headX, 0.0, bitThick * 0.9, 1.2, bitW * 0.6, 0.6, 0.0, 0.0, 0.0, "axeCheek1");
            WeaponBox(group, headX, 0.0, -bitThick * 0.9, 1.2, bitW * 0.6, 0.6, 0.0, 0.0, 0.0, "axeCheek2");
        }

        ModelElementData head = WeaponBox(group, headX, 0.0, 0.0, 1.6, bitW * 0.5, bitThick * 1.3, 0.0, 0.0, 0.0, "axeHead");

        // The bit reaches forward (+Z, the swing direction) and drops with a beard; the edge curves (sweep).
        double sweep = p.AxeSweep;
        double beard = Math.Max(0.0, p.AxeBeard);
        double topHorn = Math.Max(0.0, p.AxeTopHorn);
        WeaponEdgeProfile profile = (WeaponEdgeProfile)Math.Clamp(p.AxeEdgeProfile, 0, WeaponEdgeProfileLabels.Length - 1);
        ModelElementData bit = WeaponBox(head, 0.0, -beard * 0.4 + topHorn * 0.4, bitDepth * 0.5, bitDepth, bitW, bitThick, sweep, 0.0, 0.0, "axeBit");
        // Edge profile: convex bellies the cutting edge forward, concave (recurve) pulls the middle back.
        double edgeBow = profile == WeaponEdgeProfile.Convex ? bitDepth * 0.18 : profile == WeaponEdgeProfile.Concave ? -bitDepth * 0.14 : 0.0;
        WeaponBox(bit, bitDepth * 0.45 + edgeBow, 0.0, 0.0, bitDepth * 0.3, bitW * 1.08, bitThick * 0.6, 0.0, 0.0, 0.0, "axeEdge");
        if (profile != WeaponEdgeProfile.Straight)
        {
            WeaponBox(bit, bitDepth * 0.4 + edgeBow * 0.4, bitW * 0.42, 0.0, bitDepth * 0.4, bitW * 0.25, bitThick * 0.6, 0.0, 0.0, 0.0, "axeEdgeTop");
            WeaponBox(bit, bitDepth * 0.4 + edgeBow * 0.4, -bitW * 0.42, 0.0, bitDepth * 0.4, bitW * 0.25, bitThick * 0.6, 0.0, 0.0, 0.0, "axeEdgeBot");
        }
        if (beard > 0.1)
        {
            WeaponBox(bit, bitDepth * 0.2, -bitW * 0.5, 0.0, bitDepth * 0.7, beard, bitThick * 0.8, -18.0, 0.0, 0.0, "axeBeard");
        }
        if (topHorn > 0.1)
        {
            WeaponBox(bit, bitDepth * 0.2, bitW * 0.5, 0.0, bitDepth * 0.7, topHorn, bitThick * 0.8, 18.0, 0.0, 0.0, "axeTopHorn");
        }

        if (p.AxeDoubleBit)
        {
            ModelElementData bit2 = WeaponBox(head, 0.0, -beard * 0.4, -bitThick * 1.3 - bitDepth * 0.5, bitDepth, bitW, bitThick, -sweep, 0.0, 0.0, "axeBitBack");
            WeaponBox(bit2, -bitDepth * 0.45, 0.0, 0.0, bitDepth * 0.3, bitW * 1.08, bitThick * 0.6, 0.0, 0.0, 0.0, "axeEdgeBack");
        }
        else
        {
            ModelWeaponBuildBackImplement(p, head, (WeaponPickBack)0, bitThick, isAxe: true);
        }
    }

    private void ModelWeaponBuildBackImplement(WeaponParams p, ModelElementData head, WeaponPickBack _, double thick, bool isAxe)
    {
        if (isAxe)
        {
            WeaponAxeBack back = (WeaponAxeBack)Math.Clamp(p.AxeBack, 0, WeaponAxeBackLabels.Length - 1);
            double len = Math.Max(0.5, p.AxeBackLength);
            switch (back)
            {
                case WeaponAxeBack.Spike:
                    WeaponBox(head, 0.0, 0.0, -len * 0.5 - thick, len, 0.9, 0.9, 0.0, 0.0, 0.0, "axeBackSpike");
                    WeaponBox(head, 0.0, 0.0, -len - thick, len * 0.4, 0.5, 0.5, 0.0, 45.0, 0.0, "axeBackTip");
                    break;
                case WeaponAxeBack.Hammer:
                    WeaponBox(head, 0.0, 0.0, -len * 0.4 - thick, len * 0.8, p.AxeBitWidth * 0.3, p.AxeBitWidth * 0.3, 0.0, 0.0, 0.0, "axeBackHammer");
                    break;
                case WeaponAxeBack.Pick:
                    WeaponBox(head, 0.0, 0.0, -len * 0.5 - thick, len, 0.8, 0.8, 0.0, 0.0, 14.0, "axeBackPick");
                    break;
            }
        }
    }

    // ---- Hammer ------------------------------------------------------------

    private void ModelWeaponBuildHammer(WeaponParams p, ModelElementData group, double headX)
    {
        double l = Math.Max(0.5, p.HammerHeadLength);
        double w = Math.Max(0.5, p.HammerHeadWidth);
        double h = Math.Max(0.5, p.HammerHeadHeight);
        ModelElementData head = WeaponBox(group, headX + l * 0.5, 0.0, 0.0, l, w, h, 0.0, 0.0, 0.0, "hammerHead");

        double bevel = Math.Max(0.0, p.HammerFaceBevel);
        WeaponFaceShape face = (WeaponFaceShape)Math.Clamp(p.HammerFaceShape, 0, WeaponFaceShapeLabels.Length - 1);
        WeaponBox(head, l, 0.0, 0.0, Math.Max(0.3, bevel + 0.4), w * 0.85, h * 0.85, 0.0, 0.0, 0.0, "hammerFace");
        switch (face)
        {
            case WeaponFaceShape.Rounded:
                WeaponBox(head, l, 0.0, 0.0, Math.Max(0.3, bevel + 0.4), w * 0.6, h * 1.0, 0.0, 0.0, 0.0, "hammerFaceR1");
                WeaponBox(head, l, 0.0, 0.0, Math.Max(0.3, bevel + 0.4), w * 1.0, h * 0.6, 0.0, 0.0, 0.0, "hammerFaceR2");
                break;
            case WeaponFaceShape.Octagon:
                WeaponBox(head, l, 0.0, 0.0, Math.Max(0.3, bevel + 0.3), w * 0.65, h * 0.65, l * 0.0 + 45.0, 0.0, 0.0, "hammerFaceOct");
                break;
            case WeaponFaceShape.Waffle:
                for (int gx2 = 0; gx2 < 2; gx2++)
                    for (int gy = 0; gy < 2; gy++)
                        WeaponBox(head, l + 0.1, (gx2 - 0.5) * w * 0.4, (gy - 0.5) * h * 0.4, 0.3, w * 0.28, h * 0.28, 0.0, 0.0, 0.0, $"hammerWaffle{gx2}{gy}");
                break;
        }
        if (p.HammerReinforced)
        {
            WeaponBox(head, 0.0, 0.0, 0.0, l * 0.5, w * 1.1, h * 1.1, 0.0, 0.0, 0.0, "hammerCollar");
        }
        if (p.HammerCheeks)
        {
            WeaponBox(head, 0.0, w * 0.55, 0.0, l * 0.7, 0.5, h * 0.8, 0.0, 0.0, 0.0, "hammerCheek1");
            WeaponBox(head, 0.0, -w * 0.55, 0.0, l * 0.7, 0.5, h * 0.8, 0.0, 0.0, 0.0, "hammerCheek2");
        }

        WeaponHammerBack back = (WeaponHammerBack)Math.Clamp(p.HammerBack, 0, WeaponHammerBackLabels.Length - 1);
        double bl = Math.Max(0.4, p.HammerBackLength);
        switch (back)
        {
            case WeaponHammerBack.Spike:
                WeaponBox(head, -bl * 0.5, 0.0, 0.0, bl, 1.0, 1.0, 0.0, 0.0, 0.0, "hammerBack");
                WeaponBox(head, -bl, 0.0, 0.0, bl * 0.4, 0.6, 0.6, 0.0, 45.0, 0.0, "hammerBackTip");
                break;
            case WeaponHammerBack.CrossPeen:
                WeaponBox(head, -bl * 0.5, 0.0, 0.0, bl, w * 0.9, 0.7, 0.0, 0.0, 0.0, "hammerBack");
                break;
            case WeaponHammerBack.Claw:
                WeaponBox(head, -bl * 0.5, h * 0.3, 0.0, bl, 0.7, w * 0.7, 0.0, 0.0, -18.0, "hammerClaw1");
                WeaponBox(head, -bl * 0.5, -h * 0.3, 0.0, bl, 0.7, w * 0.7, 0.0, 0.0, 18.0, "hammerClaw2");
                break;
        }
    }

    // ---- Mace --------------------------------------------------------------

    private void ModelWeaponBuildMace(WeaponParams p, ModelElementData group, double headX)
    {
        WeaponMaceHead form = (WeaponMaceHead)Math.Clamp(p.MaceHead, 0, WeaponMaceHeadLabels.Length - 1);
        double size = Math.Max(0.8, p.MaceHeadSize);
        if (p.MaceNeck)
        {
            WeaponBox(group, headX + 0.3, 0.0, 0.0, 0.8, size * 0.55, size * 0.55, 0.0, 0.0, 0.0, "maceNeck");
        }
        ModelElementData head;
        switch (form)
        {
            case WeaponMaceHead.Cylinder:
                head = WeaponBox(group, headX + size * 0.6, 0.0, 0.0, size * 1.2, size * 0.9, size * 0.9, 0.0, 0.0, 0.0, "maceHead");
                break;
            case WeaponMaceHead.Box:
                head = WeaponBox(group, headX + size * 0.5, 0.0, 0.0, size, size, size, 0.0, 0.0, 0.0, "maceHead");
                break;
            case WeaponMaceHead.Cross:
                head = WeaponBox(group, headX + size * 0.5, 0.0, 0.0, size * 0.9, size * 0.6, size * 0.6, 0.0, 0.0, 0.0, "maceHead");
                break;
            default: // Ball
                head = WeaponBox(group, headX + size * 0.5, 0.0, 0.0, size, size, size, 0.0, 0.0, 0.0, "maceHead");
                WeaponBox(head, size * 0.5, 0.0, 0.0, size * 0.78, size * 1.06, size * 0.7, 0.0, 0.0, 0.0, "maceRound1");
                WeaponBox(head, size * 0.5, 0.0, 0.0, size * 0.78, size * 0.7, size * 1.06, 0.0, 0.0, 0.0, "maceRound2");
                break;
        }

        int count = Math.Clamp(p.FlangeCount, 0, 12);
        int rows = Math.Clamp(p.FlangeRows, 1, 4);
        WeaponFlangeType ft = (WeaponFlangeType)Math.Clamp(p.FlangeType, 0, WeaponFlangeTypeLabels.Length - 1);
        double flen = Math.Max(0.3, p.FlangeLength);
        double fwid = Math.Max(0.2, p.FlangeWidth);
        double cx = size * 0.5;
        for (int row = 0; row < rows; row++)
        {
            double rx = rows == 1 ? cx : WeaponLerp(size * 0.15, size * 0.85, (double)row / (rows - 1));
            double rowTwist = row * p.FlangeTwist;   // spiral the rows for a twisted-flange mace
            for (int i = 0; i < count; i++)
            {
                double ang = i * (360.0 / Math.Max(1, count)) + rowTwist;
                double dirY = Math.Cos(ang * Math.PI / 180.0);
                double dirZ = Math.Sin(ang * Math.PI / 180.0);
                double oy = dirY * (size * 0.5 + flen * 0.5);
                double oz = dirZ * (size * 0.5 + flen * 0.5);
                string fname = $"flange{row + 1}_{i + 1}";
                switch (ft)
                {
                    case WeaponFlangeType.Flange:
                        WeaponBox(head, rx, oy, oz, fwid, flen + size * 0.4, flen + size * 0.4, 0.0, ang, 0.0, fname);
                        break;
                    case WeaponFlangeType.Stud:
                        WeaponBox(head, rx, oy * 0.7, oz * 0.7, fwid, fwid, fwid, 0.0, ang, 45.0, fname);
                        break;
                    case WeaponFlangeType.Spike:
                        WeaponBox(head, rx, oy, oz, flen, fwid * 0.7, fwid * 0.7, 0.0, ang, 0.0, fname);
                        break;
                    case WeaponFlangeType.Knob:
                        WeaponBox(head, rx, oy * 0.8, oz * 0.8, fwid * 1.2, fwid * 1.2, fwid * 1.2, 0.0, 0.0, 0.0, fname);
                        break;
                }
            }
        }

        if (p.MaceTopSpike)
        {
            double sl = Math.Max(0.6, p.MaceTopSpikeLength);
            WeaponBox(head, size * 0.5 + sl * 0.5, 0.0, 0.0, sl, fwid, fwid, 0.0, 0.0, 0.0, "maceTopSpike");
            WeaponBox(head, size * 0.5 + sl, 0.0, 0.0, sl * 0.4, fwid * 0.7, fwid * 0.7, 0.0, 0.0, 45.0, "maceTopSpikeTip");
        }
    }

    // ---- Spear / polearm ---------------------------------------------------

    private void ModelWeaponBuildSpear(WeaponParams p, ModelElementData group, double headX)
    {
        double socketLen = Math.Max(0.5, p.SocketLength);
        ModelElementData socket = WeaponBox(group, headX + socketLen * 0.5, 0.0, 0.0, socketLen, p.GripWidth + 0.8, p.GripDepth + 0.8, 0.0, 0.0, 0.0, "socket");
        double baseX = headX + socketLen;

        // Reinforcing bands around the socket.
        int socketRings = Math.Clamp(p.SocketRings, 0, 5);
        for (int i = 0; i < socketRings; i++)
        {
            double rt = socketRings == 1 ? 0.5 : (double)i / (socketRings - 1);
            WeaponBox(socket, WeaponLerp(socketLen * 0.2, socketLen * 0.8, rt), 0.0, 0.0, 0.45, p.GripWidth + 1.3, p.GripDepth + 1.3, 0.0, 0.0, 0.0, $"socketRing{i + 1}");
        }

        if (p.SpearCurvedBlade)
        {
            // Naginata / glaive: a curved blade off the shaft instead of a straight head.
            ModelWeaponBuildPolearmBlade(p, group, baseX);
        }
        else
        {
            int prongs = Math.Clamp(p.SpearProngs, 1, 5);
            double spread = Math.Max(0.0, p.SpearProngSpread);
            for (int i = 0; i < prongs; i++)
            {
                double oy = prongs == 1 ? 0.0 : WeaponLerp(-spread, spread, (double)i / (prongs - 1));
                string name = prongs == 1 ? "spearHead" : $"prong{i + 1}";
                ModelWeaponBuildSpearTip(p, group, baseX, oy, name);
                if (p.SpearFuller)
                {
                    WeaponBox(group, baseX + p.SpearHeadLength * 0.4, oy, 0.0, p.SpearHeadLength * 0.6, p.SpearHeadWidth * 0.22, p.SpearHeadThickness * 0.5, 0.0, 0.0, 0.0, $"{name}Fuller");
                }
            }
        }

        if (p.SpearWings)
        {
            double wl = Math.Max(0.5, p.SpearWingLength);
            WeaponBox(socket, socketLen * 0.2, p.SpearHeadWidth * 0.5 + wl * 0.4, 0.0, 1.2, wl, 0.6, 0.0, 0.0, 35.0, "spearWingUp");
            WeaponBox(socket, socketLen * 0.2, -p.SpearHeadWidth * 0.5 - wl * 0.4, 0.0, 1.2, wl, 0.6, 0.0, 0.0, -35.0, "spearWingDown");
        }

        if (p.SpearSideAxe)
        {
            ModelElementData axe = WeaponBox(socket, 0.0, 0.0, p.GripDepth * 0.5 + 1.6, 1.2, p.SpearHeadWidth * 1.4, 1.0, 0.0, 0.0, 0.0, "spearAxe");
            WeaponBox(axe, 0.6, 0.0, 0.0, 1.6, p.SpearHeadWidth * 1.5, 0.6, 0.0, 0.0, 0.0, "spearAxeEdge");
        }

        if (p.SpearBackHook)
        {
            double hl = Math.Max(0.8, p.SpearBackHookLength);
            ModelElementData hook = WeaponBox(socket, 0.0, 0.0, -p.GripDepth * 0.5 - hl * 0.4, hl, 0.8, 0.8, 0.0, 0.0, 0.0, "spearHook");
            WeaponBox(hook, hl * 0.4, hl * 0.3, 0.0, hl * 0.7, 0.7, 0.7, 0.0, 0.0, 40.0, "spearHookTip");
        }
    }

    private void ModelWeaponBuildSpearTip(WeaponParams p, ModelElementData group, double baseX, double oy, string name)
    {
        WeaponSpearHead form = (WeaponSpearHead)Math.Clamp(p.SpearHead, 0, WeaponSpearHeadLabels.Length - 1);
        double len = Math.Max(1.0, p.SpearHeadLength);
        double wid = Math.Max(0.3, p.SpearHeadWidth);
        double thick = Math.Max(0.2, p.SpearHeadThickness);

        switch (form)
        {
            case WeaponSpearHead.Leaf:
                WeaponBox(group, baseX + len * 0.35, oy, 0.0, len * 0.6, wid, thick, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len * 0.78, oy, 0.0, len * 0.5, wid * 0.6, thick, 0.0, 0.0, 0.0, name + "Mid");
                WeaponBox(group, baseX + len, oy, 0.0, len * 0.3, wid * 0.5, thick, 0.0, 0.0, 45.0, name + "Tip");
                break;
            case WeaponSpearHead.Triangle:
                WeaponBox(group, baseX + len * 0.4, oy, 0.0, len * 0.8, wid, thick, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len * 0.9, oy, 0.0, len * 0.4, wid * 0.5, thick, 0.0, 0.0, 45.0, name + "Tip");
                break;
            case WeaponSpearHead.Diamond:
                WeaponBox(group, baseX + len * 0.4, oy, 0.0, len * 0.7, wid, thick * 1.6, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len * 0.85, oy, 0.0, len * 0.4, wid * 0.5, thick * 1.6, 0.0, 0.0, 45.0, name + "Tip");
                break;
            case WeaponSpearHead.Needle:
                WeaponBox(group, baseX + len * 0.5, oy, 0.0, len, wid * 0.5, wid * 0.5, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len, oy, 0.0, len * 0.3, wid * 0.35, wid * 0.35, 0.0, 45.0, 0.0, name + "Tip");
                break;
            case WeaponSpearHead.Barbed:
                WeaponBox(group, baseX + len * 0.45, oy, 0.0, len * 0.9, wid * 0.7, thick, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len * 0.95, oy, 0.0, len * 0.3, wid * 0.4, thick, 0.0, 0.0, 45.0, name + "Tip");
                WeaponBox(group, baseX + len * 0.3, oy + wid * 0.5, 0.0, 1.4, wid * 0.5, thick, 0.0, 0.0, -40.0, name + "BarbUp");
                WeaponBox(group, baseX + len * 0.3, oy - wid * 0.5, 0.0, 1.4, wid * 0.5, thick, 0.0, 0.0, 40.0, name + "BarbDown");
                break;
            case WeaponSpearHead.Lance:
                WeaponBox(group, baseX + len * 0.4, oy, 0.0, len * 0.8, wid, wid, 0.0, 0.0, 0.0, name);
                WeaponBox(group, baseX + len * 0.9, oy, 0.0, len * 0.4, wid * 0.5, wid * 0.5, 0.0, 45.0, 0.0, name + "Tip");
                break;
        }

        if (p.SpearMidrib && form != WeaponSpearHead.Needle && form != WeaponSpearHead.Lance)
        {
            WeaponBox(group, baseX + len * 0.45, oy, 0.0, len * 0.8, wid * 0.2, thick * 1.8, 0.0, 0.0, 0.0, name + "Rib");
        }
    }

    private void ModelWeaponBuildPolearmBlade(WeaponParams p, ModelElementData group, double baseX)
    {
        int segs = Math.Clamp(p.BladeSegments, 2, 8);
        double len = Math.Max(3.0, p.SpearHeadLength * 2.2);
        double segLen = len / segs;
        double baseW = Math.Max(0.6, p.SpearHeadWidth * 1.3);
        double thick = Math.Max(0.2, p.SpearHeadThickness);
        double curve = Math.Max(2.0, p.BladeCurve <= 0 ? 6.0 : p.BladeCurve);

        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < segs; k++)
        {
            double t = (double)k / (segs - 1);
            sizes.Add([segLen, WeaponLerp(baseW, baseW * 0.4, t), thick]);
            bends.Add(k == 0 ? 0.0 : curve);
        }
        ModelCreatureChain(group, [baseX, 0.0, 0.0], 0.0, 0.0, 0.0, 0, 1, sizes, bends, 2, "polearmBlade", out ModelElementData tip);
        WeaponBox(tip, tip.To[0] - tip.From[0] + segLen * 0.3, 0.0, 0.0, segLen * 0.6, baseW * 0.35, thick, 0.0, 0.0, 45.0, "polearmBladeTip");
    }

    // ---- Pick --------------------------------------------------------------

    private void ModelWeaponBuildPick(WeaponParams p, ModelElementData group, double headX)
    {
        double hw = Math.Max(0.6, p.PickHeadWidth);
        ModelElementData head = WeaponBox(group, headX + 0.4, 0.0, 0.0, 2.0, hw + 0.6, hw + 0.6, 0.0, 0.0, 0.0, "pickHead");

        double sl = Math.Max(1.0, p.PickSpikeLength);
        double curve = p.PickSpikeCurve;
        int count = Math.Clamp(p.PickSpikeCount, 1, 2);
        // Front spike reaches forward (+Z, swing direction) and hooks down.
        ModelElementData front = WeaponBox(head, 0.0, 0.0, sl * 0.5, sl, hw, hw, curve, 0.0, 0.0, "pickSpike");
        WeaponBox(front, 0.0, 0.0, sl * 0.5, sl * 0.4, hw * 0.6, hw * 0.6, curve, 0.0, 0.0, "pickSpikeTip");
        if (count == 2)
        {
            ModelElementData back = WeaponBox(head, 0.0, 0.0, -sl * 0.5, sl, hw, hw, -curve, 0.0, 0.0, "pickSpikeBack");
            WeaponBox(back, 0.0, 0.0, -sl * 0.5, sl * 0.4, hw * 0.6, hw * 0.6, -curve, 0.0, 0.0, "pickSpikeBackTip");
        }
        else
        {
            WeaponPickBack back = (WeaponPickBack)Math.Clamp(p.PickBack, 0, WeaponPickBackLabels.Length - 1);
            double bl = Math.Max(0.6, p.PickBackLength);
            switch (back)
            {
                case WeaponPickBack.Spike:
                    WeaponBox(head, 0.0, 0.0, -bl * 0.5, bl, hw * 0.8, hw * 0.8, 0.0, 0.0, 0.0, "pickBack");
                    WeaponBox(head, 0.0, 0.0, -bl, bl * 0.4, hw * 0.5, hw * 0.5, 0.0, 45.0, 0.0, "pickBackTip");
                    break;
                case WeaponPickBack.Adze:
                    WeaponBox(head, 0.0, -bl * 0.2, -bl * 0.5, bl, hw * 1.6, hw * 0.7, 0.0, 0.0, 0.0, "pickAdze");
                    break;
                case WeaponPickBack.Hammer:
                    WeaponBox(head, 0.0, 0.0, -bl * 0.4, bl * 0.8, hw * 1.5, hw * 1.5, 0.0, 0.0, 0.0, "pickHammer");
                    break;
            }
        }
    }

    // ---- Spade / shovel ----------------------------------------------------

    private void ModelWeaponBuildSpade(WeaponParams p, ModelElementData group, double headX)
    {
        double sl = Math.Max(1.0, p.SpadeLength);
        double sw = Math.Max(1.0, p.SpadeWidth);
        double st = Math.Max(0.2, p.SpadeThickness);
        WeaponBox(group, headX + 1.0, 0.0, 0.0, 2.0, p.GripWidth + 0.4, p.GripDepth + 0.4, 0.0, 0.0, 0.0, "spadeSocket");
        ModelElementData scoop = WeaponBox(group, headX + 2.0 + sl * 0.5, 0.0, 0.0, sl, sw, st, 0.0, 0.0, 0.0, "spadeScoop");

        // Curl the side rails forward into a scoop.
        double curl = p.SpadeCurl;
        WeaponBox(scoop, 0.0, sw * 0.5, st, sl, 0.6, st * 2.0, 0.0, 0.0, 0.0, "spadeRailUp");
        WeaponBox(scoop, 0.0, -sw * 0.5, st, sl, 0.6, st * 2.0, 0.0, 0.0, 0.0, "spadeRailDown");
        _ = curl;

        if (p.SpadePointed)
        {
            WeaponBox(scoop, sl * 0.5, 0.0, 0.0, sl * 0.4, sw * 0.6, st, 0.0, 0.0, 45.0, "spadeTip");
        }
        if (p.SpadeStep)
        {
            WeaponBox(scoop, -sl * 0.5, sw * 0.45, 0.0, 1.0, 0.8, st * 3.0, 0.0, 0.0, 0.0, "spadeStepUp");
            WeaponBox(scoop, -sl * 0.5, -sw * 0.45, 0.0, 1.0, 0.8, st * 3.0, 0.0, 0.0, 0.0, "spadeStepDown");
        }
    }

    // ---- Hoe / adze --------------------------------------------------------

    private void ModelWeaponBuildHoe(WeaponParams p, ModelElementData group, double headX)
    {
        WeaponBox(group, headX + 0.8, 0.0, 0.0, 1.6, p.GripWidth + 0.4, p.GripDepth + 0.4, 0.0, 0.0, 0.0, "hoeHead");
        double bw = Math.Max(0.6, p.HoeBladeWidth);
        double bl = Math.Max(0.6, p.HoeBladeLength);
        double drop = p.HoeBladeDrop;
        int prongs = Math.Clamp(p.HoeProngs, 1, 4);
        double spread = Math.Max(0.0, p.HoeProngSpread);

        if (prongs == 1)
        {
            // A flat blade bent perpendicular to the haft (drop ~70 deg about Z).
            WeaponBox(group, headX + 1.6, 0.0, 0.0, bl, bw, 0.5, 0.0, 0.0, drop, "hoeBlade");
        }
        else
        {
            for (int i = 0; i < prongs; i++)
            {
                double oy = WeaponLerp(-spread, spread, (double)i / (prongs - 1));
                WeaponBox(group, headX + 1.6, oy, 0.0, bl, 0.6, 0.6, 0.0, 0.0, drop, $"hoeProng{i + 1}");
            }
        }
    }

    // ---- Scythe / sickle ---------------------------------------------------

    private void ModelWeaponBuildScythe(WeaponParams p, ModelElementData group, double headX)
    {
        int segs = Math.Clamp(p.ScytheSegments, 2, 10);
        double len = Math.Max(2.0, p.ScytheLength);
        double segLen = len / segs;
        double w = Math.Max(0.4, p.ScytheWidth);
        double thick = Math.Max(0.15, p.ScytheThickness);
        double curve = p.ScytheCurve;

        // The blade mounts almost perpendicular to the snath (mount deg about Z), then curves along its length.
        ModelElementData tang = WeaponBox(group, headX + 0.6, 0.0, 0.0, 1.2, 1.0, 1.0, 0.0, 0.0, 0.0, "scytheTang");

        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < segs; k++)
        {
            double t = (double)k / (segs - 1);
            sizes.Add([segLen, WeaponLerp(w, w * 0.3, t), thick]);
            bends.Add(k == 0 ? 0.0 : curve);
        }
        ModelCreatureChain(tang, [0.6, 0.0, 0.0], 0.0, 0.0, p.ScytheMount, 0, 1, sizes, bends, 2, "scytheBlade", out ModelElementData tip);
        WeaponBox(tip, tip.To[0] - tip.From[0] + segLen * 0.3, 0.0, 0.0, segLen * 0.6, w * 0.25, thick, 0.0, 0.0, 45.0, "scytheBladeTip");
    }

    // ---- Bow ---------------------------------------------------------------

    private void ModelWeaponBuildBow(WeaponParams p, ModelElementData group)
    {
        WeaponBowStyle style = (WeaponBowStyle)Math.Clamp(p.BowStyle, 0, WeaponBowStyleLabels.Length - 1);
        double riser = Math.Max(1.0, p.BowRiserLength);
        double lw = Math.Max(0.3, p.BowLimbWidth);
        double lt = Math.Max(0.3, p.BowLimbThickness);
        WeaponBox(group, 0.0, 0.0, 0.0, riser, lw + 0.4, lt + 0.6, 0.0, 0.0, 0.0, "bowRiser");

        int segs = Math.Clamp(p.BowLimbSegments, 2, 8);
        double limbLen = Math.Max(2.0, p.BowLimbLength);
        double segLen = limbLen / segs;
        double recurve = style == WeaponBowStyle.Straight ? 0.0 : p.BowRecurve;
        if (style == WeaponBowStyle.Longbow) recurve = Math.Max(2.0, recurve * 0.5);

        ModelWeaponBuildBowLimb(p, group, riser * 0.5, +1, segs, segLen, lw, lt, recurve, "bowLimbUpper");
        ModelWeaponBuildBowLimb(p, group, riser * 0.5, -1, segs, segLen, lw, lt, recurve, "bowLimbLower");

        if (p.BowGripWrap)
        {
            WeaponBox(group, 0.0, 0.0, lt * 0.5 + 0.2, riser * 0.7, lw + 0.5, 0.4, 0.0, 0.0, 0.0, "bowGrip");
        }
        if (p.BowArrowRest)
        {
            WeaponBox(group, 0.0, riser * 0.5 + 0.3, lt * 0.5 + 0.3, 0.5, 0.8, 1.0, 0.0, 0.0, 0.0, "bowRest");
        }

        if (p.BowString)
        {
            // A thin string from each nock to the centre front; two straight segments approximate the brace.
            double frontZ = lt * 0.5 + 0.6;
            WeaponBox(group, 0.0, limbLen * 0.45, frontZ * 0.5, 0.2, limbLen * 0.95, 0.2, 0.0, 0.0, 0.0, "bowString1");
            WeaponBox(group, 0.0, -limbLen * 0.45, frontZ * 0.5, 0.2, limbLen * 0.95, 0.2, 0.0, 0.0, 0.0, "bowString2");
        }
    }

    private ModelElementData ModelWeaponBuildBowLimb(WeaponParams p, ModelElementData group, double attachY, int sign,
        int segs, double segLen, double lw, double lt, double recurve, string name)
    {
        double tipMul = Math.Clamp((double)p.BowLimbTaper, 0.1, 1.2);
        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < segs; k++)
        {
            double t = (double)k / (segs - 1);
            sizes.Add([segLen, WeaponLerp(lw, lw * tipMul, t), WeaponLerp(lt, lt * tipMul, t)]);
            bends.Add(k == 0 ? 0.0 : recurve);
        }
        // Limbs grow along Y from the riser, bending forward (about X) for the recurve.
        ModelCreatureChain(group, [0.0, attachY * sign, 0.0], 0.0, 0.0, 0.0, 1, sign, sizes, bends, 0, name, out ModelElementData tip);
        WeaponBox(tip, 0.0, sign * (tip.To[1] - tip.From[1]) * 0.5, 0.0, lw * 0.6, segLen * 0.4, lt * 0.4, 0.0, 0.0, 0.0, name + "Nock");
        return tip;
    }

    // ---- Staff / club ------------------------------------------------------

    private void ModelWeaponBuildStaff(WeaponParams p, ModelElementData group, double gripLen)
    {
        if (p.StaffEndKnobs)
        {
            double k = Math.Max(0.4, p.StaffKnobSize);
            WeaponBox(group, 0.3, 0.0, 0.0, k, k, k, 0.0, 0.0, 0.0, "staffKnobFront");
            WeaponBox(group, -gripLen + 0.3, 0.0, 0.0, k, k, k, 0.0, 0.0, 0.0, "staffKnobBack");
        }

        if (p.ClubHead)
        {
            double hs = Math.Max(0.6, p.ClubHeadSize);
            double hl = Math.Max(1.0, p.ClubHeadLength);
            ModelElementData head = WeaponBox(group, hl * 0.5, 0.0, 0.0, hl, hs, hs, 0.0, 0.0, 0.0, "clubHead");
            WeaponBox(head, hl * 0.5, 0.0, 0.0, hl * 0.4, hs * 0.7, hs * 0.7, 0.0, 0.0, 0.0, "clubHeadCap");
            int studs = Math.Clamp(p.ClubStuds, 0, 12);
            for (int i = 0; i < studs; i++)
            {
                double ang = i * (360.0 / Math.Max(1, studs));
                double oy = Math.Cos(ang * Math.PI / 180.0) * hs * 0.55;
                double oz = Math.Sin(ang * Math.PI / 180.0) * hs * 0.55;
                WeaponBox(head, hl * 0.3, oy, oz, 0.8, 0.8, 0.8, 0.0, ang, 45.0, $"clubStud{i + 1}");
            }
        }
    }

    // ---- Textures ----------------------------------------------------------

    private string ModelWeaponResolveTexture(string requested, string fallback, string? secondFallback = null)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        if (!string.IsNullOrWhiteSpace(secondFallback)) return secondFallback;
        return _modelDoc?.Textures.FirstOrDefault()?.Code ?? "metal";
    }

    /// <summary>Coarse material region a generated element name belongs to: handle (wood/leather), accent
    /// (cord, fuller, gem), or metal (everything structural).</summary>
    private static string ModelWeaponPartCategory(string name)
    {
        // Metal fittings on an otherwise wooden part stay metal (ferrule / collar).
        if (name.Contains("Collar", StringComparison.Ordinal)) return "metal";

        if (name.StartsWith("grip", StringComparison.Ordinal) || name.StartsWith("shaft", StringComparison.Ordinal) ||
            name.StartsWith("haft", StringComparison.Ordinal) || name.StartsWith("snath", StringComparison.Ordinal) ||
            name.StartsWith("riser", StringComparison.Ordinal) || name.StartsWith("bowRiser", StringComparison.Ordinal) ||
            name.StartsWith("bowLimb", StringComparison.Ordinal) || name.StartsWith("bowGrip", StringComparison.Ordinal) ||
            name.StartsWith("staff", StringComparison.Ordinal) || name.StartsWith("club", StringComparison.Ordinal)) return "handle";

        if (name.StartsWith("wrap", StringComparison.Ordinal) || name.StartsWith("fuller", StringComparison.Ordinal) ||
            name.StartsWith("menuki", StringComparison.Ordinal) || name.StartsWith("gem", StringComparison.Ordinal) ||
            name.StartsWith("bowString", StringComparison.Ordinal) || name.EndsWith("Nock", StringComparison.Ordinal) ||
            name.StartsWith("binding", StringComparison.Ordinal)) return "accent";

        return "metal";
    }

    private void ModelWeaponAssignFaces(ModelElementData root, string metal, string handle, string accent)
    {
        foreach (ModelElementData node in root.EnumerateSubtree())
        {
            if (ReferenceEquals(node, root)) continue;
            if (node.SizeX <= 0.0001 || node.SizeY <= 0.0001 || node.SizeZ <= 0.0001) continue;

            string code = ModelWeaponPartCategory(node.Name) switch
            {
                "handle" => handle,
                "accent" => accent,
                _ => metal
            };
            for (int face = 0; face < 6; face++)
            {
                node.Faces[face] = new ModelFaceData { Texture = code };
                ModelCreatureAutoUvFace(node, face);
            }
        }
    }

    private void ModelWeaponEnsureTextures(ModelElementData root, string baseCode)
    {
        if (_modelDoc == null) return;
        string basePath = _modelDoc.Textures.FirstOrDefault(texture => string.Equals(texture.Code, baseCode, StringComparison.Ordinal))?.Path
            ?? _modelDoc.Textures.FirstOrDefault()?.Path
            ?? "";

        HashSet<string> existing = new(_modelDoc.Textures.Select(texture => texture.Code), StringComparer.Ordinal);
        foreach (string code in ModelGeneratedTextureCodes(root))
        {
            if (existing.Add(code))
            {
                _modelDoc.Textures.Add(new ModelTextureEntry { Code = code, Path = basePath });
            }
        }
    }

    // ---- Commit / ghost ----------------------------------------------------

    private void ModelCommitWeapon()
    {
        if (_modelDoc == null || _weaponPreviewRoot == null) return;

        ModelBeginEdit();
        ModelElementData root = _weaponPreviewRoot.CloneSubtree();
        root.Name = ModelGenerateElementName(ModelSanitizeFileName(_weaponParams.Code) is { Length: > 0 } code ? code : "weapon");
        string baseName = root.Name;
        foreach (ModelElementData node in root.EnumerateSubtree())
        {
            if (ReferenceEquals(node, root)) continue;
            node.Name = $"{baseName}_{node.Name}";
        }
        _modelDoc.Roots.Add(root);

        int texturesBefore = _modelDoc.Textures.Count;
        string baseCode = ModelWeaponResolveTexture(_weaponParams.Texture, "metal");
        ModelWeaponEnsureTextures(root, baseCode);
        int texturesAdded = _modelDoc.Textures.Count - texturesBefore;

        ModelSelectElement(root);
        ModelMarkChanged();
        ModelEndEdit("Add weapon");
        _weaponStatus = texturesAdded > 0
            ? $"Added {root.Name} ({ModelCreatureElementCount(root)} elements, {texturesAdded} texture slot(s))."
            : $"Added {root.Name} ({ModelCreatureElementCount(root)} elements).";
        _modelStatus = _weaponStatus;
    }

    private void DrawWeaponGhost(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (!_weaponWindowOpen || _weaponPreviewRoot == null || !string.IsNullOrEmpty(_weaponPreviewError)) return;

        uint ghost = ImGui.ColorConvertFloat4ToU32(new NVector4(0.45f, 0.78f, 0.98f, 0.7f));
        foreach (ModelElementData element in _weaponPreviewRoot.EnumerateSubtree())
        {
            if (element.SizeX <= 0.0001 || element.SizeY <= 0.0001 || element.SizeZ <= 0.0001) continue;
            if (element.NonCuboid?.Editable == true)
            {
                DrawModelMeshWireOverlay(drawList, camera, element, ghost, 1.2f, drawVertices: false);
                continue;
            }
            var matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            foreach ((int a, int b) in ModelBoxEdges)
            {
                DrawModelViewportLine(drawList, camera, corners[a], corners[b], ghost, 1.2f);
            }
        }
    }
}
