using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Utils;

internal sealed class DevToolsPreview3DRenderer : IDisposable
{
    private const int ParticleVertexFloatCount = 10;
    private const int ParticleTextureSize = 64;
    private readonly ICoreClientAPI _api;
    private FrameBufferRef? _frameBuffer;
    private int _particleProgram;
    private int _particleVao;
    private int _particleVbo;
    private int _particleTexture;

    public DevToolsPreview3DRenderer(ICoreClientAPI api)
    {
        _api = api;
    }

    public int RenderToTexture(float width, float height, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewMeshInstance> instances, out string? skipReason)
    {
        return RenderToTexture(width, height, camera, instances, [], out skipReason);
    }

    public int RenderToTexture(
        float width,
        float height,
        DevToolsPreviewCamera camera,
        IReadOnlyList<DevToolsPreviewMeshInstance> instances,
        IReadOnlyList<DevToolsPreviewParticleInstance> particles,
        out string? skipReason)
    {
        skipReason = null;
        if (width <= 32 || height <= 32)
        {
            skipReason = "viewport too small";
            return 0;
        }

        if (instances.Count == 0 && particles.Count == 0)
        {
            skipReason = "nothing to render";
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

            if (instances.Count > 0)
            {
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
            }

            if (particles.Count > 0)
            {
                previousShader?.Stop();
                RenderParticles(camera, particles);
            }

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
        DestroyParticleResources();
    }

    private void RenderParticles(DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewParticleInstance> particles)
    {
        EnsureParticleResources();

        int[] restoreViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, restoreViewport);
        GL.GetInteger(GetPName.CurrentProgram, out int restoreProgram);
        GL.GetInteger(GetPName.VertexArrayBinding, out int restoreVertexArray);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int restoreArrayBuffer);
        GL.GetInteger(GetPName.ActiveTexture, out int restoreActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int restoreTexture2D);
        GL.GetInteger(GetPName.BlendEquationRgb, out int restoreBlendEquationRgb);
        GL.GetInteger(GetPName.BlendEquationAlpha, out int restoreBlendEquationAlpha);
        GL.GetInteger(GetPName.BlendSrcRgb, out int restoreBlendSrcRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int restoreBlendSrcAlpha);
        GL.GetInteger(GetPName.BlendDstRgb, out int restoreBlendDstRgb);
        GL.GetInteger(GetPName.BlendDstAlpha, out int restoreBlendDstAlpha);
        GL.GetBoolean(GetPName.DepthWritemask, out bool restoreDepthMask);

        try
        {
            GL.UseProgram(_particleProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_particleProgram, "projectionMatrix"), 1, false, camera.Projection.Values);
            GL.UniformMatrix4(GL.GetUniformLocation(_particleProgram, "viewMatrix"), 1, false, camera.View.Values);
            GL.Uniform1(GL.GetUniformLocation(_particleProgram, "particleTex"), 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _particleTexture);
            GL.BindVertexArray(_particleVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _particleVbo);
            GL.DepthMask(false);

            DrawParticlePass(camera, particles, glowPass: false);
            DrawParticlePass(camera, particles, glowPass: true);
        }
        finally
        {
            GL.DepthMask(restoreDepthMask);
            GL.BindBuffer(BufferTarget.ArrayBuffer, restoreArrayBuffer);
            GL.BindVertexArray(restoreVertexArray);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, restoreTexture2D);
            GL.ActiveTexture((TextureUnit)restoreActiveTexture);
            GL.BlendEquationSeparate((BlendEquationMode)restoreBlendEquationRgb, (BlendEquationMode)restoreBlendEquationAlpha);
            GL.BlendFuncSeparate((BlendingFactorSrc)restoreBlendSrcRgb, (BlendingFactorDest)restoreBlendDstRgb, (BlendingFactorSrc)restoreBlendSrcAlpha, (BlendingFactorDest)restoreBlendDstAlpha);
            GL.UseProgram(restoreProgram);
            GL.Viewport(restoreViewport[0], restoreViewport[1], restoreViewport[2], restoreViewport[3]);
        }
    }

    private void DrawParticlePass(DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewParticleInstance> particles, bool glowPass)
    {
        float[] vertices = BuildParticleVertices(camera, particles, glowPass);
        if (vertices.Length == 0) return;

        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StreamDraw);
        if (glowPass)
        {
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        }
        else
        {
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }

        GL.DrawArrays(PrimitiveType.Triangles, 0, vertices.Length / ParticleVertexFloatCount);
    }

    private static float[] BuildParticleVertices(DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewParticleInstance> particles, bool glowPass)
    {
        List<DevToolsPreviewParticleInstance> ordered = particles
            .Where(particle => glowPass ? particle.GlowLevel > 0 : particle.Color.W > 0.001f)
            .OrderByDescending(particle => Vector3.Dot(particle.Position - camera.Position, camera.Forward))
            .ToList();

        if (ordered.Count == 0) return [];

        List<float> vertices = new(ordered.Count * 6 * ParticleVertexFloatCount);
        foreach (DevToolsPreviewParticleInstance particle in ordered)
        {
            float size = Math.Clamp(particle.Size, 0.001f, 8f);
            Vector4 color = particle.Color;
            float softness = particle.IsCube ? 0.08f : 1f;

            if (glowPass)
            {
                float glow = Math.Clamp(particle.GlowLevel / 255f, 0f, 1f);
                size *= 1.25f + glow * 0.75f;
                color = new Vector4(
                    Math.Clamp(color.X + glow * 0.35f, 0f, 1f),
                    Math.Clamp(color.Y + glow * 0.18f, 0f, 1f),
                    Math.Clamp(color.Z * 0.75f, 0f, 1f),
                    color.W * Math.Clamp(0.18f + glow * 0.28f, 0.08f, 0.46f));
                softness = 1f;
            }

            if (color.W <= 0.001f) continue;

            float half = size * 0.5f;
            Vector3 right = camera.Right * half;
            Vector3 up = camera.Up * half;
            Vector3 topLeft = particle.Position - right + up;
            Vector3 topRight = particle.Position + right + up;
            Vector3 bottomRight = particle.Position + right - up;
            Vector3 bottomLeft = particle.Position - right - up;

            AppendParticleVertex(vertices, topLeft, 0f, 1f, color, softness);
            AppendParticleVertex(vertices, topRight, 1f, 1f, color, softness);
            AppendParticleVertex(vertices, bottomRight, 1f, 0f, color, softness);
            AppendParticleVertex(vertices, topLeft, 0f, 1f, color, softness);
            AppendParticleVertex(vertices, bottomRight, 1f, 0f, color, softness);
            AppendParticleVertex(vertices, bottomLeft, 0f, 0f, color, softness);
        }

        return vertices.ToArray();
    }

    private static void AppendParticleVertex(List<float> vertices, Vector3 position, float u, float v, Vector4 color, float softness)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(u);
        vertices.Add(v);
        vertices.Add(color.X);
        vertices.Add(color.Y);
        vertices.Add(color.Z);
        vertices.Add(color.W);
        vertices.Add(softness);
    }

    private void EnsureParticleResources()
    {
        if (_particleProgram != 0) return;

        _particleProgram = CreateParticleProgram();
        _particleVao = GL.GenVertexArray();
        _particleVbo = GL.GenBuffer();
        _particleTexture = CreateParticleTexture();

        GL.BindVertexArray(_particleVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _particleVbo);
        int stride = ParticleVertexFloatCount * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);
    }

    private static int CreateParticleProgram()
    {
        const string vertexShader = """
            #version 330 core
            layout(location = 0) in vec3 vertexPosition;
            layout(location = 1) in vec2 uvIn;
            layout(location = 2) in vec4 colorIn;
            layout(location = 3) in float softnessIn;

            uniform mat4 projectionMatrix;
            uniform mat4 viewMatrix;

            out vec2 uv;
            out vec4 color;
            out float softness;

            void main(void)
            {
                uv = uvIn;
                color = colorIn;
                softness = softnessIn;
                gl_Position = projectionMatrix * viewMatrix * vec4(vertexPosition, 1.0);
            }
            """;

        const string fragmentShader = """
            #version 330 core
            uniform sampler2D particleTex;

            in vec2 uv;
            in vec4 color;
            in float softness;

            out vec4 outColor;

            void main(void)
            {
                float textureAlpha = texture(particleTex, uv).a;
                float alpha = color.a * mix(1.0, textureAlpha, clamp(softness, 0.0, 1.0));
                if (alpha <= 0.002) discard;
                outColor = vec4(color.rgb, alpha);
            }
            """;

        int vertex = CompileParticleShader(ShaderType.VertexShader, vertexShader);
        int fragment = CompileParticleShader(ShaderType.FragmentShader, fragmentShader);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        if (status != 1)
        {
            string info = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            throw new InvalidOperationException($"Particle preview shader link failed: {info}");
        }

        return program;
    }

    private static int CompileParticleShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 1) return shader;

        string info = GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        throw new InvalidOperationException($"Particle preview {type} shader compile failed: {info}");
    }

    private static int CreateParticleTexture()
    {
        byte[] data = new byte[ParticleTextureSize * ParticleTextureSize * 4];
        for (int y = 0; y < ParticleTextureSize; y++)
        {
            for (int x = 0; x < ParticleTextureSize; x++)
            {
                float nx = (x + 0.5f) / ParticleTextureSize * 2f - 1f;
                float ny = (y + 0.5f) / ParticleTextureSize * 2f - 1f;
                float distance = MathF.Sqrt(nx * nx + ny * ny);
                float alpha = Math.Clamp(1f - distance, 0f, 1f);
                alpha = alpha * alpha * (3f - 2f * alpha);
                int offset = (y * ParticleTextureSize + x) * 4;
                data[offset + 0] = 255;
                data[offset + 1] = 255;
                data[offset + 2] = 255;
                data[offset + 3] = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
            }
        }

        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, ParticleTextureSize, ParticleTextureSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, data);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private void DestroyParticleResources()
    {
        if (_particleVbo != 0)
        {
            GL.DeleteBuffer(_particleVbo);
            _particleVbo = 0;
        }

        if (_particleVao != 0)
        {
            GL.DeleteVertexArray(_particleVao);
            _particleVao = 0;
        }

        if (_particleTexture != 0)
        {
            GL.DeleteTexture(_particleTexture);
            _particleTexture = 0;
        }

        if (_particleProgram != 0)
        {
            GL.DeleteProgram(_particleProgram);
            _particleProgram = 0;
        }
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

internal readonly record struct DevToolsPreviewParticleInstance(Vector3 Position, float Size, Vector4 Color, bool IsCube, int GlowLevel);

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
