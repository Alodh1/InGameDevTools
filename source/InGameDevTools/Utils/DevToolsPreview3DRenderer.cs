#if DEBUG
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Utils;

internal sealed class DevToolsPreview3DRenderer : IDisposable
{
    private readonly ICoreClientAPI _api;
    private FrameBufferRef? _frameBuffer;

    public DevToolsPreview3DRenderer(ICoreClientAPI api)
    {
        _api = api;
    }

    public int RenderToTexture(float width, float height, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewMeshInstance> instances, out string? skipReason)
    {
        skipReason = null;
        if (width <= 32 || height <= 32)
        {
            skipReason = "viewport too small";
            return 0;
        }

        if (instances.Count == 0)
        {
            skipReason = "no preview mesh";
            return 0;
        }

        int framebufferWidth = Math.Max(1, (int)Math.Ceiling(width));
        int framebufferHeight = Math.Max(1, (int)Math.Ceiling(height));
        FrameBufferRef frameBuffer = EnsureFrameBuffer(framebufferWidth, framebufferHeight);
        IRenderAPI render = _api.Render;
        FrameBufferRef? restoreFrameBuffer = render.CurrentFrameBuffer;
        IShaderProgram? previousShader = render.CurrentActiveShader;
        int[] restoreViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, restoreViewport);
        bool restoreDepthTest = GL.IsEnabled(EnableCap.DepthTest);
        GL.GetInteger(GetPName.DepthFunc, out int restoreDepthFunc);
        GL.GetBoolean(GetPName.DepthWritemask, out bool restoreDepthMask);
        bool restoreCullFace = GL.IsEnabled(EnableCap.CullFace);
        bool restoreBlend = GL.IsEnabled(EnableCap.Blend);
        float[] restoreClearColor = new float[4];
        GL.GetFloat(GetPName.ColorClearValue, restoreClearColor);
        IShaderProgram? activeShader = null;

        try
        {
            render.CurrentFrameBuffer = frameBuffer;
            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                skipReason = $"framebuffer incomplete: {status}";
                return 0;
            }

            render.GlViewport(0, 0, framebufferWidth, framebufferHeight);
            render.GLEnableDepthTest();
            GL.DepthFunc(DepthFunction.Lequal);
            render.GLDepthMask(true);
            render.GlDisableCullFace();
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            GL.ClearColor(0.035f, 0.036f, 0.032f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            previousShader?.Stop();
            activeShader = render.GetEngineShader(EnumShaderProgram.Standard);
            activeShader.Use();
            ApplyStandardShaderUniforms(activeShader, camera);

            foreach (DevToolsPreviewMeshInstance instance in instances)
            {
                if (instance.Mesh.MeshRef.Disposed || !instance.Mesh.MeshRef.Initialized) continue;
                SetUniformMatrix(activeShader, "modelMatrix", instance.ModelMatrix.Values);
                render.RenderMultiTextureMesh(instance.Mesh.MeshRef, "tex", 0);
            }

            activeShader.Stop();
            activeShader = null;
            previousShader?.Use();
            return frameBuffer.ColorTextureIds[0];
        }
        catch (Exception exception)
        {
            skipReason = exception.Message;
            return 0;
        }
        finally
        {
            activeShader?.Stop();
            render.CurrentFrameBuffer = restoreFrameBuffer;
            render.GlViewport(restoreViewport[0], restoreViewport[1], restoreViewport[2], restoreViewport[3]);
            previousShader?.Use();
            GL.ClearColor(restoreClearColor[0], restoreClearColor[1], restoreClearColor[2], restoreClearColor[3]);
            GL.DepthFunc((DepthFunction)restoreDepthFunc);
            render.GLDepthMask(restoreDepthMask);
            if (restoreCullFace) render.GlEnableCullFace();
            else render.GlDisableCullFace();
            if (restoreBlend) render.GlToggleBlend(true, EnumBlendMode.Standard);
            else render.GlToggleBlend(false);
            if (restoreDepthTest) render.GLEnableDepthTest();
            else GL.Disable(EnableCap.DepthTest);
        }
    }

    private void ApplyStandardShaderUniforms(IShaderProgram shader, DevToolsPreviewCamera camera)
    {
        SetUniform(shader, "rgbaTint", 1f, 1f, 1f, 1f);
        SetUniform(shader, "rgbaAmbientIn", 1f, 1f, 1f);
        SetUniform(shader, "rgbaLightIn", 1f, 1f, 1f, 1f);
        SetUniform(shader, "rgbaGlowIn", 0f, 0f, 0f, 0f);
        SetUniform(shader, "rgbaFogIn", 0f, 0f, 0f, 0f);
        SetUniform(shader, "extraGlow", 0);
        SetUniform(shader, "fogMinIn", 0f);
        SetUniform(shader, "fogDensityIn", 0f);
        SetUniform(shader, "dontWarpVertices", 1);
        SetUniform(shader, "fadeFromSpheresFog", 0);
        SetUniform(shader, "addRenderFlags", 0);
        SetUniform(shader, "extraZOffset", 0f);
        SetUniform(shader, "viewDistance", 1024f);
        SetUniform(shader, "overlayOpacity", 0f);
        SetUniform(shader, "normalShaded", 1);
        SetUniform(shader, "skyShaded", 0);
        SetUniform(shader, "tempGlowMode", 0);
        SetUniform(shader, "alphaTest", 0.01f);
        SetUniform(shader, "damageEffect", 0f);
        SetUniform(shader, "applySsao", 0);
        SetUniformMatrix(shader, "projectionMatrix", camera.Projection.Values);
        SetUniformMatrix(shader, "viewMatrix", camera.View.Values);
        SetUniform(shader, "lightPosition", -0.35f, 0.85f, -0.38f);
    }

    private FrameBufferRef EnsureFrameBuffer(int width, int height)
    {
        if (_frameBuffer != null && !_frameBuffer.Disposed && _frameBuffer.Width == width && _frameBuffer.Height == height)
        {
            return _frameBuffer;
        }

        DestroyFrameBuffer();
        FramebufferAttrs attrs = new("ingamedevtools-preview-3d", width, height)
        {
            Attachments =
            [
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                    Texture = new RawTexture
                    {
                        Width = width,
                        Height = height,
                        PixelFormat = EnumTexturePixelFormat.Rgba,
                        PixelInternalFormat = EnumTextureInternalFormat.Rgba8
                    }
                },
                new FramebufferAttrsAttachment
                {
                    AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                    Texture = new RawTexture
                    {
                        Width = width,
                        Height = height,
                        PixelFormat = EnumTexturePixelFormat.DepthComponent,
                        PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32
                    }
                }
            ]
        };
        _frameBuffer = _api.Render.CreateFrameBuffer(attrs);
        return _frameBuffer;
    }

    private void DestroyFrameBuffer()
    {
        if (_frameBuffer == null || _frameBuffer.Disposed) return;
        _api.Render.DestroyFrameBuffer(_frameBuffer);
        _frameBuffer = null;
    }

    public void Dispose()
    {
        DestroyFrameBuffer();
    }

    private static void SetUniform(IShaderProgram shader, string name, int value)
    {
        if (shader.HasUniform(name)) shader.Uniform(name, value);
    }

    private static void SetUniform(IShaderProgram shader, string name, float value)
    {
        if (shader.HasUniform(name)) shader.Uniform(name, value);
    }

    private static void SetUniform(IShaderProgram shader, string name, float x, float y, float z)
    {
        if (shader.HasUniform(name)) shader.Uniform(name, x, y, z);
    }

    private static void SetUniform(IShaderProgram shader, string name, float x, float y, float z, float w)
    {
        if (shader.HasUniform(name)) shader.Uniform(name, x, y, z, w);
    }

    private static void SetUniformMatrix(IShaderProgram shader, string name, float[] matrix)
    {
        if (shader.HasUniform(name)) shader.UniformMatrix(name, matrix);
    }
}

internal sealed class DevToolsPreviewMesh : IDisposable
{
    public DevToolsPreviewMesh(string label, MultiTextureMeshRef meshRef, DevToolsPreviewBounds bounds)
    {
        Label = label;
        MeshRef = meshRef;
        Bounds = bounds;
    }

    public string Label { get; }
    public MultiTextureMeshRef MeshRef { get; }
    public DevToolsPreviewBounds Bounds { get; }

    public void Dispose()
    {
        if (!MeshRef.Disposed) MeshRef.Dispose();
    }
}

internal readonly record struct DevToolsPreviewMeshInstance(DevToolsPreviewMesh Mesh, Matrixf ModelMatrix);

internal static class DevToolsPreviewMeshFactory
{
    public static DevToolsPreviewMesh? FromMesh(ICoreClientAPI api, string label, MeshData mesh)
    {
        if (mesh.VerticesCount <= 0 || mesh.xyz == null) return null;
        EnsureVertexColor(mesh);
        DevToolsPreviewBounds bounds = CalculateBounds(mesh);
        if (!bounds.IsValid) return null;
        return new DevToolsPreviewMesh(label, api.Render.UploadMultiTextureMesh(mesh), bounds);
    }

    public static DevToolsPreviewBounds CalculateBounds(MeshData mesh)
    {
        DevToolsPreviewBounds bounds = DevToolsPreviewBounds.Empty;
        if (mesh.xyz == null) return bounds;
        for (int vertex = 0; vertex < mesh.VerticesCount; vertex++)
        {
            int offset = vertex * 3;
            bounds = bounds.Include(new Vector3(mesh.xyz[offset], mesh.xyz[offset + 1], mesh.xyz[offset + 2]));
        }
        return bounds;
    }

    private static void EnsureVertexColor(MeshData mesh)
    {
        int requiredLength = mesh.VerticesCount * 4;
        if (requiredLength <= 0) return;

        if (mesh.Rgba == null || mesh.Rgba.Length < requiredLength)
        {
            mesh.Rgba = new byte[requiredLength];
            FillVertexColor(mesh.Rgba);
            return;
        }

        bool hasVisibleColor = false;
        for (int index = 0; index + 3 < requiredLength; index += 4)
        {
            if (mesh.Rgba[index + 3] == 0) continue;
            if ((mesh.Rgba[index] | mesh.Rgba[index + 1] | mesh.Rgba[index + 2]) == 0) continue;
            hasVisibleColor = true;
            break;
        }

        if (!hasVisibleColor)
        {
            FillVertexColor(mesh.Rgba);
            return;
        }

        for (int index = 3; index < requiredLength; index += 4)
        {
            if (mesh.Rgba[index] == 0) mesh.Rgba[index] = 255;
        }
    }

    private static void FillVertexColor(byte[] rgba)
    {
        for (int index = 0; index + 3 < rgba.Length; index += 4)
        {
            rgba[index] = 255;
            rgba[index + 1] = 255;
            rgba[index + 2] = 255;
            rgba[index + 3] = 255;
        }
    }
}

internal static class DevToolsPreviewPlacement
{
    public static Vector3 TopCenter(DevToolsPreviewBounds bounds)
    {
        return bounds.IsValid ? new Vector3(bounds.Center.X, bounds.Max.Y, bounds.Center.Z) : Vector3.Zero;
    }

    public static Vector3 BottomCenter(DevToolsPreviewBounds bounds)
    {
        return bounds.IsValid ? new Vector3(bounds.Center.X, bounds.Min.Y, bounds.Center.Z) : Vector3.Zero;
    }
}

internal readonly struct DevToolsPreviewBounds
{
    public DevToolsPreviewBounds(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public Vector3 Center => (Min + Max) * 0.5f;
    public float Radius => Math.Max(0.5f, (Max - Min).Length * 0.5f);

    public DevToolsPreviewBounds Include(DevToolsPreviewBounds other)
    {
        return new(Vector3.ComponentMin(Min, other.Min), Vector3.ComponentMax(Max, other.Max));
    }

    public DevToolsPreviewBounds Include(Vector3 point)
    {
        return new(Vector3.ComponentMin(Min, point), Vector3.ComponentMax(Max, point));
    }

    public static DevToolsPreviewBounds Empty => new(
        new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
        new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity));

    public bool IsValid => float.IsFinite(Min.X) && float.IsFinite(Max.X) && Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;
}

internal readonly struct DevToolsPreviewCamera
{
    public DevToolsPreviewCamera(Matrixf projection, Matrixf view, Vector3 position, Vector3 forward, Vector3 right, Vector3 up, System.Numerics.Vector2 center, float focalLength)
    {
        Projection = projection;
        View = view;
        Position = position;
        Forward = forward;
        Right = right;
        Up = up;
        Center = center;
        FocalLength = focalLength;
    }

    public Matrixf Projection { get; }
    public Matrixf View { get; }
    public Vector3 Position { get; }
    public Vector3 Forward { get; }
    public Vector3 Right { get; }
    public Vector3 Up { get; }
    public System.Numerics.Vector2 Center { get; }
    public float FocalLength { get; }

    public static DevToolsPreviewCamera Orbit(System.Numerics.Vector2 min, System.Numerics.Vector2 max, Vector3 target, float yaw, float pitch, float distance)
    {
        float width = Math.Max(1f, max.X - min.X);
        float height = Math.Max(1f, max.Y - min.Y);
        float cosPitch = MathF.Cos(pitch);
        Vector3 forwardFromTarget = Vector3.Normalize(new Vector3(
            MathF.Sin(yaw) * cosPitch,
            MathF.Sin(pitch),
            MathF.Cos(yaw) * cosPitch));
        Vector3 position = target - forwardFromTarget * Math.Max(0.05f, distance);
        Vector3 forward = Vector3.Normalize(target - position);
        Vector3 worldUp = Vector3.UnitY;
        Vector3 right = Vector3.Cross(forward, worldUp);
        if (right.LengthSquared < 0.0001f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        float fov = 55f * GameMath.DEG2RAD;
        Matrixf projection = new();
        projection.Set(Mat4f.Perspective(Mat4f.Create(), fov, width / height, 0.01f, Math.Max(64f, distance * 12f + 16f)));
        Matrixf view = new();
        Vector3 look = position + forward;
        view.Set(Mat4f.LookAt(Mat4f.Create(), [position.X, position.Y, position.Z], [look.X, look.Y, look.Z], [up.X, up.Y, up.Z]));
        float focalLength = height / (2f * MathF.Tan(fov / 2f));
        return new(projection, view, position, forward, right, up, (min + max) * 0.5f, focalLength);
    }

    public bool Project(Vector3 point, out System.Numerics.Vector2 screen, out float depth)
    {
        Vector3 relative = point - Position;
        float x = Vector3.Dot(relative, Right);
        float y = Vector3.Dot(relative, Up);
        depth = Vector3.Dot(relative, Forward);
        if (depth <= 0.04f)
        {
            screen = default;
            return false;
        }

        screen = Center + new System.Numerics.Vector2(x / depth * FocalLength, -y / depth * FocalLength);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }
}
#endif
