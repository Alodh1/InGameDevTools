using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Utils;

/// <summary>
/// Self-contained CPU particle simulation used for the particle editor preview. Physics mirror
/// <c>ParticleGeneric</c>/<c>AdvancedParticleProperties</c> so the preview matches in-game behaviour:
/// velocities are in blocks/second, gravity is <c>GravityEffect * GravityStrengthParticle(0.3) * 40</c>,
/// and Size/Opacity/Colour evolve over the normalized life fraction (0..1). Live particles are emitted as
/// 3D billboards via <see cref="CollectBillboards"/> and rendered by <see cref="DevToolsPreview3DRenderer"/>
/// into the offscreen framebuffer with the engine's own additive/soft-edge quad look (the draw-list path
/// cannot do additive blending because VSImGui's renderer rejects draw callbacks).
/// </summary>
internal sealed class PreviewParticleSystem
{
    private const float GravityScale = 0.3f * 40f; // GlobalConstants.GravityStrengthParticle * 40
    private const int MaxParticles = 6000;

    private readonly List<PreviewParticle> _particles = new(1024);
    private readonly Random _rng = new();
    private float _gameSpeed = 1f;
    private float _density = 1f;

    public int Count => _particles.Count;

    public void Clear(bool releaseCapacity = false)
    {
        _particles.Clear();
        if (releaseCapacity)
        {
            _particles.TrimExcess();
        }
    }

    /// <summary>
    /// The current in-game time speed (Calendar.SpeedOfTime / 60). The engine couples particle timing to it:
    /// spawned lifetime is scaled by 5/sqrt(speed) and the physics tick advances by realDt * speed. Matching
    /// it keeps the preview running at the same rate as the live game (normally ~1).
    /// </summary>
    public float GameSpeed
    {
        get => _gameSpeed;
        set => _gameSpeed = float.IsFinite(value) ? Math.Clamp(value, 0.05f, 200f) : 1f;
    }

    /// <summary>
    /// User-facing spawn-count multiplier (1.0 = the effect's full unthrottled in-game spawn rate). Lets the
    /// preview be dialed down to match a busy world where the shared particle pool throttles each source.
    /// </summary>
    public float Density
    {
        get => _density;
        set => _density = float.IsFinite(value) ? Math.Clamp(value, 0f, 8f) : 1f;
    }

    /// <summary>
    /// Spawns particles from a provider. The provider's randomized accessors (Pos, GetVelocity, Quantity,
    /// LifeLength, Size, GravityEffect) are sampled per particle exactly like the engine pools do.
    /// </summary>
    public int Spawn(IParticlePropertiesProvider provider, ICoreClientAPI capi)
    {
        if (provider == null) return 0;

        provider.BeginParticle();
        float quantity = provider.Quantity;
        if (!float.IsFinite(quantity) || quantity <= 0f) return 0;
        quantity *= _gameSpeed * _density; // engine: num3 = Quantity * currentGamespeed; _density is the editor override

        // Match the engine: spawn the whole part, plus the fractional part probabilistically. Many ambient
        // block effects emit < 1 particle per tick, so flooring/rounding would make them never appear.
        int budget = Math.Max(0, MaxParticles - _particles.Count);
        int whole = (int)MathF.Floor(quantity);
        if (_rng.NextDouble() < quantity - whole) whole++;
        int count = Math.Clamp(whole, 0, budget);
        int spawned = 0;
        for (int index = 0; index < count; index++)
        {
            int rgba = provider.GetRgbaColor(capi);
            Vector4 color = DecodeColor(rgba);
            if (color.W <= 0.001f && color.X + color.Y + color.Z <= 0.001f) continue;

            Vec3d pos = provider.Pos;
            Vec3f velocity = provider.GetVelocity(pos);
            float life = provider.LifeLength;
            if (!float.IsFinite(life) || life <= 0.01f) life = 0.5f;
            // The engine pools store LifeLength * 5/sqrt(gamespeed); the lifetime is then consumed in
            // game-speed time. Replicating it keeps particles alive for the same real duration as in-game.
            life *= 5f / MathF.Sqrt(_gameSpeed);
            float size = provider.Size;
            if (!float.IsFinite(size) || size <= 0f) size = 0.25f;

            int glow = provider.VertexFlags & 0xFF;
            _particles.Add(new PreviewParticle
            {
                Pos = new Vector3((float)pos.X, (float)pos.Y, (float)pos.Z),
                Vel = new Vector3(velocity.X, velocity.Y, velocity.Z),
                ParentVel = new Vector3(provider.ParentVelocity?.X ?? 0f, provider.ParentVelocity?.Y ?? 0f, provider.ParentVelocity?.Z ?? 0f),
                ParentWeight = provider.ParentVelocityWeight,
                Age = 0f,
                Life = life,
                BaseSize = size,
                Gravity = provider.GravityEffect * GravityScale,
                BaseColor = color,
                SizeEvolve = provider.SizeEvolve,
                OpacityEvolve = provider.OpacityEvolve,
                RedEvolve = provider.RedEvolve,
                GreenEvolve = provider.GreenEvolve,
                BlueEvolve = provider.BlueEvolve,
                VelocityEvolve = provider.VelocityEvolve,
                GlowBoost = MathF.Max(1f, glow / 128f),
                Quad = provider.ParticleModel == EnumParticleModel.Quad
            });
            spawned++;
        }

        return spawned;
    }

    public void Update(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        // The engine advances particles by realDt * gamespeed (and stores lifetimes in that same scaled
        // time). Mirroring it makes motion and lifetime run at the live-game rate rather than ~5x too fast.
        float dt = Math.Min(deltaSeconds * _gameSpeed, 0.5f);

        for (int index = _particles.Count - 1; index >= 0; index--)
        {
            PreviewParticle particle = _particles[index];
            particle.Age += dt;
            if (particle.Age >= particle.Life)
            {
                int lastIndex = _particles.Count - 1;
                if (index != lastIndex)
                {
                    _particles[index] = _particles[lastIndex];
                }

                _particles.RemoveAt(lastIndex);
                continue;
            }

            float seq = particle.Age / particle.Life;
            particle.Vel.Y -= particle.Gravity * dt;

            Vector3 motion = particle.Vel;
            if (particle.VelocityEvolve != null && particle.VelocityEvolve.Length >= 3)
            {
                motion.X *= particle.VelocityEvolve[0].nextFloat(0f, seq);
                motion.Y *= particle.VelocityEvolve[1].nextFloat(0f, seq);
                motion.Z *= particle.VelocityEvolve[2].nextFloat(0f, seq);
            }

            particle.Pos += motion * dt;
            if (particle.ParentWeight > 0f)
            {
                particle.Pos += particle.ParentVel * particle.ParentWeight * dt;
            }

            _particles[index] = particle;
        }
    }

    /// <summary>
    /// Emits one camera-facing billboard per live particle (3D centre + half-width + colour), applying the
    /// Size/Opacity/Colour evolves for the current life fraction. Quads carry the glow boost and are drawn
    /// additively; cubes are drawn opaque by the renderer.
    /// </summary>
    public void CollectBillboards(List<DevToolsPreviewBillboard> output)
    {
        if (output.Capacity < output.Count + _particles.Count)
        {
            output.Capacity = output.Count + _particles.Count;
        }

        foreach (PreviewParticle particle in _particles)
        {
            float seq = particle.Life <= 0f ? 0f : Math.Clamp(particle.Age / particle.Life, 0f, 1f);

            float size = particle.SizeEvolve != EvolvingNatFloat.NoValueSet
                ? particle.SizeEvolve.nextFloat(particle.BaseSize, seq)
                : particle.BaseSize;
            if (size <= 0f) continue;

            float alpha255 = particle.OpacityEvolve != EvolvingNatFloat.NoValueSet
                ? Math.Clamp(particle.OpacityEvolve.nextFloat(particle.BaseColor.W * 255f, seq), 0f, 255f)
                : particle.BaseColor.W * 255f;
            if (alpha255 <= 1f) continue;

            float red = EvolveChannel(particle.RedEvolve, particle.BaseColor.X, seq);
            float green = EvolveChannel(particle.GreenEvolve, particle.BaseColor.Y, seq);
            float blue = EvolveChannel(particle.BlueEvolve, particle.BaseColor.Z, seq);

            // Quad model renders at ~0.25 blocks for Size 1 in-game, so half-width = size * 0.125.
            float halfWidth = MathF.Max(0.02f, size * 0.125f);
            Vector4 color = new(
                Math.Clamp(red * particle.GlowBoost, 0f, 4f),
                Math.Clamp(green * particle.GlowBoost, 0f, 4f),
                Math.Clamp(blue * particle.GlowBoost, 0f, 4f),
                Math.Clamp(alpha255 / 255f, 0f, 1f));

            output.Add(new DevToolsPreviewBillboard(particle.Pos, halfWidth, color, particle.Quad));
        }
    }

    private static float EvolveChannel(EvolvingNatFloat evolve, float baseValue01, float seq)
    {
        if (evolve == EvolvingNatFloat.NoValueSet) return baseValue01;
        float base255 = baseValue01 * 255f;
        return Math.Clamp((base255 + evolve.nextFloat(base255, seq)) / 255f, 0f, 1f);
    }

    private static Vector4 DecodeColor(int rgba)
    {
        // AdvancedParticleProperties.GetRgbaColor packs the colour as 0xAARRGGBB.
        float a = ((rgba >> 24) & 0xFF) / 255f;
        float r = ((rgba >> 16) & 0xFF) / 255f;
        float g = ((rgba >> 8) & 0xFF) / 255f;
        float b = (rgba & 0xFF) / 255f;
        return new Vector4(r, g, b, a);
    }

    private struct PreviewParticle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public Vector3 ParentVel;
        public float ParentWeight;
        public float Age;
        public float Life;
        public float BaseSize;
        public float Gravity;
        public Vector4 BaseColor;
        public EvolvingNatFloat SizeEvolve;
        public EvolvingNatFloat OpacityEvolve;
        public EvolvingNatFloat RedEvolve;
        public EvolvingNatFloat GreenEvolve;
        public EvolvingNatFloat BlueEvolve;
        public EvolvingNatFloat[]? VelocityEvolve;
        public float GlowBoost;
        public bool Quad;
    }
}
