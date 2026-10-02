using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;

namespace Phoney.Compat;

/// <summary>
/// Soft-dependency compatibility layer for MoreCompany.
///
/// Uses MoreCompany's native CloneCosmeticsToNonPlayer pipeline to clone cosmetics
/// onto the Masked metarig with proper 0.38f bone scaling and ParentType.Masked mapping.
/// </summary>
public static class MoreCompanyCompat
{
    private static bool _checkedForMoreCompany;
    private static bool _isMoreCompanyPresent;

    private static Type? _cosmeticPatchesType;
    private static MethodInfo? _cloneCosmeticsMethod;
    private static Type? _parentTypeEnum;
    private static FieldInfo? _playerIdsAndCosmeticsField;

    private static Type? _cosmeticAppType;
    private static FieldInfo? _spawnedCosmeticIdsField;
    private static FieldInfo? _spawnedCosmeticsField;
    private static MethodInfo? _clearCosmeticsMethod;

    public static bool IsPresent
    {
        get
        {
            if (!_checkedForMoreCompany) Detect();
            return _isMoreCompanyPresent;
        }
    }

    /// <summary>
    /// Copies MoreCompany cosmetics from <paramref name="player"/> onto <paramref name="masked"/>.
    /// Supports any player in the lobby, local or remote.
    /// </summary>
    public static void CopyCosmetics(PlayerControllerB player, MaskedPlayerEnemy masked)
    {
        if (!IsPresent || player == null || masked == null) return;

        try
        {
            Transform? metarig = masked.transform.Find("ScavengerModel")?.Find("metarig")
                                 ?? FindMetarig(masked.transform);
            if (metarig == null)
            {
                PhoneyPlugin.Logger.LogWarning("[MoreCompanyCompat] metarig not found on Masked enemy.");
                return;
            }

            int clientId = (int)player.playerClientId;

            // Ensure playerIdsAndCosmetics contains this player's cosmetic IDs
            EnsurePlayerCosmeticsRegistered(player, clientId);

            // Invoke MoreCompany.CosmeticPatches.CloneCosmeticsToNonPlayer(ParentType.Masked (2), metarig, clientId, false)
            if (_cloneCosmeticsMethod != null && _parentTypeEnum != null)
            {
                object parentTypeMasked = Enum.ToObject(_parentTypeEnum, 2); // 2 = ParentType.Masked
                object? result = _cloneCosmeticsMethod.Invoke(null, new object[] { parentTypeMasked, metarig, clientId, false });
                bool success = result is bool b && b;

                // Update renderer lists on EnemyAI so Unity renders the new cosmetics properly
                masked.skinnedMeshRenderers = masked.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
                masked.meshRenderers = masked.gameObject.GetComponentsInChildren<MeshRenderer>();

                PhoneyPlugin.Logger.LogInfo(
                    $"[MoreCompanyCompat] Cloned cosmetics for '{player.playerUsername}' (clientId {clientId}) onto Masked '{masked.gameObject.name}'. Success={success}");
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogWarning($"[MoreCompanyCompat] Failed to copy cosmetics: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes all MoreCompany cosmetics currently attached to <paramref name="masked"/>.
    /// Used when the mimic transforms into its aggressive monstrous form.
    /// </summary>
    public static void ClearCosmetics(MaskedPlayerEnemy masked)
    {
        if (!IsPresent || masked == null) return;
        try
        {
            Transform? metarig = masked.transform.Find("ScavengerModel")?.Find("metarig")
                                 ?? FindMetarig(masked.transform);
            if (metarig == null) return;

            if (_cosmeticAppType != null && _clearCosmeticsMethod != null)
            {
                var cosApp = metarig.GetComponent(_cosmeticAppType);
                if (cosApp != null)
                {
                    _clearCosmeticsMethod.Invoke(cosApp, null);
                    masked.skinnedMeshRenderers = masked.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
                    masked.meshRenderers = masked.gameObject.GetComponentsInChildren<MeshRenderer>();
                    PhoneyPlugin.Logger.LogInfo($"[MoreCompanyCompat] Cleared cosmetics on Masked '{masked.gameObject.name}'.");
                }
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[MoreCompanyCompat] ClearCosmetics error: {ex.Message}");
        }
    }

    private static void EnsurePlayerCosmeticsRegistered(PlayerControllerB player, int clientId)
    {
        if (_playerIdsAndCosmeticsField == null) return;

        try
        {
            var dict = _playerIdsAndCosmeticsField.GetValue(null) as IDictionary;
            if (dict != null && !dict.Contains(clientId))
            {
                // Extract from the player's CosmeticApplication if present
                if (_cosmeticAppType != null && _spawnedCosmeticIdsField != null)
                {
                    var playerCosApp = player.GetComponentInChildren(_cosmeticAppType);
                    if (playerCosApp != null)
                    {
                        var ids = _spawnedCosmeticIdsField.GetValue(playerCosApp) as List<string>;
                        if (ids != null && ids.Count > 0)
                        {
                            dict[clientId] = new List<string>(ids);
                            PhoneyPlugin.Logger.LogInfo($"[MoreCompanyCompat] Seeded {ids.Count} cosmetic IDs for clientId {clientId} into MoreCompany registry.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[MoreCompanyCompat] EnsurePlayerCosmeticsRegistered error: {ex.Message}");
        }
    }

    private static Transform? FindMetarig(Transform root)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name.Equals("metarig", StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    private static void Detect()
    {
        _checkedForMoreCompany = true;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.GetName().Name.Equals("MoreCompany", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                _cosmeticPatchesType = asm.GetType("MoreCompany.CosmeticPatches");
                _parentTypeEnum = asm.GetType("MoreCompany.Cosmetics.ParentType");
                _cosmeticAppType = asm.GetType("MoreCompany.Cosmetics.CosmeticApplication");

                var mainClassType = asm.GetType("MoreCompany.MainClass");
                if (mainClassType != null)
                {
                    _playerIdsAndCosmeticsField = mainClassType.GetField("playerIdsAndCosmetics",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }

                if (_cosmeticAppType != null)
                {
                    _spawnedCosmeticIdsField = _cosmeticAppType.GetField("spawnedCosmeticsIds",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _spawnedCosmeticsField = _cosmeticAppType.GetField("spawnedCosmetics",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _clearCosmeticsMethod = _cosmeticAppType.GetMethod("ClearCosmetics",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_cosmeticPatchesType != null && _parentTypeEnum != null)
                {
                    _cloneCosmeticsMethod = _cosmeticPatchesType.GetMethod("CloneCosmeticsToNonPlayer",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }

                _isMoreCompanyPresent = _cosmeticPatchesType != null
                                        && _cloneCosmeticsMethod != null
                                        && _parentTypeEnum != null;

                PhoneyPlugin.Logger.LogInfo(
                    _isMoreCompanyPresent
                        ? "[MoreCompanyCompat] MoreCompany detected — native cosmetic cloning pipeline active."
                        : "[MoreCompanyCompat] MoreCompany detected but API signature differed — cosmetic sync disabled.");
            }
            catch (Exception ex)
            {
                PhoneyPlugin.Logger.LogWarning($"[MoreCompanyCompat] Detection failed: {ex.Message}");
            }

            break;
        }

        if (!_isMoreCompanyPresent && !_checkedForMoreCompany)
            PhoneyPlugin.Logger.LogInfo("[MoreCompanyCompat] MoreCompany not installed — cosmetics will not be synced.");
    }
}
