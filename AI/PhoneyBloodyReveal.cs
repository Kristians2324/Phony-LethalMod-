using System;
using System.Collections;
using System.Collections.Generic;
using Phoney.Compat;
using Phoney.Core;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Handles the terrifying visual and procedural animation transformation when the Masked mimic
/// reveals its true killer nature in Phase 3 (Ambush Strike).
///
/// Features:
///   1. Suit & Flesh Blood Soaking: Dynamically stains and tints the suit/helmet materials into deep arterial crimson gore.
///   2. Unnatural Demonic Neck Snap: The head violently jerks sideways at an impossible 50° angle with bone-crunch audio.
///   3. Violent Bodily Spasm: 30 Hz procedural micro-convulsions across spine and neck before charging.
///   4. Blood Eruption: Triggers the mask flood particle system and instantiates ground blood splatters.
///   5. Piercing Crimson Eye Glow: Ignites the theatrical mask eyes into intense pulsating blood-red.
/// </summary>
public class PhoneyBloodyReveal : MonoBehaviour
{
    private MaskedPlayerEnemy? _masked;
    private Transform? _headBone;
    private Transform? _spineBone;
    private Transform? _leftArmBone;
    private Transform? _rightArmBone;

    private Quaternion _baseHeadLocalRotation = Quaternion.identity;
    private Quaternion _baseSpineLocalRotation = Quaternion.identity;
    private Quaternion _baseLeftArmLocalRotation = Quaternion.identity;
    private Quaternion _baseRightArmLocalRotation = Quaternion.identity;
    private bool _hasBaseRotations;

    private bool _isRevealed;
    public bool IsRevealed => _isRevealed;

    public bool IsTransforming { get; private set; }

    private bool _spasmActive;
    private float _revealTime;

    private readonly List<Material> _tintedMaterials = new();
    private readonly Dictionary<Material, Color> _originalBaseColors = new();
    private readonly Dictionary<Material, Color> _originalColors = new();

    public void Initialize(MaskedPlayerEnemy masked)
    {
        _masked = masked;

        _headBone = FindBone(masked.transform, "spine.004") 
                 ?? FindBone(masked.transform, "head");

        _spineBone = FindBone(masked.transform, "spine.003") 
                  ?? FindBone(masked.transform, "spine.002")
                  ?? FindBone(masked.transform, "spine");

        _leftArmBone = FindBone(masked.transform, "shoulder.L") 
                    ?? FindBone(masked.transform, "upper_arm.L");

        _rightArmBone = FindBone(masked.transform, "shoulder.R") 
                     ?? FindBone(masked.transform, "upper_arm.R");

        if (_headBone != null) _baseHeadLocalRotation = _headBone.localRotation;
        if (_spineBone != null) _baseSpineLocalRotation = _spineBone.localRotation;
        if (_leftArmBone != null) _baseLeftArmLocalRotation = _leftArmBone.localRotation;
        if (_rightArmBone != null) _baseRightArmLocalRotation = _rightArmBone.localRotation;
        _hasBaseRotations = true;

        CacheMaterials();
    }

    private void CacheMaterials()
    {
        if (_masked == null) return;

        var renderers = new[] { _masked.rendererLOD0, _masked.rendererLOD1, _masked.rendererLOD2 };
        foreach (var rend in renderers)
        {
            if (rend == null) continue;
            foreach (var mat in rend.materials)
            {
                if (mat == null || _tintedMaterials.Contains(mat)) continue;
                _tintedMaterials.Add(mat);

                if (mat.HasProperty("_BaseColor"))
                    _originalBaseColors[mat] = mat.GetColor("_BaseColor");
                if (mat.HasProperty("_Color"))
                    _originalColors[mat] = mat.color;
            }
        }
    }

    /// <summary>
    /// Triggers the full bloody appearance transformation and procedural reveal animation.
    /// </summary>
    public void TriggerBloodyReveal()
    {
        if (_masked == null || _isRevealed) return;
        _isRevealed = true;
        IsTransforming = true;
        _revealTime = Time.time;

        PhoneyPlugin.Logger.LogInfo($"[BloodyReveal] '{_masked.gameObject.name}' triggered bloody transformation!");

        // Instantly halt in place while transforming
        NavMeshUtil.SafeSetStopped(_masked.agent, true);
        NavMeshUtil.SafeSetVelocity(_masked.agent, Vector3.zero);

        // Remove cosmetics when turning aggressive
        MoreCompanyCompat.ClearCosmetics(_masked);

        // ── 1. Sound Effect: Flesh-Tear / Bone-Crunch ────────────────────────
        PlayGoreSound();

        // ── 2. Blood Particles & Decals ──────────────────────────────────────
        EruptBlood();

        // ── 3. Blood-Soaked Suit & Face Materials ─────────────────────────────
        ApplyBloodyMaterials();

        // ── 4. Reveal Mask & Piercing Red Eyes ────────────────────────────────
        IgniteBloodyMask();

        // ── 5. Procedural Neck Snap & Demonic Spasm Animation ─────────────────
        StartCoroutine(RevealSpasmRoutine());
    }

    // ─── Sound Effects ────────────────────────────────────────────────────────

    private void PlayGoreSound()
    {
        try
        {
            if (StartOfRound.Instance?.bloodGoreSFX != null && _masked != null)
            {
                if (_masked.creatureSFX != null)
                {
                    _masked.creatureSFX.PlayOneShot(StartOfRound.Instance.bloodGoreSFX, 1.0f);
                }
                else
                {
                    AudioSource.PlayClipAtPoint(StartOfRound.Instance.bloodGoreSFX, _masked.transform.position, 1.0f);
                }
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[BloodyReveal] PlayGoreSound error: {ex.Message}");
        }
    }

    // ─── Blood Eruption ───────────────────────────────────────────────────────

    private void EruptBlood()
    {
        if (_masked == null) return;

        try
        {
            // Trigger vanilla mask flood blood eruption particle as a brief burst (not infinite looping vomit)
            if (_masked.maskFloodParticle != null)
            {
                _masked.maskFloodParticle.Play();
                StartCoroutine(StopBloodParticleRoutine());
            }

            // Spawn ground blood splatter pool
            if (StartOfRound.Instance?.playerBloodPrefab != null)
            {
                Instantiate(
                    StartOfRound.Instance.playerBloodPrefab,
                    _masked.transform.position + Vector3.up * 0.04f,
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            }

            // Activate any built-in body blood decals on the model hierarchy
            foreach (var t in _masked.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    t.gameObject.SetActive(true);
                }
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[BloodyReveal] EruptBlood error: {ex.Message}");
        }
    }

    private IEnumerator StopBloodParticleRoutine()
    {
        yield return new WaitForSeconds(0.75f);
        if (_masked != null && _masked.maskFloodParticle != null)
        {
            _masked.maskFloodParticle.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    // ─── Material Modification ────────────────────────────────────────────────

    private void ApplyBloodyMaterials()
    {
        float intensity = Mathf.Clamp(PhoneyPlugin.BloodIntensity.Value, 0.1f, 2.0f);

        // Dark, fresh arterial blood red:
        Color goreBase = Color.Lerp(new Color(0.45f, 0.08f, 0.08f, 1f), new Color(0.25f, 0.02f, 0.02f, 1f), 0.5f);
        goreBase *= intensity;
        goreBase.a = 1f;

        Color goreEmission = new Color(0.22f, 0.01f, 0.01f, 1f) * intensity;

        foreach (var mat in _tintedMaterials)
        {
            if (mat == null) continue;

            try
            {
                if (mat.HasProperty("_Color"))
                    mat.color = goreBase;

                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", goreBase);

                if (mat.HasProperty("_EmissionColor"))
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", goreEmission);
                }
            }
            catch {}
        }
    }

    // ─── Mask & Eye Glow ─────────────────────────────────────────────────────

    private void IgniteBloodyMask()
    {
        if (_masked == null) return;

        try
        {
            // Unhide mask (so the true face of the killer is unveiled)
            if (_masked.maskTypes != null)
            {
                foreach (var m in _masked.maskTypes)
                {
                    if (m != null) m.SetActive(true);
                }
            }

            // Piercing crimson eye glow
            if (_masked.maskEyesGlowLight != null)
            {
                _masked.maskEyesGlowLight.enabled = true;
                _masked.maskEyesGlowLight.color = new Color(1.0f, 0.05f, 0.05f);
                _masked.maskEyesGlowLight.intensity = 18f;
                _masked.maskEyesGlowLight.range = 8f;
            }

            if (_masked.maskEyesGlow != null)
            {
                foreach (var r in _masked.maskEyesGlow)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                        if (r.material != null)
                        {
                            r.material.color = Color.red;
                            if (r.material.HasProperty("_EmissionColor"))
                            {
                                r.material.EnableKeyword("_EMISSION");
                                r.material.SetColor("_EmissionColor", Color.red * 2f);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[BloodyReveal] IgniteBloodyMask error: {ex.Message}");
        }
    }

    // ─── Procedural Animation: Demonic Neck Snap & Spasm ───────────────────────

    private IEnumerator RevealSpasmRoutine()
    {
        if (_masked == null) yield break;

        _spasmActive = true;
        IsTransforming = true;

        // Force complete standstill during the 1.1s transformation phase
        NavMeshUtil.SafeSetStopped(_masked.agent, true);
        NavMeshUtil.SafeSetVelocity(_masked.agent, Vector3.zero);

        // Trigger the built-in Masked vent spawn / seizing animation on the creature animator
        if (_masked.creatureAnimator != null)
        {
            try
            {
                int spawnHash = Animator.StringToHash("Base Layer.MaskedPlayerSpawn");
                if (_masked.creatureAnimator.HasState(0, spawnHash))
                {
                    _masked.creatureAnimator.Play(spawnHash, 0, 0f);
                }
                else
                {
                    _masked.creatureAnimator.Play("MaskedPlayerSpawn", 0, 0f);
                }
                _masked.creatureAnimator.SetBool("Stunned", true);
                _masked.creatureAnimator.SetTrigger("HitEnemy");
            }
            catch (Exception ex)
            {
                PhoneyPlugin.Logger.LogDebug($"[BloodyReveal] Spawn animation trigger error: {ex.Message}");
            }
        }

        // Phase A: Transformation convulsion & freeze (1.1s)
        // Blood erupts, neck snaps sideways, mask appears, vent spawn seizure animation plays, gives player reaction window!
        yield return new WaitForSeconds(1.1f);

        IsTransforming = false;

        if (_masked == null) yield break;

        // Release animator stun/seizure
        if (_masked.creatureAnimator != null)
        {
            _masked.creatureAnimator.SetBool("Stunned", false);
        }
        _masked.stunNormalizedTimer = 0f;

        // Phase B: Release standstill into reaction window jog with hands raised
        // (Sprint bursts and stamina are regulated dynamically by PhoneyDeceptiveAI)
        NavMeshUtil.SafeSetStopped(_masked.agent, false);
        NavMeshUtil.SafeSetSpeed(_masked.agent, 3.1f);

        bool isServer = Unity.Netcode.NetworkManager.Singleton?.IsServer == true || Unity.Netcode.NetworkManager.Singleton?.IsHost == true;
        if (isServer)
        {
            if (!_masked.handsOut) { _masked.handsOut = true; _masked.SetHandsOutServerRpc(true); }
            if (_masked.running)   { _masked.running  = false; _masked.SetRunningServerRpc(false); }
        }

        // Lingering bodily micro-spasms for another 0.4s while beginning pursuit
        yield return new WaitForSeconds(0.4f);

        _spasmActive = false;
    }

    private void LateUpdate()
    {
        if (_masked == null || _masked.isEnemyDead || !_isRevealed || _headBone == null) return;

        if (!_hasBaseRotations)
        {
            _baseHeadLocalRotation = _headBone.localRotation;
            if (_spineBone != null) _baseSpineLocalRotation = _spineBone.localRotation;
            if (_leftArmBone != null) _baseLeftArmLocalRotation = _leftArmBone.localRotation;
            if (_rightArmBone != null) _baseRightArmLocalRotation = _rightArmBone.localRotation;
            _hasBaseRotations = true;
        }

        // ── 1. Violent High-Speed Jitter during the 0.9s Reveal ───────────────
        if (_spasmActive)
        {
            float freq = 40f;
            float jitterPitch = (Mathf.PerlinNoise(Time.time * freq, 0f) - 0.5f) * 22f;
            float jitterYaw   = (Mathf.PerlinNoise(0f, Time.time * freq) - 0.5f) * 22f;
            float jitterRoll  = (Mathf.PerlinNoise(Time.time * freq, Time.time * freq) - 0.5f) * 35f;

            // Sickening 50° sideways broken-neck angle with severe twitch
            Quaternion snapAngle = Quaternion.Euler(-15f + jitterPitch, jitterYaw, 48f + jitterRoll);
            _headBone.localRotation = _baseHeadLocalRotation * snapAngle;

            if (_spineBone != null)
            {
                float spineJitter = (Mathf.PerlinNoise(Time.time * freq * 0.7f, 10f) - 0.5f) * 10f;
                _spineBone.localRotation = _baseSpineLocalRotation * Quaternion.Euler(spineJitter, 0f, spineJitter * 0.5f);
            }

            if (_leftArmBone != null)
            {
                float armJitter = (Mathf.PerlinNoise(Time.time * freq * 1.1f, 5f) - 0.5f) * 14f;
                _leftArmBone.localRotation = _baseLeftArmLocalRotation * Quaternion.Euler(armJitter, 0f, armJitter);
            }
            if (_rightArmBone != null)
            {
                float armJitter = (Mathf.PerlinNoise(Time.time * freq * 1.1f, 15f) - 0.5f) * 14f;
                _rightArmBone.localRotation = _baseRightArmLocalRotation * Quaternion.Euler(-armJitter, 0f, -armJitter);
            }
        }
        else
        {
            // ── 2. Lingering Creepy Head-Tilt while Chasing ───────────────────
            // Even while running down players, the mimic keeps an eerie 18° broken neck angle
            _headBone.localRotation = _baseHeadLocalRotation * Quaternion.Euler(-5f, 0f, 18f);
        }
    }

    /// <summary>
    /// Resets the disguise if the mimic tactically retreats and re-enters undercover mode.
    /// </summary>
    public void ResetDisguise()
    {
        if (!_isRevealed || _masked == null) return;
        _isRevealed = false;
        _spasmActive = false;

        // Restore baseline bone rotations immediately so head never looks up or remains broken!
        if (_hasBaseRotations)
        {
            if (_headBone != null) _headBone.localRotation = _baseHeadLocalRotation;
            if (_spineBone != null) _spineBone.localRotation = _baseSpineLocalRotation;
            if (_leftArmBone != null) _leftArmBone.localRotation = _baseLeftArmLocalRotation;
            if (_rightArmBone != null) _rightArmBone.localRotation = _baseRightArmLocalRotation;
        }

        // Stop any blood emission particles immediately
        if (_masked.maskFloodParticle != null)
        {
            _masked.maskFloodParticle.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        // Restore original material colors
        foreach (var mat in _tintedMaterials)
        {
            if (mat == null) continue;
            try
            {
                if (_originalColors.TryGetValue(mat, out var c) && mat.HasProperty("_Color"))
                    mat.color = c;
                if (_originalBaseColors.TryGetValue(mat, out var bc) && mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", bc);
                if (mat.HasProperty("_EmissionColor"))
                    mat.DisableKeyword("_EMISSION");
            }
            catch {}
        }

        // Re-hide mask if HideMask is enabled
        if (PhoneyPlugin.HideMask.Value)
        {
            if (_masked.maskTypes != null)
                foreach (var m in _masked.maskTypes)
                    if (m != null) m.SetActive(false);

            if (_masked.maskEyesGlowLight != null)
                _masked.maskEyesGlowLight.enabled = false;

            if (_masked.maskEyesGlow != null)
                foreach (var r in _masked.maskEyesGlow)
                    if (r != null) r.enabled = false;
        }

        // De-activate any body blood decals on the model hierarchy
        foreach (var t in _masked.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name.IndexOf("blood", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                t.gameObject.SetActive(false);
            }
        }

        CacheMaterials();
        PhoneyPlugin.Logger.LogInfo($"[BloodyReveal] '{_masked.gameObject.name}' reset disguise.");
    }

    private static Transform? FindBone(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
}
