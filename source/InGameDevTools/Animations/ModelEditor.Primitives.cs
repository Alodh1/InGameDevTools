using ImGuiNET;
using InGameDevTools.Utils;
using Vintagestory.API.Client;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace InGameDevTools.Animations;

public sealed partial class DebugWindowManager
{
    // Prism/primitive helper: Vintage Story elements can only have rectangular faces, so
    // smooth-looking solids have to be assembled from cuboids. Exact merged mode voxelizes
    // the intended solid on a shared grid, greedily merges occupied cells into cuboids, and
    // culls exactly shared internal faces. Smooth legacy mode keeps the old rotated-slab
    // approximation for users who prefer its silhouette over strict no-overlap geometry.
    private const int ModelPrimitiveMaxElements = 400;
    private const float ModelPrimitiveMaxDimension = 256f;

    private enum ModelPrimitiveKind
    {
        Cylinder,
        Cone,
        Sphere,
        Torus,
        Pyramid,
        Wedge,
        Capsule,
        Helix,
        BoxTube,
        Star,
        Cross,
        Arrow,
        Heart,
        TrianglePlate,
        Sector
    }

    private static readonly string[] ModelPrimitiveKindLabels =
    [
        "Cylinder / Prism",
        "Cone",
        "Sphere / Dome",
        "Torus / Arch",
        "Pyramid / Frustum",
        "Wedge / Stairs",
        "Capsule",
        "Helix / Spiral",
        "Box tube",
        "Star (flat)",
        "Cross / Plus (flat)",
        "Arrow (flat)",
        "Heart (flat)",
        "Triangle (flat)",
        "Disc sector (flat)"
    ];
    private static readonly string[] ModelPrimitiveAxisLabels = ["X", "Y", "Z"];
    private static readonly string[] ModelPrimitiveDomeLabels = ["Full", "Top half", "Bottom half"];
    private static readonly ModelPrimitiveMetrics ModelPrimitiveEmptyMetrics = new();

    private bool _modelPrimitiveWindowOpen;
    private int _modelPrimitiveKindIndex = (int)ModelPrimitiveKind.Sphere;
    private bool _modelPrimitiveStepped = true;
    private bool _modelPrimitiveCullInternalFaces = true;
    private int _modelPrimitiveAxis = 1;
    private NVector3 _modelPrimitiveCenter = new(8f, 8f, 8f);
    private NVector3 _modelPrimitiveRotation;
    private string _modelPrimitiveTexture = "";
    private float _modelPrimitiveDiameter = 16f;
    private float _modelPrimitiveHeight = 16f;
    private float _modelPrimitiveTopDiameter = 4f;
    private int _modelPrimitiveSides = 6;
    private int _modelPrimitiveLayers = 8;
    private bool _modelPrimitiveHollow;
    private float _modelPrimitiveWall = 2f;
    private float _modelPrimitiveMinor = 4f;
    private int _modelPrimitiveSegments = 16;
    private float _modelPrimitiveSweep = 360f;
    private float _modelPrimitiveStep = 1f;
    private int _modelPrimitiveDome;
    private float _modelPrimitiveDepth = 16f;
    private float _modelPrimitiveRise = 8f;
    private float _modelPrimitiveTopScale;
    private float _modelPrimitiveTurns = 1.5f;
    private float _modelPrimitiveThickness = 1f;
    private int _modelPrimitiveStarSquares = 2;
    private ModelElementData? _modelPrimitivePreviewParent;
    private string _modelPrimitivePreviewError = "";
    private bool _modelPrimitivePreviewDirty = true;
    private ModelPrimitiveMetrics _modelPrimitivePreviewMetrics = ModelPrimitiveEmptyMetrics;

    private static bool ModelPrimitiveSupportsExactMergedStyle(ModelPrimitiveKind kind)
    {
        return kind is ModelPrimitiveKind.Cylinder or ModelPrimitiveKind.Cone or ModelPrimitiveKind.Sphere or ModelPrimitiveKind.Torus or
            ModelPrimitiveKind.Capsule or ModelPrimitiveKind.Star or ModelPrimitiveKind.Cross or ModelPrimitiveKind.Arrow or
            ModelPrimitiveKind.Heart or ModelPrimitiveKind.TrianglePlate or ModelPrimitiveKind.Sector;
    }

    private static bool ModelPrimitiveUsesExactGridConstruction(ModelPrimitiveKind kind)
    {
        return ModelPrimitiveSupportsExactMergedStyle(kind);
    }

    private static bool ModelPrimitiveUsesExactValidation(ModelPrimitiveKind kind, bool exactMergedSelected)
    {
        if (kind == ModelPrimitiveKind.Helix) return false;
        return !ModelPrimitiveSupportsExactMergedStyle(kind) || exactMergedSelected;
    }

    private void DrawModelPrimitivePanel()
    {
        if (!_modelPrimitiveWindowOpen) return;

        {
            if (_modelDoc == null)
            {
                ImGui.TextDisabled("Open a shape or create a new one first.");
                return;
            }

            bool changed = false;
            ImGui.SetNextItemWidth(190f);
            changed |= ImGui.Combo("Shape##model-prim-kind", ref _modelPrimitiveKindIndex, ModelPrimitiveKindLabels, ModelPrimitiveKindLabels.Length);
            ModelPrimitiveKind kind = (ModelPrimitiveKind)_modelPrimitiveKindIndex;

            bool styleApplies = !ModelIsMeshLibMode && ModelPrimitiveSupportsExactMergedStyle(kind);
            if (styleApplies)
            {
                int style = _modelPrimitiveStepped ? 0 : 1;
                changed |= ImGui.RadioButton("Exact merged##model-prim-style-exact", ref style, 0);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Grid-based cuboids merged for no intentional overlap, no tiny coordinate gaps, and fewer enabled internal faces.");
                }
                ImGui.SameLine();
                changed |= ImGui.RadioButton("Smooth slabs (legacy)##model-prim-style-rotated", ref style, 1);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Old rotated-cuboid approximation. It can look smoother, but uses intentional overlap and may retain internal coincident faces.");
                }
                _modelPrimitiveStepped = style == 0;
            }
            else if (!ModelIsMeshLibMode)
            {
                _modelPrimitiveStepped = true;
                ImGui.TextDisabled(kind switch
                {
                    ModelPrimitiveKind.Helix => "Construction: rotated segments.",
                    _ => "Construction: exact axis-aligned cuboids."
                });
            }
            else
            {
                ImGui.TextDisabled("Construction: one shared-vertex MeshLib triangle/quad surface.");
            }

            if (!ModelIsMeshLibMode && ModelPrimitiveUsesExactValidation(kind, _modelPrimitiveStepped))
            {
                changed |= ImGui.Checkbox("Cull internal faces##model-prim-cull-internal", ref _modelPrimitiveCullInternalFaces);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Disables pairs of exactly shared internal cuboid faces. Partial shared faces are left intact to avoid creating visible holes.");
                }
            }

            ImGui.SetNextItemWidth(110f);
            changed |= ImGui.Combo("Axis##model-prim-axis", ref _modelPrimitiveAxis, ModelPrimitiveAxisLabels, ModelPrimitiveAxisLabels.Length);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The primitive's length/rotation axis. Use X or Z for standing arches and lying cylinders.");
            }

            changed |= ImGui.DragFloat3("Center##model-prim-center", ref _modelPrimitiveCenter, 0.25f, -ModelPrimitiveMaxDimension, ModelPrimitiveMaxDimension + 16f, "%.2f");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Center of the primitive in shape units (16 units = 1 block). Becomes the parent element's pivot.");
            }

            changed |= ImGui.DragFloat3("Rotation##model-prim-rotation", ref _modelPrimitiveRotation, 1f, -360f, 360f, "%.1f deg");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Preview and parent rotation around the center pivot. The created primitive keeps this rotation on its parent element.");
            }

            List<string> textureCodes = _modelDoc.Textures.Select(texture => texture.Code).ToList();
            ImGui.SetNextItemWidth(190f);
            if (ModelFilteredCombo("Texture##model-prim-texture", _modelPrimitiveTexture, textureCodes, out string pickedTexture, allowCustom: true, filterHint: "filter texture codes"))
            {
                _modelPrimitiveTexture = pickedTexture;
                changed = true;
            }

            ImGui.Separator();
            changed |= DrawModelPrimitiveKindControls(kind);

            if (changed) _modelPrimitivePreviewDirty = true;
            if (_modelPrimitivePreviewDirty)
            {
                _modelPrimitivePreviewDirty = false;
                _modelPrimitivePreviewParent = ModelBuildPrimitive(out _modelPrimitivePreviewError);
            }

            ImGui.Separator();
            int elementCount = _modelPrimitivePreviewParent == null ? 0 : ModelPrimitiveCuboidCount(_modelPrimitivePreviewParent.Children);
            if (!string.IsNullOrEmpty(_modelPrimitivePreviewError))
            {
                ImGui.TextColored(new NVector4(1f, 0.42f, 0.34f, 1f), _modelPrimitivePreviewError);
            }
            else
            {
                ImGui.TextUnformatted(ModelIsMeshLibMode && _modelPrimitivePreviewParent?.NonCuboid != null
                    ? $"MeshLib surface: {_modelPrimitivePreviewParent.NonCuboid.Vertices.Count} vertices, {_modelPrimitivePreviewParent.NonCuboid.Faces.Count} faces, quality: {_modelPrimitivePreviewMetrics.QualityLabel}."
                    : $"{_modelPrimitivePreviewMetrics.ModeLabel}: {elementCount} cuboid(s), {_modelPrimitivePreviewMetrics.EnabledFaces} enabled face(s), {_modelPrimitivePreviewMetrics.CulledInternalFaces} internal face(s) culled, quality: {_modelPrimitivePreviewMetrics.QualityLabel}.");
                if (_modelPrimitivePreviewMetrics.Warnings.Count > 0)
                {
                    ImGui.TextColored(new NVector4(1f, 0.72f, 0.32f, 1f), $"{_modelPrimitivePreviewMetrics.Warnings.Count} warning(s): {_modelPrimitivePreviewMetrics.Warnings[0]}");
                }
            }

            bool canCreate = _modelPrimitivePreviewParent != null && string.IsNullOrEmpty(_modelPrimitivePreviewError) && _modelPrimitivePreviewMetrics.Errors.Count == 0;
            if (!canCreate) ImGui.BeginDisabled();
            if (ImGui.Button("Create##model-prim-create"))
            {
                ModelCommitPrimitive();
            }
            if (!canCreate) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(ModelIsMeshLibMode
                    ? "Add this true triangle/quad mesh as one MeshLib element (single undo step)."
                    : "Add the cuboids to the shape, grouped under a new face-less parent element (single undo step).");
            }
            ImGui.SameLine();
            if (ImGui.Button("Close##model-prim-close"))
            {
                _modelPrimitiveWindowOpen = false;
            }
        }
    }

    private bool DrawModelPrimitiveKindControls(ModelPrimitiveKind kind)
    {
        bool changed = false;
        switch (kind)
        {
            case ModelPrimitiveKind.Cylinder:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Height##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(_modelPrimitiveHollow
                            ? "Wall segments around the ring; the ring is an exact N-sided polygon."
                            : "Crossing slabs; N slabs form a smooth 2N-sided cylinder.");
                    }
                }
                changed |= ImGui.Checkbox("Hollow##model-prim-hollow", ref _modelPrimitiveHollow);
                if (_modelPrimitiveHollow)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(110f);
                    changed |= ImGui.DragFloat("Wall##model-prim-wall", ref _modelPrimitiveWall, 0.25f, 0.25f, 16f, "%.2f");
                }
                break;
            case ModelPrimitiveKind.Cone:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Bottom diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Top diameter##model-prim-topdiameter", ref _modelPrimitiveTopDiameter, 0.25f, 0f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Height##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Layers##model-prim-layers", ref _modelPrimitiveLayers, 1, 32);
                }
                break;
            case ModelPrimitiveKind.Sphere:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Equatorial diameter, perpendicular to the axis.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Polar height##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Diameter along the axis. Different values make an ellipsoid.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.Combo("Part##model-prim-dome", ref _modelPrimitiveDome, ModelPrimitiveDomeLabels, ModelPrimitiveDomeLabels.Length);
                changed |= ImGui.Checkbox("Hollow##model-prim-hollow", ref _modelPrimitiveHollow);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Builds a shell instead of a solid - bowls from a bottom half, hollow domes from a top half. Shells have no caps at the open side.");
                }
                if (_modelPrimitiveHollow)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(110f);
                    changed |= ImGui.DragFloat("Wall##model-prim-wall", ref _modelPrimitiveWall, 0.25f, 0.25f, 16f, "%.2f");
                }
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Layers##model-prim-layers", ref _modelPrimitiveLayers, 2, 32);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Discs stacked along the axis; the radius of each follows the smooth sphere profile.");
                    }
                }
                break;
            case ModelPrimitiveKind.Pyramid:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Base width##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Base depth##model-prim-depth", ref _modelPrimitiveDepth, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Height##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderFloat("Top scale##model-prim-topscale", ref _modelPrimitiveTopScale, 0f, 1f, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("0 tapers to a point (pyramid); larger values leave a flat top (frustum).");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Layers##model-prim-layers", ref _modelPrimitiveLayers, 1, 32);
                break;
            case ModelPrimitiveKind.Wedge:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Run##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Horizontal length of the slope.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Rise##model-prim-rise", ref _modelPrimitiveRise, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Total height the slope climbs over the run.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Length##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Extrusion length along the chosen axis.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Steps##model-prim-layers", ref _modelPrimitiveLayers, 1, 32);
                break;
            case ModelPrimitiveKind.Capsule:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Total height##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Overall length including both rounded caps. Below one diameter it degrades into a squashed sphere.");
                }
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Cap layers##model-prim-layers", ref _modelPrimitiveLayers, 1, 16);
                }
                break;
            case ModelPrimitiveKind.Helix:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Major diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 1f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Step size##model-prim-minor", ref _modelPrimitiveMinor, 0.25f, 0.25f, 32f, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Cross-section size of each helix segment.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Total climb##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderFloat("Turns##model-prim-turns", ref _modelPrimitiveTurns, 0.25f, 6f, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Segments##model-prim-segments", ref _modelPrimitiveSegments, 3, 64);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Total segments over all turns. Spiral stairs work well with 8-12 segments per turn.");
                }
                break;
            case ModelPrimitiveKind.BoxTube:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Width##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Height##model-prim-depth", ref _modelPrimitiveDepth, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Cross-section height of the rectangular tube.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Length##model-prim-height", ref _modelPrimitiveHeight, 0.25f, 0.25f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Wall##model-prim-wall", ref _modelPrimitiveWall, 0.25f, 0.25f, 16f, "%.2f");
                break;
            case ModelPrimitiveKind.Star:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Size##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Point sets##model-prim-starsquares", ref _modelPrimitiveStarSquares, 2, 6);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Rotated squares stacked into a star: {_modelPrimitiveStarSquares} squares make a {_modelPrimitiveStarSquares * 4}-pointed star.");
                }
                break;
            case ModelPrimitiveKind.Cross:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Size##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Arm width##model-prim-minor", ref _modelPrimitiveMinor, 0.25f, 0.25f, 32f, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Rotate the parent element 45 degrees afterwards for an X instead of a plus.");
                }
                break;
            case ModelPrimitiveKind.Arrow:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Length##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Shaft width##model-prim-minor", ref _modelPrimitiveMinor, 0.25f, 0.25f, 32f, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Head size##model-prim-depth", ref _modelPrimitiveDepth, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Side length of the diamond-shaped arrow head. The arrow points along the first cross-section direction.");
                }
                break;
            case ModelPrimitiveKind.Heart:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Width##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Slabs per lobe disc; more slabs make rounder heart lobes.");
                }
                break;
            case ModelPrimitiveKind.TrianglePlate:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Base width##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Height##model-prim-rise", ref _modelPrimitiveRise, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt(_modelPrimitiveStepped ? "Rows##model-prim-layers" : "Edge slabs##model-prim-layers", ref _modelPrimitiveLayers, 1, 32);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(_modelPrimitiveStepped
                        ? "Isosceles triangle built from rows of shrinking width. More rows look smoother."
                        : "Interior fill rows plus rotated edge slabs; more slabs reduce visible overlap.");
                }
                break;
            case ModelPrimitiveKind.Sector:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 0.5f, ModelPrimitiveMaxDimension, "%.2f");
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderFloat("Sweep##model-prim-sweep", ref _modelPrimitiveSweep, 15f, 360f, "%.0f deg");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("180 makes a half disc, 90 a quarter. Combine with Hollow for flat arcs and rainbows.");
                }
                changed |= ImGui.Checkbox("Hollow##model-prim-hollow", ref _modelPrimitiveHollow);
                if (_modelPrimitiveHollow)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(110f);
                    changed |= ImGui.DragFloat("Wall##model-prim-wall", ref _modelPrimitiveWall, 0.25f, 0.25f, 16f, "%.2f");
                }
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Segments##model-prim-segments", ref _modelPrimitiveSegments, 3, 64);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Arc segments for the smooth sector. More segments make rounder discs and arcs.");
                    }
                }
                break;
            case ModelPrimitiveKind.Torus:
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Major diameter##model-prim-diameter", ref _modelPrimitiveDiameter, 0.25f, 1f, ModelPrimitiveMaxDimension, "%.2f");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Diameter of the ring centerline.");
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.DragFloat("Tube size##model-prim-minor", ref _modelPrimitiveMinor, 0.25f, 0.25f, 32f, "%.2f");
                if (!_modelPrimitiveStepped)
                {
                    ImGui.SetNextItemWidth(130f);
                    changed |= ImGui.SliderInt("Tube slabs##model-prim-sides", ref _modelPrimitiveSides, 3, 16);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Segments around the torus tube profile. More slabs make the tube rounder.");
                    }
                }
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderInt("Segments##model-prim-segments", ref _modelPrimitiveSegments, 3, 64);
                ImGui.SetNextItemWidth(130f);
                changed |= ImGui.SliderFloat("Sweep##model-prim-sweep", ref _modelPrimitiveSweep, 15f, 360f, "%.0f deg");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("360 is a full torus; less makes an arch. Rotate the parent element afterwards to orient it.");
                }
                break;
        }

        bool usesExactGridConstruction = _modelPrimitiveStepped && ModelPrimitiveUsesExactGridConstruction(kind);
        if (usesExactGridConstruction)
        {
            ImGui.SetNextItemWidth(130f);
            changed |= ImGui.DragFloat("Grid step##model-prim-step", ref _modelPrimitiveStep, 0.25f, 0.25f, 8f, "%.2f");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Voxel size in shape units for exact merged construction. Smaller follows curves more closely but can use more cuboids.");
            }
        }

        if (kind is ModelPrimitiveKind.Star or ModelPrimitiveKind.Cross or ModelPrimitiveKind.Arrow or ModelPrimitiveKind.Heart or ModelPrimitiveKind.TrianglePlate or ModelPrimitiveKind.Sector)
        {
            ImGui.SetNextItemWidth(130f);
            changed |= ImGui.DragFloat("Thickness##model-prim-thickness", ref _modelPrimitiveThickness, 0.25f, 0.25f, 32f, "%.2f");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Plate thickness along the chosen axis. The flat face lies perpendicular to the axis.");
            }
        }

        return changed;
    }

    private void ModelCommitPrimitive()
    {
        if (_modelDoc == null || _modelPrimitivePreviewParent == null) return;

        ModelBeginEdit();
        ModelElementData parent = _modelPrimitivePreviewParent.CloneSubtree();
        parent.Name = ModelGenerateElementName(parent.Name);
        int childIndex = 1;
        ModelNamePrimitiveChildren(parent, parent.Name, ref childIndex);
        _modelDoc.Roots.Add(parent);
        ModelSelectElement(parent);
        ModelMarkChanged();
        ModelEndEdit("Add primitive");
        _modelStatus = parent.NonCuboid != null
            ? $"Added MeshLib {parent.Name} with {parent.NonCuboid.Vertices.Count} vertices and {parent.NonCuboid.Faces.Count} faces."
            : $"Added {parent.Name} with {ModelPrimitiveCuboidCount(parent.Children)} cuboid(s).";
    }

    private static void ModelNamePrimitiveChildren(ModelElementData element, string baseName, ref int index)
    {
        foreach (ModelElementData child in element.Children)
        {
            child.Name = $"{baseName}_{index++}";
            ModelNamePrimitiveChildren(child, baseName, ref index);
        }
    }

    private void DrawModelPrimitiveGhost(ImDrawListPtr drawList, DevToolsPreviewCamera camera)
    {
        if (!_modelPrimitiveWindowOpen || _modelPrimitivePreviewParent == null || !string.IsNullOrEmpty(_modelPrimitivePreviewError))
        {
            return;
        }

        uint ghost = ImGui.ColorConvertFloat4ToU32(new NVector4(0.3f, 0.95f, 0.9f, 0.65f));
        if (_modelPrimitivePreviewParent.NonCuboid?.Editable == true)
        {
            DrawModelMeshWireOverlay(drawList, camera, _modelPrimitivePreviewParent, ghost, 1.2f, drawVertices: false);
            return;
        }
        foreach (ModelElementData child in ModelPrimitiveLeafElements(_modelPrimitivePreviewParent.Children))
        {
            Matrixf matrix = ModelComputeElementMatrix(child);
            OpenTK.Mathematics.Vector3[] corners = ModelTransformBoxCorners(matrix, child);
            foreach ((int a, int b) in ModelBoxEdges)
            {
                DrawModelViewportLine(drawList, camera, corners[a], corners[b], ghost, 1.2f);
            }
        }
    }

    private ModelElementData? ModelBuildPrimitive(out string error)
    {
        error = "";
        if (_modelDoc == null) return null;

        try
        {
            // Drag widgets do not hard-clamp typed values, so sanitize before doing math.
            _modelPrimitiveDiameter = Math.Clamp(_modelPrimitiveDiameter, 0.5f, ModelPrimitiveMaxDimension);
            _modelPrimitiveHeight = Math.Clamp(_modelPrimitiveHeight, 0.25f, ModelPrimitiveMaxDimension);
            _modelPrimitiveTopDiameter = Math.Clamp(_modelPrimitiveTopDiameter, 0f, ModelPrimitiveMaxDimension);
            _modelPrimitiveWall = Math.Clamp(_modelPrimitiveWall, 0.25f, 16f);
            _modelPrimitiveMinor = Math.Clamp(_modelPrimitiveMinor, 0.25f, 32f);
            _modelPrimitiveSweep = Math.Clamp(_modelPrimitiveSweep, 15f, 360f);
            _modelPrimitiveStep = Math.Clamp(_modelPrimitiveStep, 0.25f, 8f);
            _modelPrimitiveSides = Math.Clamp(_modelPrimitiveSides, 3, 16);
            _modelPrimitiveLayers = Math.Clamp(_modelPrimitiveLayers, 1, 32);
            _modelPrimitiveSegments = Math.Clamp(_modelPrimitiveSegments, 3, 64);
            _modelPrimitiveDepth = Math.Clamp(_modelPrimitiveDepth, 0.5f, ModelPrimitiveMaxDimension);
            _modelPrimitiveRise = Math.Clamp(_modelPrimitiveRise, 0.5f, ModelPrimitiveMaxDimension);
            _modelPrimitiveTopScale = Math.Clamp(_modelPrimitiveTopScale, 0f, 1f);
            _modelPrimitiveTurns = Math.Clamp(_modelPrimitiveTurns, 0.25f, 6f);
            _modelPrimitiveThickness = Math.Clamp(_modelPrimitiveThickness, 0.25f, 32f);
            _modelPrimitiveStarSquares = Math.Clamp(_modelPrimitiveStarSquares, 2, 6);
            _modelPrimitiveRotation.X = (float)ModelWrapDegrees(_modelPrimitiveRotation.X);
            _modelPrimitiveRotation.Y = (float)ModelWrapDegrees(_modelPrimitiveRotation.Y);
            _modelPrimitiveRotation.Z = (float)ModelWrapDegrees(_modelPrimitiveRotation.Z);

            ModelPrimitiveKind kind = (ModelPrimitiveKind)_modelPrimitiveKindIndex;
            if (ModelIsMeshLibMode)
            {
                return ModelBuildMeshLibPrimitive(kind, out error);
            }
            ModelElementData parent = new()
            {
                Name = kind switch
                {
                    ModelPrimitiveKind.Cylinder => _modelPrimitiveHollow ? "Tube" : "Cylinder",
                    ModelPrimitiveKind.Cone => "Cone",
                    ModelPrimitiveKind.Sphere => _modelPrimitiveDome == 0
                        ? _modelPrimitiveHollow ? "HollowSphere" : "Sphere"
                        : _modelPrimitiveHollow ? "Bowl" : "Dome",
                    ModelPrimitiveKind.Pyramid => _modelPrimitiveTopScale > 0.001f ? "Frustum" : "Pyramid",
                    ModelPrimitiveKind.Wedge => _modelPrimitiveLayers > 1 ? "Stairs" : "Wedge",
                    ModelPrimitiveKind.Capsule => "Capsule",
                    ModelPrimitiveKind.Helix => "Helix",
                    ModelPrimitiveKind.BoxTube => "BoxTube",
                    ModelPrimitiveKind.Star => "Star",
                    ModelPrimitiveKind.Cross => "Cross",
                    ModelPrimitiveKind.Arrow => "Arrow",
                    ModelPrimitiveKind.Heart => "Heart",
                    ModelPrimitiveKind.TrianglePlate => "Triangle",
                    ModelPrimitiveKind.Sector => _modelPrimitiveSweep >= 359f ? "Disc" : "Sector",
                    _ => _modelPrimitiveSweep >= 359f ? "Torus" : "Arch"
                },
                From = [_modelPrimitiveCenter.X, _modelPrimitiveCenter.Y, _modelPrimitiveCenter.Z],
                To = [_modelPrimitiveCenter.X, _modelPrimitiveCenter.Y, _modelPrimitiveCenter.Z],
                RotationOrigin = [_modelPrimitiveCenter.X, _modelPrimitiveCenter.Y, _modelPrimitiveCenter.Z],
                RotationX = ModelPrimitiveRound(_modelPrimitiveRotation.X),
                RotationY = ModelPrimitiveRound(_modelPrimitiveRotation.Y),
                RotationZ = ModelPrimitiveRound(_modelPrimitiveRotation.Z)
            };

            List<ModelElementData> children = kind switch
            {
                ModelPrimitiveKind.Cylinder => ModelBuildCylinder(),
                ModelPrimitiveKind.Cone => ModelBuildCone(),
                ModelPrimitiveKind.Sphere => ModelBuildSphere(),
                ModelPrimitiveKind.Pyramid => ModelBuildPyramid(),
                ModelPrimitiveKind.Wedge => ModelBuildWedge(),
                ModelPrimitiveKind.Capsule => ModelBuildCapsule(),
                ModelPrimitiveKind.Helix => ModelBuildHelix(),
                ModelPrimitiveKind.BoxTube => ModelBuildBoxTube(),
                ModelPrimitiveKind.Star => ModelBuildStar(),
                ModelPrimitiveKind.Cross => ModelBuildCross(),
                ModelPrimitiveKind.Arrow => ModelBuildArrow(),
                ModelPrimitiveKind.Heart => ModelBuildHeart(),
                ModelPrimitiveKind.TrianglePlate => ModelBuildTrianglePlate(),
                ModelPrimitiveKind.Sector => ModelBuildSector(),
                _ => ModelBuildTorus()
            };

            if (children.Count == 0)
            {
                _modelPrimitivePreviewMetrics = ModelPrimitiveEmptyMetrics;
                error = "The current parameters produce no cuboids.";
                return null;
            }
            int cuboidCount = ModelPrimitiveCuboidCount(children);
            if (cuboidCount > ModelPrimitiveMaxElements)
            {
                _modelPrimitivePreviewMetrics = ModelPrimitiveEmptyMetrics;
                error = $"Too many cuboids ({cuboidCount} > {ModelPrimitiveMaxElements}). Increase the step or reduce layers/segments.";
                return null;
            }

            string texture = string.IsNullOrWhiteSpace(_modelPrimitiveTexture)
                ? _modelDoc.Textures.FirstOrDefault()?.Code ?? ""
                : _modelPrimitiveTexture;
            foreach (ModelElementData child in children)
            {
                child.Parent = parent;
                ModelAssignPrimitiveFaces(child, texture);
            }
            parent.Children.AddRange(children);
            bool exactValidation = ModelPrimitiveUsesExactValidation(kind, _modelPrimitiveStepped);
            string modeLabel = kind == ModelPrimitiveKind.Helix
                ? "Rotated segments"
                : exactValidation ? "Exact merged" : "Smooth legacy";
            _modelPrimitivePreviewMetrics = ModelAnalyzePrimitive(
                parent,
                exactValidation,
                cullInternalFaces: exactValidation && _modelPrimitiveCullInternalFaces,
                modeLabel);
            if (_modelPrimitivePreviewMetrics.Errors.Count > 0)
            {
                error = _modelPrimitivePreviewMetrics.Errors[0];
                return null;
            }
            return parent;
        }
        catch (Exception exception)
        {
            _modelPrimitivePreviewMetrics = ModelPrimitiveEmptyMetrics;
            error = $"Generation failed: {exception.Message}";
            return null;
        }
    }

    private static int ModelPrimitiveCuboidCount(IEnumerable<ModelElementData> elements)
    {
        int count = 0;
        foreach (ModelElementData element in elements)
        {
            count += element.Children.Count == 0 ? 1 : ModelPrimitiveCuboidCount(element.Children);
        }
        return count;
    }

    private static IEnumerable<ModelElementData> ModelPrimitiveLeafElements(IEnumerable<ModelElementData> elements)
    {
        foreach (ModelElementData element in elements)
        {
            if (element.Children.Count == 0)
            {
                yield return element;
                continue;
            }

            foreach (ModelElementData child in ModelPrimitiveLeafElements(element.Children))
            {
                yield return child;
            }
        }
    }

    private void ModelAssignPrimitiveFaces(ModelElementData element, string texture)
    {
        if (element.Children.Count == 0)
        {
            for (int face = 0; face < 6; face++)
            {
                element.Faces[face] = new ModelFaceData { Texture = texture };
                ModelAutoUvFace(element, face);
            }
            return;
        }

        foreach (ModelElementData child in element.Children)
        {
            child.Parent = element;
            ModelAssignPrimitiveFaces(child, texture);
        }
    }

    private ModelPrimitiveMetrics ModelAnalyzePrimitive(ModelElementData parent, bool exactMode, bool cullInternalFaces, string modeLabel)
    {
        ModelPrimitiveMetrics metrics = new() { ModeLabel = modeLabel };
        List<ModelElementData> leaves = ModelPrimitiveLeafElements(parent.Children).ToList();
        metrics.Cuboids = leaves.Count;

        HashSet<string> seenBoxes = new(StringComparer.Ordinal);
        for (int index = 0; index < leaves.Count; index++)
        {
            ModelElementData element = leaves[index];
            if (element.SizeX <= 0.0001 || element.SizeY <= 0.0001 || element.SizeZ <= 0.0001)
            {
                metrics.Errors.Add($"Generated cuboid {index + 1} has inverted or zero dimensions.");
            }

            string key = ModelPrimitiveBoxKey(element);
            if (!seenBoxes.Add(key))
            {
                string message = $"Generated duplicate cuboid at {ModelPrimitiveFormatBox(element)}.";
                if (exactMode) metrics.Errors.Add(message);
                else metrics.Warnings.Add(message);
            }
        }

        for (int leftIndex = 0; leftIndex < leaves.Count; leftIndex++)
        {
            ModelElementData left = leaves[leftIndex];
            for (int rightIndex = leftIndex + 1; rightIndex < leaves.Count; rightIndex++)
            {
                ModelElementData right = leaves[rightIndex];
                if (!ModelPrimitiveBothAxisAligned(left, right))
                {
                    if (!exactMode && ModelPrimitiveBoxesMayOverlap(left, right))
                    {
                        metrics.Warnings.Add("Smooth legacy mode uses rotated cuboids; overlaps and internal faces are possible.");
                    }
                    continue;
                }

                if (ModelPrimitiveBoxesOverlapVolume(left, right))
                {
                    string message = $"Generated cuboids overlap: {ModelPrimitiveFormatBox(left)} and {ModelPrimitiveFormatBox(right)}.";
                    if (exactMode) metrics.Errors.Add(message);
                    else metrics.Warnings.Add(message);
                    continue;
                }

                if (cullInternalFaces && ModelTryCullSharedPrimitiveFace(left, right))
                {
                    metrics.CulledInternalFaces += 2;
                }
                else if (exactMode && ModelPrimitiveHasEnabledSharedPrimitiveFace(left, right))
                {
                    metrics.Errors.Add($"Generated cuboids have enabled coincident internal faces: {ModelPrimitiveFormatBox(left)} and {ModelPrimitiveFormatBox(right)}.");
                }
            }
        }

        if (!exactMode && metrics.Warnings.Count == 0 && leaves.Any(element => !ModelPrimitiveAxisAligned(element)))
        {
            metrics.Warnings.Add("Smooth legacy mode can intentionally overlap rotated slabs; use Exact merged for no-overlap output.");
        }

        metrics.EnabledFaces = leaves.Sum(ModelPrimitiveEnabledFaceCount);
        metrics.QualityLabel = metrics.Errors.Count > 0
            ? "blocked"
            : metrics.Warnings.Count > 0 ? "review" : exactMode ? "clean exact grid" : "legacy visual";
        return metrics;
    }

    private static bool ModelPrimitiveBothAxisAligned(ModelElementData left, ModelElementData right)
    {
        return ModelPrimitiveAxisAligned(left) && ModelPrimitiveAxisAligned(right);
    }

    private static bool ModelPrimitiveAxisAligned(ModelElementData element)
    {
        return Math.Abs(element.RotationX) < 0.0001 &&
            Math.Abs(element.RotationY) < 0.0001 &&
            Math.Abs(element.RotationZ) < 0.0001;
    }

    private static bool ModelPrimitiveBoxesOverlapVolume(ModelElementData left, ModelElementData right)
    {
        return ModelPrimitiveIntervalOverlap(left.From[0], left.To[0], right.From[0], right.To[0]) > 0.0001 &&
            ModelPrimitiveIntervalOverlap(left.From[1], left.To[1], right.From[1], right.To[1]) > 0.0001 &&
            ModelPrimitiveIntervalOverlap(left.From[2], left.To[2], right.From[2], right.To[2]) > 0.0001;
    }

    private static bool ModelPrimitiveBoxesMayOverlap(ModelElementData left, ModelElementData right)
    {
        return ModelPrimitiveIntervalOverlap(left.From[0], left.To[0], right.From[0], right.To[0]) > 0.0001 &&
            ModelPrimitiveIntervalOverlap(left.From[1], left.To[1], right.From[1], right.To[1]) > 0.0001 &&
            ModelPrimitiveIntervalOverlap(left.From[2], left.To[2], right.From[2], right.To[2]) > 0.0001;
    }

    private static double ModelPrimitiveIntervalOverlap(double a0, double a1, double b0, double b1)
    {
        return Math.Min(a1, b1) - Math.Max(a0, b0);
    }

    private static bool ModelTryCullSharedPrimitiveFace(ModelElementData left, ModelElementData right)
    {
        const double epsilon = 0.0001;

        if (Math.Abs(left.To[0] - right.From[0]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            left.Faces[1] = null;
            right.Faces[3] = null;
            return true;
        }

        if (Math.Abs(left.From[0] - right.To[0]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            left.Faces[3] = null;
            right.Faces[1] = null;
            return true;
        }

        if (Math.Abs(left.To[1] - right.From[1]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            left.Faces[4] = null;
            right.Faces[5] = null;
            return true;
        }

        if (Math.Abs(left.From[1] - right.To[1]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            left.Faces[5] = null;
            right.Faces[4] = null;
            return true;
        }

        if (Math.Abs(left.To[2] - right.From[2]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]))
        {
            left.Faces[2] = null;
            right.Faces[0] = null;
            return true;
        }

        if (Math.Abs(left.From[2] - right.To[2]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]))
        {
            left.Faces[0] = null;
            right.Faces[2] = null;
            return true;
        }

        return false;
    }

    private static bool ModelPrimitiveHasEnabledSharedPrimitiveFace(ModelElementData left, ModelElementData right)
    {
        const double epsilon = 0.0001;

        if (Math.Abs(left.To[0] - right.From[0]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            return left.Faces[1]?.Enabled == true && right.Faces[3]?.Enabled == true;
        }

        if (Math.Abs(left.From[0] - right.To[0]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            return left.Faces[3]?.Enabled == true && right.Faces[1]?.Enabled == true;
        }

        if (Math.Abs(left.To[1] - right.From[1]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            return left.Faces[4]?.Enabled == true && right.Faces[5]?.Enabled == true;
        }

        if (Math.Abs(left.From[1] - right.To[1]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[2], left.To[2], right.From[2], right.To[2]))
        {
            return left.Faces[5]?.Enabled == true && right.Faces[4]?.Enabled == true;
        }

        if (Math.Abs(left.To[2] - right.From[2]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]))
        {
            return left.Faces[2]?.Enabled == true && right.Faces[0]?.Enabled == true;
        }

        if (Math.Abs(left.From[2] - right.To[2]) < epsilon &&
            ModelPrimitiveSameInterval(left.From[0], left.To[0], right.From[0], right.To[0]) &&
            ModelPrimitiveSameInterval(left.From[1], left.To[1], right.From[1], right.To[1]))
        {
            return left.Faces[0]?.Enabled == true && right.Faces[2]?.Enabled == true;
        }

        return false;
    }

    private static bool ModelPrimitiveSameInterval(double a0, double a1, double b0, double b1)
    {
        return Math.Abs(a0 - b0) < 0.0001 && Math.Abs(a1 - b1) < 0.0001;
    }

    private static int ModelPrimitiveEnabledFaceCount(ModelElementData element)
    {
        return element.Faces.Count(face => face?.Enabled == true);
    }

    private static string ModelPrimitiveBoxKey(ModelElementData element)
    {
        return string.Join(',',
            element.From.Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Concat(element.To.Select(value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))
                .Concat([
                    element.RotationX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    element.RotationY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    element.RotationZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                ]));
    }

    private static string ModelPrimitiveFormatBox(ModelElementData element)
    {
        return $"[{element.From[0]:0.###},{element.From[1]:0.###},{element.From[2]:0.###}] to [{element.To[0]:0.###},{element.To[1]:0.###},{element.To[2]:0.###}]";
    }

    /// <summary>Maps primitive-local coordinates (u, w = cross-section plane, v = along axis) to model x/y/z.</summary>
    private (double X, double Y, double Z) ModelPrimitiveAxisMap(double u, double v, double w)
    {
        return _modelPrimitiveAxis switch
        {
            0 => (v, u, w),
            2 => (u, w, v),
            _ => (u, v, w)
        };
    }

    internal readonly record struct ModelPrimitiveRotationDebug(double RotationX, double RotationY, double RotationZ);

    internal static ModelPrimitiveRotationDebug TestMapModelPrimitiveRotation(int primitiveAxis, double rotationU, double rotationV, double rotationW)
    {
        (double rotationX, double rotationY, double rotationZ) = ModelPrimitiveMapRotation(primitiveAxis, rotationU, rotationV, rotationW);
        return new ModelPrimitiveRotationDebug(rotationX, rotationY, rotationZ);
    }

    private static (double RotationX, double RotationY, double RotationZ) ModelPrimitiveMapRotation(int primitiveAxis, double rotationU, double rotationV, double rotationW)
    {
        return primitiveAxis switch
        {
            0 => (rotationV, rotationU, rotationW),
            2 => (rotationU, rotationW, rotationV),
            _ => (rotationU, rotationV, rotationW)
        };
    }

    private ModelElementData ModelPrimitiveBox(double u0, double v0, double w0, double u1, double v1, double w1, double rotationDegrees, double originU = 0, double originV = 0, double originW = 0)
    {
        return ModelPrimitiveBoxRotated(u0, v0, w0, u1, v1, w1, 0.0, rotationDegrees, 0.0, originU, originV, originW);
    }

    private ModelElementData ModelPrimitiveCenteredBox(double centerU, double centerV, double centerW, double sizeU, double sizeV, double sizeW, double rotationU = 0.0, double rotationV = 0.0, double rotationW = 0.0)
    {
        double halfU = Math.Max(0.001, sizeU) * 0.5;
        double halfV = Math.Max(0.001, sizeV) * 0.5;
        double halfW = Math.Max(0.001, sizeW) * 0.5;
        return ModelPrimitiveBoxRotated(
            centerU - halfU, centerV - halfV, centerW - halfW,
            centerU + halfU, centerV + halfV, centerW + halfW,
            rotationU, rotationV, rotationW,
            centerU, centerV, centerW);
    }

    private ModelElementData ModelPrimitiveOrbitCenteredBox(double centerU, double centerV, double centerW, double sizeU, double sizeV, double sizeW, double orbitDegrees, double localRotationU = 0.0, double localRotationW = 0.0)
    {
        ModelElementData orbit = ModelPrimitiveEmptyNode(0.0, orbitDegrees, 0.0, 0.0, centerV, 0.0);
        ModelElementData panel = ModelPrimitiveCenteredBox(
            centerU, centerV, centerW,
            sizeU, sizeV, sizeW,
            rotationU: localRotationU,
            rotationW: localRotationW);
        panel.Parent = orbit;
        orbit.Children.Add(panel);
        return orbit;
    }

    private ModelElementData ModelPrimitiveEmptyNode(double rotationU, double rotationV, double rotationW, double originU = 0.0, double originV = 0.0, double originW = 0.0)
    {
        (double x, double y, double z) = ModelPrimitiveAxisMap(0.0, 0.0, 0.0);
        ModelElementData node = new()
        {
            From = [ModelPrimitiveRound(x), ModelPrimitiveRound(y), ModelPrimitiveRound(z)],
            To = [ModelPrimitiveRound(x), ModelPrimitiveRound(y), ModelPrimitiveRound(z)]
        };

        if (Math.Abs(rotationU) > 0.0001 || Math.Abs(rotationV) > 0.0001 || Math.Abs(rotationW) > 0.0001)
        {
            (double originX, double originY, double originZ) = ModelPrimitiveAxisMap(originU, originV, originW);
            node.RotationOrigin = [ModelPrimitiveRound(originX), ModelPrimitiveRound(originY), ModelPrimitiveRound(originZ)];
            (double rotationX, double rotationY, double rotationZ) = ModelPrimitiveMapRotation(_modelPrimitiveAxis, rotationU, rotationV, rotationW);
            node.RotationX = ModelPrimitiveRound(rotationX);
            node.RotationY = ModelPrimitiveRound(rotationY);
            node.RotationZ = ModelPrimitiveRound(rotationZ);
        }

        return node;
    }

    private ModelElementData ModelPrimitiveBoxRotated(double u0, double v0, double w0, double u1, double v1, double w1, double rotationU, double rotationV, double rotationW, double originU = 0, double originV = 0, double originW = 0)
    {
        (double xa, double ya, double za) = ModelPrimitiveAxisMap(u0, v0, w0);
        (double xb, double yb, double zb) = ModelPrimitiveAxisMap(u1, v1, w1);
        ModelElementData box = new()
        {
            From = [ModelPrimitiveRound(Math.Min(xa, xb)), ModelPrimitiveRound(Math.Min(ya, yb)), ModelPrimitiveRound(Math.Min(za, zb))],
            To = [ModelPrimitiveRound(Math.Max(xa, xb)), ModelPrimitiveRound(Math.Max(ya, yb)), ModelPrimitiveRound(Math.Max(za, zb))]
        };

        if (Math.Abs(rotationU) > 0.0001 || Math.Abs(rotationV) > 0.0001 || Math.Abs(rotationW) > 0.0001)
        {
            (double originX, double originY, double originZ) = ModelPrimitiveAxisMap(originU, originV, originW);
            box.RotationOrigin = [ModelPrimitiveRound(originX), ModelPrimitiveRound(originY), ModelPrimitiveRound(originZ)];
            (double rotationX, double rotationY, double rotationZ) = ModelPrimitiveMapRotation(_modelPrimitiveAxis, rotationU, rotationV, rotationW);
            box.RotationX = ModelPrimitiveRound(rotationX);
            box.RotationY = ModelPrimitiveRound(rotationY);
            box.RotationZ = ModelPrimitiveRound(rotationZ);
        }

        return box;
    }

    private static double ModelPrimitiveRound(double value)
    {
        return Math.Round(value, 3);
    }

    private List<ModelElementData> ModelBuildCylinder()
    {
        double radius = _modelPrimitiveDiameter * 0.5;
        double height = _modelPrimitiveHeight;
        double v0 = -height * 0.5;
        double v1 = height * 0.5;

        if (_modelPrimitiveStepped)
        {
            double wall = _modelPrimitiveHollow ? Math.Min(_modelPrimitiveWall, radius) : 0;
            return ModelBuildSteppedSolid(v0, v1, radius, _ => (radius, _modelPrimitiveHollow ? radius - wall : 0.0));
        }

        List<ModelElementData> result = [];
        int sides = Math.Max(3, _modelPrimitiveSides);
        if (_modelPrimitiveHollow)
        {
            ModelAddWallRing(result, v0, v1, radius, Math.Min(_modelPrimitiveWall, radius - 0.01), sides);
        }
        else
        {
            ModelAddCrossingSlabDisc(result, v0, v1, radius, sides);
        }

        return result;
    }

    /// <summary>Solid disc made of N crossing slabs; the union is an exact regular 2N-gon.</summary>
    private List<ModelElementData> ModelAddCrossingSlabDisc(List<ModelElementData> result, double v0, double v1, double radius, int sides)
    {
        return ModelAddCrossingSlabDiscAt(result, 0.0, 0.0, v0, v1, radius, sides);
    }

    /// <summary>Crossing-slab disc centered at (centerU, centerW) in the cross-section plane.</summary>
    private List<ModelElementData> ModelAddCrossingSlabDiscAt(List<ModelElementData> result, double centerU, double centerW, double v0, double v1, double radius, int sides)
    {
        if (radius < 0.05) return result;

        double overlap = ModelPrimitiveSmoothOverlap(radius);
        double halfThickness = radius * Math.Tan(Math.PI / (2.0 * sides)) + overlap;
        for (int side = 0; side < sides; side++)
        {
            result.Add(ModelPrimitiveBox(
                centerU - radius - overlap, v0, centerW - halfThickness,
                centerU + radius + overlap, v1, centerW + halfThickness,
                side * (180.0 / sides),
                centerU, 0.0, centerW));
        }

        return result;
    }

    /// <summary>Hollow N-gon ring made of tangential wall segments whose outer corners meet exactly.</summary>
    private List<ModelElementData> ModelAddWallRing(List<ModelElementData> result, double v0, double v1, double outerRadius, double wall, int sides)
    {
        if (outerRadius < 0.05 || wall <= 0) return result;

        double overlap = ModelPrimitiveSmoothOverlap(outerRadius);
        double halfWidth = outerRadius * Math.Tan(Math.PI / sides) + overlap;
        for (int side = 0; side < sides; side++)
        {
            result.Add(ModelPrimitiveBox(-halfWidth, v0, outerRadius - wall - overlap, halfWidth, v1, outerRadius + overlap, side * (360.0 / sides)));
        }

        return result;
    }

    private static double ModelPrimitiveSurfaceThickness(double radius)
    {
        return Math.Clamp(radius * 0.08, 0.125, 1.0);
    }

    private static double ModelPrimitiveSmoothOverlap(double referenceSize)
    {
        return Math.Clamp(referenceSize * 0.035, 0.125, 0.85);
    }

    private void ModelAddCapDisc(List<ModelElementData> result, double v, double radius, int sides)
    {
        if (radius < 0.05) return;

        double overlap = ModelPrimitiveSmoothOverlap(radius);
        double capHalfThickness = Math.Clamp(radius * 0.025 + overlap * 0.5, 0.08, 0.5);
        ModelAddCrossingSlabDisc(result, v - capHalfThickness, v + capHalfThickness, radius + overlap, sides);
    }

    private void ModelAddSurfaceRingBand(List<ModelElementData> result, double v0, double v1, double radius0, double radius1, int sides, double surfaceThickness)
    {
        double radiusMid = (radius0 + radius1) * 0.5;
        if (radiusMid < 0.05 || v1 - v0 < 0.001) return;

        double dv = v1 - v0;
        double dr = radius1 - radius0;
        double radiusForWidth = Math.Max(radius0, radius1);
        double overlap = ModelPrimitiveSmoothOverlap(Math.Max(radiusForWidth, Math.Sqrt(dv * dv + dr * dr)));
        double profileLength = Math.Sqrt(dv * dv + dr * dr) + overlap * 2.0;
        double halfWidth = Math.Max(0.025, radiusForWidth * Math.Tan(Math.PI / sides) + overlap);
        double centerV = (v0 + v1) * 0.5;
        double tiltDegrees = Math.Atan2(dr, dv) * 180.0 / Math.PI;
        double thickness = Math.Clamp(surfaceThickness + overlap * 2.0, 0.03, Math.Max(0.03, radiusForWidth + overlap));

        for (int side = 0; side < sides; side++)
        {
            double rotationV = side * (360.0 / sides);
            result.Add(ModelPrimitiveOrbitCenteredBox(
                0.0, centerV, radiusMid,
                halfWidth * 2.0, profileLength, thickness,
                orbitDegrees: rotationV,
                localRotationU: tiltDegrees));
        }
    }

    private void ModelAddSmoothSectorPanels(List<ModelElementData> result, double v0, double v1, double innerRadius, double outerRadius, double sweepDegrees, int segments)
    {
        if (outerRadius < 0.05 || outerRadius - innerRadius < 0.025) return;

        double sweepRadians = Math.Clamp(sweepDegrees, 1.0, 360.0) * Math.PI / 180.0;
        bool fullSweep = sweepDegrees >= 359.9;
        int panelCount = Math.Max(1, segments);
        double step = sweepRadians / panelCount;
        double radialSize = outerRadius - innerRadius;
        double centerW = innerRadius + radialSize * 0.5;
        double overlap = ModelPrimitiveSmoothOverlap(outerRadius);
        double halfTangent = Math.Max(0.025, outerRadius * Math.Tan(step * 0.5) + overlap);
        double radialSizeWithOverlap = radialSize + overlap * 2.0;

        for (int panel = 0; panel < panelCount; panel++)
        {
            double angle = -sweepRadians * 0.5 + (panel + 0.5) * step;
            result.Add(ModelPrimitiveOrbitCenteredBox(
                0.0, (v0 + v1) * 0.5, centerW,
                halfTangent * 2.0, v1 - v0 + overlap, radialSizeWithOverlap,
                orbitDegrees: angle * 180.0 / Math.PI));
        }

        if (fullSweep) return;

        double capWidth = Math.Clamp(outerRadius * 0.035, 0.125, 1.0);
        foreach (double boundary in new[] { -sweepRadians * 0.5, sweepRadians * 0.5 })
        {
            result.Add(ModelPrimitiveOrbitCenteredBox(
                0.0, (v0 + v1) * 0.5, centerW,
                capWidth + overlap, v1 - v0 + overlap, radialSizeWithOverlap,
                orbitDegrees: boundary * 180.0 / Math.PI));
        }
    }

    private List<ModelElementData> ModelBuildCone()
    {
        double bottomRadius = _modelPrimitiveDiameter * 0.5;
        double topRadius = _modelPrimitiveTopDiameter * 0.5;
        double height = _modelPrimitiveHeight;
        double v0 = -height * 0.5;

        double RadiusAt(double v)
        {
            double fraction = Math.Clamp((v - v0) / height, 0.0, 1.0);
            return bottomRadius + (topRadius - bottomRadius) * fraction;
        }

        if (_modelPrimitiveStepped)
        {
            return ModelBuildSteppedSolid(v0, v0 + height, Math.Max(bottomRadius, topRadius), v => (RadiusAt(v), 0.0));
        }

        List<ModelElementData> result = [];
        int sides = Math.Max(3, _modelPrimitiveSides);
        int layers = Math.Max(1, _modelPrimitiveLayers);
        double layerHeight = height / layers;
        double maxRadius = Math.Max(bottomRadius, topRadius);
        double surfaceThickness = ModelPrimitiveSurfaceThickness(maxRadius);
        for (int layer = 0; layer < layers; layer++)
        {
            double layerV0 = v0 + layer * layerHeight;
            double layerV1 = layerV0 + layerHeight;
            ModelAddSurfaceRingBand(result, layerV0, layerV1, RadiusAt(layerV0), RadiusAt(layerV1), sides, surfaceThickness);
        }

        ModelAddCapDisc(result, v0, bottomRadius, sides);
        if (topRadius > 0.05)
        {
            ModelAddCapDisc(result, v0 + height, topRadius, sides);
        }

        return result;
    }

    private List<ModelElementData> ModelBuildSphere()
    {
        double equatorRadius = _modelPrimitiveDiameter * 0.5;
        double polarRadius = _modelPrimitiveHeight * 0.5;
        double vMin = _modelPrimitiveDome == 1 ? 0.0 : -polarRadius;
        double vMax = _modelPrimitiveDome == 2 ? 0.0 : polarRadius;
        if (vMax - vMin < 0.01) return [];

        bool hollow = _modelPrimitiveHollow;
        double wall = Math.Min(_modelPrimitiveWall, equatorRadius - 0.01);
        double innerEquator = equatorRadius - wall;
        double innerPolar = polarRadius - wall;

        double RadiusAt(double v)
        {
            double normalized = Math.Clamp(v / polarRadius, -1.0, 1.0);
            return equatorRadius * Math.Sqrt(Math.Max(0.0, 1.0 - normalized * normalized));
        }

        double InnerRadiusAt(double v)
        {
            if (!hollow || innerEquator <= 0 || innerPolar <= 0 || Math.Abs(v) >= innerPolar) return 0.0;
            double normalized = Math.Clamp(v / innerPolar, -1.0, 1.0);
            return innerEquator * Math.Sqrt(Math.Max(0.0, 1.0 - normalized * normalized));
        }

        if (_modelPrimitiveStepped)
        {
            return ModelBuildSteppedSolid(vMin, vMax, equatorRadius, v => (RadiusAt(v), InnerRadiusAt(v)));
        }

        List<ModelElementData> result = [];
        int sides = Math.Max(3, _modelPrimitiveSides);
        int layers = Math.Max(2, _modelPrimitiveLayers);
        double layerHeight = (vMax - vMin) / layers;
        double surfaceThickness = hollow
            ? Math.Clamp(_modelPrimitiveWall, 0.125, Math.Max(0.125, equatorRadius))
            : ModelPrimitiveSurfaceThickness(equatorRadius);
        for (int layer = 0; layer < layers; layer++)
        {
            double layerV0 = vMin + layer * layerHeight;
            double layerV1 = layerV0 + layerHeight;
            double radius0 = RadiusAt(layerV0);
            double radius1 = RadiusAt(layerV1);
            ModelAddSurfaceRingBand(result, layerV0, layerV1, radius0, radius1, sides, surfaceThickness);
        }

        if (!hollow)
        {
            if (vMin > -polarRadius + 0.001)
            {
                ModelAddCapDisc(result, vMin, RadiusAt(vMin), sides);
            }
            if (vMax < polarRadius - 0.001)
            {
                ModelAddCapDisc(result, vMax, RadiusAt(vMax), sides);
            }
        }

        return result;
    }

    private List<ModelElementData> ModelBuildTorus()
    {
        double majorRadius = _modelPrimitiveDiameter * 0.5;
        double minorHalf = Math.Min(_modelPrimitiveMinor * 0.5, majorRadius);
        int segments = Math.Max(3, _modelPrimitiveSegments);
        double sweepRadians = Math.Clamp(_modelPrimitiveSweep, 1f, 360f) * Math.PI / 180.0;

        if (_modelPrimitiveStepped)
        {
            double outerRadius = majorRadius + minorHalf;
            double sweepDegrees = _modelPrimitiveSweep;
            return ModelBuildSteppedSolid(-minorHalf, minorHalf, outerRadius, v =>
            {
                double chord = Math.Sqrt(Math.Max(0.0, minorHalf * minorHalf - v * v));
                return (majorRadius + chord, majorRadius - chord);
            }, sweepDegrees);
        }

        List<ModelElementData> result = [];
        double step = sweepRadians / segments;
        int tubeSides = Math.Max(3, _modelPrimitiveSides);
        double tubeStep = Math.PI * 2.0 / tubeSides;
        double surfaceThickness = ModelPrimitiveSurfaceThickness(minorHalf);
        double torusOverlap = ModelPrimitiveSmoothOverlap(majorRadius + minorHalf);
        for (int segment = 0; segment < segments; segment++)
        {
            double angle = -sweepRadians * 0.5 + (segment + 0.5) * step;
            for (int side = 0; side < tubeSides; side++)
            {
                double tubeAngle0 = side * tubeStep;
                double tubeAngle1 = (side + 1) * tubeStep;
                double tubeAngle = (side + 0.5) * tubeStep;
                double centerV = minorHalf * Math.Sin(tubeAngle);
                double centerW = majorRadius + minorHalf * Math.Cos(tubeAngle);
                if (centerW < 0.05) continue;

                double maxSegmentRadius = majorRadius + minorHalf * Math.Max(Math.Cos(tubeAngle), Math.Max(Math.Cos(tubeAngle0), Math.Cos(tubeAngle1)));
                double halfSweepWidth = Math.Max(0.025, maxSegmentRadius * Math.Tan(step * 0.5) + torusOverlap);
                double profileLength = Math.Max(0.025, minorHalf * tubeStep + torusOverlap * 2.0);
                result.Add(ModelPrimitiveOrbitCenteredBox(
                    0.0, centerV, centerW,
                    halfSweepWidth * 2.0, profileLength, surfaceThickness + torusOverlap * 2.0,
                    orbitDegrees: angle * 180.0 / Math.PI,
                    localRotationU: -tubeAngle * 180.0 / Math.PI));
            }
        }

        return result;
    }

    private List<ModelElementData> ModelBuildPyramid()
    {
        double baseHalfU = _modelPrimitiveDiameter * 0.5;
        double baseHalfW = _modelPrimitiveDepth * 0.5;
        double height = _modelPrimitiveHeight;
        double topScale = _modelPrimitiveTopScale;
        int layers = Math.Max(1, _modelPrimitiveLayers);
        double v0 = -height * 0.5;
        double layerHeight = height / layers;

        List<ModelElementData> result = [];
        for (int layer = 0; layer < layers; layer++)
        {
            double layerV0 = v0 + layer * layerHeight;
            double fraction = (layer + 0.5) / layers;
            double scale = 1.0 + (topScale - 1.0) * fraction;
            double halfU = baseHalfU * scale;
            double halfW = baseHalfW * scale;
            if (halfU < 0.025 || halfW < 0.025) continue;

            result.Add(ModelPrimitiveBox(-halfU, layerV0, -halfW, halfU, layerV0 + layerHeight, halfW, 0.0));
        }

        return result;
    }

    private List<ModelElementData> ModelBuildWedge()
    {
        double run = _modelPrimitiveDiameter;
        double rise = _modelPrimitiveRise;
        double length = _modelPrimitiveHeight;
        int steps = Math.Max(1, _modelPrimitiveLayers);
        double v0 = -length * 0.5;
        double v1 = length * 0.5;
        double stepRun = run / steps;
        double stepRise = rise / steps;

        // Staircase profile rising along +U; each step is one cuboid extruded along the axis.
        List<ModelElementData> result = [];
        for (int step = 0; step < steps; step++)
        {
            double u0 = -run * 0.5 + step * stepRun;
            double topW = -rise * 0.5 + (step + 1) * stepRise;
            result.Add(ModelPrimitiveBox(u0, v0, -rise * 0.5, u0 + stepRun, v1, topW, 0.0));
        }

        return result;
    }

    private List<ModelElementData> ModelBuildCapsule()
    {
        double radius = _modelPrimitiveDiameter * 0.5;
        double halfHeight = Math.Max(_modelPrimitiveHeight * 0.5, radius * 0.1);
        double bodyHalf = Math.Max(0.0, halfHeight - radius);
        double capHeight = halfHeight - bodyHalf;

        double RadiusAt(double v)
        {
            double beyondBody = Math.Max(0.0, Math.Abs(v) - bodyHalf);
            double normalized = capHeight <= 0.0001 ? 0.0 : Math.Clamp(beyondBody / capHeight, 0.0, 1.0);
            return radius * Math.Sqrt(Math.Max(0.0, 1.0 - normalized * normalized));
        }

        if (_modelPrimitiveStepped)
        {
            return ModelBuildSteppedSolid(-halfHeight, halfHeight, radius, v => (RadiusAt(v), 0.0));
        }

        List<ModelElementData> result = [];
        int sides = Math.Max(3, _modelPrimitiveSides);
        int capLayers = Math.Max(1, Math.Min(_modelPrimitiveLayers, 16));
        double surfaceThickness = ModelPrimitiveSurfaceThickness(radius);
        if (bodyHalf > 0.005)
        {
            ModelAddSurfaceRingBand(result, -bodyHalf, bodyHalf, radius, radius, sides, surfaceThickness);
        }

        double capLayerHeight = capHeight / capLayers;
        for (int layer = 0; layer < capLayers; layer++)
        {
            double capV0 = bodyHalf + layer * capLayerHeight;
            double capV1 = capV0 + capLayerHeight;
            ModelAddSurfaceRingBand(result, capV0, capV1, RadiusAt(capV0), RadiusAt(capV1), sides, surfaceThickness);
            ModelAddSurfaceRingBand(result, -capV1, -capV0, RadiusAt(-capV1), RadiusAt(-capV0), sides, surfaceThickness);
        }

        return result;
    }

    private List<ModelElementData> ModelBuildHelix()
    {
        double majorRadius = _modelPrimitiveDiameter * 0.5;
        double stepHalf = Math.Min(_modelPrimitiveMinor * 0.5, majorRadius);
        double climb = _modelPrimitiveHeight;
        double turns = _modelPrimitiveTurns;
        int segments = Math.Max(3, _modelPrimitiveSegments);

        double totalRadians = turns * Math.PI * 2.0;
        double stepAngle = totalRadians / segments;
        double outer = majorRadius + stepHalf;
        // Above ~120 degrees per segment the tangent join length explodes; cap it so absurd
        // turn/segment combinations stay drawable instead of generating mile-long boxes.
        double halfLength = Math.Min(outer * Math.Tan(Math.Min(stepAngle, 2.1) * 0.5), outer * 2.0);

        List<ModelElementData> result = [];
        for (int segment = 0; segment < segments; segment++)
        {
            double angle = (segment + 0.5) * stepAngle;
            double vCenter = -climb * 0.5 + (segment + 0.5) * (climb / segments);
            result.Add(ModelPrimitiveBox(
                -halfLength, vCenter - stepHalf, majorRadius - stepHalf,
                halfLength, vCenter + stepHalf, majorRadius + stepHalf,
                angle * 180.0 / Math.PI));
        }

        return result;
    }

    private List<ModelElementData> ModelBuildStar()
    {
        // K squares rotated in 90/K degree increments union into an exact 4K-pointed star;
        // the square corners are the star tips, all at the same radius.
        double side = _modelPrimitiveDiameter / Math.Sqrt(2.0);
        double half = side * 0.5;
        double thicknessHalf = _modelPrimitiveThickness * 0.5;
        int squares = Math.Clamp(_modelPrimitiveStarSquares, 2, 6);

        if (_modelPrimitiveStepped)
        {
            double max = _modelPrimitiveDiameter * 0.5;
            return ModelBuildExactFlatSolid(-thicknessHalf, thicknessHalf, -max, max, -max, max, (u, w) =>
            {
                for (int square = 0; square < squares; square++)
                {
                    double radians = -square * (90.0 / squares) * Math.PI / 180.0;
                    (double ru, double rw) = ModelPrimitiveRotatePoint(u, w, radians);
                    if (Math.Abs(ru) <= half && Math.Abs(rw) <= half) return true;
                }

                return false;
            });
        }

        List<ModelElementData> result = [];
        for (int square = 0; square < squares; square++)
        {
            result.Add(ModelPrimitiveBox(-half, -thicknessHalf, -half, half, thicknessHalf, half, square * (90.0 / squares)));
        }

        return result;
    }

    private List<ModelElementData> ModelBuildCross()
    {
        double half = _modelPrimitiveDiameter * 0.5;
        double armHalf = Math.Min(_modelPrimitiveMinor * 0.5, half);
        double thicknessHalf = _modelPrimitiveThickness * 0.5;

        if (_modelPrimitiveStepped)
        {
            return
            [
                ModelPrimitiveBox(-half, -thicknessHalf, -armHalf, -armHalf, thicknessHalf, armHalf, 0.0),
                ModelPrimitiveBox(-armHalf, -thicknessHalf, -half, armHalf, thicknessHalf, half, 0.0),
                ModelPrimitiveBox(armHalf, -thicknessHalf, -armHalf, half, thicknessHalf, armHalf, 0.0)
            ];
        }

        return
        [
            ModelPrimitiveBox(-half, -thicknessHalf, -armHalf, half, thicknessHalf, armHalf, 0.0),
            ModelPrimitiveBox(-armHalf, -thicknessHalf, -half, armHalf, thicknessHalf, half, 0.0)
        ];
    }

    private List<ModelElementData> ModelBuildArrow()
    {
        double length = _modelPrimitiveDiameter;
        double shaftHalf = Math.Min(_modelPrimitiveMinor * 0.5, length * 0.4);
        double headSide = Math.Min(_modelPrimitiveDepth, length);
        double headHalfDiagonal = headSide * Math.Sqrt(2.0) * 0.5;
        double thicknessHalf = _modelPrimitiveThickness * 0.5;

        // Tip at +U: the head is a square rotated 45 degrees around its own center
        // so one corner points forward; the shaft runs up to the head center.
        double headCenterU = length * 0.5 - headHalfDiagonal;
        if (_modelPrimitiveStepped)
        {
            double minU = -length * 0.5;
            double maxU = length * 0.5;
            double maxW = Math.Max(headHalfDiagonal, shaftHalf);
            return ModelBuildExactFlatSolid(-thicknessHalf, thicknessHalf, minU, maxU, -maxW, maxW, (u, w) =>
            {
                bool inShaft = u >= minU && u <= headCenterU && Math.Abs(w) <= shaftHalf;
                double du = u - headCenterU;
                double dw = w;
                bool inDiamond = Math.Abs(du) + Math.Abs(dw) <= headHalfDiagonal;
                return inShaft || inDiamond;
            });
        }

        List<ModelElementData> result =
        [
            ModelPrimitiveBox(
                headCenterU - headSide * 0.5, -thicknessHalf, -headSide * 0.5,
                headCenterU + headSide * 0.5, thicknessHalf, headSide * 0.5,
                45.0,
                headCenterU, 0.0, 0.0)
        ];
        if (headCenterU > -length * 0.5 + 0.05)
        {
            result.Add(ModelPrimitiveBox(-length * 0.5, -thicknessHalf, -shaftHalf, headCenterU, thicknessHalf, shaftHalf, 0.0));
        }

        return result;
    }

    private List<ModelElementData> ModelBuildHeart()
    {
        // Classic construction: a square rotated 45 degrees (tip down) plus a disc on each
        // upper edge midpoint, disc diameter equal to the square side.
        double side = _modelPrimitiveDiameter / Math.Sqrt(2.0);
        double half = side * 0.5;
        double thicknessHalf = _modelPrimitiveThickness * 0.5;
        int sides = Math.Max(3, _modelPrimitiveSides);

        if (_modelPrimitiveStepped)
        {
            double exactLobeOffset = side * Math.Sqrt(2.0) * 0.25;
            double max = _modelPrimitiveDiameter * 0.55;
            return ModelBuildExactFlatSolid(-thicknessHalf, thicknessHalf, -max, max, -max, max, (u, w) =>
            {
                (double ru, double rw) = ModelPrimitiveRotatePoint(u, w, -45.0 * Math.PI / 180.0);
                bool inDiamond = Math.Abs(ru) <= half && Math.Abs(rw) <= half;
                bool inLeftLobe = (u + exactLobeOffset) * (u + exactLobeOffset) + (w - exactLobeOffset) * (w - exactLobeOffset) <= half * half;
                bool inRightLobe = (u - exactLobeOffset) * (u - exactLobeOffset) + (w - exactLobeOffset) * (w - exactLobeOffset) <= half * half;
                return inDiamond || inLeftLobe || inRightLobe;
            });
        }

        List<ModelElementData> result =
        [
            ModelPrimitiveBox(-half, -thicknessHalf, -half, half, thicknessHalf, half, 45.0)
        ];

        double lobeOffset = side * Math.Sqrt(2.0) * 0.25;
        ModelAddCrossingSlabDiscAt(result, -lobeOffset, lobeOffset, -thicknessHalf, thicknessHalf, half, sides);
        ModelAddCrossingSlabDiscAt(result, lobeOffset, lobeOffset, -thicknessHalf, thicknessHalf, half, sides);
        return result;
    }

    private List<ModelElementData> ModelBuildTrianglePlate()
    {
        double baseHalf = _modelPrimitiveDiameter * 0.5;
        double height = _modelPrimitiveRise;
        int rows = Math.Max(1, _modelPrimitiveLayers);
        double thicknessHalf = _modelPrimitiveThickness * 0.5;

        if (_modelPrimitiveStepped)
        {
            double baseW = -height * 0.5;
            double tipW = height * 0.5;
            return ModelBuildExactFlatSolid(-thicknessHalf, thicknessHalf, -baseHalf, baseHalf, baseW, tipW, (u, w) =>
            {
                double fraction = Math.Clamp((w - baseW) / height, 0.0, 1.0);
                return w >= baseW && w <= tipW && Math.Abs(u) <= baseHalf * (1.0 - fraction);
            });
        }

        if (!_modelPrimitiveStepped)
        {
            double edgeWidth = Math.Clamp(Math.Min(baseHalf * 2.0, height) / Math.Max(8.0, rows * 2.0), 0.125, 1.5);
            double overlap = ModelPrimitiveSmoothOverlap(Math.Max(baseHalf * 2.0, height));
            double baseW = -height * 0.5;
            double sideLength = Math.Sqrt(baseHalf * baseHalf + height * height);
            double leftAngle = Math.Atan2(height, baseHalf) * 180.0 / Math.PI;
            double rightAngle = Math.Atan2(-height, baseHalf) * 180.0 / Math.PI;

            List<ModelElementData> smooth = [];
            smooth.Add(ModelPrimitiveCenteredBox(0.0, 0.0, baseW + edgeWidth * 0.5, baseHalf * 2.0 + overlap * 2.0, _modelPrimitiveThickness, edgeWidth + overlap));
            smooth.Add(ModelPrimitiveCenteredBox(-baseHalf * 0.5, 0.0, 0.0, sideLength + overlap * 2.0, _modelPrimitiveThickness, edgeWidth + overlap, rotationV: leftAngle));
            smooth.Add(ModelPrimitiveCenteredBox(baseHalf * 0.5, 0.0, 0.0, sideLength + overlap * 2.0, _modelPrimitiveThickness, edgeWidth + overlap, rotationV: rightAngle));

            double smoothRowHeight = height / rows;
            for (int row = 0; row < rows; row++)
            {
                double w0 = baseW + row * smoothRowHeight;
                double w1 = w0 + smoothRowHeight;
                double rowMid = (w0 + w1) * 0.5;
                double fraction = Math.Clamp((rowMid - baseW) / height, 0.0, 1.0);
                double rowHalf = baseHalf * (1.0 - fraction) + overlap * 0.5;
                if (rowHalf < 0.025) continue;

                smooth.Add(ModelPrimitiveCenteredBox(0.0, 0.0, rowMid, rowHalf * 2.0, _modelPrimitiveThickness, smoothRowHeight + overlap, rotationV: 0.0));
            }

            return smooth;
        }

        return [];
    }

    private List<ModelElementData> ModelBuildSector()
    {
        double radius = _modelPrimitiveDiameter * 0.5;
        double thicknessHalf = _modelPrimitiveThickness * 0.5;
        double wall = _modelPrimitiveHollow ? Math.Min(_modelPrimitiveWall, radius - 0.01) : 0.0;
        double innerRadius = _modelPrimitiveHollow ? radius - wall : 0.0;
        if (!_modelPrimitiveStepped)
        {
            List<ModelElementData> result = [];
            int segments = Math.Max(3, _modelPrimitiveSegments);
            if (_modelPrimitiveSweep >= 359.9)
            {
                if (_modelPrimitiveHollow)
                {
                    ModelAddWallRing(result, -thicknessHalf, thicknessHalf, radius, wall, segments);
                }
                else
                {
                    ModelAddCrossingSlabDisc(result, -thicknessHalf, thicknessHalf, radius, segments);
                }
                return result;
            }

            ModelAddSmoothSectorPanels(result, -thicknessHalf, thicknessHalf, innerRadius, radius, _modelPrimitiveSweep, segments);
            return result;
        }

        return ModelBuildSteppedSolid(-thicknessHalf, thicknessHalf, radius, _ => (radius, innerRadius), _modelPrimitiveSweep);
    }

    private List<ModelElementData> ModelBuildBoxTube()
    {
        double halfU = _modelPrimitiveDiameter * 0.5;
        double halfW = _modelPrimitiveDepth * 0.5;
        double v0 = -_modelPrimitiveHeight * 0.5;
        double v1 = _modelPrimitiveHeight * 0.5;
        double wall = Math.Min(_modelPrimitiveWall, Math.Min(halfU, halfW) - 0.01);
        if (wall <= 0)
        {
            return [ModelPrimitiveBox(-halfU, v0, -halfW, halfU, v1, halfW, 0.0)];
        }

        // Four wall slabs forming a rectangular tube: full-width top/bottom, side walls between.
        return
        [
            ModelPrimitiveBox(-halfU, v0, halfW - wall, halfU, v1, halfW, 0.0),
            ModelPrimitiveBox(-halfU, v0, -halfW, halfU, v1, -halfW + wall, 0.0),
            ModelPrimitiveBox(-halfU, v0, -halfW + wall, -halfU + wall, v1, halfW - wall, 0.0),
            ModelPrimitiveBox(halfU - wall, v0, -halfW + wall, halfU, v1, halfW - wall, 0.0)
        ];
    }

    /// <summary>
    /// Voxelizes a rotationally symmetric solid into a shared 3D grid and greedily merges
    /// occupied cells into cuboids. The rasterizer is conservative at the shape boundary so
    /// exact-mode output does not develop holes inside the chosen grid approximation.
    /// </summary>
    private List<ModelElementData> ModelBuildSteppedSolid(double vMin, double vMax, double maxRadius, Func<double, (double Outer, double Inner)> radiiAt, double sweepDegrees = 360.0)
    {
        double step = Math.Clamp(_modelPrimitiveStep, 0.25f, 8f);
        int cells = Math.Max(1, (int)Math.Ceiling(maxRadius * 2.0 / step));
        double extent = cells * step * 0.5;
        int layers = Math.Max(1, (int)Math.Ceiling((vMax - vMin) / step));
        double sweepRadians = Math.Clamp(sweepDegrees, 1.0, 360.0) * Math.PI / 180.0;
        bool fullSweep = sweepDegrees >= 359.9;

        bool[,,] occupied = new bool[layers, cells, cells];

        for (int layer = 0; layer < layers; layer++)
        {
            double layerV0 = vMin + layer * step;
            double layerV1 = Math.Min(vMax, layerV0 + step);
            (double outerRadius, double innerRadius) = radiiAt((layerV0 + layerV1) * 0.5);

            for (int u = 0; u < cells; u++)
            {
                double u0 = -extent + u * step;
                double u1 = u0 + step;
                for (int w = 0; w < cells; w++)
                {
                    double w0 = -extent + w * step;
                    double w1 = w0 + step;
                    occupied[layer, u, w] = ModelPrimitiveCellIntersectsAnnularSector(u0, u1, w0, w1, outerRadius, innerRadius, sweepRadians, fullSweep);
                }
            }
        }

        return ModelMergeOccupancyToCuboids(occupied, layers, cells, cells, -extent, -extent, vMin, vMax, step);
    }

    private List<ModelElementData> ModelBuildExactFlatSolid(double vMin, double vMax, double minU, double maxU, double minW, double maxW, Func<double, double, bool> contains)
    {
        double step = Math.Clamp(_modelPrimitiveStep, 0.25f, 8f);
        int cellsU = Math.Max(1, (int)Math.Ceiling((maxU - minU) / step));
        int cellsW = Math.Max(1, (int)Math.Ceiling((maxW - minW) / step));
        bool[,,] occupied = new bool[1, cellsU, cellsW];

        for (int u = 0; u < cellsU; u++)
        {
            double u0 = minU + u * step;
            double u1 = Math.Min(maxU, u0 + step);
            for (int w = 0; w < cellsW; w++)
            {
                double w0 = minW + w * step;
                double w1 = Math.Min(maxW, w0 + step);
                occupied[0, u, w] = ModelPrimitiveCellIntersectsShape(u0, u1, w0, w1, contains);
            }
        }

        return ModelMergeOccupancyToCuboids(occupied, 1, cellsU, cellsW, minU, minW, vMin, vMax, step);
    }

    private List<ModelElementData> ModelMergeOccupancyToCuboids(bool[,,] occupied, int layers, int cellsU, int cellsW, double minU, double minW, double vMin, double vMax, double step)
    {
        List<ModelElementData> result = [];
        bool[,,] consumed = new bool[layers, cellsU, cellsW];

        for (int v = 0; v < layers; v++)
        {
            for (int u = 0; u < cellsU; u++)
            {
                for (int w = 0; w < cellsW; w++)
                {
                    if (!occupied[v, u, w] || consumed[v, u, w]) continue;

                    int uEnd = u;
                    while (uEnd + 1 < cellsU && occupied[v, uEnd + 1, w] && !consumed[v, uEnd + 1, w])
                    {
                        uEnd++;
                    }

                    int wEnd = w;
                    while (wEnd + 1 < cellsW && ModelPrimitiveRectAvailable(occupied, consumed, v, u, uEnd, wEnd + 1))
                    {
                        wEnd++;
                    }

                    int vEnd = v;
                    while (vEnd + 1 < layers && ModelPrimitiveBoxAvailable(occupied, consumed, vEnd + 1, u, uEnd, w, wEnd))
                    {
                        vEnd++;
                    }

                    for (int vv = v; vv <= vEnd; vv++)
                    {
                        for (int uu = u; uu <= uEnd; uu++)
                        {
                            for (int ww = w; ww <= wEnd; ww++)
                            {
                                consumed[vv, uu, ww] = true;
                            }
                        }
                    }

                    double u0 = minU + u * step;
                    double u1 = minU + (uEnd + 1) * step;
                    double w0 = minW + w * step;
                    double w1 = minW + (wEnd + 1) * step;
                    double vv0 = vMin + v * step;
                    double vv1 = vEnd + 1 >= layers ? vMax : vMin + (vEnd + 1) * step;
                    result.Add(ModelPrimitiveBox(u0, vv0, w0, u1, vv1, w1, 0.0));
                }
            }
        }

        return result;
    }

    private static bool ModelPrimitiveRectAvailable(bool[,,] occupied, bool[,,] consumed, int v, int u0, int u1, int w)
    {
        for (int u = u0; u <= u1; u++)
        {
            if (!occupied[v, u, w] || consumed[v, u, w]) return false;
        }

        return true;
    }

    private static bool ModelPrimitiveBoxAvailable(bool[,,] occupied, bool[,,] consumed, int v, int u0, int u1, int w0, int w1)
    {
        for (int u = u0; u <= u1; u++)
        {
            for (int w = w0; w <= w1; w++)
            {
                if (!occupied[v, u, w] || consumed[v, u, w]) return false;
            }
        }

        return true;
    }

    private static bool ModelPrimitiveCellIntersectsAnnularSector(double u0, double u1, double w0, double w1, double outerRadius, double innerRadius, double sweepRadians, bool fullSweep)
    {
        if (outerRadius <= 0.0) return false;

        double closestU = u0 > 0 ? u0 : u1 < 0 ? u1 : 0.0;
        double closestW = w0 > 0 ? w0 : w1 < 0 ? w1 : 0.0;
        if (closestU * closestU + closestW * closestW > outerRadius * outerRadius) return false;

        if (innerRadius > 0.0)
        {
            double farthestDistanceSquared = Math.Max(
                Math.Max(u0 * u0 + w0 * w0, u0 * u0 + w1 * w1),
                Math.Max(u1 * u1 + w0 * w0, u1 * u1 + w1 * w1));
            if (farthestDistanceSquared < innerRadius * innerRadius) return false;
        }

        if (fullSweep) return true;

        return ModelPrimitiveCellIntersectsShape(u0, u1, w0, w1, (u, w) =>
        {
            double distanceSquared = u * u + w * w;
            if (distanceSquared > outerRadius * outerRadius) return false;
            if (innerRadius > 0.0 && distanceSquared < innerRadius * innerRadius) return false;
            double angle = Math.Atan2(u, w);
            return Math.Abs(angle) <= sweepRadians * 0.5;
        });
    }

    private static bool ModelPrimitiveCellIntersectsShape(double u0, double u1, double w0, double w1, Func<double, double, bool> contains)
    {
        double centerU = (u0 + u1) * 0.5;
        double centerW = (w0 + w1) * 0.5;
        return contains(centerU, centerW) ||
            contains(u0, w0) ||
            contains(u0, w1) ||
            contains(u1, w0) ||
            contains(u1, w1);
    }

    private static (double U, double W) ModelPrimitiveRotatePoint(double u, double w, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return (u * cos - w * sin, u * sin + w * cos);
    }

    private static bool ModelMasksEqual(bool[,] left, bool[,] right, int cells)
    {
        for (int u = 0; u < cells; u++)
        {
            for (int w = 0; w < cells; w++)
            {
                if (left[u, w] != right[u, w]) return false;
            }
        }
        return true;
    }

    /// <summary>Greedy scanline merge: horizontal runs per row, then identical runs on consecutive rows extend vertically.</summary>
    private static List<(int U0, int W0, int U1, int W1)> ModelMergeMaskToRects(bool[,] mask, int cells)
    {
        List<(int U0, int W0, int U1, int W1)> closed = [];
        List<(int U0, int U1, int W0)> active = [];

        for (int w = 0; w < cells; w++)
        {
            List<(int U0, int U1)> runs = [];
            int runStart = -1;
            for (int u = 0; u <= cells; u++)
            {
                bool filled = u < cells && mask[u, w];
                if (filled && runStart < 0)
                {
                    runStart = u;
                }
                else if (!filled && runStart >= 0)
                {
                    runs.Add((runStart, u - 1));
                    runStart = -1;
                }
            }

            List<(int U0, int U1, int W0)> nextActive = [];
            foreach ((int u0, int u1) in runs)
            {
                int match = active.FindIndex(rect => rect.U0 == u0 && rect.U1 == u1);
                if (match >= 0)
                {
                    nextActive.Add(active[match]);
                    active.RemoveAt(match);
                }
                else
                {
                    nextActive.Add((u0, u1, w));
                }
            }

            foreach ((int u0, int u1, int w0) in active)
            {
                closed.Add((u0, w0, u1, w - 1));
            }
            active = nextActive;
        }

        foreach ((int u0, int u1, int w0) in active)
        {
            closed.Add((u0, w0, u1, cells - 1));
        }

        return closed;
    }

    private sealed class ModelPrimitiveMetrics
    {
        public string ModeLabel { get; set; } = "Exact merged";
        public string QualityLabel { get; set; } = "clean exact grid";
        public int Cuboids { get; set; }
        public int EnabledFaces { get; set; }
        public int CulledInternalFaces { get; set; }
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
    }
}
