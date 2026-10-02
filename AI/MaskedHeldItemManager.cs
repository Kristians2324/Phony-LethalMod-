using System;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Manages visual-only prop items held by Masked enemies.
///
/// Layout (always):
///   • Walkie-talkie — clipped to upper chest (spine.003). 100% of the time, because
///     most players carry one and it's the first thing friends notice.
///
/// Layout (per-enemy random):
///   • Flashlight — held in right hand, light on and casting. Default 40% chance.
///     Returns a reference so MaskedPropSwitcher can toggle it during loot-carry swaps.
///
/// All props are purely cosmetic: no NetworkObject.Spawn(), no GrabbableObject tracking,
/// no loot drop on death. Each client uses NetworkObjectId as a deterministic seed so
/// all players see the same prop without any extra network traffic.
/// </summary>
public static class MaskedHeldItemManager
{
    // ─── Entry point ─────────────────────────────────────────────────────────

    public static (GameObject? flashlightProp, Transform? rightHand) TryEquipItems(
        MaskedPlayerEnemy masked, PlayerControllerB mimickedPlayer)
    {
        if (!PhoneyPlugin.EnableHeldItems.Value) return (null, null);
        if (masked.NetworkObject == null) return (null, null);

        var rng = new System.Random((int)(masked.NetworkObject.NetworkObjectId % int.MaxValue));

        // ── 1. Walkie-talkie on chest — always ────────────────────────────────
        EquipWalkieTalkie(masked);

        // ── 2. Flashlight in hand — probabilistic ─────────────────────────────
        double flashRoll = rng.NextDouble() * 100.0;
        if (flashRoll >= PhoneyPlugin.HeldItemFlashlightChance.Value)
            return (null, null);

        var (flashlightProp, rightHand) = EquipFlashlight(masked);
        return (flashlightProp, rightHand);
    }

    // ─── Walkie-talkie (always on chest) ─────────────────────────────────────

    private static void EquipWalkieTalkie(MaskedPlayerEnemy masked)
    {
        var itemDef = FindItem("walkie");
        if (itemDef?.spawnPrefab == null)
        {
            PhoneyPlugin.Logger.LogWarning("[HeldItem] Walkie-talkie item definition not found.");
            return;
        }

        Transform? chestBone = FindBone(masked.transform, "spine.003")
                               ?? FindBone(masked.transform, "chest");
        if (chestBone == null)
        {
            PhoneyPlugin.Logger.LogWarning("[HeldItem] Chest bone not found on Masked.");
            return;
        }

        var prop = MakeVisualProp(itemDef, isFlashlight: false);
        if (prop == null) return;

        prop.transform.SetParent(chestBone, worldPositionStays: false);

        // Position: slightly to the right chest, tilted like a holstered walkie
        prop.transform.localPosition = new Vector3(0.08f, 0.05f, 0.10f);
        prop.transform.localRotation = Quaternion.Euler(0f, -15f, 12f);
        prop.transform.localScale = Vector3.one * PhoneyPlugin.HeldItemWalkieScale.Value;

        PhoneyPlugin.Logger.LogInfo($"[HeldItem] Walkie-talkie attached to '{chestBone.name}' on '{masked.gameObject.name}'. localScale={PhoneyPlugin.HeldItemWalkieScale.Value:F3}");
    }

    // ─── Flashlight (probabilistic) ───────────────────────────────────────────

    private static (GameObject? prop, Transform? bone) EquipFlashlight(MaskedPlayerEnemy masked)
    {
        var itemDef = FindItem("flashlight");
        if (itemDef?.spawnPrefab == null)
        {
            PhoneyPlugin.Logger.LogWarning("[HeldItem] Flashlight item definition not found.");
            return (null, null);
        }

        // serverItemHolder is the hand attachment point used by vanilla grab logic
        Transform? handBone = FindBone(masked.transform, "serverItemHolder")
                              ?? FindBone(masked.transform, "hand.R");
        if (handBone == null)
        {
            PhoneyPlugin.Logger.LogWarning("[HeldItem] Hand bone not found on Masked.");
            return (null, null);
        }

        var prop = MakeVisualProp(itemDef, isFlashlight: true);
        if (prop == null) return (null, null);

        prop.transform.SetParent(handBone, worldPositionStays: false);
        prop.transform.localPosition = new Vector3(0f, 0f, 0.04f);
        prop.transform.localRotation = Quaternion.Euler(-20f, 0f, 0f);
        prop.transform.localScale = Vector3.one * PhoneyPlugin.HeldItemFlashlightScale.Value;

        PhoneyPlugin.Logger.LogInfo($"[HeldItem] Flashlight attached to '{handBone.name}' on '{masked.gameObject.name}'. localScale={PhoneyPlugin.HeldItemFlashlightScale.Value:F3}");
        return (prop, handBone);
    }

    // ─── Generic visual prop factory ─────────────────────────────────────────

    /// <summary>
    /// Instantiates itemDef.spawnPrefab, strips all scripts and physics,
    /// leaves Renderers and (if isFlashlight) Lights enabled.
    /// Resets the prefab's own root scale to Vector3.one so our localScale
    /// values are the single source of truth — no compound scaling.
    /// </summary>
    public static GameObject? MakeVisualProp(Item itemDef, bool isFlashlight)
    {
        try
        {
            var prop = UnityEngine.Object.Instantiate(itemDef.spawnPrefab);
            prop.name = $"[PhoneyProp] {itemDef.itemName}";

            // ── Reset the prefab's own baked scale so it doesn't compound ─────
            // Item prefabs often have a non-1 root scale baked in at authoring time.
            // We reset it here so our localScale assignment below is the only scale.
            prop.transform.localScale = Vector3.one;

            // Kill all game logic — we only want the mesh renderer, nothing else
            foreach (var mb in prop.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
                mb.enabled = false;

            // Re-enable renderers
            foreach (var r in prop.GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = true;

            // Lights: calibrated for flashlights, off for everything else
            foreach (var l in prop.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (isFlashlight)
                {
                    l.enabled  = true;
                    l.intensity = Mathf.Clamp(l.intensity, 8f, 20f);
                    l.range     = Mathf.Clamp(l.range, 10f, 30f);
                    l.shadows   = LightShadows.None;
                }
                else
                {
                    l.enabled = false;
                }
            }

            // Static physics — no movement, no collision
            foreach (var rb in prop.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic      = true;
                rb.useGravity       = false;
                rb.detectCollisions = false;
            }
            foreach (var col in prop.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            PhoneyPlugin.Logger.LogInfo($"[HeldItem] Created visual prop for '{itemDef.itemName}' (isFlashlight={isFlashlight})");
            return prop;
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogWarning($"[HeldItem] Error creating prop for '{itemDef?.itemName}': {ex.Message}");
            return null;
        }
    }

    // ─── Utility ──────────────────────────────────────────────────────────────

    public static Item? FindItem(string searchName)
    {
        var list = StartOfRound.Instance?.allItemsList?.itemsList;
        if (list == null) return null;
        return list.FirstOrDefault(i =>
                   i?.itemName?.Equals(searchName, StringComparison.OrdinalIgnoreCase) == true)
               ?? list.FirstOrDefault(i =>
                   i?.itemName?.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static Transform? FindBone(Transform root, string boneName)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
            if (t.name.Equals(boneName, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
}
