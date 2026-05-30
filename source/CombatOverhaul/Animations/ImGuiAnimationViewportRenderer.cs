#if DEBUG
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CombatOverhaul.Animations;

internal sealed class ImGuiAnimationViewportRenderer : IRenderer
{
    private readonly ICoreClientAPI _api;
    private readonly Matrixf _lightMatrix = new();
    private readonly Vec4f _lightPosition = new(1f, -1f, 0f, 0f);

    private bool _visible;
    private float _x;
    private float _y;
    private float _width;
    private float _height;
    private float _yaw;
    private float _zoom = 1f;
    private float _panX;
    private float _panY;
    private long _updatedAtMs;

    public ImGuiAnimationViewportRenderer(ICoreClientAPI api)
    {
        _api = api;
        api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "overhaullib-imgui-animation-viewport");
    }

    public double RenderOrder => 1.08;
    public int RenderRange => 9999;

    public void SetVisible(bool visible)
    {
        _visible = visible;
    }

    public void SetViewport(float x, float y, float width, float height, float yaw, float zoom, float panX, float panY)
    {
        _visible = true;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _yaw = yaw;
        _zoom = Math.Clamp(zoom, 0.55f, 1.85f);
        _panX = panX;
        _panY = panY;
        _updatedAtMs = _api.World.ElapsedMilliseconds;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Ortho) return;
        if (!_visible || _api.World?.Player?.Entity == null) return;
        if (_width <= 32 || _height <= 32) return;
        if (_api.World.ElapsedMilliseconds - _updatedAtMs > 500) return;

        ElementBounds bounds = ElementBounds
            .Fixed(_x / RuntimeEnv.GUIScale, _y / RuntimeEnv.GUIScale, _width / RuntimeEnv.GUIScale, _height / RuntimeEnv.GUIScale)
            .WithEmptyParent();
        bounds.CalcWorldBounds();

        float size = (float)Math.Min(bounds.InnerHeight * 0.84, bounds.InnerWidth * 0.58) * _zoom;
        if (size <= 1) return;

        double posX = bounds.renderX + bounds.InnerWidth / 2 - size * 0.30 + _panX;
        double posY = bounds.renderY + bounds.InnerHeight / 2 - size * 0.52 + _panY;
        double posZ = GuiElement.scaled(250);

        _api.Render.PushScissor(bounds, false);
        _api.Render.RenderRectangle((float)bounds.renderX, (float)bounds.renderY, 205f, (float)bounds.InnerWidth, (float)bounds.InnerHeight, ColorUtil.ColorFromRgba(12, 12, 12, 190));

        _api.Render.GlPushMatrix();
        _api.Render.GlTranslate(0f, 0f, 150f);
        _api.Render.GlRotate(-12f, 1f, 0f, 0f);

        _lightMatrix.Identity();
        _lightMatrix.RotateXDeg(-12f);
        Vec4f light = _lightMatrix.TransformVector(_lightPosition);
        _api.Render.CurrentActiveShader?.Uniform("lightPosition", light.X, light.Y, light.Z);

        _api.Render.RenderEntityToGui(deltaTime, _api.World.Player.Entity, posX, posY, posZ, _yaw, size, ColorUtil.WhiteArgb);

        _api.Render.CurrentActiveShader?.Uniform("lightPosition", 0.7071068f, -0.7071068f, 0f);
        _api.Render.GlPopMatrix();

        _api.Render.PopScissor();
    }

    public void Dispose()
    {
        _api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
    }
}
#endif
