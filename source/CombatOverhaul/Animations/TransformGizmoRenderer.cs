#if DEBUG
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CombatOverhaul.Animations;

internal enum TransformGizmoMode
{
    None,
    Move,
    Scale,
    Rotate
}

internal enum TransformGizmoContext
{
    Free,
    MainHand,
    OffHand,
    Ground,
    Display,
    RigPart
}

internal enum TransformGizmoSpace
{
    World,
    Local,
    Parent
}

internal enum TransformGizmoAxis
{
    None,
    X,
    Y,
    Z
}

internal readonly record struct TransformGizmoGhostModel(MultiTextureMeshRef MeshRef, float[] ModelMatrix, float[] AnimationMatrices, int MaxJointId, Vec4f Color, BlockPos LightPos);
internal readonly record struct TransformGizmoMotionPath(IReadOnlyList<Vec3d> Points, Vec3d? CurrentPoint, int PastColor, int FutureColor, int CurrentColor, float Thickness, bool SplitAtCurrentTime, double CurrentFraction);

internal readonly struct TransformGizmoAxes
{
    public TransformGizmoAxes(Vec3d x, Vec3d y, Vec3d z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public readonly Vec3d X;
    public readonly Vec3d Y;
    public readonly Vec3d Z;
}

internal sealed class TransformGizmoRenderer : IRenderer
{
    private const double AxisLength = 0.72;
    private const double PickDistance = 12;
    private const double CubeHalfSize = 0.055;
    private const double ArrowHeadLength = 0.14;
    private const double ArrowHeadWidth = 0.07;

    private static readonly int Red = ColorUtil.ColorFromRgba(255, 35, 35, 255);
    private static readonly int Green = ColorUtil.ColorFromRgba(35, 220, 35, 255);
    private static readonly int Blue = ColorUtil.ColorFromRgba(45, 120, 255, 255);
    private static readonly int Yellow = ColorUtil.ColorFromRgba(255, 230, 40, 255);
    private static readonly int Highlight = ColorUtil.ColorFromRgba(255, 180, 45, 255);

    private readonly ICoreClientAPI _api;
    private readonly DebugWindowManager _debugManager;

    private TransformGizmoAxis _hoveredAxis = TransformGizmoAxis.None;
    private TransformGizmoAxis _draggedAxis = TransformGizmoAxis.None;
    private int _dragStartX;
    private int _dragStartY;
    private Vec2d _dragScreenAxis = new(1, 0);
    private FastVec3f _dragStartTranslation;
    private FastVec3f _dragStartRotation;
    private FastVec3f _dragStartScale;
    private GizmoState _dragStartState;
    private Vec3d _dragWorldAxis = new(1, 0, 0);
    private Vec3d _dragRotateStartVector = new(1, 0, 0);
    private double _dragStartAxisCoordinate;
    private bool _dragHasRayAxisCoordinate;
    private bool _dragHasRotateVector;

    public double RenderOrder => 0.93;
    public int RenderRange => 9999;

    public TransformGizmoRenderer(ICoreClientAPI api, DebugWindowManager debugManager)
    {
        _api = api;
        _debugManager = debugManager;
        api.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "overhaullib-transform-gizmos");
        api.Event.MouseDown += OnMouseDown;
        api.Event.MouseMove += OnMouseMove;
        api.Event.MouseUp += OnMouseUp;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Opaque) return;

        bool hasHighlight = _debugManager.TryGetRigPartHighlightCorners(out Vec3d[] highlightCorners);
        bool hasOnionSkins = _debugManager.TryGetRigOnionSkinModels(out IReadOnlyList<TransformGizmoGhostModel> onionSkinModels);
        bool hasMotionPaths = _debugManager.TryGetRigMotionPaths(out IReadOnlyList<TransformGizmoMotionPath> motionPaths);
        GizmoState state = default;
        bool hasGizmo = ShouldDraw && TryBuildState(out state);
        if (!hasHighlight && !hasOnionSkins && !hasMotionPaths && !hasGizmo) return;

        if (hasGizmo && _draggedAxis == TransformGizmoAxis.None)
        {
            _hoveredAxis = PickAxis(state, _api.Input.MouseX, _api.Input.MouseY);
        }

        if (hasOnionSkins)
        {
            DrawOnionSkinModels(onionSkinModels);
        }

        _api.Render.GLDisableDepthTest();
        if (hasMotionPaths) DrawMotionPaths(motionPaths);
        if (hasHighlight) DrawWireBox(highlightCorners, Highlight);
        if (hasGizmo) DrawActiveGizmo(state);
        _api.Render.GLEnableDepthTest();
    }

    private bool ShouldDraw => _debugManager.GizmoMode != TransformGizmoMode.None && _debugManager.TryGetActiveTransformGizmo(out _, out _, out _);

    private void OnMouseDown(MouseEvent args)
    {
        if (args.Handled || args.Button != EnumMouseButton.Left) return;
        if (_debugManager.PointInsideDebugUi()) return;

        if (IsShiftDown() && TryGetMouseRay(args.X, args.Y, out Vec3d rayOrigin, out Vec3d rayDirection) && _debugManager.TryPickRigPart(rayOrigin, rayDirection))
        {
            args.Handled = true;
            return;
        }

        if (!ShouldDraw) return;
        if (!TryBuildState(out GizmoState state)) return;

        TransformGizmoAxis picked = PickAxis(state, args.X, args.Y);
        if (picked == TransformGizmoAxis.None) return;

        _draggedAxis = picked;
        _hoveredAxis = picked;
        _dragStartState = state;
        _dragStartX = args.X;
        _dragStartY = args.Y;
        _dragScreenAxis = GetScreenAxis(state, picked);
        _dragWorldAxis = GetWorldAxis(state, picked);
        _dragHasRayAxisCoordinate = TryGetAxisCoordinate(state.Center, _dragWorldAxis, args.X, args.Y, out _dragStartAxisCoordinate);
        _dragHasRotateVector = TryGetRotateVector(state.Center, _dragWorldAxis, args.X, args.Y, out _dragRotateStartVector);

        if (_debugManager.TryGetActiveTransformGizmo(out ModelTransform transform, out _, out _))
        {
            _dragStartTranslation = transform.Translation;
            _dragStartRotation = transform.Rotation;
            _dragStartScale = transform.ScaleXYZ;
        }

        _debugManager.NotifyActiveTransformGizmoDragStarted();
        args.Handled = true;
    }

    private void OnMouseMove(MouseEvent args)
    {
        if (!ShouldDraw)
        {
            _hoveredAxis = TransformGizmoAxis.None;
            if (_draggedAxis != TransformGizmoAxis.None) _debugManager.NotifyActiveTransformGizmoDragEnded();
            _draggedAxis = TransformGizmoAxis.None;
            return;
        }

        if (_draggedAxis == TransformGizmoAxis.None)
        {
            if (args.Handled || _debugManager.PointInsideDebugUi() || !TryBuildState(out GizmoState state))
            {
                _hoveredAxis = TransformGizmoAxis.None;
                return;
            }

            _hoveredAxis = PickAxis(state, args.X, args.Y);
            return;
        }

        ApplyDrag(args.X, args.Y);
        args.Handled = true;
    }

    private void OnMouseUp(MouseEvent args)
    {
        if (_draggedAxis == TransformGizmoAxis.None) return;
        _draggedAxis = TransformGizmoAxis.None;
        _hoveredAxis = TransformGizmoAxis.None;
        _debugManager.NotifyActiveTransformGizmoDragEnded();
        args.Handled = true;
    }

    private void ApplyDrag(int mouseX, int mouseY)
    {
        double dx = mouseX - _dragStartX;
        double dy = mouseY - _dragStartY;
        double fallbackPixels = dx * _dragScreenAxis.X + dy * _dragScreenAxis.Y;

        switch (_debugManager.GizmoMode)
        {
            case TransformGizmoMode.Move:
                ApplyMoveDrag(GetAxisDelta(mouseX, mouseY, fallbackPixels));
                break;
            case TransformGizmoMode.Scale:
                ApplyScaleDrag(GetAxisDelta(mouseX, mouseY, fallbackPixels));
                break;
            case TransformGizmoMode.Rotate:
                ApplyRotateDrag(mouseX, mouseY, fallbackPixels);
                break;
        }
    }

    private double GetAxisDelta(int mouseX, int mouseY, double fallbackPixels)
    {
        if (_dragHasRayAxisCoordinate && TryGetAxisCoordinate(_dragStartState.Center, _dragWorldAxis, mouseX, mouseY, out double coordinate))
        {
            return coordinate - _dragStartAxisCoordinate;
        }

        return fallbackPixels * 0.01;
    }

    private void ApplyMoveDrag(double axisWorldDelta)
    {
        Vec3d desiredWorldDelta = Scale(_dragWorldAxis, axisWorldDelta);
        Vec3d translationDelta = _dragStartState.TranslationBasis.WorldToTransformDelta(desiredWorldDelta);

        if (_debugManager.IncludeGizmoInIncrement)
        {
            double interval = Math.Max(0.0001, _debugManager.TransformGizmoIncrement);
            translationDelta.X = Snap(translationDelta.X, interval);
            translationDelta.Y = Snap(translationDelta.Y, interval);
            translationDelta.Z = Snap(translationDelta.Z, interval);
        }

        _debugManager.SetGizmoTranslation(
            _dragStartTranslation.X + (float)translationDelta.X,
            _dragStartTranslation.Y + (float)translationDelta.Y,
            _dragStartTranslation.Z + (float)translationDelta.Z
        );
    }

    private void ApplyScaleDrag(double axisWorldDelta)
    {
        int deltaPercent = (int)Math.Round(axisWorldDelta * 100);
        float x = _dragStartScale.X;
        float y = _dragStartScale.Y;
        float z = _dragStartScale.Z;

        switch (_draggedAxis)
        {
            case TransformGizmoAxis.X:
                x = ScaleAxis(_dragStartScale.X, deltaPercent);
                break;
            case TransformGizmoAxis.Y:
                y = ScaleAxis(_dragStartScale.Y, deltaPercent);
                break;
            case TransformGizmoAxis.Z:
                z = ScaleAxis(_dragStartScale.Z, deltaPercent);
                break;
        }

        _debugManager.SetGizmoScale(x, y, z);
    }

    private void ApplyRotateDrag(int mouseX, int mouseY, double fallbackPixels)
    {
        double deltaDegrees = fallbackPixels * 0.5;

        if (_dragHasRotateVector && TryGetRotateVector(_dragStartState.Center, _dragWorldAxis, mouseX, mouseY, out Vec3d currentVector))
        {
            double dot = Math.Clamp(Dot(_dragRotateStartVector, currentVector), -1, 1);
            double signed = Dot(_dragWorldAxis, _dragRotateStartVector.Cross(currentVector));
            deltaDegrees = Math.Atan2(signed, dot) * GameMath.RAD2DEG;
        }

        int snappedDegrees = (int)Math.Round(deltaDegrees);
        Matrix3 startActualRotation = Matrix3.FromEulerDegrees(
            _dragStartRotation.X + _dragStartState.AttachmentRotation.X,
            _dragStartRotation.Y + _dragStartState.AttachmentRotation.Y,
            _dragStartRotation.Z + _dragStartState.AttachmentRotation.Z
        );

        Matrix3 newActualRotation;
        if (_debugManager.GizmoSpace == TransformGizmoSpace.Local)
        {
            Matrix3 axisRotation = Matrix3.FromAxisAngle(GetCanonicalAxis(_draggedAxis), snappedDegrees * GameMath.DEG2RAD);
            newActualRotation = startActualRotation.Mul(axisRotation);
        }
        else
        {
            Matrix3 axisRotation = Matrix3.FromAxisAngle(_dragWorldAxis, snappedDegrees * GameMath.DEG2RAD);
            Matrix3 startWorldRotation = _dragStartState.RotationParentBasis.Mul(startActualRotation);
            Matrix3 newWorldRotation = axisRotation.Mul(startWorldRotation);
            newActualRotation = _dragStartState.RotationParentBasisInverse.Mul(newWorldRotation);
        }

        Vec3d euler = newActualRotation.Orthonormalized().ToEulerDegrees();
        _debugManager.SetGizmoRotation(
            NormalizeDegrees((float)(euler.X - _dragStartState.AttachmentRotation.X)),
            NormalizeDegrees((float)(euler.Y - _dragStartState.AttachmentRotation.Y)),
            NormalizeDegrees((float)(euler.Z - _dragStartState.AttachmentRotation.Z))
        );
    }

    private static float ScaleAxis(float startScale, int deltaPercent)
    {
        float sign = startScale < 0 ? -1f : 1f;
        int startPercent = Math.Clamp((int)Math.Round(Math.Abs(startScale) * 100), 1, 300);
        int newPercent = Math.Clamp(startPercent + deltaPercent, 1, 300);
        return sign * newPercent / 100f;
    }

    private static float NormalizeDegrees(float degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double Snap(double value, double interval) => Math.Round(value / interval) * interval;

    private void DrawActiveGizmo(GizmoState state)
    {
        switch (_debugManager.GizmoMode)
        {
            case TransformGizmoMode.Move:
                DrawAxisArrow(state, TransformGizmoAxis.X);
                DrawAxisArrow(state, TransformGizmoAxis.Y);
                DrawAxisArrow(state, TransformGizmoAxis.Z);
                break;
            case TransformGizmoMode.Scale:
                DrawAxisCube(state, TransformGizmoAxis.X);
                DrawAxisCube(state, TransformGizmoAxis.Y);
                DrawAxisCube(state, TransformGizmoAxis.Z);
                break;
            case TransformGizmoMode.Rotate:
                DrawAxisCircle(state, TransformGizmoAxis.X);
                DrawAxisCircle(state, TransformGizmoAxis.Y);
                DrawAxisCircle(state, TransformGizmoAxis.Z);
                break;
        }
    }

    private void DrawAxisArrow(GizmoState state, TransformGizmoAxis axis)
    {
        Vec3d direction = GetWorldAxis(state, axis);
        Vec3d end = Add(state.Center, Scale(direction, AxisLength));
        int color = AxisColor(axis);

        DrawLine(state.Center, end, color);
        Vec3d side = Perpendicular(direction, state.CameraUp);
        Vec3d side2 = Perpendicular(direction, state.CameraRight);
        Vec3d basePoint = Add(end, Scale(direction, -ArrowHeadLength));
        DrawLine(end, Add(basePoint, Scale(side, ArrowHeadWidth)), color);
        DrawLine(end, Add(basePoint, Scale(side, -ArrowHeadWidth)), color);
        DrawLine(end, Add(basePoint, Scale(side2, ArrowHeadWidth)), color);
        DrawLine(end, Add(basePoint, Scale(side2, -ArrowHeadWidth)), color);
    }

    private void DrawAxisCube(GizmoState state, TransformGizmoAxis axis)
    {
        Vec3d direction = GetWorldAxis(state, axis);
        Vec3d end = Add(state.Center, Scale(direction, AxisLength));
        int color = AxisColor(axis);

        DrawLine(state.Center, end, color);
        DrawWireCube(end, CubeHalfSize, color);
    }

    private void DrawAxisCircle(GizmoState state, TransformGizmoAxis axis)
    {
        GetCircleBasis(state, axis, out Vec3d u, out Vec3d v);
        int color = AxisColor(axis);
        Vec3d previous = Add(state.Center, Scale(u, AxisLength));

        for (int i = 1; i <= 64; i++)
        {
            double angle = GameMath.TWOPI * i / 64;
            Vec3d point = Add(state.Center, Add(Scale(u, Math.Cos(angle) * AxisLength), Scale(v, Math.Sin(angle) * AxisLength)));
            DrawLine(previous, point, color);
            previous = point;
        }
    }

    private void GetCircleBasis(GizmoState state, TransformGizmoAxis axis, out Vec3d u, out Vec3d v)
    {
        Vec3d normal = GetWorldAxis(state, axis);
        u = Perpendicular(normal, state.CameraUp);
        v = normal.Cross(u).Normalize();
    }

    private int AxisColor(TransformGizmoAxis axis)
    {
        if (axis == _draggedAxis || axis == _hoveredAxis) return Yellow;

        return axis switch
        {
            TransformGizmoAxis.X => Red,
            TransformGizmoAxis.Y => Green,
            TransformGizmoAxis.Z => Blue,
            _ => Yellow
        };
    }

    private void DrawWireCube(Vec3d center, double halfSize, int color)
    {
        Vec3d x = Scale(new Vec3d(1, 0, 0), halfSize);
        Vec3d y = Scale(new Vec3d(0, 1, 0), halfSize);
        Vec3d z = Scale(new Vec3d(0, 0, 1), halfSize);

        Vec3d[] points =
        [
            Add(center, Add(Add(Scale(x, -1), Scale(y, -1)), Scale(z, -1))),
            Add(center, Add(Add(Scale(x, 1), Scale(y, -1)), Scale(z, -1))),
            Add(center, Add(Add(Scale(x, 1), Scale(y, 1)), Scale(z, -1))),
            Add(center, Add(Add(Scale(x, -1), Scale(y, 1)), Scale(z, -1))),
            Add(center, Add(Add(Scale(x, -1), Scale(y, -1)), Scale(z, 1))),
            Add(center, Add(Add(Scale(x, 1), Scale(y, -1)), Scale(z, 1))),
            Add(center, Add(Add(Scale(x, 1), Scale(y, 1)), Scale(z, 1))),
            Add(center, Add(Add(Scale(x, -1), Scale(y, 1)), Scale(z, 1)))
        ];

        DrawLine(points[0], points[1], color);
        DrawLine(points[1], points[2], color);
        DrawLine(points[2], points[3], color);
        DrawLine(points[3], points[0], color);
        DrawLine(points[4], points[5], color);
        DrawLine(points[5], points[6], color);
        DrawLine(points[6], points[7], color);
        DrawLine(points[7], points[4], color);
        DrawLine(points[0], points[4], color);
        DrawLine(points[1], points[5], color);
        DrawLine(points[2], points[6], color);
        DrawLine(points[3], points[7], color);
    }

    private void DrawWireBox(Vec3d[] points, int color)
    {
        if (points.Length < 8) return;

        DrawLine(points[0], points[1], color);
        DrawLine(points[1], points[2], color);
        DrawLine(points[2], points[3], color);
        DrawLine(points[3], points[0], color);
        DrawLine(points[4], points[5], color);
        DrawLine(points[5], points[6], color);
        DrawLine(points[6], points[7], color);
        DrawLine(points[7], points[4], color);
        DrawLine(points[0], points[4], color);
        DrawLine(points[1], points[5], color);
        DrawLine(points[2], points[6], color);
        DrawLine(points[3], points[7], color);
    }

    private void DrawLine(Vec3d start, Vec3d end, int color) => DrawLine(start, end, color, 1f);

    private void DrawLine(Vec3d start, Vec3d end, int color, float thickness)
    {
        DrawLineRaw(start, end, color);
        int extraLines = Math.Clamp((int)Math.Round(thickness) - 1, 0, 4);
        if (extraLines <= 0) return;

        GetCameraBasis(out _, out Vec3d right, out Vec3d up);
        double offset = 0.0035 * Math.Max(1, thickness);
        for (int index = 1; index <= extraLines; index++)
        {
            double scaledOffset = offset * index;
            Vec3d horizontal = Scale(right, scaledOffset);
            Vec3d vertical = Scale(up, scaledOffset);
            DrawLineRaw(Add(start, horizontal), Add(end, horizontal), color);
            DrawLineRaw(Sub(start, horizontal), Sub(end, horizontal), color);
            DrawLineRaw(Add(start, vertical), Add(end, vertical), color);
            DrawLineRaw(Sub(start, vertical), Sub(end, vertical), color);
        }
    }

    private void DrawLineRaw(Vec3d start, Vec3d end, int color)
    {
        BlockPos origin = new((int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Floor(start.Z));
        _api.Render.RenderLine(origin, (float)(start.X - origin.X), (float)(start.Y - origin.Y), (float)(start.Z - origin.Z), (float)(end.X - origin.X), (float)(end.Y - origin.Y), (float)(end.Z - origin.Z), color);
    }

    private void DrawMotionPaths(IReadOnlyList<TransformGizmoMotionPath> paths)
    {
        foreach (TransformGizmoMotionPath path in paths)
        {
            if (path.Points.Count < 2) continue;

            int segmentCount = path.Points.Count - 1;
            for (int index = 1; index < path.Points.Count; index++)
            {
                double segmentFraction = (index - 0.5) / segmentCount;
                int color = path.SplitAtCurrentTime && segmentFraction > path.CurrentFraction ? path.FutureColor : path.PastColor;
                DrawLine(path.Points[index - 1], path.Points[index], color, path.Thickness);
            }

            if (path.CurrentPoint != null)
            {
                DrawWireCube(path.CurrentPoint, 0.035 * Math.Max(1, path.Thickness), path.CurrentColor);
            }
        }
    }

    private void DrawOnionSkinModels(IReadOnlyList<TransformGizmoGhostModel> models)
    {
        if (models.Count == 0) return;

        IRenderAPI render = _api.Render;
        IShaderProgram? previousShader = render.CurrentActiveShader;
        previousShader?.Stop();

        render.GlToggleBlend(true, EnumBlendMode.Standard);
        render.GLDepthMask(false);

        IShaderProgram shader = render.GetEngineShader(EnumShaderProgram.Entityanimated);
        shader.Use();

        foreach (TransformGizmoGhostModel model in models)
        {
            if (model.MeshRef.Disposed || !model.MeshRef.Initialized) continue;

            Vec4f light = _api.World.BlockAccessor.GetLightRGBs(model.LightPos.X, model.LightPos.Y, model.LightPos.Z);
            shader.Uniform("extraGlow", 0);
            shader.Uniform("rgbaAmbientIn", render.AmbientColor);
            shader.Uniform("rgbaFogIn", render.FogColor);
            shader.Uniform("fogMinIn", render.FogMin);
            shader.Uniform("fogDensityIn", render.FogDensity);
            shader.Uniform("rgbaLightIn", light);
            shader.Uniform("renderColor", model.Color);
            shader.Uniform("alphaTest", 0.01f);
            shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);
            shader.UniformMatrix("modelMatrix", model.ModelMatrix);
            shader.UniformMatrix("viewMatrix", render.CameraMatrixOriginf);
            shader.Uniform("addRenderFlags", 0);
            shader.Uniform("windWaveIntensity", 0f);
            shader.Uniform("entityId", 0);
            shader.Uniform("glitchFlicker", 0);
            shader.Uniform("frostAlpha", 0f);
            shader.Uniform("waterWaveCounter", render.ShaderUniforms.WaterWaveCounter);
            shader.Uniform("glitchEffectStrength", 0f);
            shader.UBOs["Animation"].Update(model.AnimationMatrices, 0, model.MaxJointId * 16 * 4);
            render.RenderMultiTextureMesh(model.MeshRef, "entityTex", 0);
        }

        shader.Stop();
        render.GLDepthMask(true);
        render.GlToggleBlend(false);
        previousShader?.Use();
    }

    private TransformGizmoAxis PickAxis(GizmoState state, int mouseX, int mouseY) => _debugManager.GizmoMode == TransformGizmoMode.Rotate ? PickCircleAxis(state, mouseX, mouseY) : PickLinearAxis(state, mouseX, mouseY);

    private TransformGizmoAxis PickLinearAxis(GizmoState state, int mouseX, int mouseY)
    {
        double best = PickDistance;
        TransformGizmoAxis picked = TransformGizmoAxis.None;

        foreach (TransformGizmoAxis axis in new[] { TransformGizmoAxis.X, TransformGizmoAxis.Y, TransformGizmoAxis.Z })
        {
            Vec3d end = Add(state.Center, Scale(GetWorldAxis(state, axis), AxisLength));
            if (!Project(state.Center, out Vec2d a) || !Project(end, out Vec2d b)) continue;

            double distance = DistancePointToSegment(mouseX, mouseY, a, b);
            if (distance >= best) continue;

            best = distance;
            picked = axis;
        }

        return picked;
    }

    private TransformGizmoAxis PickCircleAxis(GizmoState state, int mouseX, int mouseY)
    {
        double best = PickDistance;
        TransformGizmoAxis picked = TransformGizmoAxis.None;

        foreach (TransformGizmoAxis axis in new[] { TransformGizmoAxis.X, TransformGizmoAxis.Y, TransformGizmoAxis.Z })
        {
            GetCircleBasis(state, axis, out Vec3d u, out Vec3d v);
            Vec3d first = Add(state.Center, Scale(u, AxisLength));
            if (!Project(first, out Vec2d previous)) continue;

            for (int i = 1; i <= 64; i++)
            {
                double angle = GameMath.TWOPI * i / 64;
                Vec3d point = Add(state.Center, Add(Scale(u, Math.Cos(angle) * AxisLength), Scale(v, Math.Sin(angle) * AxisLength)));
                if (!Project(point, out Vec2d projected)) continue;

                double distance = DistancePointToSegment(mouseX, mouseY, previous, projected);
                if (distance < best)
                {
                    best = distance;
                    picked = axis;
                }

                previous = projected;
            }
        }

        return picked;
    }

    private Vec2d GetScreenAxis(GizmoState state, TransformGizmoAxis axis)
    {
        Vec3d end = Add(state.Center, Scale(GetWorldAxis(state, axis), AxisLength));
        if (!Project(state.Center, out Vec2d a) || !Project(end, out Vec2d b)) return new Vec2d(1, 0);

        Vec2d vector = new(b.X - a.X, b.Y - a.Y);
        double length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
        if (length < 0.001) return new Vec2d(1, 0);

        vector.X /= length;
        vector.Y /= length;
        return vector;
    }

    private bool Project(Vec3d worldPos, out Vec2d screenPos)
    {
        screenPos = new Vec2d();
        Vec3d projected = MatrixToolsd.Project(worldPos, _api.Render.PerspectiveProjectionMat, _api.Render.PerspectiveViewMat, _api.Render.FrameWidth, _api.Render.FrameHeight);
        if (projected.Z < 0) return false;

        screenPos.X = projected.X;
        screenPos.Y = _api.Render.FrameHeight - projected.Y;
        return true;
    }

    private bool TryGetMouseRay(int mouseX, int mouseY, out Vec3d origin, out Vec3d direction)
    {
        origin = new Vec3d();
        direction = new Vec3d();

        double[] projectionView = Mat4d.Create();
        Mat4d.Mul(projectionView, _api.Render.PerspectiveProjectionMat, _api.Render.PerspectiveViewMat);

        double[] inverse = Mat4d.Create();
        if (Mat4d.Invert(inverse, projectionView) == null) return false;
        if (!Unproject(inverse, mouseX, mouseY, -1, out Vec3d near)) return false;
        if (!Unproject(inverse, mouseX, mouseY, 1, out Vec3d far)) return false;

        direction = Sub(far, near);
        if (direction.LengthSq() < 0.000001) return false;

        origin = near;
        direction.Normalize();
        return true;
    }

    private bool Unproject(double[] inverseProjectionView, int mouseX, int mouseY, double clipZ, out Vec3d world)
    {
        world = new Vec3d();
        double ndcX = 2.0 * mouseX / _api.Render.FrameWidth - 1;
        double ndcY = 1 - 2.0 * mouseY / _api.Render.FrameHeight;
        double[] result = Mat4d.MulWithVec4(inverseProjectionView, new[] { ndcX, ndcY, clipZ, 1.0 });
        if (Math.Abs(result[3]) < 0.000001) return false;

        world.X = result[0] / result[3];
        world.Y = result[1] / result[3];
        world.Z = result[2] / result[3];
        return true;
    }

    private bool TryGetAxisCoordinate(Vec3d axisCenter, Vec3d axisDirection, int mouseX, int mouseY, out double coordinate)
    {
        coordinate = 0;
        if (!TryGetMouseRay(mouseX, mouseY, out Vec3d rayOrigin, out Vec3d rayDirection)) return false;

        Vec3d w0 = Sub(axisCenter, rayOrigin);
        double b = Dot(axisDirection, rayDirection);
        double d = Dot(axisDirection, w0);
        double e = Dot(rayDirection, w0);
        double denom = 1 - b * b;
        if (Math.Abs(denom) < 0.00001) return false;

        coordinate = (b * e - d) / denom;
        return true;
    }

    private bool TryGetRotateVector(Vec3d center, Vec3d normal, int mouseX, int mouseY, out Vec3d vector)
    {
        vector = new Vec3d();
        if (!TryIntersectMousePlane(center, normal, mouseX, mouseY, out Vec3d point)) return false;

        vector = Sub(point, center);
        vector = Sub(vector, Scale(normal, Dot(vector, normal)));
        if (vector.LengthSq() < 0.00001) return false;

        vector.Normalize();
        return true;
    }

    private bool TryIntersectMousePlane(Vec3d planePoint, Vec3d planeNormal, int mouseX, int mouseY, out Vec3d point)
    {
        point = new Vec3d();
        if (!TryGetMouseRay(mouseX, mouseY, out Vec3d rayOrigin, out Vec3d rayDirection)) return false;

        double denom = Dot(planeNormal, rayDirection);
        if (Math.Abs(denom) < 0.00001) return false;

        double distance = Dot(planeNormal, Sub(planePoint, rayOrigin)) / denom;
        if (distance < 0) return false;

        point = Add(rayOrigin, Scale(rayDirection, distance));
        return true;
    }

    private bool TryBuildState(out GizmoState state)
    {
        state = default;
        if (!_debugManager.TryGetActiveTransformGizmo(out ModelTransform transform, out TransformGizmoContext context, out BlockPos? blockPos, out Vec3d? worldCenter, out TransformGizmoAxes? worldAxes, out TransformGizmoAxes? parentAxes)) return false;

        GetCameraBasis(out Vec3d forward, out Vec3d right, out Vec3d up);
        Vec3d center;
        if (worldCenter != null)
        {
            center = worldCenter;
        }
        else if (!TryGetRenderedCenter(transform, context, blockPos, out center))
        {
            center = GetFallbackCenter(transform, context, blockPos, forward, right, up);
        }

        TranslationBasis basis = BuildTranslationBasis(transform, context, blockPos, center, worldAxes);
        if (!TryGetRotationSetup(transform, context, out Matrix3 rotationParentBasis, out FastVec3f attachmentRotation))
        {
            rotationParentBasis = Matrix3.Identity;
            attachmentRotation = new FastVec3f();
        }

        if (parentAxes.HasValue)
        {
            rotationParentBasis = Matrix3.FromAxes(parentAxes.Value.X, parentAxes.Value.Y, parentAxes.Value.Z).Orthonormalized();
        }

        Matrix3 actualRotation = Matrix3.FromEulerDegrees(transform.Rotation.X + attachmentRotation.X, transform.Rotation.Y + attachmentRotation.Y, transform.Rotation.Z + attachmentRotation.Z);
        Matrix3 worldRotation;
        if (worldAxes.HasValue)
        {
            worldRotation = Matrix3.FromAxes(worldAxes.Value.X, worldAxes.Value.Y, worldAxes.Value.Z).Orthonormalized();
            if (!parentAxes.HasValue)
            {
                rotationParentBasis = worldRotation.Mul(actualRotation.Inverted()).Orthonormalized();
            }
        }
        else
        {
            worldRotation = rotationParentBasis.Mul(actualRotation).Orthonormalized();
        }

        Vec3d axisX = new(1, 0, 0);
        Vec3d axisY = new(0, 1, 0);
        Vec3d axisZ = new(0, 0, 1);

        if (_debugManager.GizmoSpace != TransformGizmoSpace.World)
        {
            axisX = worldRotation.TransformDirection(axisX).Normalize();
            axisY = worldRotation.TransformDirection(axisY).Normalize();
            axisZ = worldRotation.TransformDirection(axisZ).Normalize();
        }

        state = new GizmoState
        {
            Center = center,
            AxisX = SafeNormalize(axisX, new Vec3d(1, 0, 0)),
            AxisY = SafeNormalize(axisY, new Vec3d(0, 1, 0)),
            AxisZ = SafeNormalize(axisZ, new Vec3d(0, 0, 1)),
            TranslationBasis = basis,
            RotationParentBasis = rotationParentBasis,
            RotationParentBasisInverse = rotationParentBasis.Inverted(),
            AttachmentRotation = attachmentRotation,
            CameraRight = right,
            CameraUp = up
        };

        return true;
    }

    private TranslationBasis BuildTranslationBasis(ModelTransform transform, TransformGizmoContext context, BlockPos? blockPos, Vec3d center, TransformGizmoAxes? worldAxes)
    {
        if (_debugManager.GizmoSpace == TransformGizmoSpace.World)
        {
            return new TranslationBasis(new Vec3d(1, 0, 0), new Vec3d(0, 1, 0), new Vec3d(0, 0, 1));
        }

        if (worldAxes.HasValue)
        {
            return new TranslationBasis(
                SafeNormalize(worldAxes.Value.X, new Vec3d(1, 0, 0)),
                SafeNormalize(worldAxes.Value.Y, new Vec3d(0, 1, 0)),
                SafeNormalize(worldAxes.Value.Z, new Vec3d(0, 0, 1)));
        }

        if (context == TransformGizmoContext.RigPart)
        {
            return new TranslationBasis(new Vec3d(1, 0, 0), new Vec3d(0, 1, 0), new Vec3d(0, 0, 1));
        }

        Vec3d x = TryGetRenderedCenter(WithTranslationOffset(transform, 1, 0, 0), context, blockPos, out Vec3d centerX) ? Sub(centerX, center) : new Vec3d(1, 0, 0);
        Vec3d y = TryGetRenderedCenter(WithTranslationOffset(transform, 0, 1, 0), context, blockPos, out Vec3d centerY) ? Sub(centerY, center) : new Vec3d(0, 1, 0);
        Vec3d z = TryGetRenderedCenter(WithTranslationOffset(transform, 0, 0, 1), context, blockPos, out Vec3d centerZ) ? Sub(centerZ, center) : new Vec3d(0, 0, 1);
        return new TranslationBasis(x, y, z);
    }

    private static ModelTransform WithTranslationOffset(ModelTransform transform, float x, float y, float z)
    {
        ModelTransform copy = transform.Clone();
        copy.Translation.X += x;
        copy.Translation.Y += y;
        copy.Translation.Z += z;
        return copy;
    }

    private bool TryGetRenderedCenter(ModelTransform transform, TransformGizmoContext context, BlockPos? blockPos, out Vec3d center)
    {
        center = new Vec3d();
        return context switch
        {
            TransformGizmoContext.MainHand => TryGetHeldItemCenter(transform, true, out center),
            TransformGizmoContext.OffHand => TryGetHeldItemCenter(transform, false, out center),
            TransformGizmoContext.Display or TransformGizmoContext.Ground => TryGetBlockTransformCenter(transform, blockPos, context, out center),
            _ => false
        };
    }

    private bool TryGetRotationSetup(ModelTransform transform, TransformGizmoContext context, out Matrix3 parentBasis, out FastVec3f attachmentRotation)
    {
        parentBasis = Matrix3.Identity;
        attachmentRotation = new FastVec3f();
        return context switch
        {
            TransformGizmoContext.MainHand => TryGetHeldItemRotationSetup(transform, true, out parentBasis, out attachmentRotation),
            TransformGizmoContext.OffHand => TryGetHeldItemRotationSetup(transform, false, out parentBasis, out attachmentRotation),
            _ => true
        };
    }

    private bool TryGetHeldItemRotationSetup(ModelTransform transform, bool rightHand, out Matrix3 parentBasis, out FastVec3f attachmentRotation)
    {
        parentBasis = Matrix3.Identity;
        attachmentRotation = new FastVec3f();
        EntityPlayer playerEntity = _api.World.Player.Entity;
        AttachmentPointAndPose? apap = playerEntity.AnimManager?.Animator?.GetAttachmentPointPose(rightHand ? "RightHand" : "LeftHand");
        AttachmentPoint? ap = apap?.AttachPoint;
        if (apap == null || ap == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(apap.AnimModelMatrix);
        matrix.Translate(transform.Origin.X, transform.Origin.Y, transform.Origin.Z)
            .Scale(transform.ScaleXYZ.X, transform.ScaleXYZ.Y, transform.ScaleXYZ.Z)
            .Translate(ap.PosX / 16.0 + transform.Translation.X, ap.PosY / 16.0 + transform.Translation.Y, ap.PosZ / 16.0 + transform.Translation.Z);

        parentBasis = Matrix3.FromMatrixf(matrix);
        attachmentRotation = new FastVec3f((float)ap.RotationX, (float)ap.RotationY, (float)ap.RotationZ);
        return true;
    }

    private bool TryGetHeldItemCenter(ModelTransform transform, bool rightHand, out Vec3d center)
    {
        center = new Vec3d();
        EntityPlayer playerEntity = _api.World.Player.Entity;
        AttachmentPointAndPose? apap = playerEntity.AnimManager?.Animator?.GetAttachmentPointPose(rightHand ? "RightHand" : "LeftHand");
        AttachmentPoint? ap = apap?.AttachPoint;
        if (apap == null || ap == null) return false;

        Matrixf matrix = new();
        BuildPlayerModelMatrix(matrix, playerEntity);
        matrix.Mul(apap.AnimModelMatrix);
        matrix.Translate(transform.Origin.X, transform.Origin.Y, transform.Origin.Z)
            .Scale(transform.ScaleXYZ.X, transform.ScaleXYZ.Y, transform.ScaleXYZ.Z)
            .Translate(ap.PosX / 16.0 + transform.Translation.X, ap.PosY / 16.0 + transform.Translation.Y, ap.PosZ / 16.0 + transform.Translation.Z)
            .Rotate((float)((ap.RotationX + transform.Rotation.X) * GameMath.DEG2RAD), (float)((ap.RotationY + transform.Rotation.Y) * GameMath.DEG2RAD), (float)((ap.RotationZ + transform.Rotation.Z) * GameMath.DEG2RAD))
            .Translate(-transform.Origin.X, -transform.Origin.Y, -transform.Origin.Z);

        Vec4f relative = matrix.TransformVector(new Vec4f(0.5f, 0.5f, 0.5f, 1f));
        Vec3d camera = playerEntity.CameraPos;
        center = new Vec3d(camera.X + relative.X, camera.Y + relative.Y, camera.Z + relative.Z);
        return true;
    }

    private bool TryGetBlockTransformCenter(ModelTransform transform, BlockPos? blockPos, TransformGizmoContext context, out Vec3d center)
    {
        center = new Vec3d();
        BlockPos? pos = blockPos ?? _api.World.Player.CurrentBlockSelection?.Position;
        if (pos == null) return false;

        double yOffset = context == TransformGizmoContext.Ground ? 1.05 : 0.5;
        center = new Vec3d(pos.X + 0.5 + transform.Translation.X, pos.Y + yOffset + transform.Translation.Y, pos.Z + 0.5 + transform.Translation.Z);
        return true;
    }

    private void BuildPlayerModelMatrix(Matrixf matrix, EntityPlayer playerEntity)
    {
        matrix.Identity();
        Vec3d camera = playerEntity.CameraPos;
        matrix.Translate(playerEntity.Pos.X - camera.X, playerEntity.Pos.InternalY - camera.Y, playerEntity.Pos.Z - camera.Z);

        float rotX = playerEntity.Properties.Client.Shape?.rotateX ?? 0;
        float rotY = playerEntity.Properties.Client.Shape?.rotateY ?? 0;
        float rotZ = playerEntity.Properties.Client.Shape?.rotateZ ?? 0;

        matrix.Translate(0, playerEntity.SelectionBox.Y2 / 2f, 0);
        matrix.RotateX(playerEntity.Pos.Roll + rotX * GameMath.DEG2RAD);
        matrix.RotateY(playerEntity.BodyYaw + (90f + rotY) * GameMath.DEG2RAD);
        matrix.RotateZ(playerEntity.WalkPitch + rotZ * GameMath.DEG2RAD);
        matrix.Translate(0, -playerEntity.SelectionBox.Y2 / 2f, 0);

        float size = playerEntity.Properties.Client.Size;
        matrix.Scale(size, size, size);
        matrix.Translate(-0.5f, 0, -0.5f);
    }

    private Vec3d GetFallbackCenter(ModelTransform transform, TransformGizmoContext context, BlockPos? blockPos, Vec3d forward, Vec3d right, Vec3d up)
    {
        Vec3d camera = _api.World.Player.Entity.CameraPos.Clone();
        Vec3d delta = new(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);

        if ((context == TransformGizmoContext.Display || context == TransformGizmoContext.Ground) && blockPos != null)
        {
            return Add(new Vec3d(blockPos.X + 0.5, blockPos.Y + 1.05, blockPos.Z + 0.5), delta);
        }

        return context switch
        {
            TransformGizmoContext.MainHand => Add(camera, Add(Add(Add(Scale(forward, 1.45), Scale(right, 0.45)), Scale(up, -0.35)), delta)),
            TransformGizmoContext.OffHand => Add(camera, Add(Add(Add(Scale(forward, 1.45), Scale(right, -0.45)), Scale(up, -0.35)), delta)),
            _ => Add(camera, Add(Add(Scale(forward, 2), Scale(up, 0.1)), delta))
        };
    }

    private void GetCameraBasis(out Vec3d forward, out Vec3d right, out Vec3d up)
    {
        double yaw = _api.World.Player.CameraYaw;
        double pitch = _api.World.Player.CameraPitch;
        forward = new Vec3d(-Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch)).Normalize();
        right = new Vec3d(Math.Cos(yaw), 0, -Math.Sin(yaw)).Normalize();
        up = right.Cross(forward).Normalize();
        if (up.LengthSq() < 0.0001) up = new Vec3d(0, 1, 0);
    }

    private static Vec3d GetWorldAxis(GizmoState state, TransformGizmoAxis axis) => axis switch
    {
        TransformGizmoAxis.X => state.AxisX,
        TransformGizmoAxis.Y => state.AxisY,
        TransformGizmoAxis.Z => state.AxisZ,
        _ => state.AxisX
    };

    private static Vec3d GetCanonicalAxis(TransformGizmoAxis axis) => axis switch
    {
        TransformGizmoAxis.X => new Vec3d(1, 0, 0),
        TransformGizmoAxis.Y => new Vec3d(0, 1, 0),
        TransformGizmoAxis.Z => new Vec3d(0, 0, 1),
        _ => new Vec3d(1, 0, 0)
    };

    private static Vec3d SafeNormalize(Vec3d value, Vec3d fallback) => value.LengthSq() < 0.000001 ? fallback : value.Normalize();

    private static Vec3d Perpendicular(Vec3d direction, Vec3d seed)
    {
        Vec3d perp = direction.Cross(seed);
        if (perp.LengthSq() < 0.0001) perp = direction.Cross(new Vec3d(1, 0, 0));
        if (perp.LengthSq() < 0.0001) perp = direction.Cross(new Vec3d(0, 0, 1));
        return perp.Normalize();
    }

    private static double DistancePointToSegment(double x, double y, Vec2d a, Vec2d b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double wx = x - a.X;
        double wy = y - a.Y;
        double lenSq = vx * vx + vy * vy;
        if (lenSq <= 0.0001) return Math.Sqrt(wx * wx + wy * wy);

        double t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0, 1);
        double px = a.X + t * vx;
        double py = a.Y + t * vy;
        double dx = x - px;
        double dy = y - py;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Dot(Vec3d left, Vec3d right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    private static Vec3d Add(Vec3d left, Vec3d right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    private static Vec3d Sub(Vec3d left, Vec3d right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    private static Vec3d Scale(Vec3d value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static bool IsShiftDown() => ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift) || ImGui.GetIO().KeyShift;

    public void Dispose()
    {
        _api.Event.MouseDown -= OnMouseDown;
        _api.Event.MouseMove -= OnMouseMove;
        _api.Event.MouseUp -= OnMouseUp;
        _api.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }

    private struct GizmoState
    {
        public Vec3d Center;
        public Vec3d AxisX;
        public Vec3d AxisY;
        public Vec3d AxisZ;
        public TranslationBasis TranslationBasis;
        public Matrix3 RotationParentBasis;
        public Matrix3 RotationParentBasisInverse;
        public FastVec3f AttachmentRotation;
        public Vec3d CameraRight;
        public Vec3d CameraUp;
    }

    private readonly struct Matrix3
    {
        public static Matrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        private readonly double m00, m01, m02, m10, m11, m12, m20, m21, m22;

        public Matrix3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
        {
            this.m00 = m00;
            this.m01 = m01;
            this.m02 = m02;
            this.m10 = m10;
            this.m11 = m11;
            this.m12 = m12;
            this.m20 = m20;
            this.m21 = m21;
            this.m22 = m22;
        }

        public static Matrix3 FromMatrixf(Matrixf matrix)
        {
            float[] v = matrix.Values;
            return new Matrix3(v[0], v[4], v[8], v[1], v[5], v[9], v[2], v[6], v[10]);
        }

        public static Matrix3 FromAxes(Vec3d x, Vec3d y, Vec3d z)
        {
            x = SafeNormalize(x, new Vec3d(1, 0, 0));
            y = SafeNormalize(y, new Vec3d(0, 1, 0));
            z = SafeNormalize(z, new Vec3d(0, 0, 1));
            return new Matrix3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
        }

        public static Matrix3 FromEulerDegrees(double xDegrees, double yDegrees, double zDegrees)
        {
            double x = xDegrees * GameMath.DEG2RAD;
            double y = yDegrees * GameMath.DEG2RAD;
            double z = zDegrees * GameMath.DEG2RAD;
            double sx = Math.Sin(x), cx = Math.Cos(x), sy = Math.Sin(y), cy = Math.Cos(y), sz = Math.Sin(z), cz = Math.Cos(z);

            return new Matrix3(
                cy * cz, -cy * sz, sy,
                cx * sz + sx * sy * cz, cx * cz - sx * sy * sz, -sx * cy,
                -cx * sy * cz + sx * sz, cx * sy * sz + sx * cz, cx * cy
            );
        }

        public static Matrix3 FromAxisAngle(Vec3d axis, double radians)
        {
            axis = SafeNormalize(new Vec3d(axis.X, axis.Y, axis.Z), new Vec3d(1, 0, 0));
            double x = axis.X, y = axis.Y, z = axis.Z, s = Math.Sin(radians), c = Math.Cos(radians), t = 1 - c;

            return new Matrix3(
                t * x * x + c, t * x * y - s * z, t * x * z + s * y,
                t * x * y + s * z, t * y * y + c, t * y * z - s * x,
                t * x * z - s * y, t * y * z + s * x, t * z * z + c
            );
        }

        public Matrix3 Mul(Matrix3 right)
        {
            return new Matrix3(
                m00 * right.m00 + m01 * right.m10 + m02 * right.m20, m00 * right.m01 + m01 * right.m11 + m02 * right.m21, m00 * right.m02 + m01 * right.m12 + m02 * right.m22,
                m10 * right.m00 + m11 * right.m10 + m12 * right.m20, m10 * right.m01 + m11 * right.m11 + m12 * right.m21, m10 * right.m02 + m11 * right.m12 + m12 * right.m22,
                m20 * right.m00 + m21 * right.m10 + m22 * right.m20, m20 * right.m01 + m21 * right.m11 + m22 * right.m21, m20 * right.m02 + m21 * right.m12 + m22 * right.m22
            );
        }

        public Matrix3 Inverted()
        {
            double determinant = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
            if (Math.Abs(determinant) < 0.000001) return Identity;

            double inv = 1 / determinant;
            return new Matrix3(
                (m11 * m22 - m12 * m21) * inv, (m02 * m21 - m01 * m22) * inv, (m01 * m12 - m02 * m11) * inv,
                (m12 * m20 - m10 * m22) * inv, (m00 * m22 - m02 * m20) * inv, (m02 * m10 - m00 * m12) * inv,
                (m10 * m21 - m11 * m20) * inv, (m01 * m20 - m00 * m21) * inv, (m00 * m11 - m01 * m10) * inv
            );
        }

        public Matrix3 Orthonormalized()
        {
            Vec3d x = SafeNormalize(new Vec3d(m00, m10, m20), new Vec3d(1, 0, 0));
            Vec3d y = Sub(new Vec3d(m01, m11, m21), Scale(x, Dot(new Vec3d(m01, m11, m21), x)));
            y = SafeNormalize(y, Perpendicular(x, new Vec3d(0, 1, 0)));
            Vec3d z = SafeNormalize(x.Cross(y), new Vec3d(0, 0, 1));
            y = SafeNormalize(z.Cross(x), new Vec3d(0, 1, 0));
            return new Matrix3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
        }

        public Vec3d TransformDirection(Vec3d direction) => new(
            m00 * direction.X + m01 * direction.Y + m02 * direction.Z,
            m10 * direction.X + m11 * direction.Y + m12 * direction.Z,
            m20 * direction.X + m21 * direction.Y + m22 * direction.Z
        );

        public Vec3d ToEulerDegrees()
        {
            double y = Math.Asin(Math.Clamp(m02, -1, 1));
            double cy = Math.Cos(y);
            double x;
            double z;

            if (Math.Abs(cy) > 0.00001)
            {
                x = Math.Atan2(-m12, m22);
                z = Math.Atan2(-m01, m00);
            }
            else
            {
                z = 0;
                x = m02 > 0 ? Math.Atan2(m10, m11) : Math.Atan2(-m10, m11);
            }

            return new Vec3d(x * GameMath.RAD2DEG, y * GameMath.RAD2DEG, z * GameMath.RAD2DEG);
        }
    }

    private readonly struct TranslationBasis
    {
        private readonly Vec3d x;
        private readonly Vec3d y;
        private readonly Vec3d z;
        private readonly double determinant;

        public TranslationBasis(Vec3d x, Vec3d y, Vec3d z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            determinant = x.X * (y.Y * z.Z - y.Z * z.Y) - y.X * (x.Y * z.Z - x.Z * z.Y) + z.X * (x.Y * y.Z - x.Z * y.Y);
        }

        public Vec3d WorldToTransformDelta(Vec3d worldDelta)
        {
            if (Math.Abs(determinant) < 0.000001) return FallbackWorldToTransformDelta(worldDelta);

            double tx = worldDelta.X * (y.Y * z.Z - y.Z * z.Y) - y.X * (worldDelta.Y * z.Z - worldDelta.Z * z.Y) + z.X * (worldDelta.Y * y.Z - worldDelta.Z * y.Y);
            double ty = x.X * (worldDelta.Y * z.Z - worldDelta.Z * z.Y) - worldDelta.X * (x.Y * z.Z - x.Z * z.Y) + z.X * (x.Y * worldDelta.Z - x.Z * worldDelta.Y);
            double tz = x.X * (y.Y * worldDelta.Z - y.Z * worldDelta.Y) - y.X * (x.Y * worldDelta.Z - x.Z * worldDelta.Y) + worldDelta.X * (x.Y * y.Z - x.Z * y.Y);
            return new Vec3d(tx / determinant, ty / determinant, tz / determinant);
        }

        private Vec3d FallbackWorldToTransformDelta(Vec3d worldDelta)
        {
            return new Vec3d(ProjectOntoBasis(worldDelta, x), ProjectOntoBasis(worldDelta, y), ProjectOntoBasis(worldDelta, z));
        }

        private static double ProjectOntoBasis(Vec3d worldDelta, Vec3d basis)
        {
            double lengthSq = basis.LengthSq();
            if (lengthSq < 0.000001) return 0;
            return Dot(worldDelta, basis) / lengthSq;
        }
    }
}
#endif
