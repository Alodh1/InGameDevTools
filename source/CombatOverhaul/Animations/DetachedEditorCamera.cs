#if DEBUG
using ImGuiNET;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace CombatOverhaul.Animations;

internal enum RigEditorCameraMode
{
    FirstPerson,
    Orbiting,
    Detached
}

internal sealed class DetachedEditorCamera : IRenderer
{
    private static readonly string[] CameraModeNames = ["First person", "Orbiting", "Detached"];
    private static DetachedEditorCamera? _activeInstance;
    private static readonly FieldInfo? CameraModeField = typeof(Camera).GetField("CameraMode", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly ICoreClientAPI _api;
    private bool _initialized;
    private bool _cameraStateCaptured;
    private bool _previousAllowCameraControl;
    private bool _previousUpdateCameraPos = true;
    private EnumCameraMode? _previousCameraMode;
    private double _yaw;
    private double _pitch = -0.15;
    private double _distance = 4.0;
    private Vec3d _targetOffset = new();
    private Vec3d _detachedPosition = new();
    private float _moveSpeed = 2.5f;
    private float _orbitSensitivity = 0.006f;
    private RigEditorCameraMode _mode = RigEditorCameraMode.FirstPerson;

    public bool Enabled => _mode != RigEditorCameraMode.FirstPerson;
    public double RenderOrder => 0.98;
    public int RenderRange => 9999;
    internal static bool IsActive => _activeInstance?.Enabled == true;

    public DetachedEditorCamera(ICoreClientAPI api)
    {
        _api = api;
        _activeInstance = this;
        api.Event.RegisterRenderer(this, EnumRenderStage.Before, "overhaullib-detached-editor-camera");
    }

    public void Update(float deltaSeconds, bool editorOpen)
    {
        if (!editorOpen)
        {
            SetMode(RigEditorCameraMode.FirstPerson);
            return;
        }

        if (!Enabled) return;

        EnsureInitialized();
        ApplyDetachedCameraState();
        UpdateControls(deltaSeconds);
        SuppressPlayerMovementControls();
    }

    public void DrawControls(string id)
    {
        int modeIndex = (int)_mode;
        ImGui.SetNextItemWidth(150);
        if (ImGui.Combo($"Camera mode##{id}", ref modeIndex, CameraModeNames, CameraModeNames.Length))
        {
            SetMode((RigEditorCameraMode)modeIndex);
        }

        if (_mode == RigEditorCameraMode.FirstPerson)
        {
            ImGui.TextDisabled("Uses the normal game camera.");
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button($"Focus player##{id}")) FocusPlayer();
        ImGui.SameLine();
        if (ImGui.Button($"Reset camera##{id}")) ResetCamera();

        if (_mode == RigEditorCameraMode.Orbiting)
        {
            float distance = (float)_distance;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderFloat($"Distance##{id}", ref distance, 0.75f, 12f)) _distance = distance;

            ImGui.TextDisabled("RMB orbits. MMB or Shift+RMB pans. Mouse wheel zooms.");
        }
        else
        {
            ImGui.SetNextItemWidth(140);
            ImGui.SliderFloat($"Move speed##{id}", ref _moveSpeed, 0.25f, 12f);

            ImGui.TextDisabled("RMB drag looks around. WASD moves the camera, Q/E moves down/up. Shift speeds up.");
        }
    }

    public void SetEnabled(bool enabled)
    {
        SetMode(enabled ? RigEditorCameraMode.Orbiting : RigEditorCameraMode.FirstPerson);
    }

    internal void SetEditorMode(RigEditorCameraMode mode)
    {
        SetMode(mode);
    }

    private void SetMode(RigEditorCameraMode mode)
    {
        if (_mode == mode) return;

        bool wasEnabled = Enabled;
        _mode = mode;

        if (!Enabled)
        {
            RestoreCameraState();
            return;
        }

        _activeInstance = this;
        EnsureInitialized(force: !wasEnabled);

        if (_mode == RigEditorCameraMode.Detached)
        {
            SetDetachedPositionFromOrbit();
        }

        if (!wasEnabled)
        {
            CaptureCameraState();
        }

        ApplyDetachedCameraState();
    }

    public void FocusPlayer()
    {
        _targetOffset.Set(0, 0, 0);
        EnsureInitialized(force: true);

        if (_mode == RigEditorCameraMode.Detached)
        {
            SetDetachedPositionFromOrbit();
        }
    }

    public void ResetCamera()
    {
        _targetOffset.Set(0, 0, 0);
        _distance = 4.0;
        _pitch = -0.15;
        _yaw = _api.World.Player.CameraYaw;
        _initialized = true;

        if (_mode == RigEditorCameraMode.Detached)
        {
            SetDetachedPositionFromOrbit();
        }
    }

    internal static bool SuppressVanillaCameraUpdate(ClientMain client)
    {
        if (_activeInstance?.Enabled != true) return false;

        _activeInstance.ApplyDetachedCameraState();
        _activeInstance.SuppressPlayerMovementControls();
        client.MouseDeltaX = 0;
        client.MouseDeltaY = 0;
        client.DelayedMouseDeltaX = 0;
        client.DelayedMouseDeltaY = 0;
        return true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Before || !Enabled) return;
        if (_api.World is not ClientMain client || client.MainCamera == null) return;

        ApplyDetachedCameraState();
        SuppressPlayerMovementControls();
        OverrideCamera(client.MainCamera, client);
    }

    private void EnsureInitialized(bool force = false)
    {
        if (_initialized && !force) return;

        _yaw = _api.World.Player.CameraYaw;
        _pitch = Math.Clamp(_api.World.Player.CameraPitch, -1.25, 1.25);
        if (Math.Abs(_pitch) < 0.01) _pitch = -0.15;
        _distance = Math.Clamp(_distance, 0.75, 12.0);
        _initialized = true;
    }

    private void UpdateControls(float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();

        bool shift = ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift);
        bool panOrbit = _mode == RigEditorCameraMode.Orbiting && !io.WantCaptureMouse && (io.MouseDown[2] || (shift && io.MouseDown[1]));
        if (panOrbit)
        {
            GetBasis(out _, out Vec3d panRight, out Vec3d panUp);
            double panSpeed = Math.Clamp(_distance * 0.0018, 0.001, 0.04);
            _targetOffset.Add(panRight.Mul(-io.MouseDelta.X * panSpeed));
            _targetOffset.Add(panUp.Mul(io.MouseDelta.Y * panSpeed));
        }
        else if (!io.WantCaptureMouse && io.MouseDown[1])
        {
            _yaw -= io.MouseDelta.X * _orbitSensitivity;
            _pitch = Math.Clamp(_pitch - io.MouseDelta.Y * _orbitSensitivity, -1.35, 1.35);
        }

        if (_mode == RigEditorCameraMode.Orbiting && !io.WantCaptureMouse && Math.Abs(io.MouseWheel) > 0.001f)
        {
            _distance = Math.Clamp(_distance - io.MouseWheel * 0.35, 0.75, 12.0);
        }

        if (_mode != RigEditorCameraMode.Detached) return;

        if (io.WantTextInput) return;

        GetBasis(out Vec3d forward, out Vec3d right, out Vec3d up);
        Vec3d move = new();
        if (ImGui.IsKeyDown(ImGuiKey.W)) move.Add(forward);
        if (ImGui.IsKeyDown(ImGuiKey.S)) move.Sub(forward);
        if (ImGui.IsKeyDown(ImGuiKey.D)) move.Add(right);
        if (ImGui.IsKeyDown(ImGuiKey.A)) move.Sub(right);
        if (ImGui.IsKeyDown(ImGuiKey.E)) move.Add(up);
        if (ImGui.IsKeyDown(ImGuiKey.Q)) move.Sub(up);

        if (move.LengthSq() < 0.000001) return;

        move.Normalize();
        double speed = _moveSpeed * (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift) ? 3.0 : 1.0);
        _detachedPosition.Add(move.Mul(speed * deltaSeconds));
    }

    private void OverrideCamera(PlayerCamera camera, ClientMain client)
    {
        GetCameraPoints(out Vec3d cameraPos, out Vec3d targetPos);
        double[] view = Mat4d.Create();
        Mat4d.LookAt(view, cameraPos.ToDoubleArray(), targetPos.ToDoubleArray(), new double[] { 0, 1, 0 });
        Vec3d relativeTarget = targetPos.SubCopy(cameraPos);
        double[] originView = Mat4d.Create();
        Mat4d.LookAt(originView, new double[] { 0, 0, 0 }, relativeTarget.ToDoubleArray(), new double[] { 0, 1, 0 });

        camera.CameraEyePos.Set(cameraPos);
        camera.CamSourcePosition.Set(cameraPos);
        camera.OriginPosition.Set(cameraPos);
        camera.Yaw = _yaw;
        camera.Pitch = _pitch;
        camera.CameraMatrix = view;
        camera.CameraMatrixOrigin = originView;

        if (camera.CameraMatrixOriginf == null || camera.CameraMatrixOriginf.Length != 16)
        {
            camera.CameraMatrixOriginf = Mat4f.Create();
        }

        for (int i = 0; i < 16; i++) camera.CameraMatrixOriginf[i] = (float)originView[i];

        client.EntityPlayer.CameraPos.Set(cameraPos);
        UpdateShaderCameraPosition(client, cameraPos);
    }

    private static void UpdateShaderCameraPosition(ClientMain client, Vec3d cameraPos)
    {
        if (client.shUniforms.playerReferencePos == null)
        {
            client.shUniforms.playerReferencePos = new Vec3d(client.BlockAccessor.MapSizeX / 2, 0.0, client.BlockAccessor.MapSizeZ / 2);
            client.shUniforms.playerReferencePosForFoam = new Vec3d(client.BlockAccessor.MapSizeX / 2, 0.0, client.BlockAccessor.MapSizeZ / 2);
        }

        if (client.shUniforms.playerReferencePos.HorizontalSquareDistanceTo(cameraPos.X, cameraPos.Z) > 400000000.0)
        {
            client.shUniforms.playerReferencePos.Set((float)cameraPos.X, 0.0, (float)cameraPos.Z);
        }

        if (client.shUniforms.playerReferencePosForFoam.HorizontalSquareDistanceTo(cameraPos.X, cameraPos.Z) > 40000.0)
        {
            client.shUniforms.playerReferencePosForFoam.Set((float)cameraPos.X, 0.0, (float)cameraPos.Z);
        }

        client.shUniforms.PlayerPos.Set(cameraPos.SubCopy(client.shUniforms.playerReferencePos));
        client.shUniforms.PlayerPosForFoam.Set(cameraPos.SubCopy(client.shUniforms.playerReferencePosForFoam));
    }

    private void CaptureCameraState()
    {
        if (_cameraStateCaptured || _api.World is not ClientMain client || client.MainCamera == null) return;

        _previousAllowCameraControl = client.AllowCameraControl;
        _previousUpdateCameraPos = client.MainCamera.UpdateCameraPos;
        if (CameraModeField?.GetValue(client.MainCamera) is EnumCameraMode mode)
        {
            _previousCameraMode = mode;
        }
        _cameraStateCaptured = true;
    }

    private void ApplyDetachedCameraState()
    {
        if (_api.World is not ClientMain client || client.MainCamera == null) return;

        CaptureCameraState();
        client.AllowCameraControl = false;
        client.MainCamera.UpdateCameraPos = false;
        CameraModeField?.SetValue(client.MainCamera, EnumCameraMode.ThirdPerson);
    }

    private void RestoreCameraState()
    {
        if (!_cameraStateCaptured || _api.World is not ClientMain client || client.MainCamera == null)
        {
            _cameraStateCaptured = false;
            return;
        }

        client.AllowCameraControl = _previousAllowCameraControl;
        client.MainCamera.UpdateCameraPos = _previousUpdateCameraPos;
        if (_previousCameraMode.HasValue)
        {
            CameraModeField?.SetValue(client.MainCamera, _previousCameraMode.Value);
        }
        _cameraStateCaptured = false;
        _previousCameraMode = null;
    }

    private void SuppressPlayerMovementControls()
    {
        EntityPlayer? player = _api.World?.Player?.Entity;
        if (player == null) return;

        ClearMovement(player.Controls);
        ClearMovement(player.ServerControls);
    }

    private static void ClearMovement(EntityControls? controls)
    {
        if (controls == null) return;

        controls.Forward = false;
        controls.Backward = false;
        controls.Left = false;
        controls.Right = false;
        controls.Jump = false;
        controls.Sneak = false;
        controls.Sprint = false;
        controls.Up = false;
        controls.Down = false;
        controls.WalkVector.Set(0, 0, 0);
        controls.FlyVector.Set(0, 0, 0);
    }

    private void GetCameraPoints(out Vec3d cameraPos, out Vec3d targetPos)
    {
        if (_mode == RigEditorCameraMode.Detached)
        {
            cameraPos = _detachedPosition.Clone();
            GetBasis(out Vec3d forward, out _, out _);
            targetPos = cameraPos.AddCopy(forward);
            return;
        }

        GetOrbitCameraPoints(out cameraPos, out targetPos);
    }

    private void GetOrbitCameraPoints(out Vec3d cameraPos, out Vec3d targetPos)
    {
        EntityPlayer player = _api.World.Player.Entity;
        targetPos = new Vec3d(player.Pos.X, player.Pos.InternalY + player.SelectionBox.Y2 * 0.55, player.Pos.Z).Add(_targetOffset);
        GetBasis(out Vec3d forward, out _, out _);
        cameraPos = targetPos.SubCopy(forward.Mul(_distance));
    }

    private void SetDetachedPositionFromOrbit()
    {
        GetOrbitCameraPoints(out Vec3d cameraPos, out _);
        _detachedPosition.Set(cameraPos);
    }

    private void GetBasis(out Vec3d forward, out Vec3d right, out Vec3d up)
    {
        forward = new Vec3d(-Math.Sin(_yaw) * Math.Cos(_pitch), Math.Sin(_pitch), -Math.Cos(_yaw) * Math.Cos(_pitch)).Normalize();
        right = new Vec3d(Math.Cos(_yaw), 0, -Math.Sin(_yaw)).Normalize();
        up = right.Cross(forward).Normalize();
        if (up.LengthSq() < 0.0001) up = new Vec3d(0, 1, 0);
    }

    public void Dispose()
    {
        SetEnabled(false);
        _api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        if (_activeInstance == this)
        {
            _activeInstance = null;
        }
    }
}
#endif
