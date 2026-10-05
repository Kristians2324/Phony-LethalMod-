using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace Phoney.AI;

/// <summary>
/// Snapshot of a player's equipment while alive, used by the mimic to faithfully copy
/// equipment even after the teammate has died (when vanilla drops all items).
/// </summary>
public class PlayerEquipmentSnapshot
{
    public ulong SteamId { get; set; }
    public string PlayerUsername { get; set; } = "";
    public bool HasWalkie { get; set; }
    public Item? HeldItemDef { get; set; }
    public string? HeldItemName { get; set; }
    public bool IsTwoHanded { get; set; }
    public bool IsFlashlight { get; set; }
    public bool IsProFlashlight { get; set; }
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Manages visual-only prop items held by Masked enemies, perfectly mirroring the copied player.
///
/// Features:
///   1. Equipment Mirroring:
///      • Walkie-talkie on chest: ONLY equipped if the mimicked player actually carries a walkie-talkie!
///        If the player has no walkie, the mimic's chest is completely clean.
///      • Pro-Flashlight vs Standard Flashlight: If the player has a Pro-flashlight, the mimic copies
///        the Pro-flashlight (yellow casing, brighter warm beam). If standard flashlight, copies standard.
///      • Weapons & Tools: Copies held Shotguns, Shovels, Stop signs, Yield signs, Knives, or other tools.
///      • Empty hands: If the player has empty hands/inventory, the mimic has empty hands with natural walking arms.
///   2. Zero Duplication on Death:
///      • All props are purely visual (no GrabbableObject, no NetworkObject, no ScanNode).
///      • On enemy death, all props are immediately destroyed so nothing drops on the ground.
///   3. Authentic Animations:
///      • Two-handed items (Shotgun, Shovel, Signs) set 'HoldingItemsBothHands' = 1f.
///      • One-handed items set 'HoldingItemsRightHand' = 1f.
///      • HandsOut (zombie arms) is strictly forced false while friendly.
/// </summary>
public static class MaskedHeldItemManager
{
    public static readonly Dictionary<ulong, PlayerEquipmentSnapshot> LastKnownPlayerEquipment = new();
    private static float s_lastTrackTime;

    // ─── Tracking Living Players ──────────────────────────────────────────────

    /// <summary>
    /// Continuously tracks living player inventories so if a player dies,
    /// a mimic copying them can still accurately mirror what they were holding before death.
    /// </summary>
    public static void TrackAllLivingPlayers()
    {
        if (Time.time - s_lastTrackTime < 0.25f) return;
        s_lastTrackTime = Time.time;

        var allPlayers = StartOfRound.Instance?.allPlayerScripts;
        if (allPlayers == null) return;

        for (int i = 0; i < allPlayers.Length; i++)
        {
            var p = allPlayers[i];
            if (p == null || !p.isPlayerControlled || p.isPlayerDead || p.playerSteamId == 0) continue;
            TrackPlayer(p);
        }
    }

    private static void TrackPlayer(PlayerControllerB player)
    {
        bool hasWalkie = false;
        Item? heldTool = null;

        // 1. Walkie check in inventory slots
        if (player.ItemSlots != null)
        {
            for (int i = 0; i < player.ItemSlots.Length; i++)
            {
                var slotItem = player.ItemSlots[i];
                if (slotItem?.itemProperties != null)
                {
                    if (slotItem.itemProperties.itemName.IndexOf("walkie", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hasWalkie = true;
                        break;
                    }
                }
            }
        }

        // 2. Currently held item in hand
        if (player.currentlyHeldObjectServer != null && player.currentlyHeldObjectServer.itemProperties != null)
        {
            var held = player.currentlyHeldObjectServer.itemProperties;
            if (held.itemName.IndexOf("walkie", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasWalkie = true;
                heldTool = null; // Walkie goes on chest
            }
            else
            {
                heldTool = held;
            }
        }
        else if (player.ItemSlots != null)
        {
            // Player hand is empty right now. Check if they have tools/weapons in pocket slots.
            Item? bestTool = null;
            int bestPriority = -1;

            for (int i = 0; i < player.ItemSlots.Length; i++)
            {
                var slotItem = player.ItemSlots[i];
                if (slotItem?.itemProperties == null) continue;
                var item = slotItem.itemProperties;
                string name = item.itemName;

                if (name.IndexOf("walkie", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasWalkie = true;
                    continue;
                }

                int prio = 0;
                if (name.IndexOf("shotgun", StringComparison.OrdinalIgnoreCase) >= 0) prio = 6;
                else if (name.IndexOf("shovel", StringComparison.OrdinalIgnoreCase) >= 0) prio = 5;
                else if (name.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0) prio = 4;
                else if (name.IndexOf("knife", StringComparison.OrdinalIgnoreCase) >= 0) prio = 4;
                else if (name.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0) prio = 3;
                else if (name.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0) prio = 2;
                else if (!item.isScrap) prio = 1;

                if (prio > bestPriority)
                {
                    bestPriority = prio;
                    bestTool = item;
                }
            }

            heldTool = bestTool;
        }

        bool isFlashlight = heldTool != null && heldTool.itemName.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isPro = isFlashlight && heldTool!.itemName.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0;

        LastKnownPlayerEquipment[player.playerSteamId] = new PlayerEquipmentSnapshot
        {
            SteamId = player.playerSteamId,
            PlayerUsername = player.playerUsername,
            HasWalkie = hasWalkie,
            HeldItemDef = heldTool,
            HeldItemName = heldTool?.itemName,
            IsTwoHanded = heldTool?.twoHanded == true,
            IsFlashlight = isFlashlight,
            IsProFlashlight = isPro,
            LastUpdated = DateTime.UtcNow
        };
    }

    // ─── Resolve Equipment ───────────────────────────────────────────────────

    public static (bool hasWalkie, Item? heldTool) ResolvePlayerEquipment(PlayerControllerB? player)
    {
        if (player == null) return (false, null);

        // If player is dead, consult our snapshot cache
        if (player.isPlayerDead)
        {
            if (LastKnownPlayerEquipment.TryGetValue(player.playerSteamId, out var snapshot))
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[HeldItem] Using cached equipment for dead teammate '{player.playerUsername}': walkie={snapshot.HasWalkie}, heldTool='{snapshot.HeldItemName ?? "none"}'");
                return (snapshot.HasWalkie, snapshot.HeldItemDef);
            }
        }

        // Live player: update snapshot and resolve
        TrackPlayer(player);
        if (LastKnownPlayerEquipment.TryGetValue(player.playerSteamId, out var snap))
        {
            return (snap.HasWalkie, snap.HeldItemDef);
        }

        return (false, null);
    }

    // ─── Entry Point ─────────────────────────────────────────────────────────

    public static (MaskedHeldItemHolder? holder, Transform? rightHand) TryEquipItems(
        MaskedPlayerEnemy masked, PlayerControllerB? mimickedPlayer)
    {
        if (masked == null || masked.NetworkObject == null) return (null, null);

        Transform? handBone = FindBone(masked.transform, "serverItemHolder")
                           ?? FindBone(masked.transform, "hand.R")
                           ?? FindBone(masked.transform, "RightHand");

        if (handBone != null && !handBone.name.Equals("serverItemHolder", StringComparison.OrdinalIgnoreCase))
        {
            var childHolder = handBone.Find("serverItemHolder");
            if (childHolder == null)
            {
                var holderObj = new GameObject("serverItemHolder");
                holderObj.transform.SetParent(handBone, false);
                Transform? templateHolder = StartOfRound.Instance?.allPlayerScripts?.FirstOrDefault(p => p != null && p.serverItemHolder != null)?.serverItemHolder;
                if (templateHolder != null)
                {
                    holderObj.transform.localPosition = templateHolder.localPosition;
                    holderObj.transform.localRotation = templateHolder.localRotation;
                    holderObj.transform.localScale = templateHolder.localScale;
                }
                else
                {
                    holderObj.transform.localPosition = new Vector3(-0.02f, 0.04f, -0.05f);
                    holderObj.transform.localRotation = Quaternion.identity;
                }
                childHolder = holderObj.transform;
            }
            handBone = childHolder;
        }

        var holder = masked.gameObject.GetComponent<MaskedHeldItemHolder>()
                     ?? masked.gameObject.AddComponent<MaskedHeldItemHolder>();
        holder.Initialize(masked, handBone);

        if (!PhoneyPlugin.EnableHeldItems.Value)
        {
            return (holder, handBone);
        }

        // Clear existing props before equipping
        holder.DestroyAllProps();

        var (hasWalkie, heldToolDef) = ResolvePlayerEquipment(mimickedPlayer);

        // 1. Walkie-talkie on chest — ONLY IF PLAYER HAS ONE!
        if (hasWalkie)
        {
            EquipWalkieTalkie(masked, holder);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo($"[HeldItem] Player has NO walkie-talkie — chest left clean on '{masked.gameObject.name}'.");
        }

        // 2. Held tool / weapon in hand — ONLY IF PLAYER CARRIES ONE!
        if (heldToolDef != null && handBone != null)
        {
            EquipHeldTool(masked, holder, heldToolDef, handBone);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo($"[HeldItem] Player has NO held tool — hands left empty with natural arm swing on '{masked.gameObject.name}'.");
        }

        // Initial animation layer update
        holder.UpdateAnimationLayers(hasRealScrap: false, isAggressive: false);

        return (holder, handBone);
    }

    /// <summary>
    /// Refreshes equipment when the mimic dynamically re-disguises as a new player.
    /// </summary>
    public static void RefreshPlayerEquipment(MaskedPlayerEnemy masked, PlayerControllerB? newPlayer)
    {
        if (masked == null) return;
        PhoneyPlugin.Logger.LogInfo($"[HeldItem] Refreshing equipment on '{masked.gameObject.name}' for new disguise '{newPlayer?.playerUsername ?? "none"}'.");
        TryEquipItems(masked, newPlayer);
    }

    // ─── Walkie-talkie (Chest) ────────────────────────────────────────────────

    private static void EquipWalkieTalkie(MaskedPlayerEnemy masked, MaskedHeldItemHolder holder)
    {
        var itemDef = FindItem("Walkie-talkie") ?? FindItem("walkie");
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
        prop.transform.localPosition = new Vector3(0.08f, 0.05f, 0.10f);
        prop.transform.localRotation = Quaternion.Euler(0f, -15f, 12f);
        prop.transform.localScale = Vector3.one * PhoneyPlugin.HeldItemWalkieScale.Value;

        holder.WalkieProp = prop;
        PhoneyPlugin.Logger.LogInfo($"[HeldItem] Walkie-talkie attached to '{chestBone.name}' on '{masked.gameObject.name}'.");
    }

    // ─── Held Tool (Right Hand) ───────────────────────────────────────────────

    private static void EquipHeldTool(MaskedPlayerEnemy masked, MaskedHeldItemHolder holder, Item itemDef, Transform handBone)
    {
        if (itemDef?.spawnPrefab == null) return;

        string itemName = itemDef.itemName;
        bool isFlashlight = itemName.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isPro = isFlashlight && itemName.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0;
        bool isTwoHanded = itemDef.twoHanded;

        var prop = MakeVisualProp(itemDef, isFlashlight: isFlashlight);
        if (prop == null) return;

        prop.transform.SetParent(handBone, worldPositionStays: false);
        prop.transform.localPosition = itemDef.positionOffset;
        prop.transform.localRotation = Quaternion.Euler(itemDef.rotationOffset);

        // Respect prefab's natural local scale
        Vector3 prefabScale = itemDef.spawnPrefab.transform.localScale;
        if (prefabScale == Vector3.zero) prefabScale = Vector3.one;

        if (isFlashlight && !isPro && Math.Abs(PhoneyPlugin.HeldItemFlashlightScale.Value - 0.050f) > 0.001f)
        {
            prop.transform.localScale = Vector3.one * PhoneyPlugin.HeldItemFlashlightScale.Value;
        }
        else
        {
            prop.transform.localScale = prefabScale;
        }

        holder.HeldToolProp = prop;
        holder.HeldToolDef = itemDef;
        holder.IsTwoHanded = isTwoHanded;
        holder.IsFlashlight = isFlashlight;
        holder.IsProFlashlight = isPro;
        holder.FlashlightLight = prop.GetComponentInChildren<Light>(true);

        // Fire initial animation triggers
        if (masked.creatureAnimator != null)
        {
            if (isTwoHanded)
            {
                masked.creatureAnimator.ResetTrigger("SwitchHoldAnimationTwoHanded");
                masked.creatureAnimator.SetTrigger("SwitchHoldAnimationTwoHanded");
            }
            else
            {
                masked.creatureAnimator.ResetTrigger("SwitchHoldAnimation");
                masked.creatureAnimator.SetTrigger("SwitchHoldAnimation");
            }

            if (!string.IsNullOrEmpty(itemDef.grabAnim))
            {
                try
                {
                    masked.creatureAnimator.SetBool(itemDef.grabAnim, true);
                }
                catch { }
            }
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[HeldItem] Equipped visual prop '{itemName}' on '{handBone.name}' on '{masked.gameObject.name}' (twoHanded={isTwoHanded}, isFlashlight={isFlashlight}, isPro={isPro}).");
    }

    // ─── Visual Prop Factory ─────────────────────────────────────────────────

    /// <summary>
    /// Instantiates itemDef.spawnPrefab, removes scan nodes, NetworkObjects, audio sources,
    /// colliders and rigidbodies, leaving only visual renderers and lights enabled.
    /// Purely cosmetic: no item duplication on death, zero dropped loot.
    /// </summary>
    public static GameObject? MakeVisualProp(Item itemDef, bool isFlashlight)
    {
        try
        {
            var prop = UnityEngine.Object.Instantiate(itemDef.spawnPrefab);
            prop.name = $"[PhoneyProp] {itemDef.itemName}";

            // 1. Destroy ScanNodeProperties so scanner bracket doesn't show up on right-click scan
            foreach (var scanNode in prop.GetComponentsInChildren<ScanNodeProperties>(true))
            {
                UnityEngine.Object.Destroy(scanNode);
            }

            // 2. Destroy NetworkObject so it has no network ID and doesn't sync
            foreach (var netObj in prop.GetComponentsInChildren<NetworkObject>(true))
            {
                UnityEngine.Object.Destroy(netObj);
            }

            // 3. Disable all MonoBehaviours (GrabbableObject, PhysicsProp, etc.)
            foreach (var mb in prop.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
                mb.enabled = false;

            // 4. Re-enable renderers
            foreach (var r in prop.GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = true;

            // 5. Lights calibration
            bool isPro = isFlashlight && itemDef.itemName.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var l in prop.GetComponentsInChildren<Light>(includeInactive: true))
            {
                if (isFlashlight)
                {
                    l.enabled = true;
                    if (isPro)
                    {
                        l.intensity = Mathf.Clamp(l.intensity, 22f, 36f);
                        l.range = Mathf.Clamp(l.range, 30f, 48f);
                        l.color = new Color(1.0f, 0.96f, 0.88f); // Bright daylight warm beam
                    }
                    else
                    {
                        l.intensity = Mathf.Clamp(l.intensity, 8f, 18f);
                        l.range = Mathf.Clamp(l.range, 12f, 25f);
                        l.color = new Color(0.9f, 1.0f, 0.85f); // Normal standard tint
                    }
                    l.shadows = LightShadows.None;
                }
                else
                {
                    l.enabled = false;
                }
            }

            // 6. Disable AudioSources
            foreach (var a in prop.GetComponentsInChildren<AudioSource>(true))
            {
                a.Stop();
                a.enabled = false;
            }

            // 7. Static physics — no movement, no collision
            foreach (var rb in prop.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.detectCollisions = false;
            }
            foreach (var col in prop.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

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
