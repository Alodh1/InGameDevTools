using System.Globalization;
using ImGuiNET;
using InGameDevTools.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

// PlayerModelLib generator: builds a player-model shape compatible with the PlayerModelLib mod, plus a
// drop-in customplayermodels config JSON. Unlike the (quadruped) creature generator, a PML model is an
// ordinary upright humanoid shape whose joints MUST be named exactly as the chosen base shape's
// KeyElements - PlayerModelLib binds armor, the auto-merged seraph animations and the item anchors to
// those names. So this mode reproduces the canonical seraph / digitigrade skeletons verbatim (names,
// from/to, rotations, the Eyes/Back attachment points) and then layers parametric cosmetic features
// (muzzle, ears, horns, eyes, jaw, crest, tail, claws, fluff, dorsal ridge) parented to the named
// joints, so each rides a real animated bone for free.
//
// Coordinate frame is the seraph frame (16 = 1 block), NOT the creature generator's +X-forward frame:
//   the body faces -X (the head's forward face is at local X = 0), up is +Y, lateral is Z. The skeleton
//   already bakes that orientation, so no group yaw is applied.
public sealed partial class DebugWindowManager
{
    private enum PlayerModelBaseShape
    {
        Seraph,
        Digitigrade
    }

    private static readonly string[] PlayerModelBaseShapeLabels =
    [
        "Seraph (humanoid)",
        "Digitigrade (beast legs + tail + snout)"
    ];

    // Faceless 0-face anchor boxes the digitigrade base ships so headgear fits a snouted head. They must
    // stay face-less, so face assignment skips them.
    private static readonly HashSet<string> PlayerModelFacelessAnchors =
        new(["HeadWithSnout", "AboveSnout", "SnoutHeadWide"], StringComparer.Ordinal);

    private sealed class PlayerModelParams
    {
        // Identity / config
        public string Code = "custommodel";
        public string Name = "Custom Model";
        public string Group = "beasts";
        public string Texture = "";
        public int BaseShapeIndex = (int)PlayerModelBaseShape.Seraph;

        // Region scales (uniform, about each region's joint pivot; 1 = exact base shape).
        public float HeadScale = 1f;
        public float ArmScale = 1f;
        public float LegScale = 1f;

        // Config values (written into the customplayermodels JSON).
        public float CollisionWidth = 0.4f;
        public float CollisionHeight = 1.85f;
        public float EyeHeight = 1.7f;
        public float SizeMin = 0.9f;
        public float SizeMax = 1.1f;
        public float ModelSizeFactor = 1f;
        public bool CreateCuboidsForAttachmentPoints = true;
        public string AddTagsCsv = "beast";
        public string RemoveTagsCsv = "seraph";
        public string ClassesCsv = "";
        public string TraitsCsv = "";
        public bool StarterSkinPart = true;

        // Muzzle (a 3D snout for seraph; the digitigrade base already has one, so this extends past it).
        public bool Muzzle;
        public float MuzzleLength = 4f;
        public float MuzzleSize = 2.5f;
        public float MuzzleDroop;

        public bool Nose;
        public float NoseSize = 1.5f;

        // Ears
        public int Ears;
        public float EarSize = 2f;
        public float EarHeight = 3f;
        public float EarAngle = 10f;
        public float EarForward = 0.5f;

        // Horns
        public int HornPairs;
        public int HornSegments = 2;
        public float HornLength = 5f;
        public float HornThickness = 1.5f;
        public float HornCurl = 12f;
        public float HornSplay = 14f;

        // Eyes
        public bool Eyes;
        public float EyeSize = 1f;
        public float EyeForward = 0.25f;
        public float EyeVertical = 0.62f;
        public bool Pupils;
        public float PupilSize = 0.5f;

        // Jaw / mouth
        public bool Jaw;
        public float JawLength = 3.5f;
        public float JawDrop;
        public int Fangs;
        public float FangSize = 0.6f;
        public float FangLength = 1.2f;

        public bool Brow;
        public float BrowSize = 1.3f;

        // Head crest
        public bool Crest;
        public int CrestCount = 4;
        public float CrestHeight = 4f;
        public float CrestLength = 1.5f;
        public float CrestAngle = -55f;

        // Antennae
        public bool Antennae;
        public int AntennaeSegments = 3;
        public float AntennaeLength = 8f;
        public float AntennaeThickness = 0.5f;
        public float AntennaeCurve = 18f;
        public float AntennaeSplay = 16f;

        // Tail (digitigrade already has a base Tail; this extends it. Seraph gains a fresh tail.)
        public bool Tail;
        public int TailSegments = 4;
        public float TailLength = 12f;
        public float TailThickness = 3f;
        public float TailTaper = 0.4f;
        public float TailDroop = 8f;
        public float TailBulge = 1f;

        // Claws / paws (on the feet, and optionally the hands).
        public int Claws;
        public float ClawLength = 2f;
        public float ClawSize = 0.7f;
        public float ClawSplay = 14f;
        public float ClawCurve = -12f;
        public bool HandClaws;

        // Limb fluff (decorative cuffs that ride the upper limb bones).
        public bool ArmFluff;
        public bool LegFluff;

        // Dorsal ridge (spikes / plates along the upper back).
        public int DorsalSpikes;
        public float DorsalSpikeHeight = 4f;
        public float DorsalSpikeLength = 2f;
        public float DorsalSpikeWidth = 1.5f;
        public float DorsalSpikeAngle = 20f;

        public PlayerModelParams Clone() => (PlayerModelParams)MemberwiseClone();
    }

    private bool _playerModelWindowOpen;
    private readonly PlayerModelParams _playerModelParams = new();
    private ModelElementData? _playerModelPreviewRoot;
    private string _playerModelPreviewError = "";
    private bool _playerModelPreviewDirty = true;
    private int _playerModelPreviewCount;
    private string _playerModelStatus = "";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- Skeleton templates ------------------------------------------------

    private static ModelElementData PlayerModelMakeElement(string name, double[] from, double[] to, double[] origin,
        double rotX = 0, double rotY = 0, double rotZ = 0)
    {
        return new ModelElementData
        {
            Name = name,
            From = [ModelPrimitiveRound(from[0]), ModelPrimitiveRound(from[1]), ModelPrimitiveRound(from[2])],
            To = [ModelPrimitiveRound(to[0]), ModelPrimitiveRound(to[1]), ModelPrimitiveRound(to[2])],
            RotationOrigin = [ModelPrimitiveRound(origin[0]), ModelPrimitiveRound(origin[1]), ModelPrimitiveRound(origin[2])],
            RotationX = ModelPrimitiveRound(rotX),
            RotationY = ModelPrimitiveRound(rotY),
            RotationZ = ModelPrimitiveRound(rotZ)
        };
    }

    private static ModelElementData PlayerModelAttach(ModelElementData parent, ModelElementData child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
        return child;
    }

    private static JArray PlayerModelAttachmentPoint(string code, double x, double y, double z,
        double rx = 0, double ry = 0, double rz = 0)
    {
        return new JArray
        {
            new JObject
            {
                ["code"] = code,
                ["posX"] = x.ToString("0.###", Inv),
                ["posY"] = y.ToString("0.###", Inv),
                ["posZ"] = z.ToString("0.###", Inv),
                ["rotationX"] = rx.ToString("0.###", Inv),
                ["rotationY"] = ry.ToString("0.###", Inv),
                ["rotationZ"] = rz.ToString("0.###", Inv)
            }
        };
    }

    // The seraph base: plantigrade humanoid, 2-segment arms and legs. Values reproduced verbatim from
    // playermodellib:shapes/baseshapes/seraph.json so armor and the auto-merged seraph animations align.
    private static ModelElementData PlayerModelBuildSeraphSkeleton()
    {
        ModelElementData root = PlayerModelMakeElement("root", [0, 0, 0], [0, 0, 0], [8, 0, 8]);

        ModelElementData lowerTorso = PlayerModelAttach(root,
            PlayerModelMakeElement("LowerTorso", [6, 15, 5], [11, 20, 11], [8, 15, 8], rotZ: 3));
        ModelElementData upperTorso = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperTorso", [0, 4.3, -0.1], [5, 9.3, 6.1], [2, 5, 3], rotZ: -6));
        upperTorso.Extra = new JObject { ["attachmentpoints"] = PlayerModelAttachmentPoint("Back", 5, 3, 3) };

        ModelElementData upperArmR = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("UpperArmR", [1, -2.1, -1.2], [4, 4.9, 0.8], [2, 4.7, 0.8], rotX: 10, rotZ: 7));
        PlayerModelAttach(upperArmR,
            PlayerModelMakeElement("LowerArmR", [0.1, -7.3, 0.1], [2.6, 0.7, 2.1], [1.3, 0.7, 1.1], rotX: -8, rotZ: -6));

        ModelElementData upperArmL = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("UpperArmL", [1, -2.1, 5.4], [4, 4.9, 7.4], [2, 4.7, 5.2], rotX: -10, rotZ: 7));
        PlayerModelAttach(upperArmL,
            PlayerModelMakeElement("LowerArmL", [0.2, -7.3, -0.1], [2.7, 0.7, 1.9], [1.4, 0.7, 0.9], rotX: 8, rotZ: -6));

        ModelElementData neck = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("Neck", [1.5, 3.9, 1.6], [4.5, 7.9, 4.6], [3.2, 3.6, 3.1], rotZ: 12));
        ModelElementData head = PlayerModelAttach(neck,
            PlayerModelMakeElement("Head", [-1.7, 2.1, -1.0], [3.8, 7.1, 4.0], [1.7, 2.1, 1.5], rotZ: -7));
        head.Extra = new JObject { ["attachmentpoints"] = PlayerModelAttachmentPoint("Eyes", 1.3, 3.5, 2.5, ry: -90) };

        ModelElementData upperFootL = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperFootL", [0.4, -6, 3], [4.6, 3, 6.2], [1.5, 0.5, 4.6], rotX: -3, rotZ: -5));
        PlayerModelAttach(upperFootL,
            PlayerModelMakeElement("LowerFootL", [0.1, -9, 0.1], [4.1, 1, 3.1], [2.1, 0.5, 1.4], rotX: 2, rotZ: 2));

        ModelElementData upperFootR = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperFootR", [0.4, -6, -0.2], [4.6, 3, 3.0], [1.5, 0.5, 1.4], rotX: 3, rotZ: -5));
        PlayerModelAttach(upperFootR,
            PlayerModelMakeElement("LowerFootR", [0.1, -9, 0.1], [4.1, 1, 3.1], [2.1, 0.5, 1.4], rotX: -2, rotZ: 2));

        return root;
    }

    // The digitigrade base: 3-segment reverse-knee legs, a tail and a snout (with the faceless headgear
    // anchors). Reproduced verbatim from playermodellib:shapes/baseshapes/digitigrade.json.
    private static ModelElementData PlayerModelBuildDigitigradeSkeleton()
    {
        ModelElementData root = PlayerModelMakeElement("root", [0, 0, 0], [0, 0, 0], [8, 0, 8]);

        ModelElementData lowerTorso = PlayerModelAttach(root,
            PlayerModelMakeElement("LowerTorso", [6, 15.9, 5], [11, 20.9, 11], [8, 15.9, 8]));
        ModelElementData upperTorso = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperTorso", [0, 4.3, -0.1], [5, 9.3, 6.1], [2, 5, 3], rotZ: 11));
        upperTorso.Extra = new JObject { ["attachmentpoints"] = PlayerModelAttachmentPoint("Back", 5, 3, 3) };

        ModelElementData upperArmR = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("UpperArmR", [1, -2.1, -1.2], [4, 4.9, 0.8], [2, 4.7, 0.8], rotX: 12, rotZ: -6));
        PlayerModelAttach(upperArmR,
            PlayerModelMakeElement("LowerArmR", [0.1, -7.3, 0.1], [2.6, 0.7, 2.1], [1.3, 0.7, 1.1], rotX: -3, rotZ: -6));

        ModelElementData upperArmL = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("UpperArmL", [1, -2.1, 5.4], [4, 4.9, 7.4], [2, 4.7, 5.2], rotX: -12, rotZ: -6));
        PlayerModelAttach(upperArmL,
            PlayerModelMakeElement("LowerArmL", [0.2, -7.3, -0.1], [2.7, 0.7, 1.9], [1.4, 0.7, 0.9], rotX: 3, rotZ: -6));

        ModelElementData neck = PlayerModelAttach(upperTorso,
            PlayerModelMakeElement("Neck", [1.5, 3.9, 1.6], [4.5, 7.9, 4.6], [3.2, 3.6, 3.1], rotZ: 7));
        ModelElementData head = PlayerModelAttach(neck,
            PlayerModelMakeElement("Head", [-1.7, 2.1, -1.0], [3.8, 7.1, 4.0], [1.7, 2.1, 1.5], rotZ: -16));
        head.Extra = new JObject { ["attachmentpoints"] = PlayerModelAttachmentPoint("Eyes", 1.3, 3.5, 2.5, ry: -90) };

        PlayerModelAttach(head, PlayerModelMakeElement("Snout", [-3, 0, 1.5], [0, 2, 3.5], [0, 2, 2.5]));
        PlayerModelAttach(head, PlayerModelMakeElement("HeadWithSnout", [-3, 0, 0], [5.5, 5, 5], [-3, 0, 0]));
        PlayerModelAttach(head, PlayerModelMakeElement("AboveSnout", [-3, 2, 0], [0, 5, 5], [-3, 2, 0]));
        PlayerModelAttach(head, PlayerModelMakeElement("SnoutHeadWide", [-3, 0, 0], [0, 2, 5], [0, 2, 2.5]));

        ModelElementData upperFootL = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperFootL", [0.4, -6, 3], [4.6, 3, 6.2], [1.5, 0.5, 4.6], rotX: -4, rotY: -1, rotZ: -22));
        ModelElementData middleFootL = PlayerModelAttach(upperFootL,
            PlayerModelMakeElement("MiddleFootL", [0, -6, 0.1], [4, 0, 3.1], [0, 0, 3.1], rotX: 4, rotZ: 70));
        PlayerModelAttach(middleFootL,
            PlayerModelMakeElement("LowerFootL", [0, -10, 0], [4, 0, 3], [4, 0, 1.3], rotY: 1, rotZ: -50));

        ModelElementData upperFootR = PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("UpperFootR", [0.4, -6, -0.2], [4.6, 3, 3], [1.5, 0.5, 1.4], rotX: 4, rotY: -1, rotZ: -22));
        ModelElementData middleFootR = PlayerModelAttach(upperFootR,
            PlayerModelMakeElement("MiddleFootR", [0, -6, 0.1], [4, 0, 3.1], [0, 0, 0.1], rotX: -4, rotZ: 71));
        PlayerModelAttach(middleFootR,
            PlayerModelMakeElement("LowerFootR", [0, -10, 0], [4, 0, 3], [4, 0, 1.3], rotY: -1, rotZ: -50));

        PlayerModelAttach(lowerTorso,
            PlayerModelMakeElement("Tail", [5, 1, 2], [9, 3, 4], [5, 3, 3], rotZ: -45));

        return root;
    }

    private static ModelElementData PlayerModelBuildSkeleton(PlayerModelBaseShape baseShape)
    {
        return baseShape == PlayerModelBaseShape.Digitigrade
            ? PlayerModelBuildDigitigradeSkeleton()
            : PlayerModelBuildSeraphSkeleton();
    }

    private static ModelElementData? PlayerModelFind(ModelElementData root, string name)
    {
        return root.EnumerateSubtree().FirstOrDefault(element => element.Name == name);
    }

    // ---- Region scaling ----------------------------------------------------

    /// <summary>Uniformly scales a limb/region subtree about its joint pivot. Uniform scale commutes with
    /// the joints' rest rotations, so the limb stays connected at the pivot and keeps its pose - only its
    /// size and reach change. From/To/RotationOrigin are all relative to the parent's From, so the region
    /// root scales about its (fixed) pivot and every descendant's relative offset scales by the factor.</summary>
    private static void PlayerModelScaleRegion(ModelElementData? regionRoot, double factor)
    {
        if (regionRoot == null || Math.Abs(factor - 1.0) < 1e-6) return;

        double[] pivot = regionRoot.RotationOrigin is { Length: >= 3 } origin
            ? [origin[0], origin[1], origin[2]]
            : [regionRoot.From[0], regionRoot.From[1], regionRoot.From[2]];

        for (int axis = 0; axis < 3; axis++)
        {
            regionRoot.From[axis] = ModelPrimitiveRound(pivot[axis] + (regionRoot.From[axis] - pivot[axis]) * factor);
            regionRoot.To[axis] = ModelPrimitiveRound(pivot[axis] + (regionRoot.To[axis] - pivot[axis]) * factor);
        }

        foreach (ModelElementData node in regionRoot.EnumerateSubtree())
        {
            if (ReferenceEquals(node, regionRoot)) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                node.From[axis] = ModelPrimitiveRound(node.From[axis] * factor);
                node.To[axis] = ModelPrimitiveRound(node.To[axis] * factor);
                if (node.RotationOrigin is { Length: >= 3 } o)
                {
                    o[axis] = ModelPrimitiveRound(o[axis] * factor);
                }
            }
        }
    }

    // ---- Feature builders (humanoid frame: head-forward is -X, up +Y, lateral Z) -------------------

    private void PlayerModelBuildFeatures(PlayerModelParams p, ModelElementData root)
    {
        ModelElementData? head = PlayerModelFind(root, "Head");
        if (head != null)
        {
            PlayerModelBuildMuzzle(p, head);
            PlayerModelBuildNose(p, head);
            PlayerModelBuildEars(p, head);
            PlayerModelBuildHorns(p, head);
            PlayerModelBuildEyes(p, head);
            PlayerModelBuildJaw(p, head);
            PlayerModelBuildBrow(p, head);
            PlayerModelBuildCrest(p, head);
            PlayerModelBuildAntennae(p, head);
        }

        PlayerModelBuildTail(p, root);
        PlayerModelBuildClaws(p, root);
        PlayerModelBuildLimbFluff(p, root);
        PlayerModelBuildDorsalRidge(p, root);
    }

    private void PlayerModelBuildMuzzle(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Muzzle) return;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double len = Math.Max(0.5, p.MuzzleLength);
        double size = Math.Max(0.5, p.MuzzleSize);
        // Grows forward (-X) off the head's lower-front face; droop tips it down.
        ModelCreatureChain(head, [0, hy * 0.30, hz * 0.5], 0.0, 0.0, p.MuzzleDroop, 0, -1,
            [[len, size, size * 1.1]], null, 2, "Muzzle", out _);
    }

    private void PlayerModelBuildNose(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Nose) return;
        ModelElementData target = PlayerModelFind(head, "Muzzle") ?? PlayerModelFind(head, "Snout") ?? head;
        double ty = target.SizeY;
        double tz = target.SizeZ;
        double ns = Math.Max(0.25, p.NoseSize);
        bool muzzle = !ReferenceEquals(target, head);
        // On the forward-top of the muzzle/snout tip (its -X end at local X 0) or the head's front face.
        double[] center = muzzle ? [0, ty * 0.7, tz * 0.5] : [0, head.SizeY * 0.72, tz * 0.5];
        ModelCreatureBox(target, center, [ns * 0.8, ns, ns * 1.2], 0.0, 0.0, 0.0, "Nose");
    }

    private void PlayerModelBuildEars(PlayerModelParams p, ModelElementData head)
    {
        if (p.Ears <= 0) return;
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double es = Math.Max(0.25, p.EarSize);
        double eh = Math.Max(0.25, p.EarHeight);
        int pairs = Math.Clamp(p.Ears, 0, 2);
        for (int pair = 0; pair < pairs; pair++)
        {
            // First pair on top toward the back; a second pair a little forward.
            double xFrac = pairs == 1 ? Math.Clamp(p.EarForward, 0f, 1f) : ModelCreatureLerp(0.7, 0.3, pair);
            string suffix = pairs == 1 ? "" : (pair + 1).ToString();
            foreach (int side in ModelCreatureSides(2))
            {
                double[] attach = [hx * xFrac, hy, hz * 0.5 + side * hz * 0.3];
                ModelCreatureChain(head, attach, side * -p.EarAngle, 0.0, 0.0, 1, 1,
                    [[es, eh, es * 0.6]], null, 0, $"Ear{(side > 0 ? "R" : "L")}{suffix}", out _);
            }
        }
    }

    private void PlayerModelBuildHorns(PlayerModelParams p, ModelElementData head)
    {
        if (p.HornPairs <= 0) return;
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        int segs = Math.Clamp(p.HornSegments, 1, 6);
        double segLen = Math.Max(0.5, p.HornLength) / segs;
        double baseTh = Math.Max(0.25, p.HornThickness);

        for (int pair = 0; pair < p.HornPairs; pair++)
        {
            double xFrac = p.HornPairs == 1 ? 0.55 : ModelCreatureLerp(0.75, 0.3, (double)pair / (p.HornPairs - 1));
            foreach (int side in ModelCreatureSides(2))
            {
                List<double[]> sizes = [];
                List<double> bends = [];
                for (int k = 0; k < segs; k++)
                {
                    double t = segs <= 1 ? 0.0 : (double)k / (segs - 1);
                    double th = baseTh * ModelCreatureLerp(1.0, 0.4, t);
                    sizes.Add([th, segLen, th]);
                    bends.Add(k == 0 ? 0.0 : p.HornCurl);
                }
                double[] attach = [hx * xFrac, hy, hz * 0.5 + side * hz * 0.3];
                ModelCreatureChain(head, attach, side * -p.HornSplay, 0.0, 0.0, 1, 1, sizes, bends, 2,
                    $"Horn{(side > 0 ? "R" : "L")}{pair + 1}", out _);
            }
        }
    }

    private void PlayerModelBuildEyes(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Eyes) return;
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double eye = Math.Max(0.25, p.EyeSize);
        double pupil = Math.Max(0.1, p.PupilSize);
        foreach (int side in ModelCreatureSides(2))
        {
            double zFace = side > 0 ? hz : 0.0;
            double[] attach = [hx * Math.Clamp(p.EyeForward, 0f, 1f), hy * Math.Clamp(p.EyeVertical, 0f, 1f), zFace];
            string eyeName = side > 0 ? "EyeR" : "EyeL";
            ModelElementData eyeEl = ModelCreatureChain(head, attach, 0.0, 0.0, 0.0, 2, side,
                [[eye, eye, eye]], null, 0, eyeName, out _);
            if (p.Pupils)
            {
                double[] pc = [eyeEl.SizeX * 0.5, eyeEl.SizeY * 0.5, side > 0 ? eyeEl.SizeZ : 0.0];
                ModelCreatureBox(eyeEl, pc, [pupil, pupil, pupil * 0.6], 0.0, 0.0, 0.0, eyeName + "Pupil");
            }
        }
    }

    private void PlayerModelBuildJaw(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Jaw) return;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double jl = Math.Max(0.5, p.JawLength);
        double jw = Math.Max(0.5, hz * 0.7);
        double jh = Math.Max(0.4, hy * 0.2);
        // Lower jaw hinged at the head's lower-front, growing forward (-X); JawDrop swings it open.
        ModelElementData jaw = ModelCreatureChain(head, [0, hy * 0.16, hz * 0.5], 0.0, 0.0, -p.JawDrop, 0, -1,
            [[jl, jh, jw]], null, 2, "Jaw", out _);

        int fangs = Math.Clamp(p.Fangs, 0, 6);
        if (fangs == 0) return;
        double fs = Math.Max(0.1, p.FangSize);
        double fl = Math.Max(0.25, p.FangLength);
        double step = fs * 1.6;
        foreach (int side in ModelCreatureSides(2))
        {
            string tag = side > 0 ? "R" : "L";
            double upperLat = side > 0 ? hz * 0.72 : hz * 0.28;
            double lowerLat = side > 0 ? jw * 0.72 : jw * 0.28;
            for (int i = 0; i < fangs; i++)
            {
                ModelCreatureBox(head, [i * step, -fl * 0.35, upperLat], [fs, fl, fs], 0.0, 0.0, 0.0, $"FangUp{tag}{i + 1}");
                ModelCreatureBox(jaw, [i * step, jh + fl * 0.3, lowerLat], [fs, fl, fs], 0.0, 0.0, 0.0, $"FangLow{tag}{i + 1}");
            }
        }
    }

    private void PlayerModelBuildBrow(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Brow) return;
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double bs = Math.Max(0.25, p.BrowSize);
        foreach (int side in ModelCreatureSides(2))
        {
            double zc = side > 0 ? hz * 0.72 : hz * 0.28;
            double[] center = [hx * Math.Clamp(p.EyeForward, 0f, 1f), hy * Math.Min(0.98, p.EyeVertical + 0.22), zc];
            ModelCreatureBox(head, center, [bs, bs * 0.7, hz * 0.34], 0.0, 0.0, 0.0, side > 0 ? "BrowR" : "BrowL");
        }
    }

    private void PlayerModelBuildCrest(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Crest) return;
        int count = Math.Clamp(p.CrestCount, 1, 12);
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
        double h = Math.Max(0.25, p.CrestHeight);
        double len = Math.Max(0.25, p.CrestLength);
        double w = Math.Max(0.25, hz * 0.12);
        for (int i = 0; i < count; i++)
        {
            double frac = count == 1 ? 0.4 : ModelCreatureLerp(0.15, 0.8, (double)i / (count - 1));
            double scale = ModelCreatureLerp(0.65, 1.0, Math.Sin(Math.PI * frac));
            ModelCreatureChain(head, [hx * frac, hy, hz * 0.5], 0.0, 0.0, p.CrestAngle, 1, 1,
                [[len, h * scale, w]], null, 0, $"Crest{i + 1}", out _);
        }
    }

    private void PlayerModelBuildAntennae(PlayerModelParams p, ModelElementData head)
    {
        if (!p.Antennae) return;
        int count = Math.Clamp(p.AntennaeSegments, 1, 8);
        double hx = head.SizeX;
        double hy = head.SizeY;
        double hz = head.SizeZ;
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
            double[] attach = [hx * 0.15, hy, hz * 0.5 + side * hz * 0.25];
            ModelCreatureChain(head, attach, side * -p.AntennaeSplay, 0.0, 0.0, 1, 1, sizes, bends, 2,
                side > 0 ? "AntennaR" : "AntennaL", out _);
        }
    }

    private void PlayerModelBuildTail(PlayerModelParams p, ModelElementData root)
    {
        if (!p.Tail) return;
        int segs = Math.Clamp(p.TailSegments, 1, 12);
        double segLen = Math.Max(0.5, p.TailLength) / segs;
        double baseTh = Math.Max(0.5, p.TailThickness);

        List<double[]> sizes = [];
        List<double> bends = [];
        for (int k = 0; k < segs; k++)
        {
            double t = segs <= 1 ? 0.0 : (double)k / (segs - 1);
            double taper = ModelCreatureLerp(1.0, Math.Max(0.05, p.TailTaper), t);
            double bulge = ModelCreatureLerp(1.0, Math.Max(0.1, p.TailBulge), 4.0 * t * (1.0 - t));
            double th = baseTh * taper * bulge;
            sizes.Add([segLen, th, th]);
            bends.Add(k == 0 ? 0.0 : -p.TailDroop);
        }

        ModelElementData? existing = PlayerModelFind(root, "Tail");
        if (existing != null)
        {
            // Digitigrade ships a base Tail box - continue a chain off its (+X) tip.
            double[] attach = [existing.SizeX, existing.SizeY * 0.5, existing.SizeZ * 0.5];
            ModelCreatureChain(existing, attach, 0.0, 0.0, 0.0, 0, 1, sizes, bends, 2, "TailSeg", out _);
            return;
        }

        // Seraph: grow a fresh tail off the lower torso's rear (+X) face, angled down.
        ModelElementData? lowerTorso = PlayerModelFind(root, "LowerTorso");
        if (lowerTorso == null) return;
        double[] rear = [lowerTorso.SizeX, lowerTorso.SizeY * 0.25, lowerTorso.SizeZ * 0.5];
        ModelCreatureChain(lowerTorso, rear, 0.0, 0.0, -(30.0 + p.TailDroop), 0, 1, sizes, bends, 2, "Tail", out _);
    }

    private void PlayerModelBuildClaws(PlayerModelParams p, ModelElementData root)
    {
        if (p.Claws <= 0) return;
        int claws = Math.Clamp(p.Claws, 1, 5);

        string[] feet = ["LowerFootR", "LowerFootL"];
        foreach (string footName in feet)
        {
            ModelElementData? foot = PlayerModelFind(root, footName);
            if (foot != null) PlayerModelClawFan(p, foot, claws, footName.EndsWith('R') ? "R" : "L", "Foot");
        }

        if (p.HandClaws)
        {
            string[] hands = ["LowerArmR", "LowerArmL"];
            foreach (string handName in hands)
            {
                ModelElementData? hand = PlayerModelFind(root, handName);
                if (hand != null) PlayerModelClawFan(p, hand, claws, handName.EndsWith('R') ? "R" : "L", "Hand");
            }
        }
    }

    private void PlayerModelClawFan(PlayerModelParams p, ModelElementData limbTip, int claws, string side, string tag)
    {
        double sz = limbTip.SizeZ;
        double cl = Math.Max(0.25, p.ClawLength);
        double cs = Math.Max(0.1, p.ClawSize);
        // Fan the claws across the tip's forward (-X) edge, splayed laterally, hooking down.
        for (int i = 0; i < claws; i++)
        {
            double frac = claws == 1 ? 0.5 : (double)i / (claws - 1);
            double zc = ModelCreatureLerp(sz * 0.2, sz * 0.8, frac);
            double splay = (frac - 0.5) * 2.0 * p.ClawSplay;
            ModelCreatureChain(limbTip, [0, 0, zc], 0.0, splay, p.ClawCurve, 0, -1,
                [[cl, cs, cs]], null, 2, $"Claw{tag}{side}{i + 1}", out _);
        }
    }

    private void PlayerModelBuildLimbFluff(PlayerModelParams p, ModelElementData root)
    {
        if (p.ArmFluff)
        {
            foreach (string arm in new[] { "UpperArmR", "UpperArmL" })
            {
                ModelElementData? el = PlayerModelFind(root, arm);
                if (el == null) continue;
                double[] center = [el.SizeX * 0.5, el.SizeY * 0.2, el.SizeZ * 0.5];
                ModelCreatureBox(el, center, [el.SizeX + 1.0, el.SizeY * 0.5, el.SizeZ + 1.0], 0.0, 0.0, 0.0,
                    arm.EndsWith('R') ? "ArmFluffR" : "ArmFluffL");
            }
        }

        if (p.LegFluff)
        {
            foreach (string leg in new[] { "UpperFootR", "UpperFootL" })
            {
                ModelElementData? el = PlayerModelFind(root, leg);
                if (el == null) continue;
                double[] center = [el.SizeX * 0.5, el.SizeY * 0.25, el.SizeZ * 0.5];
                ModelCreatureBox(el, center, [el.SizeX + 1.0, el.SizeY * 0.55, el.SizeZ + 1.0], 0.0, 0.0, 0.0,
                    leg.EndsWith('R') ? "LegFluffR" : "LegFluffL");
            }
        }
    }

    private void PlayerModelBuildDorsalRidge(PlayerModelParams p, ModelElementData root)
    {
        if (p.DorsalSpikes <= 0) return;
        ModelElementData? upperTorso = PlayerModelFind(root, "UpperTorso");
        if (upperTorso == null) return;

        int count = Math.Clamp(p.DorsalSpikes, 1, 24);
        double sx = upperTorso.SizeX;
        double sz = upperTorso.SizeZ;
        double h = Math.Max(0.25, p.DorsalSpikeHeight);
        double len = Math.Max(0.25, p.DorsalSpikeLength);
        double w = Math.Max(0.25, p.DorsalSpikeWidth);
        for (int i = 0; i < count; i++)
        {
            double frac = count == 1 ? 0.5 : (double)i / (count - 1);
            // A row of plates on the upper face of the back, fanned fore-aft along the body axis (X) and
            // growing up (+Y); the sweep angle lays them back.
            double[] attach = [ModelCreatureLerp(sx * 0.1, sx * 0.9, frac), upperTorso.SizeY, sz * 0.5];
            ModelCreatureChain(upperTorso, attach, 0.0, 0.0, p.DorsalSpikeAngle, 1, 1,
                [[len, h, w]], null, 0, $"Spike{i + 1}", out _);
        }
    }

    // ---- Build / textures --------------------------------------------------

    private ModelElementData? PlayerModelBuildShape(out string error)
    {
        error = "";
        if (_modelDoc == null) return null;

        try
        {
            PlayerModelParams p = _playerModelParams;
            var baseShape = (PlayerModelBaseShape)Math.Clamp(p.BaseShapeIndex, 0, 1);
            ModelElementData root = PlayerModelBuildSkeleton(baseShape);

            // Proportions first, so features read the scaled joint sizes.
            PlayerModelScaleRegion(PlayerModelFind(root, "Head"), Math.Clamp((double)p.HeadScale, 0.3, 3.0));
            PlayerModelScaleRegion(PlayerModelFind(root, "UpperArmR"), Math.Clamp((double)p.ArmScale, 0.3, 3.0));
            PlayerModelScaleRegion(PlayerModelFind(root, "UpperArmL"), Math.Clamp((double)p.ArmScale, 0.3, 3.0));
            PlayerModelScaleRegion(PlayerModelFind(root, "UpperFootR"), Math.Clamp((double)p.LegScale, 0.3, 3.0));
            PlayerModelScaleRegion(PlayerModelFind(root, "UpperFootL"), Math.Clamp((double)p.LegScale, 0.3, 3.0));

            PlayerModelBuildFeatures(p, root);

            string texture = string.IsNullOrWhiteSpace(p.Texture)
                ? _modelDoc.Textures.FirstOrDefault()?.Code ?? "seraph"
                : p.Texture;
            PlayerModelAssignFaces(root, texture);

            int count = ModelCreatureElementCount(root);
            if (count == 0)
            {
                error = "The current parameters produce no elements.";
                return null;
            }
            if (count > ModelCreatureMaxElements)
            {
                error = $"Too many elements ({count} > {ModelCreatureMaxElements}). Reduce feature counts.";
                return null;
            }

            return root;
        }
        catch (Exception exception)
        {
            error = $"Generation failed: {exception.Message}";
            return null;
        }
    }

    private void PlayerModelAssignFaces(ModelElementData root, string texture)
    {
        foreach (ModelElementData node in root.EnumerateSubtree())
        {
            if (ReferenceEquals(node, root)) continue;
            if (PlayerModelFacelessAnchors.Contains(node.Name)) continue;
            if (node.SizeX <= 0.0001 || node.SizeY <= 0.0001 || node.SizeZ <= 0.0001) continue;

            for (int face = 0; face < 6; face++)
            {
                node.Faces[face] = new ModelFaceData { Texture = texture };
                ModelAutoUvFace(node, face);
            }
        }
    }

    // ---- Commit / config export --------------------------------------------

    private void PlayerModelCommitShape()
    {
        if (_modelDoc == null || _playerModelPreviewRoot == null) return;

        ModelBeginEdit();
        ModelElementData root = _playerModelPreviewRoot.CloneSubtree();
        // Unlike the creature generator, the KeyElement names MUST stay verbatim (no prefixing) so
        // PlayerModelLib can bind armor and animations - only the bare top "root" is de-duplicated.
        root.Name = ModelGenerateElementName(root.Name);
        _modelDoc.Roots.Add(root);

        PlayerModelEnsureTexture();

        ModelSelectElement(root);
        ModelMarkChanged();
        ModelEndEdit("Add player model");

        bool nonEmpty = _modelDoc.Roots.Count > 1;
        _playerModelStatus = nonEmpty
            ? $"Added player-model skeleton ({ModelCreatureElementCount(root)} elements). Note: a player model is normally a standalone shape - this document already had other roots."
            : $"Added player-model skeleton ({ModelCreatureElementCount(root)} elements). Save the shape, then Export config JSON.";
    }

    private void PlayerModelEnsureTexture()
    {
        if (_modelDoc == null) return;
        string code = string.IsNullOrWhiteSpace(_playerModelParams.Texture)
            ? _modelDoc.Textures.FirstOrDefault()?.Code ?? "seraph"
            : _playerModelParams.Texture;
        if (_modelDoc.Textures.Any(texture => string.Equals(texture.Code, code, StringComparison.Ordinal))) return;

        string path = _modelDoc.Textures.FirstOrDefault()?.Path ?? "entity/humanoid/seraph-faceless";
        _modelDoc.Textures.Add(new ModelTextureEntry { Code = code, Path = path });
    }

    private static string[] PlayerModelCsvToArray(string csv)
    {
        return csv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Builds the customplayermodels config object ({ code: {...} }). Pure (no IO) so it is unit-testable.</summary>
    private JObject PlayerModelBuildConfig()
    {
        PlayerModelParams p = _playerModelParams;
        string code = ModelSanitizeFileName(p.Code);
        var baseShape = (PlayerModelBaseShape)Math.Clamp(p.BaseShapeIndex, 0, 1);
        string texture = string.IsNullOrWhiteSpace(p.Texture)
            ? _modelDoc?.Textures.FirstOrDefault()?.Code ?? "seraph"
            : p.Texture;

        JObject config = new()
        {
            ["Enabled"] = true,
            ["Name"] = string.IsNullOrWhiteSpace(p.Name) ? code : p.Name,
            ["Group"] = p.Group,
            ["Icon"] = "",
            ["GroupIcon"] = "",
            ["ShapePath"] = PlayerModelShapePath(code),
            ["BaseShapeCode"] = baseShape == PlayerModelBaseShape.Digitigrade
                ? "playermodellib:digitigrade"
                : "playermodellib:seraph",
            ["MainTextureCode"] = texture,
            ["CollisionBox"] = new JArray(Math.Round(p.CollisionWidth, 3), Math.Round(p.CollisionHeight, 3)),
            ["EyeHeight"] = Math.Round(p.EyeHeight, 3),
            ["SizeRange"] = new JArray(Math.Round(p.SizeMin, 3), Math.Round(p.SizeMax, 3)),
            ["ModelSizeFactor"] = Math.Round(p.ModelSizeFactor, 3),
            ["CreateCuboidsForAttachmentPoints"] = p.CreateCuboidsForAttachmentPoints
        };

        string[] addTags = PlayerModelCsvToArray(p.AddTagsCsv);
        string[] removeTags = PlayerModelCsvToArray(p.RemoveTagsCsv);
        string[] classes = PlayerModelCsvToArray(p.ClassesCsv);
        string[] traits = PlayerModelCsvToArray(p.TraitsCsv);
        if (addTags.Length > 0) config["AddTags"] = new JArray(addTags);
        if (removeTags.Length > 0) config["RemoveTags"] = new JArray(removeTags);
        if (classes.Length > 0)
        {
            config["AvailableClasses"] = new JArray(classes);
            config["ExclusiveClasses"] = new JArray(classes);
        }
        if (traits.Length > 0) config["ExtraTraits"] = new JArray(traits);

        if (p.StarterSkinPart)
        {
            // A no-asset starter: a HueShift recolour of the main texture, like the gold-standard mods'
            // colour parts. The user adds proper texture/shape skin parts from here.
            config["SkinnableParts"] = new JArray
            {
                new JObject
                {
                    ["code"] = $"{code}-tint",
                    ["type"] = "texture",
                    ["textureTarget"] = texture,
                    ["targetSkinParts"] = new JArray("base"),
                    ["overlayMode"] = "HueShift",
                    ["solidColor"] = true,
                    ["size"] = new JArray(1, 1),
                    ["FixedSaturation"] = "0",
                    ["FixedHue"] = "1",
                    ["FixedAlpha"] = "1",
                    ["variants"] = new JArray
                    {
                        new JObject { ["code"] = "#00ffffff", ["color"] = "128128128" }
                    }
                }
            };
        }

        return new JObject { [code] = config };
    }

    private string PlayerModelShapePath(string code)
    {
        // Resolve the saved shape's asset path into a VS ShapePath ("domain:path/without/shapes-prefix").
        if (_modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.AssetPath))
        {
            string path = _modelDoc.AssetPath.Replace('\\', '/');
            if (path.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)) path = path["shapes/".Length..];
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^".json".Length];
            return $"{_modelDoc.Domain}:{path}";
        }
        return $"game:{code}";
    }

    private void PlayerModelExportConfig()
    {
        try
        {
            string code = ModelSanitizeFileName(_playerModelParams.Code);
            string domain = _modelDoc != null && !string.IsNullOrWhiteSpace(_modelDoc.Domain) ? _modelDoc.Domain : "game";
            string json = PlayerModelBuildConfig().ToString(Formatting.Indented);
            string relative = Path.Combine("assets", domain, "config", "customplayermodels", code + ".json");
            string outputPath = GetToolAuthoredAssetPath("custommodels", relative);
            string writeError = WriteAuthoredFile(outputPath, json);
            _playerModelStatus = string.IsNullOrEmpty(writeError)
                ? $"Exported config to {outputPath}"
                : $"Config export failed: {writeError}";
        }
        catch (Exception exception)
        {
            _playerModelStatus = $"Config export failed: {exception.Message}";
        }
    }

    // ---- UI ----------------------------------------------------------------

    private void DrawPlayerModelPanel()
    {
        if (!_playerModelWindowOpen) return;
        if (_modelDoc == null)
        {
            ImGui.TextDisabled("Open a shape or create a new one first (a player model is best on a fresh shape).");
            return;
        }

        bool changed = false;
        PlayerModelParams p = _playerModelParams;

        ImGui.SetNextItemWidth(260f);
        changed |= ImGui.Combo("Base shape##pml-base", ref p.BaseShapeIndex, PlayerModelBaseShapeLabels, PlayerModelBaseShapeLabels.Length);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Seraph = vanilla humanoid rig. Digitigrade = beast legs (3-segment reverse knee), a tail and a snout. " +
                "Both carry the exact KeyElement joint names PlayerModelLib needs, so armor and the vanilla animations attach automatically.");
        }

        ImGui.SetNextItemWidth(190f);
        changed |= ImGui.InputText("Code##pml-code", ref p.Code, 64);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Model code / filename (sanitized to lowercase-kebab). Used as the config key and ShapePath.");
        ImGui.SetNextItemWidth(190f);
        changed |= ImGui.InputText("Name##pml-name", ref p.Name, 64);
        ImGui.SetNextItemWidth(190f);
        changed |= ImGui.InputText("Group##pml-group", ref p.Group, 64);

        List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
        ImGui.SetNextItemWidth(190f);
        if (ModelFilteredCombo("Texture##pml-texture", p.Texture, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "main texture code"))
        {
            p.Texture = pickedTexture;
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Main texture code (MainTextureCode in the config). Faces auto-unwrap onto it.");

        ImGui.Separator();
        changed |= DrawPlayerModelSections(p);

        if (changed) _playerModelPreviewDirty = true;
        if (_playerModelPreviewDirty)
        {
            _playerModelPreviewDirty = false;
            _playerModelPreviewRoot = PlayerModelBuildShape(out _playerModelPreviewError);
            _playerModelPreviewCount = _playerModelPreviewRoot == null ? 0 : ModelCreatureElementCount(_playerModelPreviewRoot);
        }

        ImGui.Separator();
        if (!string.IsNullOrEmpty(_playerModelPreviewError))
        {
            ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _playerModelPreviewError);
        }
        else
        {
            ImGui.TextUnformatted($"{_playerModelPreviewCount} element(s) / {ModelCreatureMaxElements} max. Green ghost shows the result.");
        }

        bool canCreate = _playerModelPreviewRoot != null && string.IsNullOrEmpty(_playerModelPreviewError);
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create shape##pml-create"))
        {
            PlayerModelCommitShape();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add the skeleton to the shape (KeyElement names kept verbatim, one undo step).");
        ImGui.SameLine();
        if (ImGui.Button("Create & animate##pml-create-animate"))
        {
            PlayerModelCommitShape();
            ModelAnimateCurrentShape();
        }
        if (!canCreate) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Export config JSON##pml-export"))
        {
            PlayerModelExportConfig();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Write the customplayermodels/<code>.json config to the InGameDevTools authored-assets folder (save the shape first so ShapePath resolves).");
        ImGui.SameLine();
        if (ImGui.Button("Close##pml-close"))
        {
            _playerModelWindowOpen = false;
        }

        if (!string.IsNullOrEmpty(_playerModelStatus))
        {
            ImGui.TextWrapped(_playerModelStatus);
        }
    }

    private bool DrawPlayerModelSections(PlayerModelParams p)
    {
        bool changed = false;

        if (ImGui.CollapsingHeader("Proportions##pml-prop", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.SliderFloat("Head size##pml-head-scale", ref p.HeadScale, 0.3f, 3f, "%.2fx");
            changed |= ImGui.SliderFloat("Arm size##pml-arm-scale", ref p.ArmScale, 0.3f, 3f, "%.2fx");
            changed |= ImGui.SliderFloat("Leg size##pml-leg-scale", ref p.LegScale, 0.3f, 3f, "%.2fx");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Uniform per-region scale about each joint pivot, so limbs stay connected and keep the rig's pose. Use ModelSizeFactor (Config) for overall size.");
        }

        if (ImGui.CollapsingHeader("Head features##pml-head", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("Muzzle / snout##pml-muzzle", ref p.Muzzle);
            if (p.Muzzle)
            {
                changed |= ImGui.DragFloat("Muzzle length##pml-muzzle-len", ref p.MuzzleLength, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Muzzle size##pml-muzzle-size", ref p.MuzzleSize, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Muzzle droop##pml-muzzle-droop", ref p.MuzzleDroop, 0.5f, -45f, 45f, "%.1f deg");
            }
            changed |= ImGui.Checkbox("Nose##pml-nose", ref p.Nose);
            if (p.Nose) changed |= ImGui.DragFloat("Nose size##pml-nose-size", ref p.NoseSize, 0.1f, 0.25f, 8f, "%.2f");

            changed |= ImGui.SliderInt("Ear pairs##pml-ears", ref p.Ears, 0, 2);
            if (p.Ears > 0)
            {
                changed |= ImGui.DragFloat("Ear size##pml-ear-size", ref p.EarSize, 0.25f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Ear height##pml-ear-h", ref p.EarHeight, 0.25f, 0.25f, 24f, "%.2f");
                changed |= ImGui.DragFloat("Ear splay##pml-ear-splay", ref p.EarAngle, 0.5f, -45f, 60f, "%.1f deg");
                changed |= ImGui.SliderFloat("Ear position##pml-ear-fwd", ref p.EarForward, 0f, 1f, "%.2f");
            }

            changed |= ImGui.SliderInt("Horn pairs##pml-horns", ref p.HornPairs, 0, 2);
            if (p.HornPairs > 0)
            {
                changed |= ImGui.SliderInt("Horn segments##pml-horn-seg", ref p.HornSegments, 1, 6);
                changed |= ImGui.DragFloat("Horn length##pml-horn-len", ref p.HornLength, 0.25f, 0.5f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Horn thickness##pml-horn-th", ref p.HornThickness, 0.1f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Horn curl##pml-horn-curl", ref p.HornCurl, 0.5f, -45f, 45f, "%.1f deg");
                changed |= ImGui.DragFloat("Horn splay##pml-horn-splay", ref p.HornSplay, 0.5f, -45f, 60f, "%.1f deg");
            }

            changed |= ImGui.Checkbox("Eyes##pml-eyes", ref p.Eyes);
            if (p.Eyes)
            {
                changed |= ImGui.DragFloat("Eye size##pml-eye-size", ref p.EyeSize, 0.1f, 0.25f, 8f, "%.2f");
                changed |= ImGui.SliderFloat("Eye forward##pml-eye-fwd", ref p.EyeForward, 0f, 1f, "%.2f");
                changed |= ImGui.SliderFloat("Eye height##pml-eye-h", ref p.EyeVertical, 0f, 1f, "%.2f");
                changed |= ImGui.Checkbox("Pupils##pml-pupils", ref p.Pupils);
                if (p.Pupils) changed |= ImGui.DragFloat("Pupil size##pml-pupil-size", ref p.PupilSize, 0.05f, 0.1f, 6f, "%.2f");
            }

            changed |= ImGui.Checkbox("Jaw / mouth##pml-jaw", ref p.Jaw);
            if (p.Jaw)
            {
                changed |= ImGui.DragFloat("Jaw length##pml-jaw-len", ref p.JawLength, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Jaw open##pml-jaw-drop", ref p.JawDrop, 0.5f, 0f, 70f, "%.1f deg");
                changed |= ImGui.SliderInt("Fangs / side##pml-fangs", ref p.Fangs, 0, 6);
                if (p.Fangs > 0)
                {
                    changed |= ImGui.DragFloat("Fang size##pml-fang-size", ref p.FangSize, 0.05f, 0.1f, 6f, "%.2f");
                    changed |= ImGui.DragFloat("Fang length##pml-fang-len", ref p.FangLength, 0.1f, 0.25f, 8f, "%.2f");
                }
            }

            changed |= ImGui.Checkbox("Brow ridge##pml-brow", ref p.Brow);
            if (p.Brow) changed |= ImGui.DragFloat("Brow size##pml-brow-size", ref p.BrowSize, 0.1f, 0.25f, 8f, "%.2f");

            changed |= ImGui.Checkbox("Head crest##pml-crest", ref p.Crest);
            if (p.Crest)
            {
                changed |= ImGui.SliderInt("Crest plates##pml-crest-count", ref p.CrestCount, 1, 12);
                changed |= ImGui.DragFloat("Crest height##pml-crest-h", ref p.CrestHeight, 0.25f, 0.5f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Crest length##pml-crest-l", ref p.CrestLength, 0.1f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Crest sweep##pml-crest-angle", ref p.CrestAngle, 0.5f, -90f, 90f, "%.1f deg");
            }

            changed |= ImGui.Checkbox("Antennae##pml-antennae", ref p.Antennae);
            if (p.Antennae)
            {
                changed |= ImGui.SliderInt("Antenna segments##pml-ant-seg", ref p.AntennaeSegments, 1, 8);
                changed |= ImGui.DragFloat("Antenna length##pml-ant-len", ref p.AntennaeLength, 0.25f, 0.5f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Antenna thickness##pml-ant-th", ref p.AntennaeThickness, 0.05f, 0.1f, 6f, "%.2f");
                changed |= ImGui.DragFloat("Antenna curve##pml-ant-curve", ref p.AntennaeCurve, 0.5f, -45f, 45f, "%.1f deg");
                changed |= ImGui.DragFloat("Antenna splay##pml-ant-splay", ref p.AntennaeSplay, 0.5f, -45f, 60f, "%.1f deg");
            }
        }

        if (ImGui.CollapsingHeader("Tail##pml-tail"))
        {
            changed |= ImGui.Checkbox("Tail##pml-tail-on", ref p.Tail);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Digitigrade already has a base Tail joint; this extends it. Seraph gains a fresh tail off the lower torso.");
            if (p.Tail)
            {
                changed |= ImGui.SliderInt("Segments##pml-tail-seg", ref p.TailSegments, 1, 12);
                changed |= ImGui.DragFloat("Length##pml-tail-len", ref p.TailLength, 0.25f, 1f, 64f, "%.2f");
                changed |= ImGui.DragFloat("Base thickness##pml-tail-th", ref p.TailThickness, 0.25f, 0.5f, 24f, "%.2f");
                changed |= ImGui.SliderFloat("Tip taper##pml-tail-taper", ref p.TailTaper, 0.05f, 1f, "%.2f");
                changed |= ImGui.DragFloat("Droop / segment##pml-tail-droop", ref p.TailDroop, 0.5f, -45f, 45f, "%.1f deg");
                changed |= ImGui.SliderFloat("Fluff / bulge##pml-tail-bulge", ref p.TailBulge, 0.4f, 3f, "x%.2f");
            }
        }

        if (ImGui.CollapsingHeader("Limbs / paws##pml-limbs"))
        {
            changed |= ImGui.SliderInt("Claws / extremity##pml-claws", ref p.Claws, 0, 5);
            if (p.Claws > 0)
            {
                changed |= ImGui.DragFloat("Claw length##pml-claw-len", ref p.ClawLength, 0.1f, 0.25f, 16f, "%.2f");
                changed |= ImGui.DragFloat("Claw size##pml-claw-size", ref p.ClawSize, 0.05f, 0.1f, 6f, "%.2f");
                changed |= ImGui.DragFloat("Claw splay##pml-claw-splay", ref p.ClawSplay, 0.5f, -45f, 45f, "%.1f deg");
                changed |= ImGui.DragFloat("Claw curve##pml-claw-curve", ref p.ClawCurve, 0.5f, -60f, 30f, "%.1f deg");
                changed |= ImGui.Checkbox("Also on hands##pml-hand-claws", ref p.HandClaws);
            }
            changed |= ImGui.Checkbox("Arm fluff##pml-arm-fluff", ref p.ArmFluff);
            changed |= ImGui.Checkbox("Leg fluff##pml-leg-fluff", ref p.LegFluff);
        }

        if (ImGui.CollapsingHeader("Dorsal ridge##pml-ridge"))
        {
            changed |= ImGui.SliderInt("Plates / spikes##pml-ridge-count", ref p.DorsalSpikes, 0, 24);
            if (p.DorsalSpikes > 0)
            {
                changed |= ImGui.DragFloat("Height##pml-ridge-h", ref p.DorsalSpikeHeight, 0.25f, 0.25f, 48f, "%.2f");
                changed |= ImGui.DragFloat("Length##pml-ridge-len", ref p.DorsalSpikeLength, 0.25f, 0.25f, 32f, "%.2f");
                changed |= ImGui.DragFloat("Width##pml-ridge-w", ref p.DorsalSpikeWidth, 0.25f, 0.25f, 24f, "%.2f");
                changed |= ImGui.DragFloat("Sweep##pml-ridge-angle", ref p.DorsalSpikeAngle, 0.5f, -80f, 80f, "%.1f deg");
            }
        }

        if (ImGui.CollapsingHeader("Config (customplayermodels JSON)##pml-config"))
        {
            changed |= ImGui.DragFloat("Collision width##pml-col-w", ref p.CollisionWidth, 0.01f, 0.1f, 4f, "%.2f");
            changed |= ImGui.DragFloat("Collision height##pml-col-h", ref p.CollisionHeight, 0.01f, 0.2f, 6f, "%.2f");
            changed |= ImGui.DragFloat("Eye height##pml-eye-height", ref p.EyeHeight, 0.01f, 0.2f, 6f, "%.2f");
            changed |= ImGui.DragFloat("Size min##pml-size-min", ref p.SizeMin, 0.01f, 0.2f, 3f, "%.2f");
            changed |= ImGui.DragFloat("Size max##pml-size-max", ref p.SizeMax, 0.01f, 0.2f, 3f, "%.2f");
            changed |= ImGui.DragFloat("Model size factor##pml-msf", ref p.ModelSizeFactor, 0.01f, 0.1f, 4f, "%.2f");
            changed |= ImGui.Checkbox("Create attachment-point cuboids##pml-cuboids", ref p.CreateCuboidsForAttachmentPoints);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Let PlayerModelLib auto-create the held-item hand anchors and other attachment cuboids it copies from seraph. Leave on unless you authored your own.");
            changed |= ImGui.InputText("Add tags##pml-add-tags", ref p.AddTagsCsv, 256);
            changed |= ImGui.InputText("Remove tags##pml-rem-tags", ref p.RemoveTagsCsv, 256);
            changed |= ImGui.InputText("Classes (csv)##pml-classes", ref p.ClassesCsv, 512);
            changed |= ImGui.InputText("Extra traits (csv)##pml-traits", ref p.TraitsCsv, 512);
            changed |= ImGui.Checkbox("Starter skin part##pml-skin", ref p.StarterSkinPart);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add a no-asset HueShift recolour skinnable part to the config as a starting point.");
        }

        return changed;
    }

    private void DrawPlayerModelGhost(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (!_playerModelWindowOpen || _playerModelPreviewRoot == null || !string.IsNullOrEmpty(_playerModelPreviewError))
        {
            return;
        }

        uint ghost = ImGui.ColorConvertFloat4ToU32(new NVector4(0.4f, 0.95f, 0.55f, 0.7f));
        foreach (ModelElementData element in _playerModelPreviewRoot.EnumerateSubtree())
        {
            if (element.SizeX <= 0.0001 || element.SizeY <= 0.0001 || element.SizeZ <= 0.0001) continue;
            if (PlayerModelFacelessAnchors.Contains(element.Name)) continue;
            Matrixf matrix = ModelComputeElementMatrix(element);
            Vector3[] corners = ModelTransformBoxCorners(matrix, element);
            foreach ((int a, int b) in ModelBoxEdges)
            {
                DrawModelViewportLine(drawList, camera, corners[a], corners[b], ghost, 1.2f);
            }
        }
    }
}
