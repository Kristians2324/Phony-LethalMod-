using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Phoney.Network;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Phoney.AI;

/// <summary>
/// Manages realistic scrap looting for the Masked mimic.
/// 
/// Behaviors:
///   1. Scans for real unheld GrabbableObject scrap items lying on the floor in nearby rooms.
///   2. Navigates to scrap, pauses to "pick it up", and physically holds it in its hands.
///   3. Hauls the scrap toward the facility Main Entrance / Exit door just like real crewmates.
///   4. Drops the loot in a collection pile near the door, then returns to search for more.
///   5. Synchronizes item pickup and drops across the network so all players see real loot moving.
/// </summary>
public class MaskedScrapManager : MonoBehaviour
{
    private MaskedPlayerEnemy? _masked;
    private Transform? _rightHandBone;
    private Transform? _beltBone;
    private Transform? _chestBone;
    private Transform? _heldItemAnchor;

    public Transform? HeldItemAnchor => _heldItemAnchor;

    public readonly List<GrabbableObject> CarriedItems = new();
    public GrabbableObject? HeldScrap => CarriedItems.Count > 0 ? CarriedItems[0] : null;
    public bool HasHeldScrap => CarriedItems.Count > 0;
    public int CarriedCount => CarriedItems.Count;
    public bool IsHoldingTwoHanded => HeldScrap?.itemProperties?.twoHanded == true;
    public bool IsCarryingTwoHanded => CarriedItems.Any(i => i?.itemProperties?.twoHanded == true);

    /// <summary>
    /// Computes the mimic's total carried weight, mirroring vanilla Lethal Company mechanics.
    /// Base weight is 1.0f (0 lbs). Additional items add their extra weight (weight - 1.0f).
    /// </summary>
    public float TotalCarryWeight
    {
        get
        {
            float total = 1.0f;
            for (int i = 0; i < CarriedItems.Count; i++)
            {
                var item = CarriedItems[i];
                if (item != null && item.itemProperties != null)
                {
                    total += Mathf.Max(0f, item.itemProperties.weight - 1.0f);
                }
            }
            return total;
        }
    }

    /// <summary>
    /// Checks if the mimic can pick up additional scrap items.
    /// Limits inventory to MaxCarriedScrapCount (default 4) and prevents multi-item carry if holding a two-handed item.
    /// </summary>
    public bool CanPickUpMoreScrap()
    {
        // If currently holding a two-handed item, hands are completely occupied - CANNOT pick up anything else!
        if (HeldScrap != null && HeldScrap.itemProperties != null && HeldScrap.itemProperties.twoHanded)
            return false;

        // If any carried item is two-handed, inventory is locked to only that item
        for (int i = 0; i < CarriedItems.Count; i++)
        {
            if (CarriedItems[i]?.itemProperties != null && CarriedItems[i].itemProperties.twoHanded)
                return false;
        }

        int maxSlots = Mathf.Clamp(PhoneyPlugin.MaxCarriedScrapCount.Value, 1, 4);
        if (CarriedItems.Count >= maxSlots) return false;

        return true;
    }

    private float _nextScanTime;
    private readonly List<GrabbableObject> _ignoredItems = new();

    public void Initialize(MaskedPlayerEnemy masked, Transform? rightHandBone)
    {
        _masked = masked;
        _rightHandBone = rightHandBone ?? FindHandBone(masked.transform);
        if (_rightHandBone != null && !_rightHandBone.name.Equals("serverItemHolder", StringComparison.OrdinalIgnoreCase))
        {
            var childHolder = _rightHandBone.Find("serverItemHolder");
            if (childHolder != null) _rightHandBone = childHolder;
        }
        _beltBone = FindBeltBone(masked.transform);
        _chestBone = FindChestBone(masked.transform);

        if (_chestBone != null)
        {
            var anchor = new GameObject("[Phoney] HeldScrapAnchor");
            anchor.transform.SetParent(_chestBone, false);
            // Default position: held up in front of chest/chin at face level like a player
            anchor.transform.localPosition = new Vector3(0.04f, 0.18f, 0.38f);
            anchor.transform.localRotation = Quaternion.Euler(12f, 0f, 0f);
            _heldItemAnchor = anchor.transform;
        }

        PhoneyPlugin.Logger.LogInfo($"[ScrapManager] Initialized on '{masked.gameObject.name}'. RightHandBone={_rightHandBone?.name ?? "none"}, BeltBone={_beltBone?.name ?? "none"}, ChestBone={_chestBone?.name ?? "none"}");
    }

    private static Transform? FindChestBone(Transform root)
    {
        return root.GetComponentsInChildren<Transform>(includeInactive: true)
            .FirstOrDefault(t => t.name.Equals("spine.003", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("spine.002", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("chest", StringComparison.OrdinalIgnoreCase));
    }

    private static Transform? FindBeltBone(Transform root)
    {
        return root.GetComponentsInChildren<Transform>(includeInactive: true)
            .FirstOrDefault(t => t.name.Equals("spine", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("spine.001", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
    }

    private static Transform? FindHandBone(Transform root)
    {
        return root.GetComponentsInChildren<Transform>(includeInactive: true)
            .FirstOrDefault(t => t.name.Equals("serverItemHolder", StringComparison.OrdinalIgnoreCase))
            ?? root.GetComponentsInChildren<Transform>(includeInactive: true)
            .FirstOrDefault(t => t.name.Equals("hand.R", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("RightHand", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("RightHandSlot", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Multi-layer key detection: prevents mimics from ever targeting, picking up, or tainting keys.
    /// Checks runtime type (KeyItem), type name, item name, and gameObject name for "key".
    /// </summary>
    public static bool IsKeyItem(GrabbableObject? item)
    {
        if (item == null) return false;

        // Layer 1: Vanilla KeyItem class or any subclass
        if (item is KeyItem) return true;

        // Layer 2: Modded key types that don't inherit from KeyItem
        string typeName = item.GetType().Name;
        if (typeName.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // Layer 3: Item properties name contains "key"
        if (item.itemProperties != null &&
            item.itemProperties.itemName != null &&
            item.itemProperties.itemName.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Layer 4: GameObject name contains "key"  
        if (item.gameObject.name.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return false;
    }

    /// <summary>
    /// Global registry of scrap network object IDs that have already been gathered or dropped by any mimic.
    /// Once an item is dropped in the collection pile, it is permanently recorded so NO mimic ever picks it up again.
    /// </summary>
    public static readonly HashSet<ulong> GloballyProcessedScrapIds = new();
    public static readonly HashSet<ulong> TaintedScrapIds = new();

    /// <summary>
    /// Checks if a position is near any EntranceTeleport door.
    /// </summary>
    public static bool IsNearEntranceDoor(Vector3 position, float radius = 7.0f)
    {
        try
        {
            var teleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            if (teleports != null)
            {
                foreach (var t in teleports)
                {
                    if (t == null) continue;
                    Vector3 doorPos = GetDoorPosition(t);
                    if (Vector3.Distance(position, doorPos) <= radius)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Checks if a position is near the Ship's secure drop area.
    /// </summary>
    public static bool IsNearShip(Vector3 position, float radius = 8.0f)
    {
        try
        {
            if (StartOfRound.Instance?.shipDoorAudioSource != null)
            {
                if (Vector3.Distance(position, StartOfRound.Instance.shipDoorAudioSource.transform.position) <= radius)
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Checks if a position is near any EntranceTeleport door or the Ship collection zone.
    /// </summary>
    public static bool IsNearEntranceOrShip(Vector3 position)
    {
        return IsNearEntranceDoor(position) || IsNearShip(position);
    }

    /// <summary>
    /// Finds the EntranceTeleport matching the specified direction.
    /// </summary>
    public static EntranceTeleport? FindDoor(bool wantEntranceToBuilding)
    {
        try
        {
            var teleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            if (teleports == null || teleports.Length == 0) return null;

            var match = teleports.FirstOrDefault(t => t != null && t.entranceId == 0 && t.isEntranceToBuilding == wantEntranceToBuilding);
            match ??= teleports.FirstOrDefault(t => t != null && t.isEntranceToBuilding == wantEntranceToBuilding);
            return match;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the position of an EntranceTeleport door.
    /// Prioritizes the actual interact trigger (door knob / handle) where players stand.
    /// </summary>
    public static Vector3 GetDoorPosition(EntranceTeleport? door)
    {
        if (door == null) return Vector3.zero;
        if (door.triggerScript != null)
            return door.triggerScript.transform.position;
        if (door.entrancePoint != null)
            return door.entrancePoint.position;
        return door.transform.position;
    }

    /// <summary>
    /// Scans the map for unheld, reachable scrap items within maxDistance of the mimic.
    /// Respects interior vs. exterior environment boundaries.
    /// Outside mimics can collect scrap sitting near the outside entrance door to haul it to the Ship!
    /// </summary>
    public GrabbableObject? FindNearbyReachableScrap(float maxDistance = 22f, bool forceScan = false)
    {
        if (_masked == null || _masked.isEnemyDead) return null;
        if (!forceScan && Time.time < _nextScanTime) return null;
        _nextScanTime = Time.time + 2.5f; // Scan every 2.5s to save CPU

        var allItems = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
        if (allItems == null || allItems.Length == 0) return null;

        Vector3 myPos = transform.position;
        GrabbableObject? bestItem = null;
        float bestDist = maxDistance;

        var navPath = new NavMeshPath();

        foreach (var item in allItems)
        {
            if (item == null) continue;
            if (item.NetworkObject != null && GloballyProcessedScrapIds.Contains(item.NetworkObject.NetworkObjectId)) continue;
            if (_ignoredItems.Contains(item)) continue;
            if (item.itemProperties == null) continue;
            // Only target actual scrap quota items — never touch keys!
            if (!item.itemProperties.isScrap) continue;
            if (IsKeyItem(item)) continue;

            // Two-handed inventory rules (matching real player behavior):
            // - If already carrying ANY item, skip two-handed items (can't carry both)
            // - If holding a two-handed item, skip everything (hands are full)
            if (CarriedItems.Count > 0)
            {
                if (item.itemProperties.twoHanded) continue; // Can't add a two-handed item on top
                if (HeldScrap?.itemProperties?.twoHanded == true) continue; // Hands full with two-handed
            }

            if (item.isHeld || item.isPocketed || item.deactivated || !item.grabbable) continue;

            // Ensure matching environment: inside facility vs exterior
            if (item.isInFactory == _masked.isOutside) continue;

            // Items already safely delivered to the ship are strictly untouchable!
            if (IsNearShip(item.transform.position)) continue;

            // Inside mimics do not re-grab scrap already dropped at the interior door.
            // But outside mimics CAN grab scrap left outside near the door to haul it back to the ship!
            if (!_masked.isOutside && IsNearEntranceDoor(item.transform.position)) continue;

            float d = Vector3.Distance(myPos, item.transform.position);
            if (d > bestDist) continue;

            // Check if item is reachable on NavMesh (not trapped behind locked door or void)
            int mask = _masked.agent != null ? _masked.agent.areaMask : NavMesh.AllAreas;
            if (NavMesh.CalculatePath(myPos, item.transform.position, mask, navPath) &&
                navPath.status == NavMeshPathStatus.PathComplete)
            {
                bestDist = d;
                bestItem = item;
            }
        }

        if (bestItem != null)
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' scan detected reachable scrap '{bestItem.itemProperties?.itemName ?? "Item"}' (${bestItem.scrapValue}) at {bestDist:F1}m (twoHanded: {bestItem.itemProperties?.twoHanded == true}).");
        }

        return bestItem;
    }

    /// <summary>
    /// Finds the entrance / exit position suitable for dropping off hauled scrap.
    /// For inside facility: finds the main exit door.
    /// For outside: finds the main entrance door or the hangar ship.
    /// </summary>
    public Vector3 GetEntranceDropPosition()
    {
        if (_masked == null) return transform.position;

        try
        {
            // Outside: haul scrap toward the Ship drop zone!
            if (_masked.isOutside && StartOfRound.Instance?.shipDoorAudioSource != null)
            {
                return StartOfRound.Instance.shipDoorAudioSource.transform.position;
            }

            var teleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            if (teleports != null && teleports.Length > 0)
            {
                // If inside facility, find the main interior exit door (isEntranceToBuilding == false)
                var match = teleports.FirstOrDefault(t => 
                    t != null && 
                    t.entranceId == 0 && 
                    !t.isEntranceToBuilding);

                match ??= teleports.FirstOrDefault(t => t != null && !t.isEntranceToBuilding);

                if (match != null)
                {
                    Vector3 doorPos = GetDoorPosition(match);
                    // Slightly offset from the door so loot isn't right on the trigger
                    return doorPos + match.transform.forward * 1.5f;
                }
            }

            // Fallback for outside if ship wasn't found first, or inside fallback
            if (StartOfRound.Instance?.shipDoorAudioSource != null)
            {
                return StartOfRound.Instance.shipDoorAudioSource.transform.position;
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[ScrapManager] GetEntranceDropPosition error: {ex.Message}");
        }

        return transform.position;
    }

    /// <summary>
    /// Picks up a real scrap item into the mimic's hands/inventory and syncs to all clients.
    /// Supports carrying multiple items up to MaxCarriedScrapCount (default 4).
    /// </summary>
    public bool PickUpRealScrap(GrabbableObject scrap)
    {
        if (_masked == null || scrap == null) return false;
        if (scrap.isHeld || scrap.isPocketed) return false;
        if (!CanPickUpMoreScrap()) return false;

        // NEVER pick up keys — they are reserved for player facility access!
        if (IsKeyItem(scrap))
        {
            string scrapName = scrap.itemProperties?.itemName ?? scrap.gameObject.name;
            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' refused to touch key '{scrapName}'. Keys are reserved for player facility access!");
            return false;
        }

        // Only pick up real scrap quota items
        if (scrap.itemProperties != null && !scrap.itemProperties.isScrap) return false;

        // If carrying items already, cannot grab a two-handed item!
        if (CarriedItems.Count > 0 && scrap.itemProperties != null && scrap.itemProperties.twoHanded)
            return false;

        // If holding a two-handed item, cannot grab any more items!
        if (HeldScrap != null && HeldScrap.itemProperties != null && HeldScrap.itemProperties.twoHanded)
            return false;

        ExecuteGrabLocally(scrap);

        // Apply flat value reduction (-20) and corrupt item name when touched by a mimic
        if (PhoneyPlugin.EnableMimicTouchReduction.Value && scrap.NetworkObject != null)
        {
            ulong scrapNetId = scrap.NetworkObject.NetworkObjectId;
            if (!TaintedScrapIds.Contains(scrapNetId))
            {
                TaintedScrapIds.Add(scrapNetId);
                int reduction = Mathf.Max(1, PhoneyPlugin.MimicTouchValueReduction.Value);
                int oldValue = scrap.scrapValue;
                bool isScrap = scrap.itemProperties != null && scrap.itemProperties.isScrap;
                int newValue = isScrap && scrap.scrapValue > 1 ? Mathf.Max(1, scrap.scrapValue - reduction) : scrap.scrapValue;
                string originalName = scrap.itemProperties?.itemName ?? "Item";
                string distortedName = GenerateDistortedName(originalName);

                ApplyTaintLocally(scrap, newValue, distortedName);

                PhoneyNetworkManager.Instance.BroadcastItemTaint(scrapNetId, newValue, distortedName);

                PhoneyPlugin.Logger.LogInfo(
                    $"[ScrapManager] Mimic touch corrupted item '{originalName}' -> '{distortedName}', value: ${oldValue} -> ${newValue} (loss: -${(isScrap && scrap.scrapValue > 1 ? reduction : 0)})");
            }
        }

        // Broadcast to clients
        if (_masked.NetworkObject != null && scrap.NetworkObject != null)
        {
            PhoneyNetworkManager.Instance.BroadcastItemGrab(
                _masked.NetworkObject.NetworkObjectId,
                scrap.NetworkObject.NetworkObjectId);
        }

        float extraLbs = (TotalCarryWeight - 1.0f) * 105f;
        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] '{_masked.gameObject.name}' picked up real scrap '{scrap.itemProperties?.itemName ?? "Item"}' (Val: ${scrap.scrapValue}, Slot: {CarriedItems.Count}/{Mathf.Clamp(PhoneyPlugin.MaxCarriedScrapCount.Value, 1, 4)}, Weight: {TotalCarryWeight:F2}x [+{extraLbs:F0} lbs], TwoHanded: {scrap.itemProperties?.twoHanded == true}). Attached to chest anchor at chin/face level.");

        return true;
    }

    /// <summary>
    /// Counts unheld scrap lying on the floor near the elevator bottom station.
    /// </summary>
    public static int CountScrapNearElevatorBottom(Vector3 elevatorBottomPos, float radius = 9.0f)
    {
        int count = 0;
        try
        {
            var allProps = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            if (allProps != null)
            {
                foreach (var prop in allProps)
                {
                    if (prop == null || prop.isHeld || prop.isPocketed || prop.deactivated) continue;
                    if (!prop.itemProperties.isScrap) continue;
                    if (Vector3.Distance(prop.transform.position, elevatorBottomPos) <= radius)
                    {
                        count++;
                    }
                }
            }
        }
        catch { }
        return count;
    }

    /// <summary>
    /// Drops all carried scrap items in a neat grouping at the target floor position and syncs to all clients.
    /// </summary>
    public void DropRealScrap(Vector3 targetFloorPos, bool isElevatorStaged = false, bool isLadderStaged = false)
    {
        if (CarriedItems.Count == 0 || _masked == null) return;

        bool inShip = StartOfRound.Instance != null && StartOfRound.Instance.shipBounds != null 
                      && StartOfRound.Instance.shipBounds.bounds.Contains(targetFloorPos);
        if (!inShip && IsNearShip(targetFloorPos, 5.0f)) inShip = true;

        var itemsToDrop = CarriedItems.ToList();
        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] '{_masked.gameObject.name}' dropping {itemsToDrop.Count} carried item(s) at {targetFloorPos} (isElevatorStaged={isElevatorStaged}, isLadderStaged={isLadderStaged}, inShip={inShip}).");

        for (int i = 0; i < itemsToDrop.Count; i++)
        {
            var scrap = itemsToDrop[i];
            if (scrap == null) continue;

            // Slightly stagger drop position so multiple items don't overlap into each other
            Vector3 itemFloorPos = targetFloorPos;
            if (i > 0)
            {
                Vector3 spread = UnityEngine.Random.insideUnitSphere * 0.45f;
                spread.y = 0;
                itemFloorPos += spread;
            }

            ExecuteDropLocally(scrap, itemFloorPos, isElevatorStaged, isLadderStaged);

            // Broadcast to clients (isElevatorStaged / isLadderStaged both indicate temporary staging)
            if (_masked.NetworkObject != null && scrap.NetworkObject != null)
            {
                PhoneyNetworkManager.Instance.BroadcastItemDrop(
                    _masked.NetworkObject.NetworkObjectId,
                    scrap.NetworkObject.NetworkObjectId,
                    itemFloorPos,
                    isElevatorStaged || isLadderStaged);
            }

            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' dropped real scrap '{scrap.itemProperties?.itemName ?? "Item"}' ({i + 1}/{itemsToDrop.Count}, isElevatorStaged={isElevatorStaged}, isLadderStaged={isLadderStaged}, inShip={inShip}).");
        }

        CarriedItems.Clear();
    }

    /// <summary>
    /// Immediately drops all held and carried scrap at current feet position (e.g. on ambush charge or death).
    /// </summary>
    public void DropHeldScrapImmediately()
    {
        if (CarriedItems.Count > 0)
        {
            DropRealScrap(transform.position + transform.forward * 0.4f);
        }
    }

    // ─── Local Execution (Shared by Host and Receiving Clients) ───────────────

    /// <summary>
    /// Attaches the primary held scrap item to the mimic's body.
    /// One-handed items (like bottles, scrap) are parented to the right hand (_rightHandBone / serverItemHolder)
    /// using official positionOffset and rotationOffset, matching real player hand grip.
    /// Two-handed items (engines, axles, etc.) are parented to _heldItemAnchor in front of chest.
    /// </summary>
    private void AttachPrimaryItem(GrabbableObject scrap)
    {
        if (scrap == null) return;

        bool twoHanded = scrap.itemProperties != null && scrap.itemProperties.twoHanded;

        // In vanilla Lethal Company, all held items (one-handed and two-handed) are parented to serverItemHolder.
        // The animator layers 'HoldingItemsBothHands' and 'HoldingItemsRightHand' position the arms correctly.
        Transform holdBone = _rightHandBone ?? _heldItemAnchor ?? _chestBone ?? transform;

        scrap.parentObject = holdBone;
        scrap.transform.SetParent(holdBone, false);
        scrap.hasHitGround = false;
        scrap.isHeldByEnemy = true;
        if (_masked != null)
        {
            scrap.GrabItemFromEnemy(_masked);
        }
        scrap.EnablePhysics(false);
        scrap.isHeld = true;
        scrap.isPocketed = false;
        scrap.EnableItemMeshes(true);

        if (scrap.itemProperties != null)
        {
            scrap.transform.localPosition = scrap.itemProperties.positionOffset;
            scrap.transform.localRotation = Quaternion.Euler(scrap.itemProperties.rotationOffset);
        }

        // Hide visual tool while carrying real scrap so mimic never holds double items!
        var holder = GetComponent<MaskedHeldItemHolder>();
        if (holder != null)
        {
            holder.SetHeldToolActive(false);
            holder.UpdateAnimationLayers(hasRealScrap: true, isAggressive: false);
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] Attached primary item '{scrap.itemProperties?.itemName ?? "Item"}' to '{(holdBone != null ? holdBone.name : "null")}' (twoHanded={twoHanded}).");
    }


    public void ExecuteGrabLocally(GrabbableObject scrap)
    {
        if (scrap == null) return;
        if (!CarriedItems.Contains(scrap)) CarriedItems.Add(scrap);

        if (CarriedItems[0] == scrap)
        {
            AttachPrimaryItem(scrap);
        }
        else
        {
            // Secondary items: pocketed in inventory!
            // In vanilla Lethal Company, pocketed items have their 3D meshes HIDDEN.
            // They are NOT attached visibly to the belt (which would look glitched with multiple items sticking out).
            Transform beltBone = _beltBone ?? transform;
            scrap.parentObject = beltBone;
            scrap.hasHitGround = false;
            scrap.isHeldByEnemy = true;
            if (_masked != null)
            {
                scrap.GrabItemFromEnemy(_masked);
            }
            scrap.EnablePhysics(false);
            scrap.isHeld = true;
            scrap.isPocketed = true;
            scrap.transform.SetParent(beltBone, false);
            scrap.transform.localPosition = Vector3.zero;
            scrap.EnableItemMeshes(false); // CRITICAL: POCKETED ITEMS ARE INVISIBLE!
        }
    }

    public void ExecuteDropLocally(GrabbableObject scrap, Vector3 targetFloorPos, bool isElevatorStaged = false, bool isLadderStaged = false)
    {
        if (scrap == null) return;
        CarriedItems.Remove(scrap);

        // ── 1. Determine drop parent & elevator / ship region ──────────────────
        bool inShip = StartOfRound.Instance != null && StartOfRound.Instance.shipBounds != null 
                      && StartOfRound.Instance.shipBounds.bounds.Contains(targetFloorPos);
        if (!inShip && IsNearShip(targetFloorPos, 5.0f)) inShip = true;

        if (inShip && StartOfRound.Instance?.elevatorTransform != null)
        {
            scrap.transform.SetParent(StartOfRound.Instance.elevatorTransform, true);
            EnemyAI.SetItemInElevatorNonPlayer(true, true, scrap);
        }
        else if (isElevatorStaged && StartOfRound.Instance?.elevatorTransform != null)
        {
            scrap.transform.SetParent(StartOfRound.Instance.elevatorTransform, true);
            EnemyAI.SetItemInElevatorNonPlayer(false, true, scrap);
        }
        else
        {
            if (StartOfRound.Instance?.propsContainer != null)
            {
                scrap.transform.SetParent(StartOfRound.Instance.propsContainer, true);
            }
            EnemyAI.SetItemInElevatorNonPlayer(false, false, scrap);
        }

        // ── 2. Find exact physical floor surface via Raycast ──────────────────
        Vector3 floorPoint = targetFloorPos;
        Ray ray = new Ray(targetFloorPos + Vector3.up * 0.8f, Vector3.down);
        int layerMask = StartOfRound.Instance != null 
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault 
            : ~0;

        if (Physics.Raycast(ray, out RaycastHit hit, 4.0f, layerMask, QueryTriggerInteraction.Ignore))
        {
            floorPoint = hit.point;
        }

        // ── 3. Apply item vertical offset so model NEVER clips into floor ────
        float vertOffset = scrap.itemProperties != null ? scrap.itemProperties.verticalOffset : 0.05f;
        if (vertOffset < 0.04f) vertOffset = 0.06f; // Safe minimum clearance
        Vector3 finalFloorPos = floorPoint + Vector3.up * vertOffset;

        // ── 4. Full State Restoration (Mirroring BaboonBirdAI.DropScrap) ─────
        scrap.parentObject = null;
        scrap.isHeld = false;
        scrap.isPocketed = false;
        scrap.isHeldByEnemy = false;
        scrap.grabbable = true;
        scrap.deactivated = false;
        scrap.hasHitGround = false;
        scrap.fallTime = 0f;

        scrap.EnablePhysics(true);
        scrap.EnableItemMeshes(true);
        scrap.transform.localScale = scrap.originalScale;

        Transform parentT = scrap.transform.parent;
        if (parentT != null)
        {
            scrap.startFallingPosition = parentT.InverseTransformPoint(scrap.transform.position);
            scrap.targetFloorPosition = parentT.InverseTransformPoint(finalFloorPos);
        }
        else
        {
            scrap.startFallingPosition = scrap.transform.position;
            scrap.targetFloorPosition = finalFloorPos;
        }

        scrap.transform.position = finalFloorPos;
        scrap.floorYRot = UnityEngine.Random.Range(0, 360);
        scrap.DiscardItemFromEnemy();

        if (_masked != null)
        {
            scrap.isInFactory = !_masked.isOutside;
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] '{_masked?.gameObject.name}' placed '{scrap.itemProperties?.itemName ?? "Item"}' on floor at {finalFloorPos} (vertOffset: {vertOffset:F2}m, hitGround: {hit.collider != null}). Remaining inventory: {CarriedItems.Count}.");

        // Only add to GloballyProcessedScrapIds if not staged for moving up the elevator or dropping at ladder!
        bool isTemporarilyStaged = isElevatorStaged || isLadderStaged;
        if (!isTemporarilyStaged)
        {
            if (scrap.NetworkObject != null)
            {
                GloballyProcessedScrapIds.Add(scrap.NetworkObject.NetworkObjectId);
            }

            if (!_ignoredItems.Contains(scrap))
            {
                _ignoredItems.Add(scrap);
            }
        }

        // Promote next carried item to held hands/chest position
        if (CarriedItems.Count > 0)
        {
            var nextPrimary = CarriedItems[0];
            if (nextPrimary != null)
            {
                AttachPrimaryItem(nextPrimary);
                PhoneyPlugin.Logger.LogInfo(
                    $"[ScrapManager] Promoted next pocketed item '{nextPrimary.itemProperties?.itemName ?? "Item"}' to held position.");
            }
        }
        else
        {
            // All scrap dropped! Restore visual held tool and animation layers
            var holder = GetComponent<MaskedHeldItemHolder>();
            if (holder != null)
            {
                holder.SetHeldToolActive(true);
                holder.UpdateAnimationLayers(hasRealScrap: false, isAggressive: false);
            }
        }
    }


    /// <summary>
    /// Cycles through carried inventory items, swapping which item is actively held in hands.
    /// Only works if carrying multiple items and the current held item is NOT a two-handed item.
    /// (Two-handed items occupy both hands and cannot be hotbar-cycled in vanilla Lethal Company).
    /// </summary>
    public bool CycleInventory()
    {
        if (CarriedItems.Count <= 1 || _masked == null) return false;

        // Two-handed items cannot be cycled away from — player must hold it in both hands!
        if (HeldScrap != null && HeldScrap.itemProperties != null && HeldScrap.itemProperties.twoHanded)
            return false;

        // Current primary item gets pocketed
        var oldPrimary = CarriedItems[0];
        CarriedItems.RemoveAt(0);
        CarriedItems.Add(oldPrimary); // Move to back of inventory

        // Hide old primary mesh and pocket it
        if (oldPrimary != null)
        {
            oldPrimary.isPocketed = true;
            Transform belt = _beltBone ?? transform;
            oldPrimary.parentObject = belt;
            oldPrimary.transform.SetParent(belt, false);
            oldPrimary.transform.localPosition = Vector3.zero;
            oldPrimary.EnableItemMeshes(false);
        }

        // New primary item gets held and displayed in hands
        var newPrimary = CarriedItems[0];
        if (newPrimary != null)
        {
            AttachPrimaryItem(newPrimary);

            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' cycled hotbar item -> now holding '{newPrimary.itemProperties?.itemName ?? "Item"}' ({CarriedItems.Count} total carried).");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Generates an eerie retro-horror distorted name for scrap handled by a mimic.
    /// Distorts vowels and key consonants into corrupted glitch glyphs while preserving recognizability.
    /// </summary>
    public static string GenerateDistortedName(string originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName)) return "???";

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < originalName.Length; i++)
        {
            char c = originalName[i];
            char lower = char.ToLowerInvariant(c);

            switch (lower)
            {
                case 'a': sb.Append('@'); break;
                case 'e': sb.Append('3'); break;
                case 'i': sb.Append('!'); break;
                case 'o': sb.Append('0'); break;
                case 'u': sb.Append('v'); break;
                case 's': sb.Append('$'); break;
                case 't': sb.Append('7'); break;
                case 'l': sb.Append('|'); break;
                case 'r': sb.Append('%'); break;
                case 'c': sb.Append('('); break;
                case 'k': sb.Append('<'); break;
                case 'x': sb.Append('*'); break;
                default:
                    if (char.IsLetter(c) && (i % 4 == 1))
                    {
                        sb.Append('#');
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Applies scrap value reduction, HUD hover tooltip, and scan node updates locally.
    /// Safe for both server and receiving clients without breaking ScriptableObject asset references.
    /// </summary>
    public static void ApplyTaintLocally(GrabbableObject scrap, int newValue, string distortedName)
    {
        if (scrap == null) return;

        if (string.IsNullOrEmpty(distortedName))
        {
            distortedName = GenerateDistortedName(scrap.itemProperties?.itemName ?? "Item");
        }

        bool isScrap = scrap.itemProperties != null && scrap.itemProperties.isScrap;
        if (isScrap)
        {
            scrap.SetScrapValue(newValue);
        }

        var scanNodes = scrap.GetComponentsInChildren<ScanNodeProperties>();
        if (scanNodes != null && scanNodes.Length > 0)
        {
            foreach (var scanNode in scanNodes)
            {
                if (scanNode != null)
                {
                    scanNode.headerText = distortedName;
                    if (isScrap)
                    {
                        scanNode.scrapValue = newValue;
                        scanNode.subText = $"Value: ${newValue}";
                    }
                }
            }
        }

        scrap.customGrabTooltip = $"Grab {distortedName} : [E]";
    }
}
