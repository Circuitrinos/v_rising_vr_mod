using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using ProjectM;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using GameCamType = ProjectM.CameraType;

namespace VRisingVR;

/// <summary>What the game's top-down camera is doing this frame.</summary>
internal struct GameCameraState
{
    /// <summary>The smoothed look-at point: the character plus LookAtHeight (no pan, no shake).</summary>
    public Vector3 Pivot;
    /// <summary>The smoothed point the character stands on (Pivot minus LookAtHeight): its feet.</summary>
    public Vector3 Feet;
    public float Zoom;
    public float PitchRad;
    public float YawRad;
    /// <summary>
    /// The camera entity's position. HybridCameraSystem poses the Unity camera at this plus the screen
    /// shake (position only; the rotation is the entity's), so this is the camera without the shake.
    /// </summary>
    public Vector3 CameraPos;
    public bool HaveCameraPos;
}

/// <summary>
/// Read-only access to the game's ECS camera state in the client world "Client_0" (main thread only).
/// The camera entity is LocalUserSystem._CameraEntity; its TopdownCameraState holds the smoothed
/// look-at point and zoom, and TopdownCamera the look-at height above the character's feet.
/// Recipe verified against this build by the research workflow (see CLAUDE.md).
/// </summary>
internal static class GameCamera
{
    private const float RetrySeconds = 1f;

    private static World _client;
    private static int _worldGeneration;
    private static LocalUserSystem _localUsers;
    private static bool _typesReady;
    private static ComponentType _stateType;
    private static ComponentType _configType;
    private static ComponentType _translationType;
    private static float _worldRetryAt;
    private static bool _loggedFound;
    // Each reader logs its own first failure per world and backs off on its own, so one failing reader
    // neither hides nor starves the other.
    private static bool _cameraErrorLogged;
    private static bool _aimErrorLogged;
    private static float _cameraRetryAt;
    private static float _aimRetryAt;

    public static void Reset()
    {
        _client = null;
        _localUsers = null;
    }

    public static bool TryRead(out GameCameraState state)
    {
        state = default;
        if (Time.unscaledTime < _cameraRetryAt)
            return false;
        try
        {
            if (!EnsureWorld())
                return false;

            if (!_typesReady)
            {
                _stateType = new ComponentType(Il2CppType.Of<TopdownCameraState>(), ComponentType.AccessMode.ReadOnly);
                _configType = new ComponentType(Il2CppType.Of<TopdownCamera>(), ComponentType.AccessMode.ReadOnly);
                _translationType = new ComponentType(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadOnly);
                _typesReady = true;
            }

            _localUsers ??= _client.GetExistingSystemManaged<LocalUserSystem>();
            if (_localUsers == null || _localUsers._CameraType != GameCamType.TopDownCamera)
                return false;

            EntityManager em = _client.EntityManager;
            Entity camera = _localUsers._CameraEntity;
            if (camera == Entity.Null || !em.Exists(camera) || !em.HasComponent(camera, _stateType) || !em.HasComponent(camera, _configType))
                return false;

            TopdownCameraState cameraState = Read<TopdownCameraState>(em, camera, _stateType);
            TopdownCamera config = Read<TopdownCamera>(em, camera, _configType);
            // While a world loads the camera state is still all zeros (seen on hardware: zoom 0 at the origin).
            if (cameraState.Current.Zoom <= 0.01f)
                return false;

            state.Pivot = ToVector(cameraState.Current.LookAtRootPos);
            state.Feet = state.Pivot - new Vector3(0f, config.LookAtHeight, 0f);
            state.Zoom = cameraState.Current.Zoom;
            state.PitchRad = cameraState.Current.Pitch;
            state.YawRad = cameraState.Yaw;
            if (em.HasComponent(camera, _translationType))
            {
                state.CameraPos = ToVector(Read<Translation>(em, camera, _translationType).Value);
                // Zero means it isn't posed yet (seen right after loading, 2234 m from the Unity camera).
                state.HaveCameraPos = state.CameraPos != Vector3.zero;
            }

            if (!_loggedFound)
            {
                _loggedFound = true;
                Plugin.Logger.LogInfo($"Game camera state found: feet {state.Feet}, zoom {state.Zoom:F1}, pitch {state.PitchRad * Mathf.Rad2Deg:F1}, " +
                                      $"look-at height {config.LookAtHeight:F2}, camera entity position {(state.HaveCameraPos ? state.CameraPos.ToString() : "n/a")}.");
            }
            return true;
        }
        catch (Exception e)
        {
            if (!_cameraErrorLogged)
            {
                _cameraErrorLogged = true;
                Plugin.Logger.LogWarning($"Reading the game camera state failed (HUD falls back to camera pitch): {e.Message}");
            }
            // Keep the world (EnsureWorld re-finds it if it goes away) and retry in a while.
            _localUsers = null;
            _cameraRetryAt = Time.unscaledTime + RetrySeconds;
            return false;
        }
    }

    private static ComponentType _cursorType;
    private static EntityQuery _cursorQuery;
    private static int _cursorQueryGeneration = -1;
    private static bool _loggedAim;
    private static bool _loggedAimCount;

    /// <summary>
    /// The game's own aim point: the CursorPosition singleton, which CursorPositionSystem computes each
    /// frame by raycasting the mouse against the physics world. <paramref name="ground"/> is its
    /// WorldPosition, the nearest hit on the ground (or floor) under the cursor, which is what
    /// GameplayInputSystem aims abilities at.
    /// </summary>
    public static bool TryReadAim(out Vector3 ground)
    {
        ground = default;
        if (Time.unscaledTime < _aimRetryAt)
            return false;
        try
        {
            if (!EnsureWorld())
                return false;

            EntityManager em = _client.EntityManager;
            if (_cursorQueryGeneration != _worldGeneration)
            {
                _cursorType = new ComponentType(Il2CppType.Of<CursorPosition>(), ComponentType.AccessMode.ReadOnly);
                // CursorPosition lives on CursorPositionSystem's own system entity, which queries skip
                // unless they include systems (the game's own CursorPosition queries do the same).
                var desc = new EntityQueryDesc
                {
                    All = new ComponentType[] { _cursorType },
                    Options = EntityQueryOptions.IncludeSystems,
                };
                _cursorQuery = em.CreateEntityQuery(new Il2CppReferenceArray<EntityQueryDesc>(new[] { desc }));
                // Only once it exists: a failed creation is retried, and a failed read doesn't create another.
                _cursorQueryGeneration = _worldGeneration;
            }

            int count = _cursorQuery.CalculateEntityCount();
            if (count != 1)
            {
                if (!_loggedAimCount)
                {
                    _loggedAimCount = true;
                    Plugin.Logger.LogWarning($"The game's aim point (CursorPosition) matched {count} entities instead of 1; no ground cursor yet.");
                }
                return false;
            }
            Entity entity = _cursorQuery.GetSingletonEntity();
            CursorPosition cursor = Read<CursorPosition>(em, entity, _cursorType);
            // WorldPosition is only written when the cursor ray hits something, and is zero until then.
            float3 hit = cursor.WorldPosition;
            ground = hit.x == 0f && hit.y == 0f && hit.z == 0f ? ToVector(cursor.WorldPositionPlayerPlane) : ToVector(hit);

            if (!_loggedAim)
            {
                _loggedAim = true;
                Plugin.Logger.LogInfo($"Game aim point found: {ground} (hit {ToVector(cursor.WorldPosition)}, " +
                                      $"player plane {ToVector(cursor.WorldPositionPlayerPlane)}, " +
                                      $"terrain under the character {ToVector(cursor.WorldPositionPlayerPlaneTerrainProjection)}).");
            }
            return true;
        }
        catch (Exception e)
        {
            if (!_aimErrorLogged)
            {
                _aimErrorLogged = true;
                Plugin.Logger.LogWarning($"Reading the game's aim point failed (no ground cursor): {e.Message}");
            }
            _aimRetryAt = Time.unscaledTime + RetrySeconds;
            return false;
        }
    }

    private static bool EnsureWorld()
    {
        if (_client != null && _client.IsCreated)
            return true;
        Reset();
        // World lookup walks all worlds; don't do it every frame while in the menu.
        if (Time.unscaledTime < _worldRetryAt)
            return false;
        _worldRetryAt = Time.unscaledTime + RetrySeconds;
        _client = FindClientWorld();
        if (_client == null)
            return false;
        // A new world (or the same one found again): queries are per world, and each world gets to log
        // its own first failure.
        _worldGeneration++;
        _cameraErrorLogged = false;
        _aimErrorLogged = false;
        return true;
    }

    private static World FindClientWorld()
    {
        var all = World.All;
        for (int i = 0; i < all.Count; i++)
        {
            World world = all[i];
            if (world != null && world.IsCreated && world.Name == "Client_0")
                return world;
        }
        return null;
    }

    /// <summary>
    /// Read a component through the non-generic raw accessor. GetComponentData&lt;T&gt; has no exact AOT
    /// instantiation for these types in this build (only IL2CPP's fully shared fallback); this read
    /// doesn't depend on it.
    /// </summary>
    private static unsafe T Read<T>(EntityManager em, Entity entity, ComponentType type) where T : unmanaged
        => *(T*)em.GetComponentDataRawRO(entity, type.TypeIndex);

    private static Vector3 ToVector(float3 f) => new Vector3(f.x, f.y, f.z);
}
