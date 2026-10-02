using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;

namespace Phoney.Compat;

/// <summary>
/// Soft-dependency VR deception layer for LCVR (LethalCompanyVR by DaXcess).
///
/// The problem: in VR, real players have their hands tracked to actual controller positions —
/// arms may be at chest height, pointing a flashlight, slightly bent, etc.
/// Masked enemies use the desktop walk animation (rhythmic arm-swing) which VR players
/// immediately recognise as inhuman.
///
/// What Phoney does to counter this:
///   1. Detects if LCVR is installed (via reflection) and if we are in a VR session.
///   2. When in VR mode, forces the Masked into the "item-hold" animator state
///      so arms are static and forward-facing (like a VR player holding a controller).
///   3. Adds periodic subtle head-turn events that mimic a VR player glancing around.
///   4. Notes: the held flashlight from MaskedHeldItemManager already helps enormously —
///      the arm goes into a "forward hold" pose that looks like a VR player's right hand.
/// </summary>
public static class VRDeceptionHelper
{
    private static bool _checked;
    private static bool _lcvrPresent;
    private static Type? _vrSessionType;
    private static PropertyInfo? _isInVRProp;

    // ─── Detection ────────────────────────────────────────────────────────────

    public static bool IsLCVRPresent
    {
        get
        {
            if (!_checked) Detect();
            return _lcvrPresent;
        }
    }

    /// <summary>
    /// Returns true if the local player is currently playing in VR.
    /// Safe to call when LCVR is not installed — returns false.
    /// </summary>
    public static bool LocalPlayerIsInVR()
    {
        if (!IsLCVRPresent || _vrSessionType == null || _isInVRProp == null) return false;
        try
        {
            return (bool)(_isInVRProp.GetValue(null) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static void Detect()
    {
        _checked = true;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.GetName().Name.Equals("LCVR", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                _vrSessionType = asm.GetType("LCVR.Managers.VRSession");
                _isInVRProp    = _vrSessionType?.GetProperty("IsInVR",
                    BindingFlags.Public | BindingFlags.Static);

                _lcvrPresent = _vrSessionType != null && _isInVRProp != null;
                PhoneyPlugin.Logger.LogInfo(
                    _lcvrPresent
                        ? "[VRDeception] LCVR detected — VR deception mode active."
                        : "[VRDeception] LCVR detected but API changed — VR deception disabled.");
            }
            catch (Exception ex)
            {
                PhoneyPlugin.Logger.LogWarning($"[VRDeception] Detection error: {ex.Message}");
            }
            return;
        }
    }
}

/// <summary>
/// MonoBehaviour attached to a Masked enemy when VR deception is enabled.
/// Drives subtle head-bob and gaze behaviours that VR players exhibit naturally
/// but desktop animations never produce.
/// </summary>
public class VRDeceptionBehaviour : MonoBehaviour
{
    private MaskedPlayerEnemy? _masked;
    private Transform? _headBone;
    private Quaternion _baseHeadLocalRotation = Quaternion.identity;
    private bool _hasBaseRotation;

    // Timing
    private float _nextGlanceTime;
    private Quaternion _currentGlanceOffset = Quaternion.identity;
    private Quaternion _targetGlanceOffset  = Quaternion.identity;

    public void Initialize(MaskedPlayerEnemy masked)
    {
        _masked   = masked;
        _headBone = FindBone(masked.transform, "spine.004") // upper spine / neck
                    ?? FindBone(masked.transform, "head");

        if (_headBone != null)
        {
            _baseHeadLocalRotation = _headBone.localRotation;
            _hasBaseRotation = true;
        }

        ScheduleNextGlance();
    }

    private void LateUpdate()
    {
        // LateUpdate runs after animations
        if (_masked == null || _masked.isEnemyDead || _headBone == null) return;
        if (!PhoneyPlugin.VRFriendlyDeception.Value) return;

        // If in attack/ambush mode, BloodyReveal controls the head
        var bloody = _masked.GetComponent<Phoney.AI.PhoneyBloodyReveal>();
        if (bloody != null && bloody.IsRevealed) return;

        if (!_hasBaseRotation)
        {
            _baseHeadLocalRotation = _headBone.localRotation;
            _hasBaseRotation = true;
        }

        ApplyGlance();
    }

    // ─── Periodic head-turn glances (Absolute from baseline) ─────────────────

    private void ApplyGlance()
    {
        if (Time.time >= _nextGlanceTime)
        {
            // Subtle, calm horizontal gaze: yaw [-16°, +16°], strictly level pitch [-2°, +3°]
            // Never tilts head up at the ceiling or sky!
            float yaw   = UnityEngine.Random.Range(-16f, 16f);
            float pitch = UnityEngine.Random.Range(-2f, 3f);
            _targetGlanceOffset = Quaternion.Euler(pitch, yaw, 0f);
            ScheduleNextGlance();
        }

        _currentGlanceOffset = Quaternion.Slerp(_currentGlanceOffset, _targetGlanceOffset, Time.deltaTime * 2.8f);

        // Assign relative to base rotation — NEVER accumulate cumulatively
        _headBone!.localRotation = _baseHeadLocalRotation * _currentGlanceOffset;
    }

    private void ScheduleNextGlance()
    {
        _nextGlanceTime = Time.time + UnityEngine.Random.Range(3.0f, 6.0f);
    }

    // ─── Utility ──────────────────────────────────────────────────────────

    private static Transform? FindBone(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
}
