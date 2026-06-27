using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

// Clothing / wearable generator: builds a wearable shape the way Vintage Story actually composes one, plus a
// drop-in wearable item JSON. Unlike the creature or player-model generators (which build a standalone
// skeleton), a wearable is NOT a self-contained shape - at render time the game STEP-PARENTS the garment's
// root elements onto the wearer entity's skeleton bones (see CollectibleBehaviorWearableAttachment.
// genFullBodyMesh -> Shape.StepParentShape). So every garment piece is authored in the wearer's (seraph)
// frame as a face-less anchor element carrying `stepParentName = "<bone>"`, with the real geometry nested
// beneath it - exactly the structure vanilla/мод clothing shapes use (e.g. a tabard anchors "Tabard-Upper"
// to UpperTorso, "Tabard-Lower" to LowerTorso, legs to UpperFootR/L).
//
// To make fit visible while editing, the preview builds the full wearer rig (the same seraph / digitigrade
// base the player-model generator reproduces) and hangs the garment anchors on the matching bones, so the
// ghost shows body + clothes composed through the real bone transforms. On Create only the garment anchors
// are emitted (as step-parented roots) - never the body, which the wearer entity supplies at runtime.
//
// The slot (clothescategory -> EnumCharacterDressType) is chosen independently and written into the item
// JSON; all 15 vanilla slots are offered, including the three armor slots with protectionModifiers. Coverage
// is region-based (head, face, neck, chest, belly, shoulders, arms, hands, waist, legs, feet, cape) so any
// combination - a skullcap, a full plate suit, a hooded cloak - is reachable, and archetype presets wire
// sensible region/size/attribute defaults for the common garments.
//
// Coordinate frame (seraph, 16 = 1 block), expressed in each bone's LOCAL space (relative to the bone's
// From corner, which is where step-parenting roots the anchor): X = back(+)/front(-, the wearer faces -X),
// Y = the bone's long/up axis (up for torso & head, down-the-limb for arms & legs), Z = lateral.
public sealed partial class DebugWindowManager
{
    // The 15 vanilla clothescategory slots (EnumCharacterDressType), label + JSON code, in a friendly order.
    private static readonly (string Label, string Code)[] ClothingSlots =
    [
        ("Head (hats / hoods)", "head"),
        ("Face (masks / visors)", "face"),
        ("Neck (scarves / gorgets)", "neck"),
        ("Shoulder (pauldrons / capes)", "shoulder"),
        ("Upper body (shirts / jackets)", "upperbody"),
        ("Upper body over (coats / robes)", "upperbodyover"),
        ("Arm (bracers / sleeves)", "arm"),
        ("Hand (gloves / gauntlets)", "hand"),
        ("Waist (belts / sashes)", "waist"),
        ("Lower body (trousers / skirts)", "lowerbody"),
        ("Foot (boots / shoes)", "foot"),
        ("Emblem (badges / brooches)", "emblem"),
        ("Armor: head (helmets)", "armorhead"),
        ("Armor: body (cuirass)", "armorbody"),
        ("Armor: legs (greaves)", "armorlegs")
    ];

    private static readonly string[] ClothingSlotLabels = ClothingSlots.Select(slot => slot.Label).ToArray();

    private enum ClothingArchetype
    {
        Hood, Cap, Helmet, Mask, Necklace, Scarf, Shirt, Jacket, Coat, Robe, Tabard,
        Cuirass, Pauldrons, Bracers, Gloves, Gauntlets, Belt, Trousers, Skirt, Greaves, Boots, Cloak, Emblem,
        WildwoodCloak, DruidRobe, PlatedArmor, Brigand, RegalMantle, RaggedShroud
    }

    private static readonly string[] ClothingArchetypeLabels =
    [
        "Hood", "Cap / hat", "Helmet (armor)", "Mask", "Necklace", "Scarf", "Shirt / tunic", "Jacket",
        "Coat (long)", "Robe", "Tabard", "Cuirass (armor)", "Pauldrons", "Bracers", "Gloves", "Gauntlets",
        "Belt", "Trousers", "Skirt / kilt", "Greaves (armor)", "Boots", "Cloak / cape", "Emblem / badge",
        "Wildwood cloak (tattered + branches)", "Druid robe (vines + leaves)", "Plated armor (full)",
        "Brigand (studded leather)", "Regal mantle (gems + feathers)", "Ragged shroud (heavy wear)"
    ];

    private enum ClothingStyle { Cloth, Leather, Plate }

    private static readonly string[] ClothingStyleLabels = ["Cloth (thin, drapes)", "Leather (medium)", "Plate (thick, boxy)"];

    private enum ClothingHeadStyle { Skullcap, Cap, Hood, Helmet, TallHat }

    private static readonly string[] ClothingHeadStyleLabels = ["Skullcap", "Cap + brim", "Hood", "Helmet (full)", "Tall hat"];

    private enum ClothingFaceStyle { Full, Lower, Upper, Goggles }

    private static readonly string[] ClothingFaceStyleLabels = ["Full mask", "Lower (muzzle)", "Upper (visor band)", "Goggles"];

    // Where a surface ornament (branches, studs, gems, feathers, spikes, leaves) scatters.
    private enum ClothingSurface { Cape, Shoulders, Chest, Back, Hood, Sleeves, Belt, Whole }

    private static readonly string[] ClothingSurfaceLabels =
        ["Cape", "Shoulders", "Chest (front)", "Back", "Hood / head", "Sleeves", "Belt / hips", "Whole torso"];

    // Which edge an edge treatment (tatters, fringe, fur trim) runs along.
    private enum ClothingEdge { Cape, Skirt, Hem, Sleeves, Collar, Cape_Skirt }

    private static readonly string[] ClothingEdgeLabels =
        ["Cape hem", "Skirt hem", "Tunic hem", "Sleeve cuffs", "Collar / neck", "Cape + skirt"];

    private enum ClothingCollarStyle { None, Band, High, Popped, Ruff, Cowl }

    private static readonly string[] ClothingCollarStyleLabels = ["None", "Band", "High", "Popped", "Ruff", "Cowl"];

    private enum ClothingPlating { None, Scale, Lamellar, Plate, Brigandine }

    private static readonly string[] ClothingPlatingLabels = ["None", "Scale", "Lamellar", "Plate", "Brigandine (studded)"];

    private static readonly string[] ClothingPlatePlacementLabels = ["Chest", "Back", "Chest + back", "Shoulders", "Legs", "Arms", "Whole"];

    private sealed class ClothingParams
    {
        // Identity / item JSON
        public string Code = "customclothing";
        public string Name = "Custom Clothing";
        public string Texture = "";
        public string TrimTexture = "";
        public int BaseShapeIndex = (int)PlayerModelBaseShape.Seraph;
        public int CategoryIndex = 5;            // upperbodyover by default
        public int StyleIndex = (int)ClothingStyle.Cloth;
        public string WearerEntityCode = "player";
        public int Seed = 1;

        // Global fit
        public float Looseness = 0.6f;           // standoff from the body surface (shape units)
        public float Thickness = 0.6f;           // garment wall thickness added on top of looseness

        // ---- Region coverage toggles + per-region parameters ----
        public bool ClotheHead;
        public int HeadStyle = (int)ClothingHeadStyle.Hood;
        public float Brim = 3f;
        public float CrownHeight = 5f;
        public bool HoodDown;                    // hood lowered onto the back instead of raised over the head

        public bool ClotheFace;
        public int FaceStyle = (int)ClothingFaceStyle.Full;

        public bool ClotheNeck;
        public float CollarHeight = 2.5f;
        public bool Scarf;
        public float ScarfLength = 8f;

        public bool ClotheChest;
        public bool OpenFront;                   // lapels / open jacket front
        public bool Lapels;

        public bool ClotheBelly;
        public float SkirtLength;                // coat-tails / robe skirt hanging below the pelvis (0 = none)
        public int SkirtSegments = 3;            // sway joints in the skirt
        public float SkirtFlare = 6f;

        public bool ClotheShoulders;
        public float PauldronSize = 3f;
        public int PauldronPlates = 1;

        public bool ClotheArms;
        public float SleeveLength = 1f;          // 0 = shoulder cap, 1 = full sleeve to the wrist
        public bool Cuff;

        public bool ClotheHands;
        public float GloveLength = 3f;           // up the forearm (gauntlet) past the hand

        public bool ClotheWaist;
        public float BeltWidth = 1.5f;
        public bool Buckle = true;
        public int Pouches;

        public bool ClotheLegs;
        public float LegLength = 1f;             // 0 = shorts, 1 = full to the ankle

        public bool ClotheFeet;
        public float BootHeight = 3f;            // up the shin past the foot
        public bool Sole = true;

        public bool ClotheCape;
        public float CapeLength = 18f;
        public float CapeWidth = 9f;
        public int CapeSegments = 4;             // sway joints down the cape
        public bool CapeCollar = true;

        // ---- Wearable attributes (written into the item JSON) ----
        public float Warmth = 1f;
        public float RainProtection;             // head slot only (rainProtectionPerc, 0..1)
        public int Durability = 200;
        public bool Foldable;
        public bool VisibleDamage;

        // Armor protection (armor* slots)
        public int ProtectionTier = 1;
        public float FlatDamageReduction = 0.5f;
        public float RelativeProtection = 0.15f;
        public bool HighDamageTierResistant;

        // Stat modifiers
        public float WalkSpeed;                  // e.g. -0.01 = -1%
        public float HungerRate;
        public float HealingEffectiveness;
        public float RangedAccuracy;
        public float RangedChargeSpeed;

        // ---- Decoration: a third texture for organic/jewelled accents, a shared scatter seed and global wear ----
        public string AccentTexture = "";
        public int DecorSeed = 1;
        public float Wear;                       // 0 = pristine, 1 = heavily damaged (randomly skips/shortens tatters, plates, ornaments)
        public bool Asymmetric;                  // bias scatter and paired features to one side for a worn, irregular look

        // ---- Edge treatments ----
        public bool Tatters;                     // ragged torn strips hanging off an edge (the tattered-cloak look)
        public int TatterEdge = (int)ClothingEdge.Cape;
        public int TatterCount = 9;
        public float TatterLength = 5f;
        public float TatterRaggedness = 0.6f;    // 0 = even strips, 1 = wildly varying lengths/angles
        public float TatterWidth = 1.2f;

        public bool Fringe;                      // uniform thin hanging strands (suede fringe / bullion)
        public int FringeEdge = (int)ClothingEdge.Hem;
        public int FringeCount = 14;
        public float FringeLength = 2.5f;

        public bool FurTrim;                     // a fluffy raised band along an edge
        public int FurTrimEdge = (int)ClothingEdge.Collar;
        public float FurTrimThickness = 1.6f;

        // ---- Organic protrusions ----
        public bool Branches;                    // tapering, optionally forking twigs sticking out of the garment
        public int BranchSurface = (int)ClothingSurface.Cape;
        public int BranchCount = 7;
        public float BranchLength = 7f;
        public float BranchThickness = 1.1f;
        public int BranchSegments = 3;
        public int BranchForks = 1;              // sub-branches per branch (0..3)
        public float BranchCurve = 14f;          // per-segment bend
        public float BranchSpread = 35f;         // aim jitter
        public bool BranchLeaves = true;         // a leaf cluster at each tip

        public bool Vines;                       // long thin drooping winding tendrils
        public int VineSurface = (int)ClothingSurface.Cape;
        public int VineCount = 5;
        public float VineLength = 12f;
        public float VineThickness = 0.6f;

        public bool Leaves;                      // flat leaves scattered over the surface
        public int LeafSurface = (int)ClothingSurface.Cape;
        public int LeafCount = 12;
        public float LeafSize = 2f;

        public bool Thorns;                      // short sharp spikes (thorny/brambly)
        public int ThornSurface = (int)ClothingSurface.Shoulders;
        public int ThornCount = 10;
        public float ThornLength = 2.5f;

        // ---- Hard ornaments ----
        public bool Studs;                       // rivets / studs
        public int StudSurface = (int)ClothingSurface.Chest;
        public int StudCount = 16;
        public float StudSize = 0.8f;

        public bool Gems;                        // jewels / cabochons
        public int GemSurface = (int)ClothingSurface.Chest;
        public int GemCount = 5;
        public float GemSize = 1.2f;

        public bool Spikes;                      // armor spikes
        public int SpikeSurface = (int)ClothingSurface.Shoulders;
        public int SpikeCount = 6;
        public float SpikeLength = 4f;
        public float SpikeSize = 1.2f;

        public bool Feathers;                    // a fan of flat feathers / plume
        public int FeatherSurface = (int)ClothingSurface.Hood;
        public int FeatherCount = 6;
        public float FeatherLength = 7f;
        public float FeatherSpread = 60f;

        // ---- Layering ----
        public bool Mantle;                      // a capelet: a fan of draped panels over the shoulders
        public int MantleCount = 8;
        public float MantleLength = 6f;
        public float MantleFlare = 2f;
        public bool MantleTatters;

        public int Layers = 1;                   // extra offset cloth layers on the torso/cape (1 = none)
        public float LayerOffset = 0.8f;

        // ---- Armor plating ----
        public int Plating = (int)ClothingPlating.None;
        public int PlatePlacement;               // ClothingPlatePlacementLabels index
        public int PlateRows = 4;
        public int PlateCols = 4;
        public float PlateSize = 1.6f;

        // ---- Straps ----
        public bool Straps;
        public int StrapSurface = (int)ClothingSurface.Chest;
        public int StrapCount = 1;
        public bool StrapBuckles = true;

        // ---- Collar / hood expansion ----
        public int CollarStyle = (int)ClothingCollarStyle.Band;
        public bool HoodPuff;                    // puffy bunched sides on a raised hood
        public bool HoodCowl;                    // a long cowl drape down the back
        public float HoodPointed;                // a drooping pointed tip (0 = none)
        public bool HoodChestDrape;              // a capelet onto the chest (a lowered hood / cowl)

        public ClothingParams Clone() => (ClothingParams)MemberwiseClone();
    }

    private bool _clothingWindowOpen;
    private bool _clothingAdvanced;
    private int _clothingArchetypeIndex = (int)ClothingArchetype.Coat;
    private readonly ClothingParams _clothingParams = new();
    private ModelElementData? _clothingPreviewRig;
    private string _clothingPreviewError = "";
    private bool _clothingPreviewDirty = true;
    private int _clothingPreviewCount;
    private string _clothingStatus = "";

    private static int ClothingSlotIndex(string code)
    {
        for (int i = 0; i < ClothingSlots.Length; i++)
        {
            if (ClothingSlots[i].Code == code) return i;
        }
        return 0;
    }

    private double ClothingPad => Math.Max(0.05, _clothingParams.Looseness + _clothingParams.Thickness * ClothingStyleScale());

    private double ClothingStyleScale() => (ClothingStyle)_clothingParams.StyleIndex switch
    {
        ClothingStyle.Plate => 1.6,
        ClothingStyle.Leather => 1.15,
        _ => 1.0
    };

    // ---- Build -------------------------------------------------------------

    /// <summary>Builds the wearer rig with every enabled garment region hung on the matching bones. The rig is
    /// the preview (body + clothes); the garment anchors (the elements that carry a stepParentName) are what
    /// gets emitted on Create. Pure apart from reading <see cref="_modelDoc"/>'s textures, so it is unit-testable.</summary>
    private ModelElementData? ClothingBuildRig(out string error)
    {
        error = "";
        if (_modelDoc == null) return null;

        try
        {
            ClothingParams p = _clothingParams;
            var baseShape = (PlayerModelBaseShape)Math.Clamp(p.BaseShapeIndex, 0, 1);
            ModelElementData rig = PlayerModelBuildSkeleton(baseShape);

            if (p.ClotheHead) ClothingBuildHead(p, rig);
            if (p.ClotheFace) ClothingBuildFace(p, rig);
            if (p.ClotheNeck) ClothingBuildNeck(p, rig);
            if (p.ClotheShoulders) ClothingBuildShoulders(p, rig);
            if (p.ClotheChest) ClothingBuildTorso(p, rig, "UpperTorso", "Chest");
            if (p.ClotheBelly) ClothingBuildTorso(p, rig, "LowerTorso", "Belly");
            if (p.ClotheBelly && p.SkirtLength > 0.01f) ClothingBuildSkirt(p, rig);
            if (p.ClotheArms) ClothingBuildSleeves(p, rig);
            if (p.ClotheHands) ClothingBuildHands(p, rig);
            if (p.ClotheWaist) ClothingBuildWaist(p, rig);
            if (p.ClotheLegs) ClothingBuildLegs(p, rig);
            if (p.ClotheFeet) ClothingBuildFeet(p, rig);
            if (p.ClotheCape) ClothingBuildCape(p, rig);

            ClothingDecorate(p, rig);

            string baseTex = ClothingResolveTexture(p.Texture, "cloth");
            string trimTex = string.IsNullOrWhiteSpace(p.TrimTexture) ? baseTex : p.TrimTexture;
            string accentTex = string.IsNullOrWhiteSpace(p.AccentTexture) ? trimTex : p.AccentTexture;
            foreach (ModelElementData anchor in ClothingAnchors(rig))
            {
                ClothingAssignFaces(anchor, baseTex, trimTex, accentTex);
            }

            int count = ClothingGarmentElementCount(rig);
            if (count == 0)
            {
                error = "No regions enabled - turn on at least one coverage region (or apply a preset).";
                return null;
            }
            if (count > ModelCreatureMaxElements)
            {
                error = $"Too many elements ({count} > {ModelCreatureMaxElements}). Reduce coverage or segment counts.";
                return null;
            }

            return rig;
        }
        catch (Exception exception)
        {
            error = $"Generation failed: {exception.Message}";
            return null;
        }
    }

    private string ClothingResolveTexture(string preferred, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        return _modelDoc?.Textures.FirstOrDefault()?.Code ?? fallback;
    }

    /// <summary>A face-less anchor child of <paramref name="bone"/> carrying its step-parent binding. Its From is the
    /// bone's local origin and To spans the bone, so nested geometry is authored relative to the bone's corner -
    /// the same space step-parenting roots it into at runtime, so the preview and the worn result match.</summary>
    private static ModelElementData ClothingMakeAnchor(ModelElementData bone, string name)
    {
        ModelElementData anchor = new()
        {
            Name = name,
            From = [0, 0, 0],
            To = [ModelPrimitiveRound(bone.SizeX), ModelPrimitiveRound(bone.SizeY), ModelPrimitiveRound(bone.SizeZ)],
            RotationOrigin = [0, 0, 0],
            StepParentName = bone.Name,
            Parent = bone
        };
        bone.Children.Add(anchor);
        return anchor;
    }

    /// <summary>Enumerates the garment anchors (elements with a stepParentName) in build order.</summary>
    private static IEnumerable<ModelElementData> ClothingAnchors(ModelElementData rig)
    {
        return rig.EnumerateSubtree().Where(element => !string.IsNullOrEmpty(element.StepParentName));
    }

    private static int ClothingGarmentElementCount(ModelElementData rig)
    {
        // Every element under an anchor, plus the anchors themselves - i.e. what Create will emit.
        return ClothingAnchors(rig).Sum(anchor => anchor.EnumerateSubtree().Count());
    }

    // ---- Region builders (each in the target bone's local frame; -X = front, +Y = up/along-limb, Z = lateral) --

    private void ClothingBuildHead(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? head = PlayerModelFind(rig, "Head");
        if (head == null) return;
        double hx = head.SizeX, hy = head.SizeY, hz = head.SizeZ;
        double pad = ClothingPad;
        ModelElementData anchor = ClothingMakeAnchor(head, "Headwear");
        var style = (ClothingHeadStyle)Math.Clamp(p.HeadStyle, 0, 4);

        switch (style)
        {
            case ClothingHeadStyle.Skullcap:
                ModelCreatureBox(anchor, [hx * 0.5, hy * 0.8, hz * 0.5], [hx + pad * 2, hy * 0.5, hz + pad * 2], 0, 0, 0, "cap");
                break;
            case ClothingHeadStyle.Cap:
                ModelCreatureBox(anchor, [hx * 0.5, hy * 0.8, hz * 0.5], [hx + pad * 2, hy * 0.5, hz + pad * 2], 0, 0, 0, "cap");
                ModelCreatureBox(anchor, [-Math.Max(0.5, p.Brim) * 0.5, hy * 0.72, hz * 0.5],
                    [Math.Max(0.5, p.Brim), Math.Max(0.3, p.Thickness), hz + pad * 2 + p.Brim], 0, 0, 0, "brim");
                break;
            case ClothingHeadStyle.Hood:
                if (p.HoodDown)
                {
                    // Lowered hood: a slack pouch on the upper back instead of over the head.
                    ModelCreatureBox(anchor, [hx + pad, hy * 0.15, hz * 0.5], [hz + pad, hy * 0.7, hz + pad * 2], 0, 0, -18, "hood_down");
                }
                else
                {
                    // Raised hood: back + sides + crown shell, leaving the face (-X) open, with a short neck drape.
                    ModelCreatureBox(anchor, [hx * 0.62, hy * 0.55, hz * 0.5], [hx + pad * 2, hy + pad * 2, hz + pad * 2], 0, 0, 0, "hood");
                    ModelCreatureBox(anchor, [hx + pad, -hy * 0.18, hz * 0.5], [hz, hy * 0.5, hz + pad * 2], 0, 0, 0, "hood_nape");
                }
                break;
            case ClothingHeadStyle.Helmet:
                ModelCreatureBox(anchor, [hx * 0.5, hy * 0.55, hz * 0.5], [hx + pad * 2, hy + pad * 2, hz + pad * 2], 0, 0, 0, "helm");
                break;
            case ClothingHeadStyle.TallHat:
                ModelCreatureBox(anchor, [hx * 0.5, hy * 0.85, hz * 0.5], [hx + pad * 2, hy * 0.4, hz + pad * 2], 0, 0, 0, "band");
                ModelCreatureBox(anchor, [hx * 0.5, hy + Math.Max(0.5, p.CrownHeight) * 0.5, hz * 0.5],
                    [hx * 0.85, Math.Max(0.5, p.CrownHeight), hz * 0.85], 0, 0, 0, "crown");
                if (p.Brim > 0.05f)
                {
                    ModelCreatureBox(anchor, [hx * 0.5, hy * 0.68, hz * 0.5],
                        [hx + pad * 2 + p.Brim, Math.Max(0.3, p.Thickness), hz + pad * 2 + p.Brim], 0, 0, 0, "brim");
                }
                break;
        }
    }

    private void ClothingBuildFace(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? head = PlayerModelFind(rig, "Head");
        if (head == null) return;
        double hy = head.SizeY, hz = head.SizeZ;
        double th = Math.Max(0.4, p.Thickness * ClothingStyleScale());
        ModelElementData anchor = ClothingMakeAnchor(head, "Facewear");
        var style = (ClothingFaceStyle)Math.Clamp(p.FaceStyle, 0, 3);

        // The wearer faces -X, so the face plane is the bone's low-X (X = 0) side; masks sit just in front of it.
        switch (style)
        {
            case ClothingFaceStyle.Full:
                ModelCreatureBox(anchor, [-th * 0.4, hy * 0.5, hz * 0.5], [th, hy * 0.95, hz + 0.6], 0, 0, 0, "mask");
                break;
            case ClothingFaceStyle.Lower:
                ModelCreatureBox(anchor, [-th * 0.4, hy * 0.28, hz * 0.5], [th, hy * 0.5, hz + 0.6], 0, 0, 0, "mask");
                break;
            case ClothingFaceStyle.Upper:
                ModelCreatureBox(anchor, [-th * 0.4, hy * 0.66, hz * 0.5], [th, hy * 0.36, hz + 0.6], 0, 0, 0, "visor");
                break;
            case ClothingFaceStyle.Goggles:
                foreach (int side in ModelCreatureSides(2))
                {
                    double zc = side > 0 ? hz * 0.72 : hz * 0.28;
                    ModelCreatureBox(anchor, [-th * 0.4, hy * 0.66, zc], [th, hy * 0.28, hz * 0.34], 0, 0, 0,
                        side > 0 ? "goggleR" : "goggleL");
                }
                break;
        }
    }

    private void ClothingBuildNeck(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? neck = PlayerModelFind(rig, "Neck");
        if (neck == null) return;
        double nx = neck.SizeX, ny = neck.SizeY, nz = neck.SizeZ;
        double pad = ClothingPad;
        double collar = Math.Max(0.4, p.CollarHeight);
        ModelElementData anchor = ClothingMakeAnchor(neck, "Neckwear");
        ModelCreatureBox(anchor, [nx * 0.5, ny * 0.45, nz * 0.5], [nx + pad * 2, collar, nz + pad * 2], 0, 0, 0, "collar");

        if (p.Scarf)
        {
            // A front drape hanging off the collar down the chest (a sway chain so it animates).
            double len = Math.Max(1.0, p.ScarfLength);
            int segs = Math.Clamp((int)Math.Round(len / 4.0), 1, 5);
            double segLen = len / segs;
            List<double[]> sizes = Enumerable.Range(0, segs).Select(_ => new[] { Math.Max(0.4, p.Thickness), segLen, nz * 0.6 }).ToList();
            ModelCreatureChain(anchor, [-nx * 0.1, 0.0, nz * 0.5], 0, 0, -8, 1, -1, sizes, null, 2, "scarf", out _);
        }
    }

    private void ClothingBuildShoulders(ClothingParams p, ModelElementData rig)
    {
        double size = Math.Max(0.5, p.PauldronSize);
        int plates = Math.Clamp(p.PauldronPlates, 1, 4);
        double pad = ClothingPad;
        foreach (string side in new[] { "R", "L" })
        {
            ModelElementData? arm = PlayerModelFind(rig, "UpperArm" + side);
            if (arm == null) continue;
            double ax = arm.SizeX, ay = arm.SizeY, az = arm.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(arm, "Pauldron" + side);
            for (int i = 0; i < plates; i++)
            {
                double drop = i * size * 0.6;
                double scale = ModelCreatureLerp(1.0, 0.7, plates <= 1 ? 0.0 : (double)i / (plates - 1));
                ModelCreatureBox(anchor, [ax * 0.5, ay - drop, az * 0.5],
                    [ax + pad * 2 + size, (size + pad) * scale, az + pad * 2 + size], 0, 0, 0, $"plate{i + 1}");
            }
        }
    }

    private void ClothingBuildTorso(ClothingParams p, ModelElementData rig, string boneName, string anchorName)
    {
        ModelElementData? torso = PlayerModelFind(rig, boneName);
        if (torso == null) return;
        double tx = torso.SizeX, ty = torso.SizeY, tz = torso.SizeZ;
        double pad = ClothingPad;
        ModelElementData anchor = ClothingMakeAnchor(torso, anchorName);

        if (p.OpenFront && boneName == "UpperTorso")
        {
            // Two front halves with a central gap (an open jacket) + a full back/side shell.
            ModelCreatureBox(anchor, [tx * 0.5, ty * 0.5, tz * 0.5], [tx + pad * 2, ty + pad, tz * 0.55 + pad], 0, 0, 0, "back");
            foreach (int side in ModelCreatureSides(2))
            {
                double zc = side > 0 ? tz * 0.78 : tz * 0.22;
                ModelCreatureBox(anchor, [tx * 0.18, ty * 0.5, zc], [tx * 0.5, ty + pad, tz * 0.42 + pad], 0, 0, 0,
                    side > 0 ? "frontR" : "frontL");
            }
        }
        else
        {
            ModelCreatureBox(anchor, [tx * 0.5, ty * 0.5, tz * 0.5], [tx + pad * 2, ty + pad, tz + pad * 2], 0, 0, 0, "panel");
        }

        if (p.Lapels && boneName == "UpperTorso")
        {
            foreach (int side in ModelCreatureSides(2))
            {
                double zc = side > 0 ? tz * 0.66 : tz * 0.34;
                ModelCreatureBox(anchor, [-pad * 0.5, ty * 0.62, zc], [Math.Max(0.4, p.Thickness) * 1.5, ty * 0.6, tz * 0.3], 0, 0, 12 * side,
                    side > 0 ? "lapelR" : "lapelL");
            }
        }
    }

    private void ClothingBuildSkirt(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? pelvis = PlayerModelFind(rig, "LowerTorso");
        if (pelvis == null) return;
        double tx = pelvis.SizeX, tz = pelvis.SizeZ;
        double len = Math.Max(1.0, p.SkirtLength);
        int segs = Math.Clamp(p.SkirtSegments, 1, 6);
        double segLen = len / segs;
        double flare = Math.Max(0.0, p.SkirtFlare);
        double pad = ClothingPad;
        ModelElementData anchor = ClothingMakeAnchor(pelvis, "Skirt");

        // Front and back drapes hanging off the pelvis bottom (Y = 0), each a sway chain widening toward the hem.
        foreach ((string tag, double xAttach, double rotZ) in new[] { ("front", -pad, 4.0), ("back", tx + pad, -4.0) })
        {
            List<double[]> sizes = [];
            for (int k = 0; k < segs; k++)
            {
                double t = segs <= 1 ? 1.0 : (double)k / (segs - 1);
                double width = (tz + pad * 2) + flare * t;
                sizes.Add([Math.Max(0.4, p.Thickness), segLen, width]);
            }
            ModelCreatureChain(anchor, [xAttach, 0.0, tz * 0.5], 0, 0, rotZ, 1, -1, sizes, null, 2, "skirt_" + tag, out _);
        }
    }

    private void ClothingBuildSleeves(ClothingParams p, ModelElementData rig)
    {
        double pad = ClothingPad;
        double len = Math.Clamp(p.SleeveLength, 0f, 1f);
        foreach (string side in new[] { "R", "L" })
        {
            ModelElementData? upper = PlayerModelFind(rig, "UpperArm" + side);
            if (upper == null) continue;
            double ux = upper.SizeX, uy = upper.SizeY, uz = upper.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(upper, "Sleeve" + side);

            // The shoulder is the bone's high-Y end; the sleeve grows down from it.
            double upperCover = uy * Math.Min(1.0, len * 2.0);
            ModelCreatureBox(anchor, [ux * 0.5, uy - upperCover * 0.5, uz * 0.5],
                [ux + pad * 2, upperCover, uz + pad * 2], 0, 0, 0, "upper");

            if (len > 0.5)
            {
                ModelElementData? lower = PlayerModelFind(rig, "LowerArm" + side);
                if (lower != null)
                {
                    double lx = lower.SizeX, ly = lower.SizeY, lz = lower.SizeZ;
                    ModelElementData lowerAnchor = ClothingMakeAnchor(lower, "Sleeve" + side + "Fore");
                    double lowerCover = ly * Math.Min(1.0, (len - 0.5) * 2.0);
                    ModelCreatureBox(lowerAnchor, [lx * 0.5, ly - lowerCover * 0.5, lz * 0.5],
                        [lx + pad * 2, lowerCover, lz + pad * 2], 0, 0, 0, "fore");
                    if (p.Cuff)
                    {
                        ModelCreatureBox(lowerAnchor, [lx * 0.5, ly - lowerCover + 0.4, lz * 0.5],
                            [lx + pad * 2 + 0.8, Math.Max(0.6, p.Thickness * 2), lz + pad * 2 + 0.8], 0, 0, 0, "cuff");
                    }
                }
            }
        }
    }

    private void ClothingBuildHands(ClothingParams p, ModelElementData rig)
    {
        double pad = ClothingPad;
        double up = Math.Max(0.5, p.GloveLength);
        foreach (string side in new[] { "R", "L" })
        {
            ModelElementData? fore = PlayerModelFind(rig, "LowerArm" + side);
            if (fore == null) continue;
            double lx = fore.SizeX, lz = fore.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(fore, "Glove" + side);
            // The hand is at the bone's low-Y end; the glove wraps it and rides up the forearm by GloveLength.
            ModelCreatureBox(anchor, [lx * 0.5, up * 0.5 - 1.0, lz * 0.5], [lx + pad * 2, up, lz + pad * 2], 0, 0, 0, "glove");
        }
    }

    private void ClothingBuildWaist(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? pelvis = PlayerModelFind(rig, "LowerTorso");
        if (pelvis == null) return;
        double tx = pelvis.SizeX, ty = pelvis.SizeY, tz = pelvis.SizeZ;
        double pad = ClothingPad;
        double w = Math.Max(0.4, p.BeltWidth);
        ModelElementData anchor = ClothingMakeAnchor(pelvis, "Belt");
        ModelCreatureBox(anchor, [tx * 0.5, ty * 0.85, tz * 0.5], [tx + pad * 2 + 0.4, w, tz + pad * 2 + 0.4], 0, 0, 0, "band");

        if (p.Buckle)
        {
            ModelCreatureBox(anchor, [-pad * 0.5, ty * 0.85, tz * 0.5], [Math.Max(0.5, p.Thickness * 2), w * 1.4, w * 1.4], 0, 0, 0, "buckle");
        }
        int pouches = Math.Clamp(p.Pouches, 0, 4);
        for (int i = 0; i < pouches; i++)
        {
            double zc = pouches == 1 ? tz * 0.5 : ModelCreatureLerp(tz * 0.2, tz * 0.8, (double)i / (pouches - 1));
            ModelCreatureBox(anchor, [tx + pad, ty * 0.7, zc], [w * 1.5, w * 2.2, w * 1.6], 0, 0, 0, $"pouch{i + 1}");
        }
    }

    private void ClothingBuildLegs(ClothingParams p, ModelElementData rig)
    {
        double pad = ClothingPad;
        double len = Math.Clamp(p.LegLength, 0f, 1f);
        foreach (string side in new[] { "R", "L" })
        {
            ModelElementData? thigh = PlayerModelFind(rig, "UpperFoot" + side);
            if (thigh == null) continue;
            double ux = thigh.SizeX, uy = thigh.SizeY, uz = thigh.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(thigh, "Legwear" + side);
            double thighCover = uy * Math.Min(1.0, len * 2.0);
            ModelCreatureBox(anchor, [ux * 0.5, uy - thighCover * 0.5, uz * 0.5],
                [ux + pad * 2, thighCover, uz + pad * 2], 0, 0, 0, "thigh");

            if (len > 0.5)
            {
                // Seraph shin = LowerFoot; digitigrade shin = MiddleFoot (its LowerFoot is the paw, clothed by Feet).
                ModelElementData? shin = PlayerModelFind(rig, "MiddleFoot" + side) ?? PlayerModelFind(rig, "LowerFoot" + side);
                if (shin != null)
                {
                    double sx = shin.SizeX, sy = shin.SizeY, sz = shin.SizeZ;
                    ModelElementData shinAnchor = ClothingMakeAnchor(shin, "Legwear" + side + "Shin");
                    double shinCover = sy * Math.Min(1.0, (len - 0.5) * 2.0);
                    ModelCreatureBox(shinAnchor, [sx * 0.5, sy - shinCover * 0.5, sz * 0.5],
                        [sx + pad * 2, shinCover, sz + pad * 2], 0, 0, 0, "shin");
                }
            }
        }
    }

    private void ClothingBuildFeet(ClothingParams p, ModelElementData rig)
    {
        double pad = ClothingPad;
        double up = Math.Max(0.0, p.BootHeight);
        foreach (string side in new[] { "R", "L" })
        {
            ModelElementData? foot = PlayerModelFind(rig, "LowerFoot" + side);
            if (foot == null) continue;
            double fx = foot.SizeX, fy = foot.SizeY, fz = foot.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(foot, "Boot" + side);
            ModelCreatureBox(anchor, [fx * 0.5, fy * 0.5, fz * 0.5], [fx + pad * 2, fy + pad * 2, fz + pad * 2], 0, 0, 0, "boot");

            if (up > 0.05)
            {
                // A shaft climbing the shin above the foot's high-Y end.
                ModelCreatureBox(anchor, [fx * 0.5, fy + up * 0.5, fz * 0.5], [fx + pad * 1.5, up, fz + pad * 1.5], 0, 0, 0, "shaft");
            }
            if (p.Sole)
            {
                ModelCreatureBox(anchor, [fx * 0.5, -Math.Max(0.4, p.Thickness), fz * 0.5], [fx + pad * 2, Math.Max(0.6, p.Thickness * 2), fz + pad * 2 + 1.0], 0, 0, 0, "sole");
            }
        }
    }

    private void ClothingBuildCape(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? torso = PlayerModelFind(rig, "UpperTorso");
        if (torso == null) return;
        double tx = torso.SizeX, ty = torso.SizeY, tz = torso.SizeZ;
        double pad = ClothingPad;
        double len = Math.Max(1.0, p.CapeLength);
        int segs = Math.Clamp(p.CapeSegments, 1, 8);
        double segLen = len / segs;
        double width = Math.Max(1.0, p.CapeWidth);
        ModelElementData anchor = ClothingMakeAnchor(torso, "Cape");

        if (p.CapeCollar)
        {
            ModelCreatureBox(anchor, [tx + pad, ty * 0.9, tz * 0.5], [Math.Max(0.6, p.Thickness * 2), ty * 0.4, width + pad], 0, 0, 0, "collar");
        }

        // A wide sway chain off the upper back (high X = behind, since the wearer faces -X) hanging straight down.
        List<double[]> sizes = Enumerable.Range(0, segs).Select(_ => new[] { Math.Max(0.4, p.Thickness), segLen, width }).ToList();
        ModelCreatureChain(anchor, [tx + pad, ty * 0.85, tz * 0.5], 0, 0, -2, 1, -1, sizes, null, 2, "cape", out _);
    }

    private void ClothingAssignFaces(ModelElementData anchor, string baseTexture, string trimTexture, string accentTexture)
    {
        foreach (ModelElementData node in anchor.EnumerateSubtree())
        {
            if (ReferenceEquals(node, anchor)) continue;     // anchors stay face-less, like vanilla clothing
            if (node.SizeX <= 0.0001 || node.SizeY <= 0.0001 || node.SizeZ <= 0.0001) continue;

            string code = ClothingFaceCategory(node.Name) switch
            {
                1 => trimTexture,
                2 => accentTexture,
                _ => baseTexture
            };
            for (int face = 0; face < 6; face++)
            {
                node.Faces[face] = new ModelFaceData { Texture = code };
                ModelCreatureAutoUvFace(node, face);
            }
        }
    }

    // 0 = main cloth, 1 = trim/metal (edges, buckles, plates, studs), 2 = accent (organic, gems, feathers).
    private static int ClothingFaceCategory(string name)
    {
        foreach (string token in new[] { "branch", "twig", "vine", "root", "leaf", "thorn", "feather", "plume", "gem", "jewel" })
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return 2;
        }
        foreach (string token in new[] { "trim", "cuff", "buckle", "band", "sole", "collar", "brim", "stud", "rivet", "plate", "scale", "lamel", "strap", "fur", "spike" })
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 0;
    }

    // ---- Decoration pass ---------------------------------------------------
    //
    // Runs after the base regions are built. Everything here scatters or hangs extra geometry onto the garment
    // anchors (or a per-bone "Decor<Bone>" anchor when the underlying region is off), so ornaments are reachable
    // on any garment. Scatter is seeded off DecorSeed for reproducibility, and Wear randomly drops/shortens
    // pieces for a damaged look. This is the bulk of the "everything" surface: tatters, branches, vines, leaves,
    // thorns, studs, gems, spikes, feathers, a capelet mantle, cloth layers, armor plating and straps.

    private void ClothingDecorate(ClothingParams p, ModelElementData rig)
    {
        ClothingApplyCollarAndHood(p, rig);
        if (p.Mantle) ClothingBuildMantle(p, rig);
        if (p.Layers > 1) ClothingBuildLayers(p, rig);
        if ((ClothingPlating)p.Plating != ClothingPlating.None) ClothingBuildPlating(p, rig);
        if (p.Straps) ClothingBuildStraps(p, rig);

        if (p.Tatters) ClothingBuildTatters(p, rig);
        if (p.Fringe) ClothingBuildFringe(p, rig);
        if (p.FurTrim) ClothingBuildFurTrim(p, rig);

        if (p.Branches) ClothingBuildBranches(p, rig);
        if (p.Vines) ClothingBuildVines(p, rig);
        if (p.Leaves) ClothingBuildLeaves(p, rig);
        if (p.Thorns) ClothingBuildThorns(p, rig);

        if (p.Studs) ClothingBuildStuds(p, rig);
        if (p.Gems) ClothingBuildGems(p, rig);
        if (p.Spikes) ClothingBuildSpikes(p, rig);
        if (p.Feathers) ClothingBuildFeathers(p, rig);
    }

    private static double ClothingRand(Random rng, double a, double b) => a + (b - a) * rng.NextDouble();

    private static ModelElementData? ClothingFindAnchor(ModelElementData rig, string name)
    {
        return rig.EnumerateSubtree().FirstOrDefault(e => !string.IsNullOrEmpty(e.StepParentName) && e.Name == name);
    }

    private ModelElementData ClothingDecorAnchor(ModelElementData rig, string boneName)
    {
        ModelElementData bone = PlayerModelFind(rig, boneName) ?? PlayerModelFind(rig, "UpperTorso")!;
        string anchorName = "Decor" + bone.Name;
        return bone.Children.FirstOrDefault(child => child.Name == anchorName && !string.IsNullOrEmpty(child.StepParentName))
            ?? ClothingMakeAnchor(bone, anchorName);
    }

    private ModelElementData ClothingAttachTarget(ModelElementData rig, string boneName, string? preferredAnchor)
    {
        if (preferredAnchor != null)
        {
            ModelElementData? found = ClothingFindAnchor(rig, preferredAnchor);
            if (found != null) return found;
        }
        return ClothingDecorAnchor(rig, boneName);
    }

    // A scatter region on an anchor: x/y/z ranges in the anchor's local frame plus the outward aim (deg).
    private readonly record struct ClothingPatch(ModelElementData Anchor, double[] X, double[] Y, double[] Z, double AimX, double AimZ);

    private List<ClothingPatch> ClothingSurfacePatches(ModelElementData rig, ClothingSurface surface, ClothingParams p)
    {
        double pad = ClothingPad;
        List<ClothingPatch> patches = [];

        void TorsoFace(bool front)
        {
            ModelElementData a = ClothingAttachTarget(rig, "UpperTorso", front ? "Chest" : null);
            double tx = a.SizeX, ty = a.SizeY, tz = a.SizeZ;
            double x = front ? -pad * 0.5 : tx + pad * 0.5;
            patches.Add(new ClothingPatch(a, [x, x], [ty * 0.2, ty * 0.85], [tz * 0.18, tz * 0.82], 0, front ? 70 : -70));
        }

        void Shoulders(double aimZ)
        {
            foreach (string side in new[] { "R", "L" })
            {
                ModelElementData a = ClothingAttachTarget(rig, "UpperArm" + side, "Pauldron" + side);
                double ax = a.SizeX, ay = a.SizeY, az = a.SizeZ;
                double sideSign = side == "R" ? -1 : 1;
                patches.Add(new ClothingPatch(a, [ax * 0.2, ax * 0.8], [ay * 0.75, ay + 1.0], [az * 0.2, az * 0.8], (int)(sideSign * 30), aimZ));
            }
        }

        switch (surface)
        {
            case ClothingSurface.Cape:
            {
                ModelElementData? cape = ClothingFindAnchor(rig, "Cape");
                if (cape != null)
                {
                    double tx = cape.SizeX, ty = cape.SizeY, tz = cape.SizeZ;
                    double len = Math.Max(1.0, p.CapeLength), w = Math.Max(1.0, p.CapeWidth);
                    double top = ty * 0.85;
                    double xOut = tx + pad + Math.Max(0.4, p.Thickness);
                    patches.Add(new ClothingPatch(cape, [xOut, xOut], [top - len * 0.92, top - len * 0.08],
                        [tz * 0.5 - w * 0.45, tz * 0.5 + w * 0.45], 0, -62));
                }
                else
                {
                    TorsoFace(front: false);
                }
                break;
            }
            case ClothingSurface.Back: TorsoFace(front: false); break;
            case ClothingSurface.Chest: TorsoFace(front: true); break;
            case ClothingSurface.Shoulders: Shoulders(-8); break;
            case ClothingSurface.Hood:
            {
                ModelElementData a = ClothingAttachTarget(rig, "Head", "Headwear");
                double hx = a.SizeX, hy = a.SizeY, hz = a.SizeZ;
                patches.Add(new ClothingPatch(a, [hx * 0.45, hx + pad], [hy * 0.9, hy + pad], [hz * 0.2, hz * 0.8], 0, -28));
                break;
            }
            case ClothingSurface.Sleeves:
                foreach (string side in new[] { "R", "L" })
                {
                    ModelElementData a = ClothingAttachTarget(rig, "UpperArm" + side, "Sleeve" + side);
                    double ax = a.SizeX, ay = a.SizeY, az = a.SizeZ;
                    double sideSign = side == "R" ? -1 : 1;
                    patches.Add(new ClothingPatch(a, [ax * 0.3, ax * 0.7], [ay * 0.2, ay * 0.8], [az + pad * 0.3, az + pad * 0.3], (int)(sideSign * 45), 0));
                }
                break;
            case ClothingSurface.Belt:
            {
                ModelElementData a = ClothingAttachTarget(rig, "LowerTorso", "Belt");
                double tx = a.SizeX, ty = a.SizeY, tz = a.SizeZ;
                patches.Add(new ClothingPatch(a, [-pad * 0.5, -pad * 0.5], [ty * 0.75, ty * 0.95], [tz * 0.15, tz * 0.85], 0, 60));
                break;
            }
            case ClothingSurface.Whole:
                TorsoFace(true); TorsoFace(false); Shoulders(-8);
                break;
        }
        return patches;
    }

    private void ClothingScatter(ClothingPatch patch, int count, int seed, double spread, double wear,
        Action<ModelElementData, double[], double, double, Random, int> place)
    {
        if (count <= 0) return;
        Random rng = new(seed);
        int placed = 0;
        for (int i = 0; i < count; i++)
        {
            if (wear > 0 && rng.NextDouble() < wear * 0.55) { rng.NextDouble(); continue; }
            double bias = _clothingParams.Asymmetric ? Math.Pow(rng.NextDouble(), 1.7) : rng.NextDouble();
            double[] at =
            [
                ClothingRand(rng, patch.X[0], patch.X[1]),
                ClothingRand(rng, patch.Y[0], patch.Y[1]),
                patch.Z[0] + (patch.Z[1] - patch.Z[0]) * bias
            ];
            double aimX = patch.AimX + (rng.NextDouble() - 0.5) * 2 * spread;
            double aimZ = patch.AimZ + (rng.NextDouble() - 0.5) * 2 * spread;
            place(patch.Anchor, at, aimX, aimZ, rng, placed);
            placed++;
        }
    }

    // ---- Organic ornaments -------------------------------------------------

    private ModelElementData ClothingBranchAt(ModelElementData parent, double[] attach, double aimX, double aimZ,
        double length, double thick, int segs, double curve, Random rng, bool leaves, double leafSize, string name)
    {
        segs = Math.Clamp(segs, 1, 6);
        double segLen = Math.Max(0.4, length) / segs;
        double gnarl = rng.NextDouble() < 0.5 ? 1.0 : -1.0;
        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < segs; k++)
        {
            double t = segs <= 1 ? 0.0 : (double)k / (segs - 1);
            double w = Math.Max(0.15, thick * ModelCreatureLerp(1.0, 0.3, t));
            sizes.Add([w, segLen, w]);
            bends.Add(k == 0 ? 0.0 : curve * gnarl * (k % 2 == 0 ? 1.0 : -0.7));
        }
        ModelCreatureChain(parent, attach, aimX, 0, aimZ, 1, 1, sizes, bends, 2, name, out ModelElementData tip);
        if (leaves && leafSize > 0.05) ClothingLeafCluster(tip, leafSize, rng.Next(), name + "leaf");
        return tip;
    }

    private void ClothingLeafCluster(ModelElementData tip, double size, int seed, string name)
    {
        Random rng = new(seed);
        double[] at = ModelCreatureDistalAttach(tip, 1, 1);
        int n = 3;
        for (int i = 0; i < n; i++)
        {
            double rx = ClothingRand(rng, -45, 45);
            double rz = ClothingRand(rng, -45, 45);
            ModelCreatureBox(tip, at, [size * 1.3, Math.Max(0.15, size * 0.2), size], rx, 0, rz, $"{name}{i + 1}");
        }
    }

    private void ClothingBuildBranches(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.BranchSurface, p);
        int per = Math.Max(1, p.BranchCount / Math.Max(1, patches.Count));
        double leafSize = p.BranchLeaves ? Math.Max(0.6, p.BranchThickness * 1.6) : 0.0;
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 17 * (pi + 1), p.BranchSpread, p.Wear, (anchor, at, aimX, aimZ, rng, idx) =>
            {
                ModelElementData tip = ClothingBranchAt(anchor, at, aimX, aimZ, p.BranchLength, p.BranchThickness,
                    p.BranchSegments, p.BranchCurve, rng, p.BranchLeaves, leafSize, $"branch{pi}_{idx}");
                int forks = Math.Clamp(p.BranchForks, 0, 3);
                double[] tipAt = ModelCreatureDistalAttach(tip, 1, 1);
                for (int f = 0; f < forks; f++)
                {
                    ClothingBranchAt(tip, tipAt, aimX + ClothingRand(rng, -35, 35), aimZ + ClothingRand(rng, -55, 55),
                        p.BranchLength * 0.55, p.BranchThickness * 0.6, Math.Max(1, p.BranchSegments - 1),
                        p.BranchCurve, rng, p.BranchLeaves, leafSize, $"branch{pi}_{idx}f{f}");
                }
            });
        }
    }

    private void ClothingBuildVines(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.VineSurface, p);
        int per = Math.Max(1, p.VineCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            // Vines droop: they aim outward then curl down, so we bias the aim downward and add a strong curl.
            ClothingScatter(patches[pi], per, p.DecorSeed + 29 * (pi + 1), 28, p.Wear, (anchor, at, aimX, aimZ, rng, idx) =>
                ClothingBranchAt(anchor, at, aimX, aimZ * 0.4 + 35, p.VineLength, p.VineThickness, 5, 22, rng, p.Leaves, p.Leaves ? p.LeafSize : 0, $"vine{pi}_{idx}"));
        }
    }

    private void ClothingBuildLeaves(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.LeafSurface, p);
        int per = Math.Max(1, p.LeafCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 41 * (pi + 1), 50, p.Wear, (anchor, at, aimX, aimZ, rng, idx) =>
            {
                double s = Math.Max(0.4, p.LeafSize) * ClothingRand(rng, 0.7, 1.3);
                ModelCreatureBox(anchor, at, [s * 1.4, Math.Max(0.15, s * 0.2), s], aimX, ClothingRand(rng, -30, 30), aimZ, $"leaf{pi}_{idx}");
            });
        }
    }

    private void ClothingBuildThorns(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.ThornSurface, p);
        int per = Math.Max(1, p.ThornCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 53 * (pi + 1), 40, p.Wear, (anchor, at, aimX, aimZ, rng, idx) =>
            {
                double len = Math.Max(0.5, p.ThornLength) * ClothingRand(rng, 0.7, 1.2);
                ModelCreatureChain(anchor, at, aimX, 0, aimZ, 1, 1, [[len * 0.5, len * 0.4, len * 0.5], [0.05, len * 0.6, 0.05]], null, 2, $"thorn{pi}_{idx}", out _);
            });
        }
    }

    // ---- Hard ornaments ----------------------------------------------------

    private void ClothingBuildStuds(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.StudSurface, p);
        int per = Math.Max(1, p.StudCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 61 * (pi + 1), 6, p.Wear, (anchor, at, _, _, rng, idx) =>
            {
                double s = Math.Max(0.2, p.StudSize);
                ModelCreatureBox(anchor, at, [s * 0.7, s, s], 0, 0, 0, $"stud{pi}_{idx}");
            });
        }
    }

    private void ClothingBuildGems(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.GemSurface, p);
        int per = Math.Max(1, p.GemCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 71 * (pi + 1), 6, p.Wear, (anchor, at, _, _, rng, idx) =>
            {
                double s = Math.Max(0.3, p.GemSize) * ClothingRand(rng, 0.8, 1.2);
                ModelCreatureBox(anchor, at, [s * 0.6, s, s], 0, 45, 0, $"gem{pi}_{idx}");
            });
        }
    }

    private void ClothingBuildSpikes(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.SpikeSurface, p);
        int per = Math.Max(1, p.SpikeCount / Math.Max(1, patches.Count));
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingScatter(patches[pi], per, p.DecorSeed + 83 * (pi + 1), 22, p.Wear, (anchor, at, aimX, aimZ, rng, idx) =>
            {
                double len = Math.Max(0.5, p.SpikeLength) * ClothingRand(rng, 0.8, 1.2);
                double s = Math.Max(0.2, p.SpikeSize);
                ModelCreatureChain(anchor, at, aimX, 0, aimZ, 1, 1, [[s, len * 0.45, s], [s * 0.3, len * 0.55, s * 0.3]], null, 2, $"spike{pi}_{idx}", out _);
            });
        }
    }

    private void ClothingBuildFeathers(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.FeatherSurface, p);
        // Feathers fan from a point (a plume) at each patch, rather than scattering.
        int count = Math.Clamp(p.FeatherCount, 1, 24);
        double len = Math.Max(1.0, p.FeatherLength);
        for (int pi = 0; pi < patches.Count; pi++)
        {
            ClothingPatch patch = patches[pi];
            double midZ = (patch.Z[0] + patch.Z[1]) * 0.5;
            double baseY = patch.Y[1];
            double baseX = patch.X[1];
            for (int i = 0; i < count; i++)
            {
                double t = count == 1 ? 0.5 : (double)i / (count - 1);
                double spread = (t - 0.5) * p.FeatherSpread;
                double scale = ModelCreatureLerp(0.7, 1.0, Math.Sin(Math.PI * t));
                ModelCreatureChain(patch.Anchor, [baseX, baseY, midZ], spread, 0, patch.AimZ, 1, 1,
                    [[Math.Max(0.2, len * 0.12), len * scale, Math.Max(0.4, len * 0.28)]], null, 2, $"feather{pi}_{i + 1}", out _);
            }
        }
    }

    // ---- Edge treatments ---------------------------------------------------

    private readonly record struct ClothingHem(ModelElementData Anchor, double EdgeY, double X, double[] Z, double Drop);

    private List<ClothingHem> ClothingHems(ModelElementData rig, ClothingEdge edge, ClothingParams p)
    {
        double pad = ClothingPad;
        List<ClothingHem> hems = [];

        void CapeHem()
        {
            ModelElementData? cape = ClothingFindAnchor(rig, "Cape");
            if (cape == null) return;
            double tx = cape.SizeX, ty = cape.SizeY, tz = cape.SizeZ;
            double w = Math.Max(1.0, p.CapeWidth);
            hems.Add(new ClothingHem(cape, ty * 0.85 - Math.Max(1.0, p.CapeLength), tx + pad, [tz * 0.5 - w * 0.45, tz * 0.5 + w * 0.45], 1));
        }
        void SkirtHem()
        {
            ModelElementData? skirt = ClothingFindAnchor(rig, "Skirt");
            if (skirt == null) return;
            double tx = skirt.SizeX, tz = skirt.SizeZ;
            double y = -Math.Max(1.0, p.SkirtLength);
            hems.Add(new ClothingHem(skirt, y, -pad, [tz * 0.15, tz * 0.85], 1));
            hems.Add(new ClothingHem(skirt, y, tx + pad, [tz * 0.15, tz * 0.85], 1));
        }
        void TunicHem()
        {
            ModelElementData a = ClothingAttachTarget(rig, "LowerTorso", "Belly");
            double tx = a.SizeX, tz = a.SizeZ;
            hems.Add(new ClothingHem(a, -pad, -pad, [tz * 0.12, tz * 0.88], 1));
            hems.Add(new ClothingHem(a, -pad, tx + pad, [tz * 0.12, tz * 0.88], 1));
        }
        void SleeveHem()
        {
            foreach (string side in new[] { "R", "L" })
            {
                ModelElementData? sleeve = ClothingFindAnchor(rig, "Sleeve" + side + "Fore") ?? ClothingFindAnchor(rig, "Sleeve" + side);
                if (sleeve == null) continue;
                double ax = sleeve.SizeX, az = sleeve.SizeZ;
                hems.Add(new ClothingHem(sleeve, -pad, ax * 0.5, [az * 0.15, az * 0.85], 1));
            }
        }

        switch (edge)
        {
            case ClothingEdge.Cape: CapeHem(); break;
            case ClothingEdge.Skirt: SkirtHem(); break;
            case ClothingEdge.Hem: TunicHem(); break;
            case ClothingEdge.Sleeves: SleeveHem(); break;
            case ClothingEdge.Cape_Skirt: CapeHem(); SkirtHem(); break;
            case ClothingEdge.Collar: break;   // handled by fur trim only
        }
        return hems;
    }

    private void ClothingBuildTatters(ClothingParams p, ModelElementData rig)
    {
        List<ClothingHem> hems = ClothingHems(rig, (ClothingEdge)p.TatterEdge, p);
        int per = Math.Max(1, p.TatterCount / Math.Max(1, hems.Count));
        double th = Math.Max(0.25, p.Thickness);
        for (int hi = 0; hi < hems.Count; hi++)
        {
            ClothingHem hem = hems[hi];
            Random rng = new(p.DecorSeed + 101 * (hi + 1));
            for (int i = 0; i < per; i++)
            {
                if (p.Wear > 0 && rng.NextDouble() < p.Wear * 0.4) { rng.NextDouble(); continue; }
                double z = hem.Z[0] + (hem.Z[1] - hem.Z[0]) * ((i + ClothingRand(rng, -0.3, 0.3)) / Math.Max(1, per - 1 + 0.0001));
                double rag = 1.0 - p.TatterRaggedness * rng.NextDouble();
                double len = Math.Max(0.5, p.TatterLength) * rag;
                double tilt = (rng.NextDouble() - 0.5) * 2 * p.TatterRaggedness * 22;
                double w = Math.Max(0.3, p.TatterWidth) * ClothingRand(rng, 0.6, 1.1);
                int segs = len > 4 ? 2 : 1;
                List<double[]> sizes = [];
                for (int k = 0; k < segs; k++) sizes.Add([th, len / segs, w * (k == 0 ? 1.0 : 0.7)]);
                ModelCreatureChain(hem.Anchor, [hem.X, hem.EdgeY, z], 0, 0, tilt, 1, -1, sizes, null, 2, $"tatter{hi}_{i}", out _);
            }
        }
    }

    private void ClothingBuildFringe(ClothingParams p, ModelElementData rig)
    {
        List<ClothingHem> hems = ClothingHems(rig, (ClothingEdge)p.FringeEdge, p);
        int per = Math.Max(1, p.FringeCount / Math.Max(1, hems.Count));
        double th = Math.Max(0.2, p.Thickness * 0.7);
        for (int hi = 0; hi < hems.Count; hi++)
        {
            ClothingHem hem = hems[hi];
            for (int i = 0; i < per; i++)
            {
                double z = hem.Z[0] + (hem.Z[1] - hem.Z[0]) * (per <= 1 ? 0.5 : (double)i / (per - 1));
                ModelCreatureBox(hem.Anchor, [hem.X, hem.EdgeY - p.FringeLength * 0.5, z], [th, Math.Max(0.4, p.FringeLength), th * 1.4], 0, 0, 0, $"fringe{hi}_{i}");
            }
        }
    }

    private void ClothingBuildFurTrim(ClothingParams p, ModelElementData rig)
    {
        var edge = (ClothingEdge)p.FurTrimEdge;
        double fur = Math.Max(0.4, p.FurTrimThickness);
        if (edge == ClothingEdge.Collar)
        {
            ModelElementData? neck = PlayerModelFind(rig, "Neck");
            if (neck != null)
            {
                ModelElementData a = ClothingAttachTarget(rig, "Neck", "Neckwear");
                double nx = a.SizeX, ny = a.SizeY, nz = a.SizeZ;
                ModelCreatureBox(a, [nx * 0.5, ny * 0.5, nz * 0.5], [nx + fur * 2, ny * 0.7, nz + fur * 2], 0, 0, 0, "furcollar");
            }
            return;
        }
        List<ClothingHem> hems = ClothingHems(rig, edge, p);
        for (int hi = 0; hi < hems.Count; hi++)
        {
            ClothingHem hem = hems[hi];
            double width = hem.Z[1] - hem.Z[0];
            ModelCreatureBox(hem.Anchor, [hem.X, hem.EdgeY, (hem.Z[0] + hem.Z[1]) * 0.5], [fur * 1.6, fur * 1.4, width + fur], 0, 0, 0, $"furtrim{hi}");
        }
    }

    // ---- Mantle / layers / plating / straps --------------------------------

    private void ClothingBuildMantle(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? torso = PlayerModelFind(rig, "UpperTorso");
        if (torso == null) return;
        double tx = torso.SizeX, ty = torso.SizeY, tz = torso.SizeZ;
        double pad = ClothingPad;
        ModelElementData anchor = ClothingMakeAnchor(torso, "Mantle");
        int count = Math.Clamp(p.MantleCount, 3, 16);
        double len = Math.Max(1.0, p.MantleLength);
        Random rng = new(p.DecorSeed + 7);
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;                // around the shoulders
            double ang = t * 360.0;
            double z = tz * 0.5 + Math.Cos(ang * Math.PI / 180.0) * (tz * 0.5 + pad);
            double x = tx * 0.5 + Math.Sin(ang * Math.PI / 180.0) * (tx * 0.5 + pad);
            double dropLen = len * (p.MantleTatters ? ClothingRand(rng, 0.5, 1.0) : 1.0);
            double w = (tz + pad) / count * 1.6 + p.MantleFlare * 0.3;
            ModelCreatureChain(anchor, [x, ty + pad, z], 0, 0, -8, 1, -1,
                [[Math.Max(0.4, p.Thickness), dropLen, w]], null, 2, $"mantle{i + 1}", out _);
        }
    }

    private void ClothingBuildLayers(ClothingParams p, ModelElementData rig)
    {
        int layers = Math.Clamp(p.Layers, 2, 4);
        double pad = ClothingPad;
        foreach (string boneName in new[] { "UpperTorso", "LowerTorso" })
        {
            ModelElementData? torso = PlayerModelFind(rig, boneName);
            if (torso == null) continue;
            double tx = torso.SizeX, ty = torso.SizeY, tz = torso.SizeZ;
            ModelElementData anchor = ClothingMakeAnchor(torso, "Layer" + boneName);
            for (int l = 1; l < layers; l++)
            {
                double off = l * Math.Max(0.2, p.LayerOffset);
                double drop = l * Math.Max(0.2, p.LayerOffset) * 2;
                ModelCreatureBox(anchor, [tx * 0.5, ty * 0.5 - drop, tz * 0.5], [tx + (pad + off) * 2, ty + pad, tz + (pad + off) * 2], 0, 0, 0, $"layer{l}");
            }
        }
    }

    private void ClothingBuildPlating(ClothingParams p, ModelElementData rig)
    {
        var style = (ClothingPlating)p.Plating;
        int placement = Math.Clamp(p.PlatePlacement, 0, ClothingPlatePlacementLabels.Length - 1);
        List<(string bone, string anchor)> targets = placement switch
        {
            0 => [("UpperTorso", "Chest")],
            1 => [("UpperTorso", "DecorBack")],
            2 => [("UpperTorso", "Chest"), ("UpperTorso", "DecorBack")],
            3 => [("UpperArmR", "PauldronR"), ("UpperArmL", "PauldronL")],
            4 => [("UpperFootR", "LegwearR"), ("UpperFootL", "LegwearL")],
            5 => [("UpperArmR", "SleeveR"), ("UpperArmL", "SleeveL")],
            _ => [("UpperTorso", "Chest"), ("UpperTorso", "DecorBack"), ("UpperArmR", "PauldronR"), ("UpperArmL", "PauldronL")]
        };
        foreach ((string boneName, string pref) in targets)
        {
            bool targetBack = pref.Contains("Back", StringComparison.Ordinal);
            ModelElementData a = ClothingAttachTarget(rig, boneName, pref.StartsWith("Decor", StringComparison.Ordinal) ? null : pref);
            ClothingPlateGrid(p, a, style, targetBack);
        }
    }

    private void ClothingPlateGrid(ClothingParams p, ModelElementData anchor, ClothingPlating style, bool back)
    {
        int rows = Math.Clamp(p.PlateRows, 1, 10);
        int cols = Math.Clamp(p.PlateCols, 1, 10);
        double sx = anchor.SizeX, sy = anchor.SizeY, sz = anchor.SizeZ;
        double size = Math.Max(0.4, p.PlateSize);
        double x = back ? sx + 0.2 : -0.2;
        double overlap = style == ClothingPlating.Scale ? 0.45 : 0.15;
        Random rng = new(p.DecorSeed + 211);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (p.Wear > 0 && rng.NextDouble() < p.Wear * 0.4) { rng.NextDouble(); continue; }
                double y = ModelCreatureLerp(sy * 0.12, sy * 0.9, rows <= 1 ? 0.5 : (double)r / (rows - 1));
                double z = ModelCreatureLerp(sz * 0.15, sz * 0.85, cols <= 1 ? 0.5 : (double)c / (cols - 1));
                double w = sz / cols * (1 + overlap);
                double h = sy / rows * (1 + overlap);
                double depth = style == ClothingPlating.Plate ? size * 1.3 : size;
                double tilt = style is ClothingPlating.Scale or ClothingPlating.Lamellar ? (back ? -10 : 10) : 0;
                ModelCreatureBox(anchor, [x, y, z], [depth, h, w], 0, 0, tilt, $"{style.ToString().ToLowerInvariant()}plate_{r}_{c}");
                if (style == ClothingPlating.Brigandine)
                {
                    ModelCreatureBox(anchor, [x + (back ? size * 0.4 : -size * 0.4), y, z], [size * 0.4, size * 0.4, size * 0.4], 0, 0, 0, $"rivet_{r}_{c}");
                }
            }
        }
    }

    private void ClothingBuildStraps(ClothingParams p, ModelElementData rig)
    {
        List<ClothingPatch> patches = ClothingSurfacePatches(rig, (ClothingSurface)p.StrapSurface, p);
        if (patches.Count == 0) return;
        ClothingPatch patch = patches[0];
        ModelElementData anchor = patch.Anchor;
        double midZ = (patch.Z[0] + patch.Z[1]) * 0.5;
        double y0 = patch.Y[0], y1 = patch.Y[1];
        double x = patch.X[0] - 0.2;
        int count = Math.Clamp(p.StrapCount, 1, 3);
        for (int i = 0; i < count; i++)
        {
            double tilt = i % 2 == 0 ? 28 : -28;
            ModelCreatureBox(anchor, [x, (y0 + y1) * 0.5, midZ], [Math.Max(0.4, p.Thickness), (y1 - y0) * 1.1, 1.4], 0, 0, tilt, $"strap{i + 1}");
            if (p.StrapBuckles)
            {
                ModelCreatureBox(anchor, [x - 0.3, (y0 + y1) * 0.5, midZ], [0.8, 1.4, 1.4], 0, 0, 0, $"buckle{i + 1}");
            }
        }
    }

    // ---- Collar / hood extras ----------------------------------------------

    private void ClothingApplyCollarAndHood(ClothingParams p, ModelElementData rig)
    {
        var collar = (ClothingCollarStyle)p.CollarStyle;
        if (collar != ClothingCollarStyle.None && (p.ClotheNeck || collar is ClothingCollarStyle.High or ClothingCollarStyle.Ruff or ClothingCollarStyle.Popped or ClothingCollarStyle.Cowl))
        {
            ModelElementData? neck = PlayerModelFind(rig, "Neck");
            if (neck != null)
            {
                double nx = neck.SizeX, ny = neck.SizeY, nz = neck.SizeZ;
                double pad = ClothingPad;
                ModelElementData a = ClothingAttachTarget(rig, "Neck", "Neckwear");
                switch (collar)
                {
                    case ClothingCollarStyle.High:
                        ModelCreatureBox(a, [nx * 0.5, ny * 0.8, nz * 0.5], [nx + pad * 2, ny * 1.1, nz + pad * 2], 0, 0, 0, "collarhigh");
                        break;
                    case ClothingCollarStyle.Popped:
                        ModelCreatureBox(a, [nx + pad, ny * 0.85, nz * 0.5], [nz, ny * 1.2, nz + pad * 2], 0, 0, -22, "collarpop");
                        break;
                    case ClothingCollarStyle.Ruff:
                        for (int i = 0; i < 10; i++)
                        {
                            double ang = i / 10.0 * 360.0;
                            double z = nz * 0.5 + Math.Cos(ang * Math.PI / 180.0) * (nz * 0.7);
                            double x = nx * 0.5 + Math.Sin(ang * Math.PI / 180.0) * (nx * 0.7);
                            ModelCreatureBox(a, [x, ny * 0.7, z], [1.2, ny * 0.9, 1.2], 0, 0, 0, $"ruff{i + 1}");
                        }
                        break;
                    case ClothingCollarStyle.Cowl:
                        ModelCreatureBox(a, [nx * 0.5, ny * 0.55, nz * 0.5], [nx + pad * 3, ny * 1.3, nz + pad * 3], 0, 0, 0, "cowlcollar");
                        break;
                }
            }
        }

        if (p.ClotheHead && (p.HoodPuff || p.HoodCowl || p.HoodPointed > 0.05f || p.HoodChestDrape))
        {
            ClothingHoodExtras(p, rig);
        }
    }

    private void ClothingHoodExtras(ClothingParams p, ModelElementData rig)
    {
        ModelElementData? head = PlayerModelFind(rig, "Head");
        if (head == null) return;
        double hx = head.SizeX, hy = head.SizeY, hz = head.SizeZ;
        double pad = ClothingPad;
        ModelElementData a = ClothingAttachTarget(rig, "Head", "Headwear");

        if (p.HoodPuff)
        {
            foreach (int side in ModelCreatureSides(2))
            {
                double z = side > 0 ? hz + pad : -pad;
                ModelCreatureBox(a, [hx * 0.62, hy * 0.5, z], [hx * 0.7, hy * 0.7, hz * 0.4], 0, 0, 0, side > 0 ? "hoodpuffR" : "hoodpuffL");
            }
        }
        if (p.HoodPointed > 0.05f)
        {
            ModelCreatureChain(a, [hx + pad, hy + pad * 0.5, hz * 0.5], 0, 0, 55, 1, 1,
                [[hz * 0.5, p.HoodPointed * 0.5, hz * 0.5], [hz * 0.25, p.HoodPointed * 0.5, hz * 0.25]], [0.0, 30.0], 2, "hoodtip", out _);
        }
        if (p.HoodCowl)
        {
            ModelCreatureChain(a, [hx + pad, 0.0, hz * 0.5], 0, 0, -10, 1, -1,
                [[Math.Max(0.4, p.Thickness), hy * 0.7, hz + pad * 2], [Math.Max(0.4, p.Thickness), hy * 0.7, hz + pad]], null, 2, "cowl", out _);
        }
        if (p.HoodChestDrape)
        {
            ModelElementData? torso = PlayerModelFind(rig, "UpperTorso");
            if (torso != null)
            {
                double tx = torso.SizeX, ty = torso.SizeY, tz = torso.SizeZ;
                ModelElementData ta = ClothingMakeAnchor(torso, "HoodDrape");
                int count = 7;
                for (int i = 0; i < count; i++)
                {
                    double t = (double)i / count;
                    double ang = 120 + t * 300;
                    double z = tz * 0.5 + Math.Cos(ang * Math.PI / 180.0) * (tz * 0.5 + pad);
                    double x = tx * 0.5 + Math.Sin(ang * Math.PI / 180.0) * (tx * 0.5 + pad);
                    ModelCreatureChain(ta, [x, ty + pad, z], 0, 0, -8, 1, -1, [[Math.Max(0.4, p.Thickness), 5.0, 2.0]], null, 2, $"drape{i + 1}", out _);
                }
            }
        }
    }

    // ---- Commit ------------------------------------------------------------

    private void ClothingCommit()
    {
        if (_modelDoc == null || _clothingPreviewRig == null) return;

        List<ModelElementData> anchors = ClothingAnchors(_clothingPreviewRig).ToList();
        if (anchors.Count == 0)
        {
            _clothingStatus = "Nothing to add - enable a coverage region first.";
            return;
        }

        ModelBeginEdit();
        int added = 0;
        foreach (ModelElementData anchor in anchors)
        {
            string bone = anchor.StepParentName;
            ModelElementData root = anchor.CloneSubtree();
            root.Parent = null;
            root.StepParentName = bone;
            root.Name = ModelGenerateElementName(root.Name);
            ClothingPrefixNames(root);
            _modelDoc.Roots.Add(root);
            added += ModelCreatureElementCount(root) + 1;
        }

        ClothingEnsureTextures();
        ModelMarkChanged();
        ModelEndEdit("Add clothing");
        _clothingStatus = $"Added {anchors.Count} step-parented piece(s) ({added} elements). Save the shape, then Export item JSON.";
    }

    private static void ClothingPrefixNames(ModelElementData anchor)
    {
        string baseName = anchor.Name;
        foreach (ModelElementData node in anchor.EnumerateSubtree())
        {
            if (ReferenceEquals(node, anchor)) continue;
            node.Name = $"{baseName}_{node.Name}";
        }
    }

    private void ClothingEnsureTextures()
    {
        if (_modelDoc == null) return;
        string basePath = _modelDoc.Textures.FirstOrDefault()?.Path ?? "block/cloth/plain";
        foreach (string code in new[] { ClothingResolveTexture(_clothingParams.Texture, "cloth"), _clothingParams.TrimTexture, _clothingParams.AccentTexture })
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (_modelDoc.Textures.Any(texture => string.Equals(texture.Code, code, StringComparison.Ordinal))) continue;
            _modelDoc.Textures.Add(new ModelTextureEntry { Code = code, Path = basePath });
        }
    }

    // ---- Item JSON ---------------------------------------------------------

    private string ClothingCategoryCode()
    {
        int index = Math.Clamp(_clothingParams.CategoryIndex, 0, ClothingSlots.Length - 1);
        return ClothingSlots[index].Code;
    }

    private static bool ClothingIsArmorCode(string code) => code is "armorhead" or "armorbody" or "armorlegs";

    /// <summary>Builds the wearable item JSON object. Pure (no IO) so it is unit-testable.</summary>
    private JObject ClothingBuildItemJson()
    {
        ClothingParams p = _clothingParams;
        string code = ModelSanitizeFileName(p.Code);
        string category = ClothingCategoryCode();
        string baseTex = ClothingResolveTexture(p.Texture, "cloth");
        string basePath = _modelDoc?.Textures.FirstOrDefault(texture => string.Equals(texture.Code, baseTex, StringComparison.Ordinal))?.Path
            ?? _modelDoc?.Textures.FirstOrDefault()?.Path
            ?? "block/cloth/plain";

        JObject attributes = new()
        {
            ["clothescategory"] = category,
            ["wearableAttachment"] = true
        };
        if (p.Warmth != 0f) attributes["warmth"] = Math.Round(p.Warmth, 3);
        if (category == "head" && p.RainProtection > 0f) attributes["rainProtectionPerc"] = Math.Round(p.RainProtection, 3);
        if (p.Foldable) attributes["isFoldable"] = true;

        JObject stats = ClothingStatModifiers(p);
        if (stats.HasValues) attributes["statModifiers"] = stats;

        if (ClothingIsArmorCode(category))
        {
            attributes["protectionModifiers"] = new JObject
            {
                ["relativeProtection"] = Math.Round(p.RelativeProtection, 3),
                ["flatDamageReduction"] = Math.Round(p.FlatDamageReduction, 3),
                ["protectionTier"] = Math.Clamp(p.ProtectionTier, 0, 99),
                ["highDamageTierResistant"] = p.HighDamageTierResistant
            };
        }

        JObject item = new()
        {
            ["code"] = code,
            ["class"] = "Item",
            ["maxstacksize"] = 1,
            ["storageFlags"] = 128,
            ["creativeinventory"] = new JObject { ["general"] = new JArray("*"), ["clothing"] = new JArray("*") },
            ["behaviors"] = new JArray(new JObject { ["name"] = "Wearable" }),
            ["attributes"] = attributes
        };
        if (p.VisibleDamage) ((JObject)item["attributes"]!)["visibleDamageEffect"] = true;
        if (p.Durability > 0) item["durability"] = p.Durability;

        item["shape"] = new JObject { ["base"] = ClothingShapePath(code) };
        item["textures"] = new JObject
        {
            // The wearer's body parts inherit the seraph texture; map it transparent so only the garment shows.
            ["seraph"] = new JObject { ["base"] = "game:block/transparent" },
            [baseTex] = new JObject { ["base"] = ClothingTexturePath(basePath) }
        };
        if (!string.IsNullOrWhiteSpace(p.TrimTexture) && !string.Equals(p.TrimTexture, baseTex, StringComparison.Ordinal))
        {
            ((JObject)item["textures"]!)[p.TrimTexture] = new JObject { ["base"] = ClothingTexturePath(basePath) };
        }

        item["guiTransform"] = new JObject
        {
            ["rotation"] = new JObject { ["x"] = 180, ["y"] = -51, ["z"] = 12 },
            ["origin"] = new JObject { ["x"] = 0.6, ["y"] = 1.3, ["z"] = 0.55 },
            ["scale"] = 2.2
        };
        return item;
    }

    private static JObject ClothingStatModifiers(ClothingParams p)
    {
        JObject stats = new();
        if (p.WalkSpeed != 0f) stats["walkSpeed"] = Math.Round(p.WalkSpeed, 3);
        if (p.HungerRate != 0f) stats["hungerrate"] = Math.Round(p.HungerRate, 3);
        if (p.HealingEffectiveness != 0f) stats["healingeffectivness"] = Math.Round(p.HealingEffectiveness, 3);
        if (p.RangedAccuracy != 0f) stats["rangedWeaponsAcc"] = Math.Round(p.RangedAccuracy, 3);
        if (p.RangedChargeSpeed != 0f) stats["rangedWeaponsSpeed"] = Math.Round(p.RangedChargeSpeed, 3);
        return stats;
    }

    private static string ClothingTexturePath(string path)
    {
        string trimmed = path.Replace('\\', '/');
        if (trimmed.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed["textures/".Length..];
        return trimmed;
    }

    private string ClothingShapePath(string code)
    {
        if (_modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.AssetPath))
        {
            string path = _modelDoc.AssetPath.Replace('\\', '/');
            if (path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)) path = path["shapes/".Length..];
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^".json".Length];
            return $"{_modelDoc.Domain}:{path}";
        }
        return $"game:{code}";
    }

    private void ClothingExportItem()
    {
        try
        {
            string code = ModelSanitizeFileName(_clothingParams.Code);
            string domain = _modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.Domain) ? _modelDoc.Domain : "game";
            string json = ClothingBuildItemJson().ToString(Formatting.Indented);
            string relative = Path.Combine("assets", domain, "itemtypes", "clothing", code + ".json");
            string outputPath = GetToolAuthoredAssetPath("clothing", relative);
            string writeError = WriteAuthoredFile(outputPath, json);
            _clothingStatus = string.IsNullOrEmpty(writeError)
                ? $"Exported item JSON to {outputPath}"
                : $"Item export failed: {writeError}";
        }
        catch (Exception exception)
        {
            _clothingStatus = $"Item export failed: {exception.Message}";
        }
    }

    // ---- Presets / randomize ----------------------------------------------

    private void ClothingApplyArchetype(ClothingArchetype archetype)
    {
        ClothingParams p = _clothingParams;
        // Reset coverage; keep identity/texture/base shape/seed.
        p.ClotheHead = p.ClotheFace = p.ClotheNeck = p.ClotheChest = p.ClotheBelly = p.ClotheShoulders =
            p.ClotheArms = p.ClotheHands = p.ClotheWaist = p.ClotheLegs = p.ClotheFeet = p.ClotheCape = false;
        p.OpenFront = p.Lapels = p.Scarf = false;
        p.SkirtLength = 0f;
        p.StyleIndex = (int)ClothingStyle.Cloth;
        // Reset the decoration layer so each preset starts clean (DecorSeed is kept for reproducibility).
        p.Tatters = p.Fringe = p.FurTrim = p.Branches = p.Vines = p.Leaves = p.Thorns =
            p.Studs = p.Gems = p.Spikes = p.Feathers = p.Mantle = p.MantleTatters = p.Straps =
            p.HoodPuff = p.HoodCowl = p.HoodChestDrape = p.Asymmetric = false;
        p.Layers = 1;
        p.Plating = (int)ClothingPlating.None;
        p.CollarStyle = (int)ClothingCollarStyle.Band;
        p.HoodPointed = 0f;
        p.Wear = 0f;

        switch (archetype)
        {
            case ClothingArchetype.Hood:
                p.CategoryIndex = ClothingSlotIndex("head"); p.ClotheHead = true; p.HeadStyle = (int)ClothingHeadStyle.Hood; p.Warmth = 1.5f; break;
            case ClothingArchetype.Cap:
                p.CategoryIndex = ClothingSlotIndex("head"); p.ClotheHead = true; p.HeadStyle = (int)ClothingHeadStyle.Cap; p.Warmth = 1f; break;
            case ClothingArchetype.Helmet:
                p.CategoryIndex = ClothingSlotIndex("armorhead"); p.ClotheHead = true; p.HeadStyle = (int)ClothingHeadStyle.Helmet;
                p.StyleIndex = (int)ClothingStyle.Plate; p.Warmth = 0f; p.ProtectionTier = 2; break;
            case ClothingArchetype.Mask:
                p.CategoryIndex = ClothingSlotIndex("face"); p.ClotheFace = true; p.FaceStyle = (int)ClothingFaceStyle.Full; p.Warmth = 0.5f; break;
            case ClothingArchetype.Necklace:
                p.CategoryIndex = ClothingSlotIndex("neck"); p.ClotheNeck = true; p.CollarHeight = 1.2f; p.Warmth = 0f; break;
            case ClothingArchetype.Scarf:
                p.CategoryIndex = ClothingSlotIndex("neck"); p.ClotheNeck = true; p.Scarf = true; p.CollarHeight = 2.5f; p.Warmth = 1f; break;
            case ClothingArchetype.Shirt:
                p.CategoryIndex = ClothingSlotIndex("upperbody"); p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true; p.SleeveLength = 1f; p.Warmth = 1f; break;
            case ClothingArchetype.Jacket:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover"); p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true;
                p.OpenFront = true; p.Lapels = true; p.StyleIndex = (int)ClothingStyle.Leather; p.Warmth = 2f; break;
            case ClothingArchetype.Coat:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover"); p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true;
                p.OpenFront = true; p.SkirtLength = 12f; p.SkirtSegments = 3; p.StyleIndex = (int)ClothingStyle.Leather; p.Warmth = 3f; break;
            case ClothingArchetype.Robe:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover"); p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true; p.ClotheNeck = true;
                p.SkirtLength = 16f; p.SkirtSegments = 4; p.SkirtFlare = 8f; p.Warmth = 2.5f; break;
            case ClothingArchetype.Tabard:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover"); p.ClotheChest = true; p.ClotheBelly = true; p.SkirtLength = 10f; p.SkirtSegments = 2; p.SkirtFlare = 2f; p.Warmth = 0.5f; break;
            case ClothingArchetype.Cuirass:
                p.CategoryIndex = ClothingSlotIndex("armorbody"); p.ClotheChest = true; p.ClotheBelly = true; p.ClotheShoulders = true;
                p.StyleIndex = (int)ClothingStyle.Plate; p.Warmth = 0f; p.ProtectionTier = 3; p.FlatDamageReduction = 1.5f; p.RelativeProtection = 0.4f; break;
            case ClothingArchetype.Pauldrons:
                p.CategoryIndex = ClothingSlotIndex("shoulder"); p.ClotheShoulders = true; p.PauldronPlates = 2; p.StyleIndex = (int)ClothingStyle.Plate; p.Warmth = 0f; break;
            case ClothingArchetype.Bracers:
                p.CategoryIndex = ClothingSlotIndex("arm"); p.ClotheArms = true; p.SleeveLength = 0.8f; p.Cuff = true; p.StyleIndex = (int)ClothingStyle.Leather; p.Warmth = 0.5f; break;
            case ClothingArchetype.Gloves:
                p.CategoryIndex = ClothingSlotIndex("hand"); p.ClotheHands = true; p.GloveLength = 3f; p.Warmth = 1f; break;
            case ClothingArchetype.Gauntlets:
                p.CategoryIndex = ClothingSlotIndex("hand"); p.ClotheHands = true; p.GloveLength = 6f; p.StyleIndex = (int)ClothingStyle.Plate; p.Warmth = 0f; break;
            case ClothingArchetype.Belt:
                p.CategoryIndex = ClothingSlotIndex("waist"); p.ClotheWaist = true; p.Pouches = 2; p.Warmth = 0f; break;
            case ClothingArchetype.Trousers:
                p.CategoryIndex = ClothingSlotIndex("lowerbody"); p.ClotheLegs = true; p.LegLength = 1f; p.ClotheWaist = true; p.BeltWidth = 1f; p.Buckle = false; p.Warmth = 1.5f; break;
            case ClothingArchetype.Skirt:
                p.CategoryIndex = ClothingSlotIndex("lowerbody"); p.ClotheBelly = true; p.SkirtLength = 12f; p.SkirtSegments = 3; p.SkirtFlare = 7f; p.Warmth = 1f; break;
            case ClothingArchetype.Greaves:
                p.CategoryIndex = ClothingSlotIndex("armorlegs"); p.ClotheLegs = true; p.LegLength = 1f; p.StyleIndex = (int)ClothingStyle.Plate; p.Warmth = 0f; p.ProtectionTier = 2; break;
            case ClothingArchetype.Boots:
                p.CategoryIndex = ClothingSlotIndex("foot"); p.ClotheFeet = true; p.BootHeight = 4f; p.Sole = true; p.Warmth = 1.5f; break;
            case ClothingArchetype.Cloak:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover"); p.ClotheCape = true; p.ClotheShoulders = false; p.CapeCollar = true; p.CapeLength = 18f; p.Warmth = 2f; break;
            case ClothingArchetype.Emblem:
                p.CategoryIndex = ClothingSlotIndex("emblem"); p.ClotheChest = true; p.OpenFront = false; p.Warmth = 0f; break;

            case ClothingArchetype.WildwoodCloak:
                // The requested piece: a semi-tattered cloak with branches (and leaves) sticking out of it.
                p.CategoryIndex = ClothingSlotIndex("upperbodyover");
                p.ClotheCape = true; p.CapeLength = 20f; p.CapeWidth = 11f; p.CapeSegments = 5; p.CapeCollar = true;
                p.ClotheHead = true; p.HeadStyle = (int)ClothingHeadStyle.Hood;
                p.Tatters = true; p.TatterEdge = (int)ClothingEdge.Cape; p.TatterCount = 12; p.TatterLength = 6f; p.TatterRaggedness = 0.7f;
                p.Branches = true; p.BranchSurface = (int)ClothingSurface.Cape; p.BranchCount = 9; p.BranchLength = 8f;
                p.BranchSegments = 3; p.BranchForks = 1; p.BranchLeaves = true; p.BranchCurve = 16f; p.BranchSpread = 38f;
                p.Leaves = true; p.LeafSurface = (int)ClothingSurface.Cape; p.LeafCount = 14;
                p.FurTrim = true; p.FurTrimEdge = (int)ClothingEdge.Collar;
                p.Wear = 0.25f; p.Asymmetric = true; p.Warmth = 2f; break;

            case ClothingArchetype.DruidRobe:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover");
                p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true; p.ClotheNeck = true;
                p.SkirtLength = 16f; p.SkirtSegments = 4; p.SkirtFlare = 8f;
                p.Mantle = true; p.MantleCount = 8; p.MantleTatters = true;
                p.Vines = true; p.VineSurface = (int)ClothingSurface.Shoulders; p.VineCount = 6;
                p.Leaves = true; p.LeafSurface = (int)ClothingSurface.Chest; p.LeafCount = 16;
                p.Tatters = true; p.TatterEdge = (int)ClothingEdge.Skirt; p.TatterCount = 14; p.TatterRaggedness = 0.6f;
                p.CollarStyle = (int)ClothingCollarStyle.Cowl; p.Warmth = 2.5f; break;

            case ClothingArchetype.PlatedArmor:
                p.CategoryIndex = ClothingSlotIndex("armorbody"); p.StyleIndex = (int)ClothingStyle.Plate;
                p.ClotheChest = true; p.ClotheBelly = true; p.ClotheShoulders = true; p.PauldronPlates = 2;
                p.ClotheArms = true; p.SleeveLength = 1f; p.ClotheLegs = true; p.LegLength = 1f;
                p.Plating = (int)ClothingPlating.Plate; p.PlatePlacement = 6; p.PlateRows = 4; p.PlateCols = 3;
                p.Studs = true; p.StudSurface = (int)ClothingSurface.Chest; p.StudCount = 10;
                p.Straps = true; p.Warmth = 0f; p.ProtectionTier = 4; p.FlatDamageReduction = 2f; p.RelativeProtection = 0.5f; break;

            case ClothingArchetype.Brigand:
                p.CategoryIndex = ClothingSlotIndex("upperbody"); p.StyleIndex = (int)ClothingStyle.Leather;
                p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true; p.SleeveLength = 0.6f;
                p.Plating = (int)ClothingPlating.Brigandine; p.PlatePlacement = 0; p.PlateRows = 4; p.PlateCols = 4;
                p.Studs = true; p.Straps = true; p.ClotheWaist = true; p.Pouches = 2; p.Wear = 0.3f; p.Warmth = 1.5f; break;

            case ClothingArchetype.RegalMantle:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover");
                p.ClotheChest = true; p.ClotheBelly = true; p.ClotheCape = true; p.CapeLength = 18f; p.CapeWidth = 12f;
                p.Mantle = true; p.MantleCount = 10; p.FurTrim = true; p.FurTrimEdge = (int)ClothingEdge.Collar;
                p.Gems = true; p.GemSurface = (int)ClothingSurface.Chest; p.GemCount = 6;
                p.Feathers = true; p.FeatherSurface = (int)ClothingSurface.Shoulders; p.FeatherCount = 6;
                p.CollarStyle = (int)ClothingCollarStyle.High; p.Warmth = 2f; break;

            case ClothingArchetype.RaggedShroud:
                p.CategoryIndex = ClothingSlotIndex("upperbodyover");
                p.ClotheChest = true; p.ClotheBelly = true; p.ClotheArms = true; p.SleeveLength = 1f;
                p.SkirtLength = 14f; p.SkirtSegments = 4;
                p.Tatters = true; p.TatterEdge = (int)ClothingEdge.Cape_Skirt; p.TatterCount = 20; p.TatterLength = 7f; p.TatterRaggedness = 0.9f;
                p.ClotheCape = true; p.CapeLength = 16f;
                p.Wear = 0.6f; p.Asymmetric = true; p.HoodChestDrape = true; p.ClotheHead = true; p.HeadStyle = (int)ClothingHeadStyle.Hood; p.Warmth = 1f; break;
        }
    }

    private void ClothingRandomize(ClothingParams p)
    {
        Random rng = new(p.Seed);
        p.Looseness = 0.3f + (float)rng.NextDouble() * 1.2f;
        p.Thickness = 0.4f + (float)rng.NextDouble() * 1.0f;
        p.StyleIndex = rng.Next(0, 3);
        if (p.ClotheHead) p.HeadStyle = rng.Next(0, 5);
        if (p.ClotheFace) p.FaceStyle = rng.Next(0, 4);
        if (p.SkirtLength > 0.01f) { p.SkirtLength = 6f + (float)rng.NextDouble() * 16f; p.SkirtSegments = rng.Next(2, 5); }
        if (p.ClotheCape) { p.CapeLength = 10f + (float)rng.NextDouble() * 16f; p.CapeWidth = 6f + (float)rng.NextDouble() * 6f; }
        if (p.ClotheArms) p.SleeveLength = (float)rng.NextDouble();
        if (p.ClotheLegs) p.LegLength = 0.4f + (float)rng.NextDouble() * 0.6f;

        // Reroll the scattered decoration layout and jitter the enabled ornament amounts.
        p.DecorSeed = p.Seed;
        if (p.Branches) { p.BranchCount = rng.Next(4, 16); p.BranchForks = rng.Next(0, 3); p.BranchSpread = 20f + (float)rng.NextDouble() * 50f; }
        if (p.Tatters) { p.TatterCount = rng.Next(6, 24); p.TatterRaggedness = 0.3f + (float)rng.NextDouble() * 0.7f; }
        if (p.Leaves) p.LeafCount = rng.Next(6, 24);
        if (p.Studs) p.StudCount = rng.Next(8, 30);
        if (p.Spikes) p.SpikeCount = rng.Next(4, 14);
        if (p.Wear > 0.01f) p.Wear = (float)rng.NextDouble() * 0.6f;
    }

    // ---- UI ----------------------------------------------------------------

    private void DrawClothingPanel()
    {
        if (!_clothingWindowOpen) return;
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one first (a wearable is best on a fresh shape).");
            return;
        }

        bool changed = false;
        ClothingParams p = _clothingParams;

        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Preset##clothing-archetype", ref _clothingArchetypeIndex, ClothingArchetypeLabels, ClothingArchetypeLabels.Length))
        {
            ClothingApplyArchetype((ClothingArchetype)_clothingArchetypeIndex);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Apply##clothing-preset"))
        {
            ClothingApplyArchetype((ClothingArchetype)_clothingArchetypeIndex);
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Set the slot, coverage regions, sizes and attributes to this garment's sensible defaults (keeps code, texture, base shape and seed).");

        ImGui.SetNextItemWidth(120f);
        changed |= ImGui.InputInt("Seed##clothing-seed", ref p.Seed);
        ImGui.SameLine();
        if (ImGui.Button("Randomize##clothing-randomize"))
        {
            p.Seed++;
            ClothingRandomize(p);
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Jitter fit, style and the enabled regions' sizes for the current seed (reproducible).");

        ImGui.SetNextItemWidth(190f);
        changed |= ImGui.InputText("Code##clothing-code", ref p.Code, 64);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item code / filename (sanitized to lowercase-kebab). Used as the JSON code and shape filename.");
        ImGui.SetNextItemWidth(190f);
        changed |= ImGui.InputText("Name##clothing-name", ref p.Name, 64);

        ImGui.SetNextItemWidth(260f);
        changed |= ImGui.Combo("Base shape##clothing-base", ref p.BaseShapeIndex, PlayerModelBaseShapeLabels, PlayerModelBaseShapeLabels.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The wearer rig the garment fits and step-parents onto. Seraph = vanilla humanoid; Digitigrade = beast legs/tail/snout (PlayerModelLib).");

        ImGui.SetNextItemWidth(260f);
        changed |= ImGui.Combo("Slot (clothescategory)##clothing-cat", ref p.CategoryIndex, ClothingSlotLabels, ClothingSlotLabels.Length);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("EnumCharacterDressType slot written into the item JSON. The three armor slots add protectionModifiers.");

        ImGui.SetNextItemWidth(180f);
        changed |= ImGui.Combo("Style##clothing-style", ref p.StyleIndex, ClothingStyleLabels, ClothingStyleLabels.Length);

        List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
        ImGui.SetNextItemWidth(190f);
        if (ModelFilteredCombo("Texture##clothing-texture", p.Texture, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "garment texture code"))
        {
            p.Texture = pickedTexture;
            changed = true;
        }
        ImGui.SetNextItemWidth(190f);
        if (ModelFilteredCombo("Trim texture##clothing-trim-texture", p.TrimTexture, textureCodes, out string pickedTrim, allowCustom: true, filterHint: "trim/cuff/sole texture (optional)"))
        {
            p.TrimTexture = pickedTrim;
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Optional second texture for cuffs, belts, soles, collars and brims. Blank = use the main texture.");
        ImGui.SetNextItemWidth(190f);
        if (ModelFilteredCombo("Accent texture##clothing-accent-texture", p.AccentTexture, textureCodes, out string pickedAccent, allowCustom: true, filterHint: "organic/gem accent texture (optional)"))
        {
            p.AccentTexture = pickedAccent;
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Optional third texture for organic and jewelled accents (branches, leaves, vines, feathers, gems). Blank = use the trim texture.");

        ImGui.Checkbox("Advanced##clothing-advanced", ref _clothingAdvanced);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reveal per-region fine sizes and the full wearable attribute set (warmth, protection, stat modifiers).");

        ImGui.Separator();
        changed |= DrawClothingSections(p);

        if (changed) _clothingPreviewDirty = true;
        if (_clothingPreviewDirty)
        {
            _clothingPreviewDirty = false;
            _clothingPreviewRig = ClothingBuildRig(out _clothingPreviewError);
            _clothingPreviewCount = _clothingPreviewRig == null ? 0 : ClothingGarmentElementCount(_clothingPreviewRig);
        }

        ImGui.Separator();
        if (!string.IsNullOrEmpty(_clothingPreviewError))
        {
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _clothingPreviewError);
        }
        else
        {
            ImGui.TextUnformatted($"{_clothingPreviewCount} garment element(s) / {ModelCreatureMaxElements} max. Green = clothes, grey = wearer body.");
        }

        bool canCreate = _clothingPreviewRig != null && string.IsNullOrEmpty(_clothingPreviewError);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create shape##clothing-create"))
        {
            ClothingCommit();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add the step-parented garment pieces to the shape (one undo step). The wearer body is NOT added - it comes from the entity at runtime.");
        if (!canCreate) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Export item JSON##clothing-export"))
        {
            ClothingExportItem();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Write the wearable itemtype JSON (clothescategory + attributes + shape path) to the InGameDevTools authored-assets folder. Save the shape first so the shape path resolves.");
        ImGui.SameLine();
        if (ImGui.Button("Close##clothing-close"))
        {
            _clothingWindowOpen = false;
        }

        if (!string.IsNullOrEmpty(_clothingStatus)) ImGui.TextWrapped(_clothingStatus);
    }

    private bool DrawClothingSections(ClothingParams p)
    {
        bool changed = false;
        bool advanced = _clothingAdvanced;

        if (ImGui.CollapsingHeader("Fit##clothing-fit", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderFloat("Looseness##clothing-loose", ref p.Looseness, 0f, 3f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("How far the garment stands off the body surface (shape units).");
            changed |= ImGui.SliderFloat("Thickness##clothing-thick", ref p.Thickness, 0.1f, 3f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Garment wall thickness (scaled up by the Plate/Leather style).");
        }

        if (ImGui.CollapsingHeader("Head / face##clothing-head", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("Headwear##clothing-c-head", ref p.ClotheHead);
            if (p.ClotheHead)
            {
                ImGui.SetNextItemWidth(160f);
                changed |= ImGui.Combo("Head style##clothing-head-style", ref p.HeadStyle, ClothingHeadStyleLabels, ClothingHeadStyleLabels.Length);
                if (p.HeadStyle is (int)ClothingHeadStyle.Cap or (int)ClothingHeadStyle.TallHat)
                    changed |= ImGui.DragFloat("Brim##clothing-brim", ref p.Brim, 0.1f, 0f, 16f, "%.2f");
                if (p.HeadStyle == (int)ClothingHeadStyle.TallHat)
                    changed |= ImGui.DragFloat("Crown height##clothing-crown", ref p.CrownHeight, 0.25f, 0.5f, 32f, "%.2f");
                if (p.HeadStyle == (int)ClothingHeadStyle.Hood)
                    changed |= ImGui.Checkbox("Hood down##clothing-hood-down", ref p.HoodDown);
            }
            changed |= ImGui.Checkbox("Facewear / mask##clothing-c-face", ref p.ClotheFace);
            if (p.ClotheFace)
            {
                ImGui.SetNextItemWidth(160f);
                changed |= ImGui.Combo("Face style##clothing-face-style", ref p.FaceStyle, ClothingFaceStyleLabels, ClothingFaceStyleLabels.Length);
            }
            changed |= ImGui.Checkbox("Neckwear##clothing-c-neck", ref p.ClotheNeck);
            if (p.ClotheNeck)
            {
                changed |= ImGui.DragFloat("Collar height##clothing-collar", ref p.CollarHeight, 0.1f, 0.25f, 12f, "%.2f");
                changed |= ImGui.Checkbox("Scarf drape##clothing-scarf", ref p.Scarf);
                if (p.Scarf) changed |= ImGui.DragFloat("Scarf length##clothing-scarf-len", ref p.ScarfLength, 0.25f, 1f, 32f, "%.2f");
            }
        }

        if (ImGui.CollapsingHeader("Torso / arms##clothing-torso", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("Chest##clothing-c-chest", ref p.ClotheChest);
            changed |= ImGui.Checkbox("Belly / pelvis##clothing-c-belly", ref p.ClotheBelly);
            if (p.ClotheChest)
            {
                changed |= ImGui.Checkbox("Open front##clothing-openfront", ref p.OpenFront);
                changed |= ImGui.Checkbox("Lapels##clothing-lapels", ref p.Lapels);
            }
            if (p.ClotheBelly)
            {
                changed |= ImGui.DragFloat("Skirt / coat-tail length##clothing-skirt-len", ref p.SkirtLength, 0.25f, 0f, 40f, "%.2f");
                if (p.SkirtLength > 0.01f)
                {
                    changed |= ImGui.SliderInt("Skirt segments##clothing-skirt-seg", ref p.SkirtSegments, 1, 6);
                    changed |= ImGui.DragFloat("Skirt flare##clothing-skirt-flare", ref p.SkirtFlare, 0.25f, 0f, 24f, "%.2f");
                }
            }
            changed |= ImGui.Checkbox("Shoulders / pauldrons##clothing-c-shoulder", ref p.ClotheShoulders);
            if (p.ClotheShoulders)
            {
                changed |= ImGui.DragFloat("Pauldron size##clothing-pauldron", ref p.PauldronSize, 0.25f, 0.5f, 16f, "%.2f");
                changed |= ImGui.SliderInt("Pauldron plates##clothing-pauldron-plates", ref p.PauldronPlates, 1, 4);
            }
            changed |= ImGui.Checkbox("Sleeves / arms##clothing-c-arms", ref p.ClotheArms);
            if (p.ClotheArms)
            {
                changed |= ImGui.SliderFloat("Sleeve length##clothing-sleeve", ref p.SleeveLength, 0f, 1f, "%.2f");
                changed |= ImGui.Checkbox("Cuff##clothing-cuff", ref p.Cuff);
            }
            changed |= ImGui.Checkbox("Gloves / hands##clothing-c-hands", ref p.ClotheHands);
            if (p.ClotheHands)
                changed |= ImGui.DragFloat("Glove length##clothing-glove", ref p.GloveLength, 0.25f, 0.5f, 16f, "%.2f");
        }

        if (ImGui.CollapsingHeader("Waist / legs / feet / cape##clothing-lower"))
        {
            changed |= ImGui.Checkbox("Belt / waist##clothing-c-waist", ref p.ClotheWaist);
            if (p.ClotheWaist)
            {
                changed |= ImGui.DragFloat("Belt width##clothing-belt-w", ref p.BeltWidth, 0.1f, 0.25f, 8f, "%.2f");
                changed |= ImGui.Checkbox("Buckle##clothing-buckle", ref p.Buckle);
                changed |= ImGui.SliderInt("Pouches##clothing-pouches", ref p.Pouches, 0, 4);
            }
            changed |= ImGui.Checkbox("Legs / trousers##clothing-c-legs", ref p.ClotheLegs);
            if (p.ClotheLegs)
                changed |= ImGui.SliderFloat("Leg length##clothing-leg-len", ref p.LegLength, 0f, 1f, "%.2f");
            changed |= ImGui.Checkbox("Boots / feet##clothing-c-feet", ref p.ClotheFeet);
            if (p.ClotheFeet)
            {
                changed |= ImGui.DragFloat("Boot height##clothing-boot-h", ref p.BootHeight, 0.25f, 0f, 24f, "%.2f");
                changed |= ImGui.Checkbox("Sole##clothing-sole", ref p.Sole);
            }
            changed |= ImGui.Checkbox("Cape / cloak##clothing-c-cape", ref p.ClotheCape);
            if (p.ClotheCape)
            {
                changed |= ImGui.DragFloat("Cape length##clothing-cape-len", ref p.CapeLength, 0.25f, 1f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Cape width##clothing-cape-w", ref p.CapeWidth, 0.25f, 1f, 24f, "%.2f");
                changed |= ImGui.SliderInt("Cape segments##clothing-cape-seg", ref p.CapeSegments, 1, 8);
                changed |= ImGui.Checkbox("Cape collar##clothing-cape-collar", ref p.CapeCollar);
            }
        }

        if (ImGui.CollapsingHeader("Edges: tatters / fringe / fur##clothing-edges"))
        {
            changed |= ImGui.Checkbox("Tatters (ragged torn strips)##clothing-tatters", ref p.Tatters);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hang irregular torn strips off an edge - the classic shredded-cloak/ghoul-robe look.");
            if (p.Tatters)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Tatter edge##clothing-tatter-edge", ref p.TatterEdge, ClothingEdgeLabels, ClothingEdgeLabels.Length);
                changed |= ImGui.SliderInt("Tatter count##clothing-tatter-n", ref p.TatterCount, 1, 40);
                changed |= ImGui.DragFloat("Tatter length##clothing-tatter-len", ref p.TatterLength, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.SliderFloat("Raggedness##clothing-tatter-rag", ref p.TatterRaggedness, 0f, 1f, "%.2f");
                changed |= ImGui.DragFloat("Tatter width##clothing-tatter-w", ref p.TatterWidth, 0.1f, 0.3f, 6f, "%.2f");
            }
            changed |= ImGui.Checkbox("Fringe / tassels##clothing-fringe", ref p.Fringe);
            if (p.Fringe)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Fringe edge##clothing-fringe-edge", ref p.FringeEdge, ClothingEdgeLabels, ClothingEdgeLabels.Length);
                changed |= ImGui.SliderInt("Fringe count##clothing-fringe-n", ref p.FringeCount, 1, 60);
                changed |= ImGui.DragFloat("Fringe length##clothing-fringe-len", ref p.FringeLength, 0.1f, 0.25f, 16f, "%.2f");
            }
            changed |= ImGui.Checkbox("Fur trim##clothing-fur", ref p.FurTrim);
            if (p.FurTrim)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Fur edge##clothing-fur-edge", ref p.FurTrimEdge, ClothingEdgeLabels, ClothingEdgeLabels.Length);
                changed |= ImGui.DragFloat("Fur thickness##clothing-fur-th", ref p.FurTrimThickness, 0.1f, 0.25f, 8f, "%.2f");
            }
        }

        if (ImGui.CollapsingHeader("Organic: branches / vines / leaves / thorns##clothing-organic"))
        {
            changed |= ImGui.Checkbox("Branches / twigs##clothing-branches", ref p.Branches);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scatter tapering, optionally forking twigs that stick out of the garment, with optional leaf clusters at the tips.");
            if (p.Branches)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Branch surface##clothing-branch-surf", ref p.BranchSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Branch count##clothing-branch-n", ref p.BranchCount, 1, 40);
                changed |= ImGui.DragFloat("Branch length##clothing-branch-len", ref p.BranchLength, 0.25f, 1f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Branch thickness##clothing-branch-th", ref p.BranchThickness, 0.05f, 0.2f, 6f, "%.2f");
                changed |= ImGui.SliderInt("Branch segments##clothing-branch-seg", ref p.BranchSegments, 1, 6);
                changed |= ImGui.SliderInt("Forks per branch##clothing-branch-fork", ref p.BranchForks, 0, 3);
                changed |= ImGui.DragFloat("Branch curve##clothing-branch-curve", ref p.BranchCurve, 0.5f, -45f, 45f, "%.1f deg");
                changed |= ImGui.DragFloat("Aim spread##clothing-branch-spread", ref p.BranchSpread, 0.5f, 0f, 90f, "%.1f deg");
                changed |= ImGui.Checkbox("Leaves at tips##clothing-branch-leaves", ref p.BranchLeaves);
            }
            changed |= ImGui.Checkbox("Vines / tendrils##clothing-vines", ref p.Vines);
            if (p.Vines)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Vine surface##clothing-vine-surf", ref p.VineSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Vine count##clothing-vine-n", ref p.VineCount, 1, 30);
                changed |= ImGui.DragFloat("Vine length##clothing-vine-len", ref p.VineLength, 0.25f, 1f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Vine thickness##clothing-vine-th", ref p.VineThickness, 0.05f, 0.15f, 4f, "%.2f");
            }
            changed |= ImGui.Checkbox("Leaves (scattered)##clothing-leaves", ref p.Leaves);
            if (p.Leaves)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Leaf surface##clothing-leaf-surf", ref p.LeafSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Leaf count##clothing-leaf-n", ref p.LeafCount, 1, 60);
                changed |= ImGui.DragFloat("Leaf size##clothing-leaf-size", ref p.LeafSize, 0.1f, 0.3f, 8f, "%.2f");
            }
            changed |= ImGui.Checkbox("Thorns##clothing-thorns", ref p.Thorns);
            if (p.Thorns)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Thorn surface##clothing-thorn-surf", ref p.ThornSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Thorn count##clothing-thorn-n", ref p.ThornCount, 1, 40);
                changed |= ImGui.DragFloat("Thorn length##clothing-thorn-len", ref p.ThornLength, 0.1f, 0.25f, 12f, "%.2f");
            }
        }

        if (ImGui.CollapsingHeader("Ornaments: studs / gems / spikes / feathers##clothing-ornaments"))
        {
            changed |= ImGui.Checkbox("Studs / rivets##clothing-studs", ref p.Studs);
            if (p.Studs)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Stud surface##clothing-stud-surf", ref p.StudSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Stud count##clothing-stud-n", ref p.StudCount, 1, 60);
                changed |= ImGui.DragFloat("Stud size##clothing-stud-size", ref p.StudSize, 0.05f, 0.15f, 4f, "%.2f");
            }
            changed |= ImGui.Checkbox("Gems / jewels##clothing-gems", ref p.Gems);
            if (p.Gems)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Gem surface##clothing-gem-surf", ref p.GemSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Gem count##clothing-gem-n", ref p.GemCount, 1, 30);
                changed |= ImGui.DragFloat("Gem size##clothing-gem-size", ref p.GemSize, 0.05f, 0.2f, 6f, "%.2f");
            }
            changed |= ImGui.Checkbox("Spikes##clothing-spikes", ref p.Spikes);
            if (p.Spikes)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Spike surface##clothing-spike-surf", ref p.SpikeSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Spike count##clothing-spike-n", ref p.SpikeCount, 1, 30);
                changed |= ImGui.DragFloat("Spike length##clothing-spike-len", ref p.SpikeLength, 0.1f, 0.5f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Spike size##clothing-spike-size", ref p.SpikeSize, 0.05f, 0.2f, 4f, "%.2f");
            }
            changed |= ImGui.Checkbox("Feathers / plume##clothing-feathers", ref p.Feathers);
            if (p.Feathers)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Feather surface##clothing-feather-surf", ref p.FeatherSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Feather count##clothing-feather-n", ref p.FeatherCount, 1, 24);
                changed |= ImGui.DragFloat("Feather length##clothing-feather-len", ref p.FeatherLength, 0.25f, 1f, 24f, "%.2f");
                changed |= ImGui.DragFloat("Feather spread##clothing-feather-spread", ref p.FeatherSpread, 0.5f, 0f, 120f, "%.1f deg");
            }
        }

        if (ImGui.CollapsingHeader("Layers / mantle / plating / straps##clothing-layers"))
        {
            changed |= ImGui.Checkbox("Mantle (capelet)##clothing-mantle", ref p.Mantle);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("A fan of draped panels around the shoulders (the acolyte/druid capelet).");
            if (p.Mantle)
            {
                changed |= ImGui.SliderInt("Mantle panels##clothing-mantle-n", ref p.MantleCount, 3, 16);
                changed |= ImGui.DragFloat("Mantle length##clothing-mantle-len", ref p.MantleLength, 0.25f, 1f, 24f, "%.2f");
                changed |= ImGui.DragFloat("Mantle flare##clothing-mantle-flare", ref p.MantleFlare, 0.1f, 0f, 12f, "%.2f");
                changed |= ImGui.Checkbox("Ragged mantle##clothing-mantle-tat", ref p.MantleTatters);
            }
            changed |= ImGui.SliderInt("Cloth layers##clothing-layers-n", ref p.Layers, 1, 4);
            if (p.Layers > 1) changed |= ImGui.DragFloat("Layer offset##clothing-layer-off", ref p.LayerOffset, 0.05f, 0.2f, 4f, "%.2f");

            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.Combo("Armor plating##clothing-plating", ref p.Plating, ClothingPlatingLabels, ClothingPlatingLabels.Length);
            if ((ClothingPlating)p.Plating != ClothingPlating.None)
            {
                ImGui.SetNextItemWidth(160f);
                changed |= ImGui.Combo("Plate placement##clothing-plate-place", ref p.PlatePlacement, ClothingPlatePlacementLabels, ClothingPlatePlacementLabels.Length);
                changed |= ImGui.SliderInt("Plate rows##clothing-plate-rows", ref p.PlateRows, 1, 10);
                changed |= ImGui.SliderInt("Plate cols##clothing-plate-cols", ref p.PlateCols, 1, 10);
                changed |= ImGui.DragFloat("Plate size##clothing-plate-size", ref p.PlateSize, 0.05f, 0.3f, 6f, "%.2f");
            }
            changed |= ImGui.Checkbox("Straps##clothing-straps", ref p.Straps);
            if (p.Straps)
            {
                ImGui.SetNextItemWidth(150f);
                changed |= ImGui.Combo("Strap surface##clothing-strap-surf", ref p.StrapSurface, ClothingSurfaceLabels, ClothingSurfaceLabels.Length);
                changed |= ImGui.SliderInt("Strap count##clothing-strap-n", ref p.StrapCount, 1, 3);
                changed |= ImGui.Checkbox("Strap buckles##clothing-strap-buckle", ref p.StrapBuckles);
            }
        }

        if (ImGui.CollapsingHeader("Collar & hood extras##clothing-collar-hood"))
        {
            ImGui.SetNextItemWidth(160f);
            changed |= ImGui.Combo("Collar style##clothing-collar", ref p.CollarStyle, ClothingCollarStyleLabels, ClothingCollarStyleLabels.Length);
            changed |= ImGui.Checkbox("Hood puffs##clothing-hood-puff", ref p.HoodPuff);
            changed |= ImGui.Checkbox("Hood cowl drape##clothing-hood-cowl", ref p.HoodCowl);
            changed |= ImGui.DragFloat("Hood pointed tip##clothing-hood-point", ref p.HoodPointed, 0.25f, 0f, 24f, "%.2f");
            changed |= ImGui.Checkbox("Hood chest drape (capelet)##clothing-hood-drape", ref p.HoodChestDrape);
        }

        if (ImGui.CollapsingHeader("Scatter seed & wear##clothing-decor-global"))
        {
            changed |= ImGui.InputInt("Decoration seed##clothing-decor-seed", ref p.DecorSeed);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Seeds every scattered ornament/tatter so the result is reproducible. Bump it to reroll the random placement.");
            changed |= ImGui.SliderFloat("Wear / damage##clothing-wear", ref p.Wear, 0f, 1f, "%.2f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Randomly drops and shortens tatters, plates and ornaments for a battle-worn, decayed look.");
            changed |= ImGui.Checkbox("Asymmetric##clothing-asym", ref p.Asymmetric);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Bias scatter toward one side for an irregular, hand-made feel.");
        }

        if (ImGui.CollapsingHeader("Wearable attributes (item JSON)##clothing-attr", advanced ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            changed |= ImGui.DragFloat("Warmth (C)##clothing-warmth", ref p.Warmth, 0.05f, -5f, 12f, "%.2f");
            if (ClothingCategoryCode() == "head")
                changed |= ImGui.SliderFloat("Rain protection##clothing-rain", ref p.RainProtection, 0f, 1f, "%.2f");
            changed |= ImGui.DragInt("Durability##clothing-dura", ref p.Durability, 1f, 0, 100000);
            changed |= ImGui.Checkbox("Foldable##clothing-foldable", ref p.Foldable);
            changed |= ImGui.Checkbox("Visible damage effect##clothing-visdmg", ref p.VisibleDamage);

            if (ClothingIsArmorCode(ClothingCategoryCode()))
            {
                ImGui.SeparatorText("Protection (armor slot)");
                changed |= ImGui.SliderInt("Protection tier##clothing-prot-tier", ref p.ProtectionTier, 0, 12);
                changed |= ImGui.DragFloat("Flat reduction##clothing-prot-flat", ref p.FlatDamageReduction, 0.05f, 0f, 20f, "%.2f");
                changed |= ImGui.SliderFloat("Relative protection##clothing-prot-rel", ref p.RelativeProtection, 0f, 1f, "%.2f");
                changed |= ImGui.Checkbox("High-tier resistant##clothing-prot-hi", ref p.HighDamageTierResistant);
            }

            if (advanced)
            {
                ImGui.SeparatorText("Stat modifiers");
                changed |= ImGui.DragFloat("Walk speed##clothing-stat-walk", ref p.WalkSpeed, 0.005f, -0.5f, 0.5f, "%.3f");
                changed |= ImGui.DragFloat("Hunger rate##clothing-stat-hunger", ref p.HungerRate, 0.005f, -0.5f, 0.5f, "%.3f");
                changed |= ImGui.DragFloat("Healing effectiveness##clothing-stat-heal", ref p.HealingEffectiveness, 0.005f, -1f, 1f, "%.3f");
                changed |= ImGui.DragFloat("Ranged accuracy##clothing-stat-acc", ref p.RangedAccuracy, 0.005f, -0.5f, 0.5f, "%.3f");
                changed |= ImGui.DragFloat("Ranged charge speed##clothing-stat-charge", ref p.RangedChargeSpeed, 0.005f, -0.5f, 0.5f, "%.3f");
            }
        }

        return changed;
    }

    // ---- Ghost -------------------------------------------------------------

    private void DrawModelClothingGhost(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (!_clothingWindowOpen || _clothingPreviewRig == null || !string.IsNullOrEmpty(_clothingPreviewError)) return;

        // Garment = anything beneath an anchor; everything else is the wearer body shown for fit.
        HashSet<ModelElementData> garment = new(ReferenceEqualityComparer.Instance);
        foreach (ModelElementData anchor in ClothingAnchors(_clothingPreviewRig))
        {
            foreach (ModelElementData node in anchor.EnumerateSubtree())
            {
                garment.Add(node);
            }
        }

        uint green = ImGui.ColorConvertFloat4ToU32(new NVector4(0.4f, 0.95f, 0.55f, 0.75f));
        uint body = ImGui.ColorConvertFloat4ToU32(new NVector4(0.55f, 0.6f, 0.7f, 0.32f));
        foreach (ModelElementData element in _clothingPreviewRig.EnumerateSubtree())
        {
            if (element.SizeX <= 0.0001 || element.SizeY <= 0.0001 || element.SizeZ <= 0.0001) continue;
            if (!string.IsNullOrEmpty(element.StepParentName)) continue;   // skip the face-less anchor box itself
            bool isGarment = garment.Contains(element);
            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            foreach ((int a, int b) in ModelBoxEdges)
            {
                DrawModelViewportLine(drawList, camera, corners[a], corners[b], isGarment ? green : body, isGarment ? 1.4f : 1f);
            }
        }
    }
}
