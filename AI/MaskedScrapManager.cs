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

    public readonly List<GrabbableObject> CarriedItems = new();
    public GrabbableObject? HeldScrap => CarriedItems.Count > 0 ? CarriedItems[0] : null;
    public bool HasHeldScrap => CarriedItems.Count > 0;
    public int CarriedCount => CarriedItems.Count;

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
        int maxSlots = Mathf.Clamp(PhoneyPlugin.MaxCarriedScrapCount.Value, 1, 4);
        if (CarriedItems.Count >= maxSlots) return false;

        // If currently holding a two-handed item, cannot carry additional items
        if (HeldScrap != null && HeldScrap.itemProperties != null && HeldScrap.itemProperties.twoHanded)
            return false;

        return true;
    }

    private float _nextScanTime;
    private readonly List<GrabbableObject> _ignoredItems = new();

    public void Initialize(MaskedPlayerEnemy masked, Transform? rightHandBone)
    {
        _masked = masked;
        _rightHandBone = rightHandBone ?? FindHandBone(masked.transform);
        _beltBone = FindBeltBone(masked.transform);
        PhoneyPlugin.Logger.LogInfo($"[ScrapManager] Initialized on '{masked.gameObject.name}'. RightHandBone={_rightHandBone?.name ?? "none"}, BeltBone={_beltBone?.name ?? "none"}");
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
            .FirstOrDefault(t => t.name.Equals("serverItemHolder", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("hand.R", StringComparison.OrdinalIgnoreCase)
                              || t.name.Equals("RightHand", StringComparison.OrdinalIgnoreCase));
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
                    Vector3 doorPos = t.entrancePoint != null ? t.entrancePoint.position : t.transform.position;
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
    /// </summary>
    public static Vector3 GetDoorPosition(EntranceTeleport? door)
    {
        if (door == null) return Vector3.zero;
        return door.entrancePoint != null ? door.entrancePoint.position : door.transform.position;
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
            // Real players pick up all scrap, keys, and tools
            bool isScrapOrUseful = item.itemProperties.isScrap || 
                                   item.itemProperties.itemName.Equals("Key", StringComparison.OrdinalIgnoreCase) ||
                                   item.itemProperties.isConductiveMetal;
            if (!isScrapOrUseful) continue;
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
                    Vector3 doorPos = match.entrancePoint != null ? match.entrancePoint.position : match.transform.position;
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

        // If carrying items already, cannot grab a two-handed item
        if (CarriedItems.Count > 0 && scrap.itemProperties != null && scrap.itemProperties.twoHanded)
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
                int newValue = scrap.scrapValue > 1 ? Mathf.Max(1, scrap.scrapValue - reduction) : scrap.scrapValue;
                string originalName = scrap.itemProperties?.itemName ?? "Item";
                string distortedName = GenerateDistortedName(originalName);

                ApplyTaintLocally(scrap, newValue, distortedName);

                PhoneyNetworkManager.Instance.BroadcastItemTaint(scrapNetId, newValue, distortedName);

                PhoneyPlugin.Logger.LogInfo(
                    $"[ScrapManager] Mimic touch corrupted item '{originalName}' -> '{distortedName}', value: ${oldValue} -> ${newValue} (loss: -${(scrap.scrapValue > 1 ? reduction : 0)})");
            }
        }

        // Broadcast to clients
        if (_masked.NetworkObject != null && scrap.NetworkObject != null)
        {
            PhoneyNetworkManager.Instance.BroadcastItemGrab(
                _masked.NetworkObject.NetworkObjectId,
                scrap.NetworkObject.NetworkObjectId);
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] '{_masked.gameObject.name}' grabbed real scrap '{scrap.itemProperties?.itemName ?? "Item"}' (value: ${scrap.scrapValue}, carried: {CarriedItems.Count}, totalWeight: {TotalCarryWeight:F2})");

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
    public void DropRealScrap(Vector3 targetFloorPos, bool isElevatorStaged = false)
    {
        if (CarriedItems.Count == 0 || _masked == null) return;

        var itemsToDrop = CarriedItems.ToList();
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

            ExecuteDropLocally(scrap, itemFloorPos, isElevatorStaged);

            // Broadcast to clients
            if (_masked.NetworkObject != null && scrap.NetworkObject != null)
            {
                PhoneyNetworkManager.Instance.BroadcastItemDrop(
                    _masked.NetworkObject.NetworkObjectId,
                    scrap.NetworkObject.NetworkObjectId,
                    itemFloorPos);
            }

            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' dropped real scrap '{scrap.itemProperties?.itemName ?? "Item"}' ({i + 1}/{itemsToDrop.Count}, isElevatorStaged={isElevatorStaged}).");
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

    public void ExecuteGrabLocally(GrabbableObject scrap)
    {
        if (scrap == null) return;
        if (!CarriedItems.Contains(scrap)) CarriedItems.Add(scrap);

        if (CarriedItems[0] == scrap)
        {
            // Primary item: hold in right hand
            Transform holdBone = _rightHandBone ?? transform;
            scrap.parentObject = holdBone;
            scrap.hasHitGround = false;
            if (_masked != null)
            {
                scrap.GrabItemFromEnemy(_masked);
            }
            scrap.EnablePhysics(false);
            scrap.isHeld = true;
            scrap.isPocketed = false;
        }
        else
        {
            // Secondary items: pocket on hip / belt
            Transform beltBone = _beltBone ?? transform;
            scrap.parentObject = beltBone;
            scrap.hasHitGround = false;
            if (_masked != null)
            {
                scrap.GrabItemFromEnemy(_masked);
            }
            scrap.EnablePhysics(false);
            scrap.isHeld = true;
            scrap.isPocketed = true;
            scrap.transform.SetParent(beltBone, false);
            scrap.transform.localPosition = Vector3.zero;
        }
    }

    public void ExecuteDropLocally(GrabbableObject scrap, Vector3 targetFloorPos, bool isElevatorStaged = false)
    {
        if (scrap == null) return;
        CarriedItems.Remove(scrap);

        scrap.parentObject = null;
        if (StartOfRound.Instance?.propsContainer != null)
        {
            scrap.transform.SetParent(StartOfRound.Instance.propsContainer, true);
        }

        scrap.EnablePhysics(true);
        scrap.fallTime = 0f;
        scrap.hasHitGround = false;
        scrap.isHeld = false;
        scrap.isPocketed = false;

        // ── 1. Find exact physical floor surface via Raycast ──────────────────
        Vector3 floorPoint = targetFloorPos;
        Ray ray = new Ray(targetFloorPos + Vector3.up * 0.8f, Vector3.down);
        int layerMask = StartOfRound.Instance != null 
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault 
            : ~0;

        if (Physics.Raycast(ray, out RaycastHit hit, 4.0f, layerMask, QueryTriggerInteraction.Ignore))
        {
            floorPoint = hit.point;
        }

        // ── 2. Apply item vertical offset so model NEVER clips into floor ────
        float vertOffset = scrap.itemProperties != null ? scrap.itemProperties.verticalOffset : 0.05f;
        if (vertOffset < 0.04f) vertOffset = 0.06f; // Safe minimum clearance for items with zero or negative offset
        Vector3 finalFloorPos = floorPoint + Vector3.up * vertOffset;

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

        // Ensure dropped scrap is properly tagged outside/inside so interior loot bugs cannot touch outside scrap
        if (_masked != null)
        {
            scrap.isInFactory = !_masked.isOutside;
            scrap.isInElevator = IsNearShip(targetFloorPos) || isElevatorStaged;
        }

        // Only add to GloballyProcessedScrapIds if not staged for moving up the elevator!
        if (!isElevatorStaged)
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
    /// Applies scrap value reduction, cloned ScriptableObject name distortion, and scan node updates locally.
    /// Safe for both server and receiving clients.
    /// </summary>
    public static void ApplyTaintLocally(GrabbableObject scrap, int newValue, string distortedName)
    {
        if (scrap == null) return;

        scrap.SetScrapValue(newValue);

        if (string.IsNullOrEmpty(distortedName))
        {
            distortedName = GenerateDistortedName(scrap.itemProperties?.itemName ?? "Item");
        }

        // Isolate itemProperties ScriptableObject so only THIS item instance has its name changed
        if (scrap.itemProperties != null)
        {
            scrap.itemProperties = UnityEngine.Object.Instantiate(scrap.itemProperties);
            scrap.itemProperties.itemName = distortedName;
        }

        var scanNodes = scrap.GetComponentsInChildren<ScanNodeProperties>();
        if (scanNodes != null && scanNodes.Length > 0)
        {
            foreach (var scanNode in scanNodes)
            {
                if (scanNode != null)
                {
                    scanNode.headerText = distortedName;
                    scanNode.scrapValue = newValue;
                    scanNode.subText = $"Value: ${newValue}";
                }
            }
        }

        scrap.customGrabTooltip = $"Grab {distortedName} : [E]";
    }
}
