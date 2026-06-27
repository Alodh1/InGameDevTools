using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace InGameDevTools.Utils;

internal sealed class DevToolsPreview3DRenderer : IDisposable
{
    private const string PreviewQuadParticleShaderName = "ingamedevtools-preview-particlesquad-v6";
    private const string BillboardShaderName = "ingamedevtools-preview-billboard-v1";

    private readonly ICoreClientAPI _api;
    private FrameBufferRef? _frameBuffer;
    private EngineParticlePreview? _particlePreview;
    private IShaderProgram? _previewQuadParticleShader;
    private IShaderProgram? _billboardShader;
    private readonly HashSet<string> _missingParticleUniformsLogged = new(StringComparer.Ordinal);

    public DevToolsPreview3DRenderer(ICoreClientAPI api)
    {
        _api = api;
        EngineParticleSpawnRedirect.EnsurePatched(api);
    }

    private static readonly IReadOnlyList<DevToolsPreviewBillboard> NoBillboards = Array.Empty<DevToolsPreviewBillboard>();

    public int RenderToTexture(float width, float height, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewMeshInstance> instances, out string? skipReason)
    {
        return RenderToTexture(width, height, camera, instances, NoBillboards, out skipReason);
    }

    public int RenderToTexture(float width, float height, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewMeshInstance> instances, float particleDeltaSeconds, out string? skipReason)
    {
        return RenderToTexture(width, height, camera, instances, NoBillboards, out skipReason);
    }

    public int RenderToTexture(
        float width,
        float height,
        DevToolsPreviewCamera camera,
        IReadOnlyList<DevToolsPreviewMeshInstance> instances,
        IReadOnlyList<DevToolsPreviewBillboard> billboards,
        out string? skipReason)
    {
        skipReason = null;
        if (width <= 32 || height <= 32)
        {
            skipReason = "viewport too small";
            return 0;
        }

        if (instances.Count == 0 && billboards.Count == 0 && ParticleCount == 0)
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
        GL.GetInteger(GetPName.BlendSrcRgb, out int restoreBlendSrcRgb);
        GL.GetInteger(GetPName.BlendDstRgb, out int restoreBlendDstRgb);
        GL.GetInteger(GetPName.BlendSrcAlpha, out int restoreBlendSrcAlpha);
        GL.GetInteger(GetPName.BlendDstAlpha, out int restoreBlendDstAlpha);
        GL.GetInteger(GetPName.BlendEquationRgb, out int restoreBlendEquationRgb);
        GL.GetInteger(GetPName.BlendEquationAlpha, out int restoreBlendEquationAlpha);
        GL.GetInteger(GetPName.ActiveTexture, out int restoreActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int restoreTexture2D);
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
            System.Numerics.Vector4 clearColor = DevToolsViewportBackground.FillColor;
            GL.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (instances.Count > 0)
            {
                previousShader?.Stop();
                activeShader = render.GetEngineShader(EnumShaderProgram.Standard);
                activeShader.Use();
                ApplyStandardShaderUniforms(activeShader, camera);

                foreach (DevToolsPreviewMeshInstance instance in instances.Where(instance => instance.Tint.W >= 0.999f))
                {
                    RenderPreviewMeshInstance(render, activeShader, instance);
                }

                bool depthMaskDisabled = false;
                foreach (DevToolsPreviewMeshInstance instance in instances.Where(instance => instance.Tint.W < 0.999f))
                {
                    if (!depthMaskDisabled)
                    {
                        render.GLDepthMask(false);
                        depthMaskDisabled = true;
                    }

                    RenderPreviewMeshInstance(render, activeShader, instance);
                }
                if (depthMaskDisabled)
                {
                    render.GLDepthMask(true);
                }

                activeShader.Stop();
                activeShader = null;
            }

            if (billboards.Count > 0)
            {
                previousShader?.Stop();
                RenderBillboards(render, camera, billboards);
            }

            if (ParticleCount > 0)
            {
                previousShader?.Stop();
                RenderEngineParticles(camera, 0f);
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
            if (restoreBlend)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendEquationSeparate((BlendEquationMode)restoreBlendEquationRgb, (BlendEquationMode)restoreBlendEquationAlpha);
                GL.BlendFuncSeparate(
                    (BlendingFactorSrc)restoreBlendSrcRgb,
                    (BlendingFactorDest)restoreBlendDstRgb,
                    (BlendingFactorSrc)restoreBlendSrcAlpha,
                    (BlendingFactorDest)restoreBlendDstAlpha);
            }
            else
            {
                render.GlToggleBlend(false);
            }

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, restoreTexture2D);
            GL.ActiveTexture((TextureUnit)restoreActiveTexture);
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

    private void RenderPreviewMeshInstance(IRenderAPI render, IShaderProgram shader, DevToolsPreviewMeshInstance instance)
    {
        if (instance.Mesh.MeshRef.Disposed || !instance.Mesh.MeshRef.Initialized) return;

        SetUniform(shader, "rgbaTint", instance.Tint.X, instance.Tint.Y, instance.Tint.Z, instance.Tint.W);
        SetUniformMatrix(shader, "modelMatrix", instance.ModelMatrix.Values);
        render.RenderMultiTextureMesh(instance.Mesh.MeshRef, "tex", 0);
    }

    // Renders the CPU-simulated particles as camera-facing billboards into the framebuffer with a
    // self-contained shader. Cubes are drawn first (opaque, depth-writing); glowing quads second
    // (additive, order-independent so no sorting needed, no depth write so they don't occlude each other).
    private void RenderBillboards(IRenderAPI render, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewBillboard> billboards)
    {
        IShaderProgram shader = EnsureBillboardShader();
        shader.Use();
        try
        {
            SetUniformMatrix(shader, "projectionMatrix", camera.Projection.Values);
            SetUniformMatrix(shader, "modelViewMatrix", camera.View.Values);

            // Opaque cube billboards.
            render.GLDepthMask(true);
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            SetUniform(shader, "softEdge", 0);
            RenderBillboardBatch(render, camera, billboards, wantQuad: false);

            // Additive glowing quad billboards.
            render.GLDepthMask(false);
            GL.Enable(EnableCap.Blend);
            GL.BlendEquationSeparate(BlendEquationMode.FuncAdd, BlendEquationMode.FuncAdd);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.One, BlendingFactorSrc.Zero, BlendingFactorDest.One);
            SetUniform(shader, "softEdge", 1);
            RenderBillboardBatch(render, camera, billboards, wantQuad: true);

            render.GLDepthMask(true);
        }
        finally
        {
            shader.Stop();
        }
    }

    private void RenderBillboardBatch(IRenderAPI render, DevToolsPreviewCamera camera, IReadOnlyList<DevToolsPreviewBillboard> billboards, bool wantQuad)
    {
        int count = 0;
        for (int index = 0; index < billboards.Count; index++)
        {
            if (billboards[index].Quad == wantQuad) count++;
        }
        if (count == 0) return;

        OpenTK.Mathematics.Vector3 right = camera.Right;
        OpenTK.Mathematics.Vector3 up = camera.Up;

        MeshData mesh = new(count * 4, count * 6);
        mesh.SetVerticesCount(count * 4);
        mesh.SetIndicesCount(count * 6);

        int vertex = 0;
        int indexPos = 0;
        for (int billboardIndex = 0; billboardIndex < billboards.Count; billboardIndex++)
        {
            DevToolsPreviewBillboard billboard = billboards[billboardIndex];
            if (billboard.Quad != wantQuad) continue;

            OpenTK.Mathematics.Vector3 center = billboard.Center;
            float hw = billboard.HalfWidth;
            OpenTK.Mathematics.Vector3 rx = right * hw;
            OpenTK.Mathematics.Vector3 uy = up * hw;
            byte r = (byte)(Math.Clamp(billboard.Color.X, 0f, 1f) * 255f);
            byte g = (byte)(Math.Clamp(billboard.Color.Y, 0f, 1f) * 255f);
            byte b = (byte)(Math.Clamp(billboard.Color.Z, 0f, 1f) * 255f);
            byte a = (byte)(Math.Clamp(billboard.Color.W, 0f, 1f) * 255f);

            int baseVertex = vertex;
            AddBillboardVertex(mesh, ref vertex, center - rx - uy, 0f, 0f, r, g, b, a);
            AddBillboardVertex(mesh, ref vertex, center + rx - uy, 1f, 0f, r, g, b, a);
            AddBillboardVertex(mesh, ref vertex, center + rx + uy, 1f, 1f, r, g, b, a);
            AddBillboardVertex(mesh, ref vertex, center - rx + uy, 0f, 1f, r, g, b, a);

            mesh.Indices[indexPos++] = baseVertex + 0;
            mesh.Indices[indexPos++] = baseVertex + 1;
            mesh.Indices[indexPos++] = baseVertex + 2;
            mesh.Indices[indexPos++] = baseVertex + 0;
            mesh.Indices[indexPos++] = baseVertex + 2;
            mesh.Indices[indexPos++] = baseVertex + 3;
        }

        MeshRef meshRef = render.UploadMesh(mesh);
        try
        {
            render.RenderMesh(meshRef);
        }
        finally
        {
            meshRef.Dispose();
        }
    }

    private static void AddBillboardVertex(MeshData mesh, ref int vertex, OpenTK.Mathematics.Vector3 position, float u, float v, byte r, byte g, byte b, byte a)
    {
        int xyz = vertex * 3;
        mesh.xyz[xyz] = position.X;
        mesh.xyz[xyz + 1] = position.Y;
        mesh.xyz[xyz + 2] = position.Z;

        int uvOffset = vertex * 2;
        mesh.Uv[uvOffset] = u;
        mesh.Uv[uvOffset + 1] = v;

        int rgba = vertex * 4;
        mesh.Rgba[rgba] = r;
        mesh.Rgba[rgba + 1] = g;
        mesh.Rgba[rgba + 2] = b;
        mesh.Rgba[rgba + 3] = a;

        vertex++;
    }

    private IShaderProgram EnsureBillboardShader()
    {
        if (_billboardShader is { Disposed: false, LoadError: false })
        {
            return _billboardShader;
        }

        IShaderProgram? existing = TryGetShaderProgram(BillboardShaderName);
        if (existing is { Disposed: false, LoadError: false })
        {
            _billboardShader = existing;
            return existing;
        }

        IShaderProgram shader = _api.Shader.NewShaderProgram();
        IShader vertexShader = _api.Shader.NewShader(EnumShaderType.VertexShader);
        vertexShader.Code = BillboardVertexShader;
        shader.VertexShader = vertexShader;

        IShader fragmentShader = _api.Shader.NewShader(EnumShaderType.FragmentShader);
        fragmentShader.Code = BillboardFragmentShader;
        shader.FragmentShader = fragmentShader;

        _api.Shader.RegisterMemoryShaderProgram(BillboardShaderName, shader);
        if (!shader.Compile())
        {
            throw new InvalidOperationException("Could not compile preview billboard shader.");
        }

        _billboardShader = shader;
        return shader;
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

    public int ParticleCount => _particlePreview?.ParticleCount ?? 0;

    public int ParticleCountFor(EnumParticleModel model)
    {
        return _particlePreview?.ParticleCountFor(model) ?? 0;
    }

    public void ResetParticles()
    {
        _particlePreview?.Dispose();
        _particlePreview = null;
    }

    public int SpawnParticles(IReadOnlyList<AdvancedParticleProperties> particles, Vector3 cameraPosition)
    {
        if (particles.Count == 0) return 0;

        return EnsureParticlePreview().Spawn(particles, cameraPosition);
    }

    public int SpawnParticleProviders(IReadOnlyList<IParticlePropertiesProvider> particles, Vector3 cameraPosition)
    {
        if (particles.Count == 0) return 0;

        return EnsureParticlePreview().Spawn(particles, cameraPosition);
    }

    public void Dispose()
    {
        DestroyFrameBuffer();
        ResetParticles();
    }

    private EngineParticlePreview EnsureParticlePreview()
    {
        if (_particlePreview != null) return _particlePreview;
        if (_api.World is not ClientMain client)
        {
            throw new InvalidOperationException("Engine particle preview requires ClientMain.");
        }

        _particlePreview = new EngineParticlePreview(_api, client);
        return _particlePreview;
    }

    private void RenderEngineParticles(DevToolsPreviewCamera camera, float deltaSeconds)
    {
        if (_particlePreview == null || _particlePreview.ParticleCount == 0) return;

        IRenderAPI render = _api.Render;
        // The engine's 3D quad shader declares particleTex but does not sample it in this
        // Vintage Story build. Clearing texture unit 0 still prevents reference mesh state
        // from leaking into preview particle shaders.
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        render.GLDepthMask(false);
        render.GLEnableDepthTest();
        GL.DepthFunc(DepthFunction.Lequal);

        _particlePreview.Update(camera.Position, Math.Clamp(deltaSeconds, 0f, 0.1f));
        Matrixf particleView = BuildCameraOriginView(camera);

        RenderEngineParticlePool(render.GetEngineShader(EnumShaderProgram.Particlescube), _particlePreview.CubePool, particleView, camera.Projection, PreviewParticleBlendMode.Standard);
        RenderEngineParticlePool(EnsurePreviewQuadParticleShader(), _particlePreview.QuadPool, particleView, camera.Projection, PreviewParticleBlendMode.AdditivePreserveFramebufferAlpha);
    }

    private void RenderEngineParticlePool(IShaderProgram shader, IParticlePool pool, Matrixf modelView, Matrixf projection, PreviewParticleBlendMode blendMode)
    {
        if (pool.QuantityAlive <= 0) return;

        ApplyParticleBlendMode(blendMode);
        shader.Use();
        try
        {
            ApplyParticleShaderUniforms(shader, modelView, projection);
            _api.Render.RenderMeshInstanced(pool.Model, pool.QuantityAlive);
        }
        finally
        {
            shader.Stop();
        }
    }

    private void ApplyParticleBlendMode(PreviewParticleBlendMode blendMode)
    {
        if (blendMode == PreviewParticleBlendMode.Standard)
        {
            _api.Render.GlToggleBlend(true, EnumBlendMode.Standard);
            return;
        }

        GL.Enable(EnableCap.Blend);
        GL.BlendEquationSeparate(BlendEquationMode.FuncAdd, BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(
            BlendingFactorSrc.SrcAlpha,
            BlendingFactorDest.One,
            BlendingFactorSrc.Zero,
            BlendingFactorDest.One);
    }

    private IShaderProgram EnsurePreviewQuadParticleShader()
    {
        if (_previewQuadParticleShader is { Disposed: false, LoadError: false })
        {
            return _previewQuadParticleShader;
        }

        IShaderProgram? existing = TryGetShaderProgram(PreviewQuadParticleShaderName);
        if (existing is { Disposed: false, LoadError: false })
        {
            _previewQuadParticleShader = existing;
            return existing;
        }

        IShaderProgram shader = _api.Shader.NewShaderProgram();
        IShader vertexShader = _api.Shader.NewShader(EnumShaderType.VertexShader);
        vertexShader.Code = PreviewQuadParticleVertexShader;
        shader.VertexShader = vertexShader;

        IShader fragmentShader = _api.Shader.NewShader(EnumShaderType.FragmentShader);
        fragmentShader.Code = PreviewQuadParticleFragmentShader;
        shader.FragmentShader = fragmentShader;

        _api.Shader.RegisterMemoryShaderProgram(PreviewQuadParticleShaderName, shader);
        if (!shader.Compile())
        {
            throw new InvalidOperationException("Could not compile preview quad particle shader.");
        }

        _previewQuadParticleShader = shader;
        return shader;
    }

    private IShaderProgram? TryGetShaderProgram(string name)
    {
        try
        {
            return _api.Shader.GetProgramByName(name);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyParticleShaderUniforms(IShaderProgram shader, Matrixf modelView, Matrixf projection)
    {
        IRenderAPI render = _api.Render;
        if (shader.HasUniform("rgbaFogIn")) shader.Uniform("rgbaFogIn", render.FogColor);
        else LogMissingParticleUniform(shader, "rgbaFogIn");
        if (shader.HasUniform("rgbaAmbientIn")) shader.Uniform("rgbaAmbientIn", render.AmbientColor);
        else LogMissingParticleUniform(shader, "rgbaAmbientIn");
        SetExpectedParticleUniform(shader, "fogMinIn", render.FogMin);
        SetExpectedParticleUniform(shader, "fogDensityIn", render.FogDensity);
        SetExpectedParticleUniformMatrix(shader, "projectionMatrix", projection.Values);
        SetExpectedParticleUniformMatrix(shader, "modelViewMatrix", modelView.Values);
    }

    private void SetExpectedParticleUniform(IShaderProgram shader, string name, float value)
    {
        if (shader.HasUniform(name))
        {
            shader.Uniform(name, value);
            return;
        }

        LogMissingParticleUniform(shader, name);
    }

    private void SetExpectedParticleUniformMatrix(IShaderProgram shader, string name, float[] matrix)
    {
        if (shader.HasUniform(name))
        {
            shader.UniformMatrix(name, matrix);
            return;
        }

        LogMissingParticleUniform(shader, name);
    }

    private void LogMissingParticleUniform(IShaderProgram shader, string name)
    {
        string key = $"{shader.GetHashCode()}:{name}";
        if (!_missingParticleUniformsLogged.Add(key)) return;
        _api.Logger.VerboseDebug("InGameDevTools: preview particle shader {0} is missing expected uniform '{1}'.", shader.GetType().Name, name);
    }

    private static Matrixf BuildCameraOriginView(DevToolsPreviewCamera camera)
    {
        Matrixf view = new();
        view.Set(Mat4f.LookAt(
            Mat4f.Create(),
            [0f, 0f, 0f],
            [camera.Forward.X, camera.Forward.Y, camera.Forward.Z],
            [camera.Up.X, camera.Up.Y, camera.Up.Z]));
        return view;
    }

    private sealed class EngineParticlePreview : IDisposable
    {
        private const float AsyncParticlePhysicsStepSeconds = 0.0625f;
        private const int QuadPoolSize = 4096;
        private const int CubePoolSize = 2048;
        private readonly ICoreClientAPI _api;
        private readonly ClientMain _client;
        private bool _primed;
        private bool _updating;

        public EngineParticlePreview(ICoreClientAPI api, ClientMain client)
        {
            _api = api;
            _client = client;
            QuadPool = new ParticlePoolQuads(QuadPoolSize, client, offthread: false);
            CubePool = new ParticlePoolCubes(CubePoolSize, client, offthread: false);
        }

        // The engine particle pools refuse to do anything while the game is paused: ParticlePool*.OnNewFrame
        // returns immediately (so QuantityAlive is never refreshed and the instance mesh is never uploaded)
        // and SpawnParticles multiplies the spawn quantity by currentGamespeed, which only ever gets set
        // inside OnNewFrame. The dev tools editor is an ImGui overlay that very often runs while the game is
        // paused (single-player pauses on lost focus the moment VSImGui spills onto an OS viewport, and the
        // escape menu pauses too), which left the preview permanently empty. Clearing IsPaused for the
        // duration of our isolated tick/spawn restores the engine behaviour without disturbing game state -
        // it is set back before the call returns, all on the render thread.
        private void RunUnpaused(Action action)
        {
            bool wasPaused = _client.IsPaused;
            if (wasPaused) _client.IsPaused = false;
            try
            {
                action();
            }
            finally
            {
                if (wasPaused) _client.IsPaused = true;
            }
        }

        public IParticlePool QuadPool { get; }
        public IParticlePool CubePool { get; }
        public int ParticleCount => QuadPool.QuantityAlive + CubePool.QuantityAlive;

        public int ParticleCountFor(EnumParticleModel model)
        {
            return model == EnumParticleModel.Quad ? QuadPool.QuantityAlive : CubePool.QuantityAlive;
        }

        public int Spawn(IParticlePropertiesProvider particleProperties, Vector3 cameraPosition)
        {
            return Spawn([particleProperties], cameraPosition);
        }

        public int Spawn(IEnumerable<IParticlePropertiesProvider> particleProperties, Vector3 cameraPosition)
        {
            int spawned = 0;
            RunUnpaused(() =>
            {
                Prime(cameraPosition);
                foreach (IParticlePropertiesProvider particle in particleProperties)
                {
                    if (particle == null) continue;
                    particle.Init(_api);
                    IParticlePool pool = particle.ParticleModel == EnumParticleModel.Quad ? QuadPool : CubePool;
                    spawned += pool.SpawnParticles(particle);
                }

                if (spawned > 0 && !_updating)
                {
                    Upload(cameraPosition);
                }
            });

            return spawned;
        }

        public void Update(Vector3 cameraPosition, float deltaSeconds)
        {
            float clampedDelta = Math.Clamp(deltaSeconds, 0f, 0.1f);
            Tick(cameraPosition, clampedDelta <= 0f ? 0f : Math.Max(AsyncParticlePhysicsStepSeconds, clampedDelta));
            _primed = true;
        }

        private void Upload(Vector3 cameraPosition)
        {
            Tick(cameraPosition, 0f);
        }

        private void Tick(Vector3 cameraPosition, float deltaSeconds)
        {
            Vec3d cameraPos = new(cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
            _updating = true;
            try
            {
                RunUnpaused(() => EngineParticleSpawnRedirect.RunWithPreview(this, () =>
                {
                    CubePool.OnNewFrame(deltaSeconds, cameraPos);
                    QuadPool.OnNewFrame(deltaSeconds, cameraPos);
                }));
            }
            finally
            {
                _updating = false;
            }
        }

        private void Prime(Vector3 cameraPosition)
        {
            if (_primed) return;
            Update(cameraPosition, 1f / 60f);
        }

        public void Dispose()
        {
            QuadPool.Dipose();
            CubePool.Dipose();
        }
    }

    private enum PreviewParticleBlendMode
    {
        Standard,
        AdditivePreserveFramebufferAlpha
    }

    private static class EngineParticleSpawnRedirect
    {
        private const string HarmonyId = "ingamedevtools.preview-particles";
        private static readonly object PatchLock = new();
        private static bool _patched;
        [ThreadStatic] private static EngineParticlePreview? _activePreview;

        public static void EnsurePatched(ICoreClientAPI api)
        {
            lock (PatchLock)
            {
                if (_patched) return;

                Harmony harmony = new(HarmonyId);
                System.Reflection.MethodInfo? method = AccessTools.Method(
                    typeof(ClientMain),
                    nameof(ClientMain.SpawnParticles),
                    [typeof(IParticlePropertiesProvider), typeof(IPlayer)]);
                if (method == null)
                {
                    throw new InvalidOperationException("ClientMain.SpawnParticles(IParticlePropertiesProvider, IPlayer) was not found.");
                }

                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(EngineParticleSpawnRedirect), nameof(SpawnParticlesPrefix)));
                _patched = true;
                api.Logger.VerboseDebug("InGameDevTools: engine particle preview spawn redirect installed.");
            }
        }

        public static void RunWithPreview(EngineParticlePreview preview, Action action)
        {
            EngineParticlePreview? previous = _activePreview;
            _activePreview = preview;
            try
            {
                action();
            }
            finally
            {
                _activePreview = previous;
            }
        }

        private static bool SpawnParticlesPrefix(IParticlePropertiesProvider particlePropertiesProvider)
        {
            EngineParticlePreview? preview = _activePreview;
            if (preview == null) return true;

            preview.Spawn(particlePropertiesProvider, Vector3.Zero);
            return false;
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

    private const string PreviewQuadParticleVertexShader = """
#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout (location = 0) in vec3 vertexPosition;
layout (location = 1) in vec2 uvIn;
layout (location = 2) in vec4 baseColor;

layout (location = 3) in int renderFlags;
layout (location = 4) in vec3 particlePosition;
layout (location = 5) in float scale;
layout (location = 6) in vec4 particleDir;
layout (location = 7) in vec4 rgbaLightIn;
layout (location = 8) in vec4 rgbaBlockIn;

uniform vec3 rgbaAmbientIn;
uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec4 color;
out vec2 uv;
out float glowLevel;

mat4 rotationZ(float angle) {
    return mat4(cos(angle), -sin(angle), 0, 0,
                sin(angle),  cos(angle), 0, 0,
                0,           0,          1, 0,
                0,           0,          0, 1);
}

void main()
{
    mat4 mvmat = modelViewMatrix;
    vec3 localPos = vertexPosition * scale;
    bool rainParticle = renderFlags < 0;

    uv = rainParticle ? vec2(0.5, 0.5) : uvIn;

    mvmat[3] = mvmat * vec4(particlePosition, 1.0);

    if (rainParticle) {
        vec3 u = normalize(particleDir.xyz);
        vec3 v = vec3(0.0, -1.0, 0.0);
        float zangle = acos(dot(u.xy, v.xy));
        mvmat = mvmat * rotationZ(zangle);
    }

    mvmat[0].xyz = vec3(1.0, 0.0, 0.0);
    if (!rainParticle) {
        mvmat[1].xyz = vec3(0.0, 1.0, 0.0);
    }
    mvmat[2].xyz = vec3(0.0, 0.0, 1.0);

    if (rainParticle) {
        localPos.y = localPos.y * 8.0 - 3.0;
        localPos.xz /= 3.5;
    }

    vec3 light = max(rgbaAmbientIn, rgbaLightIn.rgb);
    light = max(light, vec3(0.35));
    color = baseColor * vec4(rgbaBlockIn.rgb * light, rgbaBlockIn.a);
    glowLevel = clamp(float(renderFlags & 255) / 128.0, 0.0, 2.0);

    vec4 cameraPos = mvmat * vec4(localPos, 1.0);
    gl_Position = projectionMatrix * cameraPos;
}
""";

    private const string PreviewQuadParticleFragmentShader = """
#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec4 color;
in vec2 uv;
in float glowLevel;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 previewColor = color;
    vec2 edgeFade = vec2(
        max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
        max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9))
    );

    previewColor.a *= 1.0 - clamp(length(edgeFade) * 10.0, 0.0, 1.0);
    if (previewColor.a < 0.002) discard;

    float alpha = clamp(previewColor.a, 0.0, 1.0);
    vec3 rgb = previewColor.rgb * max(1.0, glowLevel);
    outColor = vec4(clamp(rgb, vec3(0.0), vec3(1.0)), alpha);
}
""";

    // Self-contained billboard shader (no engine globals): transforms world-space camera-facing quads and
    // applies the same soft-square edge falloff the engine quad particle shader uses.
    private const string BillboardVertexShader = """
#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout (location = 0) in vec3 vertexPosition;
layout (location = 1) in vec2 uvIn;
layout (location = 2) in vec4 colorIn;

uniform mat4 projectionMatrix;
uniform mat4 modelViewMatrix;

out vec2 uv;
out vec4 color;

void main()
{
    uv = uvIn;
    color = colorIn;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPosition, 1.0);
}
""";

    private const string BillboardFragmentShader = """
#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

in vec2 uv;
in vec4 color;

uniform int softEdge;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 c = color;
    if (softEdge == 1) {
        vec2 edge = vec2(
            max(max(0.0, 0.1 - uv.x), max(0.0, uv.x - 0.9)),
            max(max(0.0, 0.1 - uv.y), max(0.0, uv.y - 0.9))
        );
        c.a *= 1.0 - clamp(length(edge) * 10.0, 0.0, 1.0);
    }

    if (c.a < 0.004) discard;
    outColor = vec4(clamp(c.rgb, vec3(0.0), vec3(1.0)), clamp(c.a, 0.0, 1.0));
}
""";
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

internal readonly record struct DevToolsPreviewMeshInstance
{
    public DevToolsPreviewMeshInstance(DevToolsPreviewMesh mesh, Matrixf modelMatrix)
        : this(mesh, modelMatrix, new Vector4(1f, 1f, 1f, 1f))
    {
    }

    public DevToolsPreviewMeshInstance(DevToolsPreviewMesh mesh, Matrixf modelMatrix, Vector4 tint)
    {
        Mesh = mesh;
        ModelMatrix = modelMatrix;
        Tint = tint;
    }

    public DevToolsPreviewMesh Mesh { get; }
    public Matrixf ModelMatrix { get; }
    public Vector4 Tint { get; }
}

/// <summary>
/// A single camera-facing particle billboard in preview-world space. <paramref name="HalfWidth"/> is the
/// half-extent in blocks; <paramref name="Quad"/> particles are drawn additively with a soft-square edge,
/// cubes opaquely.
/// </summary>
internal readonly record struct DevToolsPreviewBillboard(Vector3 Center, float HalfWidth, Vector4 Color, bool Quad);

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
