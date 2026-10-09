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
    public bool IsHoldingTwoHanded => IsItemTwoHanded(HeldScrap);
    public bool IsCarryingTwoHanded => CarriedItems.Any(i => IsItemTwoHanded(i));

    /// <summary>
    /// Accurately identifies whether an item requires authentic two-handed holding.
    /// Checks itemProperties.twoHanded, itemProperties.twoHandedAnimation (e.g. toilet paper),
    /// and bulky scrap names (toilet paper, sheet metal, engines, axles, cash registers).
    /// </summary>
    public static bool IsItemTwoHanded(GrabbableObject? item)
    {
        if (item == null || item.itemProperties == null) return false;
        var p = item.itemProperties;
        if (p.twoHanded || p.twoHandedAnimation) return true;
        string name = p.itemName ?? item.gameObject.name;
        if (name.IndexOf("toilet", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("paper", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("sheet", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("engine", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("axle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("cash", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("apparatus", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("lung", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("lamp", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("painting", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }
        return false;
    }

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
        if (IsHoldingTwoHanded)
            return false;

        // If any carried item is two-handed, inventory is locked to only that item
        if (IsCarryingTwoHanded)
            return false;

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
            if (childHolder == null)
            {
                var holderObj = new GameObject("serverItemHolder");
                holderObj.transform.SetParent(_rightHandBone, false);
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
            _rightHandBone = childHolder;
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
    /// Multi-layer player body and ragdoll detection:
    /// Strictly prevents mimics from ever targeting, picking up, or corrupting dead player bodies.
    /// Checks runtime type (RagdollGrabbableObject), DeadBodyInfo components, type name, item name,
    /// gameObject name, and scan node header text ("Body of [Player]").
    /// </summary>
    public static bool IsPlayerBodyOrRagdoll(GrabbableObject? item)
    {
        if (item == null) return false;

        // Layer 1: Vanilla RagdollGrabbableObject or any subclass
        if (item is RagdollGrabbableObject) return true;

        // Layer 2: DeadBodyInfo attached to GameObject, parent, or children
        if (item.GetComponent<DeadBodyInfo>() != null ||
            item.GetComponentInParent<DeadBodyInfo>() != null ||
            item.GetComponentInChildren<DeadBodyInfo>() != null)
            return true;

        // Layer 3: Type name contains "ragdoll" or "body"
        string typeName = item.GetType().Name;
        if (typeName.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
            typeName.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Layer 4: GameObject name contains "ragdoll" or "body"
        if (item.gameObject.name.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
            item.gameObject.name.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Layer 5: Item properties name contains "ragdoll" or "body"
        if (item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.itemName))
        {
            if (item.itemProperties.itemName.IndexOf("body", StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.itemProperties.itemName.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        // Layer 6: ScanNodeProperties header contains "Body of" or "Body"
        var scanNodes = item.GetComponentsInChildren<ScanNodeProperties>(true);
        if (scanNodes != null && scanNodes.Length > 0)
        {
            for (int i = 0; i < scanNodes.Length; i++)
            {
                var sn = scanNodes[i];
                if (sn != null && !string.IsNullOrEmpty(sn.headerText))
                {
                    if (sn.headerText.IndexOf("Body of", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        sn.headerText.IndexOf("Body", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Computes a randomized small-to-medium negative value reduction for scrap touched by a mimic.
    /// Strictly guarantees:
    ///   - Reduction is always negative (value strictly decreases).
    ///   - Scaled by percentage (8% to 18% of item's value), bounded between MinReduction and MaxReduction.
    ///   - Preserves at least $1 in scrap value so item never becomes zero or negative.
    /// </summary>
    public static int CalculateTaintReduction(int currentScrapValue)
    {
        if (currentScrapValue <= 1) return 0;

        int minRed = PhoneyPlugin.MimicTouchMinReduction != null
            ? Mathf.Max(1, PhoneyPlugin.MimicTouchMinReduction.Value)
            : 3;
        int maxRed = PhoneyPlugin.MimicTouchMaxReduction != null
            ? Mathf.Max(minRed, PhoneyPlugin.MimicTouchMaxReduction.Value)
            : 14;

        // Small-to-medium random loss: 8% to 18% of current scrap value
        int percentLoss = Mathf.RoundToInt(currentScrapValue * UnityEngine.Random.Range(0.08f, 0.18f));
        int reduction = Mathf.Clamp(percentLoss, minRed, maxRed);

        // Clamping to guarantee at least $1 remaining value
        if (currentScrapValue - reduction < 1)
        {
            reduction = Mathf.Max(1, currentScrapValue - 1);
        }

        return reduction;
    }

    /// <summary>
    /// Global registry of scrap network object IDs that have already been gathered or dropped by any mimic.
    /// Once an item is dropped in the collection pile, it is permanently recorded so NO mimic ever picks it up again.
    /// </summary>
    public static readonly HashSet<ulong> GloballyProcessedScrapIds = new();
    public static readonly HashSet<ulong> TaintedScrapIds = new();

    /// <summary>
    private static EntranceTeleport[]? s_cachedTeleports;
    private static float s_lastTeleportCacheTime;

    public static EntranceTeleport[] GetCachedTeleports()
    {
        if (s_cachedTeleports == null || s_cachedTeleports.Length == 0 || Time.time - s_lastTeleportCacheTime > 6.0f)
        {
            try
            {
                s_cachedTeleports = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            }
            catch
            {
                s_cachedTeleports = Array.Empty<EntranceTeleport>();
            }
            s_lastTeleportCacheTime = Time.time;
        }
        return s_cachedTeleports ?? Array.Empty<EntranceTeleport>();
    }

    public static void ClearCachedTeleports()
    {
        s_cachedTeleports = null;
        s_lastTeleportCacheTime = 0f;
    }

    /// <summary>
    /// Checks if a position is near any EntranceTeleport door.
    /// </summary>
    public static bool IsNearEntranceDoor(Vector3 position, float radius = 7.0f)
    {
        try
        {
            var teleports = GetCachedTeleports();
            if (teleports != null && teleports.Length > 0)
            {
                for (int i = 0; i < teleports.Length; i++)
                {
                    var t = teleports[i];
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
    /// Gets the entrance doorway / catwalk ramp position of the ship, suitable for navigating into the ship from the outside.
    /// </summary>
    public static Vector3 GetShipEntrancePosition()
    {
        try
        {
            if (StartOfRound.Instance != null)
            {
                if (StartOfRound.Instance.outsideDoorPosition != null)
                    return StartOfRound.Instance.outsideDoorPosition.position;
                if (StartOfRound.Instance.shipDoorNode != null)
                    return StartOfRound.Instance.shipDoorNode.position;
                if (StartOfRound.Instance.shipDoorAudioSource != null)
                    return StartOfRound.Instance.shipDoorAudioSource.transform.position;
            }
        }
        catch { }
        return Vector3.zero;
    }

    /// <summary>
    /// Gets the interior central floor position inside the ship cabin.
    /// </summary>
    public static Vector3 GetShipInteriorPosition()
    {
        try
        {
            if (StartOfRound.Instance != null)
            {
                if (StartOfRound.Instance.middleOfShipNode != null)
                    return StartOfRound.Instance.middleOfShipNode.position;
                if (StartOfRound.Instance.insideShipPositions != null && StartOfRound.Instance.insideShipPositions.Length > 0 && StartOfRound.Instance.insideShipPositions[0] != null)
                    return StartOfRound.Instance.insideShipPositions[0].position;
            }
        }
        catch { }
        return Vector3.zero;
    }

    /// <summary>
    /// Checks if a player is currently inside the ship cabin or on the ship elevator/catwalk.
    /// </summary>
    public static bool IsPlayerInShip(PlayerControllerB? player)
    {
        if (player == null || player.isPlayerDead) return false;
        try
        {
            if (player.isInHangarShipRoom || player.isInElevator) return true;
            if (StartOfRound.Instance != null)
            {
                Vector3 pos = player.transform.position;
                if (StartOfRound.Instance.shipInnerRoomBounds != null &&
                    StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(pos))
                    return true;
                if (StartOfRound.Instance.shipBounds != null &&
                    StartOfRound.Instance.shipBounds.bounds.Contains(pos))
                    return true;
                if (StartOfRound.Instance.middleOfShipNode != null)
                {
                    Vector3 mid = StartOfRound.Instance.middleOfShipNode.position;
                    float horizontalDist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(mid.x, mid.z));
                    float verticalDist = Mathf.Abs(pos.y - mid.y);
                    if (horizontalDist <= 6.5f && verticalDist <= 2.2f)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Checks if an enemy is currently inside the ship cabin.
    /// Distinguishes being inside the cabin from standing underneath the ship on the ground.
    /// </summary>
    public static bool IsEnemyInShip(EnemyAI? enemy)
    {
        if (enemy == null || enemy.isEnemyDead) return false;
        try
        {
            if (enemy.isInsidePlayerShip) return true;
            if (StartOfRound.Instance != null)
            {
                Vector3 pos = enemy.transform.position;
                if (StartOfRound.Instance.shipInnerRoomBounds != null &&
                    StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(pos))
                    return true;
                if (StartOfRound.Instance.shipStrictInnerRoomBounds != null &&
                    StartOfRound.Instance.shipStrictInnerRoomBounds.bounds.Contains(pos))
                    return true;
                if (StartOfRound.Instance.middleOfShipNode != null)
                {
                    Vector3 mid = StartOfRound.Instance.middleOfShipNode.position;
                    float horizontalDist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(mid.x, mid.z));
                    float verticalDiff = pos.y - mid.y; // Positive if above floor, negative if below floor
                    if (horizontalDist <= 4.8f && verticalDiff >= -0.6f && verticalDiff <= 2.2f)
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Checks if an enemy is on the catwalk ramp or doorway threshold leading into the ship.
    /// </summary>
    public static bool IsEnemyAtShipEntrance(EnemyAI? enemy)
    {
        if (enemy == null || enemy.isEnemyDead) return false;
        try
        {
            Vector3 entrance = GetShipEntrancePosition();
            if (entrance == Vector3.zero) return false;
            Vector3 pos = enemy.transform.position;
            float dist = Vector3.Distance(pos, entrance);
            return dist <= 2.5f && Mathf.Abs(pos.y - entrance.y) <= 1.4f;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Checks if a position is on the ground terrain directly underneath the elevated ship belly.
    /// </summary>
    public static bool IsPositionUnderShip(Vector3 pos)
    {
        try
        {
            if (StartOfRound.Instance?.middleOfShipNode == null) return false;
            Vector3 mid = StartOfRound.Instance.middleOfShipNode.position;
            float horizontalDist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(mid.x, mid.z));
            float verticalDiff = mid.y - pos.y; // Positive when position is BELOW middleOfShipNode floor
            return horizontalDist <= 6.8f && verticalDiff >= 0.85f;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Finds the EntranceTeleport matching the specified direction.
    /// </summary>
    public static EntranceTeleport? FindDoor(bool wantEntranceToBuilding)
    {
        try
        {
            var teleports = GetCachedTeleports();
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
    /// Finds the EntranceTeleport closest to a specified position matching the desired direction.
    /// Supports both Main Entrance and Fire Exits.
    /// </summary>
    public static EntranceTeleport? FindClosestDoor(Vector3 fromPos, bool wantEntranceToBuilding)
    {
        try
        {
            var teleports = GetCachedTeleports();
            if (teleports == null || teleports.Length == 0) return null;

            return teleports
                .Where(t => t != null && t.isEntranceToBuilding == wantEntranceToBuilding)
                .OrderBy(t => Vector3.Distance(fromPos, GetDoorNavPosition(t)))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the NavMesh standing/landing position in front of an EntranceTeleport door.
    /// Uses entrancePoint (where players and enemies safely stand) and falls back to sampled NavMesh floor.
    /// </summary>
    public static Vector3 GetDoorNavPosition(EntranceTeleport? door)
    {
        if (door == null) return Vector3.zero;
        if (door.entrancePoint != null)
        {
            Vector3 pos = door.entrancePoint.position;
            if (NavMesh.SamplePosition(pos, out var hit, 2.5f, NavMesh.AllAreas))
                return hit.position;
            return pos;
        }
        if (door.triggerScript != null)
        {
            Vector3 trigPos = door.triggerScript.transform.position;
            if (NavMesh.SamplePosition(trigPos, out var hit, 3.0f, NavMesh.AllAreas))
                return hit.position;
            return trigPos;
        }
        return door.transform.position;
    }

    /// <summary>
    /// Gets the visual interact handle / knob position of an EntranceTeleport door (for gaze tracking).
    /// </summary>
    public static Vector3 GetDoorInteractPosition(EntranceTeleport? door)
    {
        if (door == null) return Vector3.zero;
        if (door.triggerScript != null)
            return door.triggerScript.transform.position;
        if (door.entrancePoint != null)
            return door.entrancePoint.position;
        return door.transform.position;
    }

    /// <summary>
    /// Gets the navigation position of an EntranceTeleport door (points to walkable NavMesh landing).
    /// </summary>
    public static Vector3 GetDoorPosition(EntranceTeleport? door)
    {
        return GetDoorNavPosition(door);
    }

    /// <summary>
    /// Computes a 100% safe, non-glitching landing position when exiting/entering a facility door.
    /// Specifically engineered for elevated catwalks (such as Experimentation exterior landing):
    ///   1. Raycasts down against solid physics colliders to find the exact top surface of the catwalk.
    ///   2. Steps 1.0m forward onto the landing away from the door frame, verifying solid floor underneath.
    ///   3. Samples NavMesh with a tight 1.2m radius and strictly rejects any sample whose height differs
    ///      by > 0.8m from the solid floor surface (preventing snapping to the desert terrain 6m below!).
    /// </summary>
    public static Vector3 GetSafeDoorExitPosition(EntranceTeleport? door, bool toOutside)
    {
        try
        {
            if (door == null) door = FindDoor(wantEntranceToBuilding: toOutside);
            if (door == null) return Vector3.zero;

            EntranceTeleport exitDoor;
            if (door.isEntranceToBuilding == toOutside)
            {
                // door is ALREADY the destination door on the target side!
                exitDoor = door;
            }
            else
            {
                // door is the source entrance door; use its exitScript to reach the target side
                if (door.exitScript == null) door.FindExitPoint();
                exitDoor = door.exitScript ?? FindDoor(wantEntranceToBuilding: toOutside) ?? door;
            }

            Transform targetTransform = exitDoor.entrancePoint != null ? exitDoor.entrancePoint : exitDoor.transform;
            Vector3 rawPos = targetTransform.position;
            Vector3 forward = targetTransform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = exitDoor.transform.forward;
            forward.y = 0;
            if (forward != Vector3.zero) forward.Normalize();
            else forward = Vector3.forward;

            if (!toOutside)
            {
                // INSIDE THE FACILITY:
                // Inside door landings are flat interior floors. Stepping slightly forward from entrancePoint (0.8m)
                // places the agent clear of door collision while firmly on the interior NavMesh.
                Vector3 insideCandidate = rawPos + forward * 0.8f;
                if (NavMesh.SamplePosition(insideCandidate, out var insideHit, 1.5f, NavMesh.AllAreas))
                {
                    return insideHit.position;
                }
                if (NavMesh.SamplePosition(rawPos, out var rawHit, 2.0f, NavMesh.AllAreas))
                {
                    return rawHit.position;
                }
                return rawPos;
            }

            // OUTSIDE ON MOON SURFACE / CATWALK:
            int mask = StartOfRound.Instance != null 
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault 
                : ~0;

            // 1. Raycast down from rawPos to find solid catwalk/platform floor
            float floorY = rawPos.y;
            if (Physics.Raycast(rawPos + Vector3.up * 0.8f, Vector3.down, out var floorHit, 3.5f, mask, QueryTriggerInteraction.Ignore))
            {
                floorY = floorHit.point.y;
            }

            // 2. Candidate step 1.0m forward onto landing
            Vector3 candidatePos = new Vector3(rawPos.x, floorY, rawPos.z) + forward * 1.0f;
            if (Physics.Raycast(candidatePos + Vector3.up * 0.8f, Vector3.down, out var forwardHit, 2.5f, mask, QueryTriggerInteraction.Ignore))
            {
                if (Mathf.Abs(forwardHit.point.y - floorY) < 0.6f)
                {
                    candidatePos = forwardHit.point;
                    floorY = forwardHit.point.y;
                }
                else
                {
                    candidatePos = new Vector3(rawPos.x, floorY, rawPos.z) + forward * 0.5f;
                }
            }
            else
            {
                candidatePos = new Vector3(rawPos.x, floorY, rawPos.z) + forward * 0.5f;
            }

            // 3. Tight NavMesh sample (1.2f radius max). Strictly reject ground below!
            if (NavMesh.SamplePosition(candidatePos, out var navHit, 1.2f, NavMesh.AllAreas))
            {
                if (Mathf.Abs(navHit.position.y - floorY) <= 0.8f)
                {
                    return navHit.position;
                }
            }

            Vector3 doorFloor = new Vector3(rawPos.x, floorY, rawPos.z);
            if (NavMesh.SamplePosition(doorFloor, out var tightHit, 0.8f, NavMesh.AllAreas))
            {
                if (Mathf.Abs(tightHit.position.y - floorY) <= 0.8f)
                {
                    return tightHit.position;
                }
            }

            return new Vector3(candidatePos.x, floorY + 0.05f, candidatePos.z);
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogWarning($"[ScrapManager] GetSafeDoorExitPosition caught: {ex.Message}");
            return door != null ? GetDoorNavPosition(door) : Vector3.zero;
        }
    }

    /// <summary>
    /// Computes a safe floor target position on the catwalk/landing outside the main door
    /// to drop hauled scrap. Clamps using NavMesh.Raycast so it NEVER lands over railings or in midair!
    /// </summary>
    public static Vector3 GetOutsideDoorDropPosition(EntranceTeleport? door = null)
    {
        try
        {
            door ??= FindDoor(wantEntranceToBuilding: true);
            if (door == null) return Vector3.zero;

            Vector3 doorNavPos = GetDoorNavPosition(door);
            Transform? doorT = door.exitScript?.entrancePoint ?? door.entrancePoint ?? door.transform;
            Vector3 forward = doorT.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = door.transform.forward;
            forward.y = 0;
            if (forward != Vector3.zero) forward.Normalize();
            else forward = Vector3.forward;

            Vector3 right = doorT.right;
            right.y = 0;
            if (right != Vector3.zero) right.Normalize();
            else right = Vector3.right;

            // Target spot: 1.3m forward, 0.45m to the right (neatly beside walking corridor on the catwalk)
            Vector3 candidate = doorNavPos + forward * 1.3f + right * 0.45f;

            // Clamping against catwalk railings/walls
            if (NavMesh.Raycast(doorNavPos, candidate, out var hit, NavMesh.AllAreas))
            {
                candidate = hit.position - forward * 0.25f;
            }

            if (NavMesh.SamplePosition(candidate, out var navHit, 1.0f, NavMesh.AllAreas))
            {
                if (Mathf.Abs(navHit.position.y - doorNavPos.y) < 0.8f)
                {
                    return navHit.position;
                }
            }

            return candidate;
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogWarning($"[ScrapManager] GetOutsideDoorDropPosition caught: {ex.Message}");
            return door != null ? GetDoorNavPosition(door) : Vector3.zero;
        }
    }

    /// <summary>
    /// Scans the map for unheld, reachable scrap items within maxDistance of the mimic.
    /// Respects interior vs. exterior environment boundaries.
    /// Outside mimics can collect scrap sitting near the outside entrance door to haul it to the Ship!
    /// </summary>
    public GrabbableObject? FindNearbyReachableScrap(float maxDistance = 22f, bool forceScan = false, Vector3? center = null, float maxCenterDistance = float.MaxValue)
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
            // Only target actual scrap quota items — never touch keys or player bodies!
            if (!item.itemProperties.isScrap) continue;
            if (IsKeyItem(item)) continue;
            if (IsPlayerBodyOrRagdoll(item)) continue;

            // Two-handed inventory rules (matching real player behavior):
            // - If already carrying ANY item, skip two-handed items (can't carry both)
            // - If holding a two-handed item, skip everything (hands are full)
            if (CarriedItems.Count > 0)
            {
                if (IsItemTwoHanded(item)) continue; // Can't add a two-handed item on top
                if (IsHoldingTwoHanded) continue; // Hands full with two-handed
                if (IsCarryingTwoHanded) continue;
            }

            if (item.isHeld || item.isPocketed || item.deactivated || !item.grabbable) continue;

            // Ensure matching environment: inside facility vs exterior
            if (item.isInFactory == _masked.isOutside) continue;

            // Filter by companion leash center if provided
            if (center.HasValue && Vector3.Distance(center.Value, item.transform.position) > maxCenterDistance)
                continue;

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
                $"[ScrapManager] '{_masked.gameObject.name}' scan detected reachable scrap '{bestItem.itemProperties?.itemName ?? "Item"}' (${bestItem.scrapValue}) at {bestDist:F1}m (twoHanded: {IsItemTwoHanded(bestItem)}).");
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

            var teleports = GetCachedTeleports();
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

        // NEVER pick up dead player bodies or ragdolls!
        if (IsPlayerBodyOrRagdoll(scrap))
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[ScrapManager] '{_masked.gameObject.name}' refused to touch dead player body '{scrap.gameObject.name}'. Bodies are not scrap!");
            return false;
        }

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
        if (CarriedItems.Count > 0 && IsItemTwoHanded(scrap))
            return false;

        // If holding a two-handed item, cannot grab any more items!
        if (IsHoldingTwoHanded)
            return false;

        ExecuteGrabLocally(scrap);

        // Apply randomized small-to-medium negative value reduction when touched by a mimic
        if (PhoneyPlugin.EnableMimicTouchReduction.Value && scrap.NetworkObject != null)
        {
            ulong scrapNetId = scrap.NetworkObject.NetworkObjectId;
            if (!TaintedScrapIds.Contains(scrapNetId))
            {
                TaintedScrapIds.Add(scrapNetId);
                int oldValue = scrap.scrapValue;
                bool isScrap = scrap.itemProperties != null && scrap.itemProperties.isScrap;
                int reduction = isScrap ? CalculateTaintReduction(oldValue) : 0;
                int newValue = isScrap ? Mathf.Max(1, oldValue - reduction) : oldValue;
                string originalName = scrap.itemProperties?.itemName ?? "Item";

                ApplyTaintLocally(scrap, newValue, originalName);

                PhoneyNetworkManager.Instance.BroadcastItemTaint(scrapNetId, newValue, originalName);

                PhoneyPlugin.Logger.LogInfo(
                    $"[ScrapManager] Mimic touch reduced item '{originalName}' value: ${oldValue} -> ${newValue} (loss: -${reduction})");
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
    /// In vanilla Lethal Company, ALL held items (one-handed and two-handed) are parented
    /// directly to serverItemHolder on the right hand.
    /// itemProperties.positionOffset and rotationOffset are authored specifically for serverItemHolder.
    /// When HoldingItemsBothHands is active at weight 1.0f on the animator, both hands are brought
    /// up together in front of the chest cradling the item (such as toilet paper covering vision).
    /// </summary>
    private void AttachPrimaryItem(GrabbableObject scrap)
    {
        if (scrap == null) return;

        bool twoHanded = IsItemTwoHanded(scrap);

        // Vanilla Lethal Company parents ALL held items (both one-handed and two-handed)
        // to serverItemHolder on the right hand. The animator layer HoldingItemsBothHands
        // positions both hands and arms up in front of the chest to cradle the item.
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

        // Apply item grab animation triggers to creatureAnimator
        if (_masked?.creatureAnimator != null && scrap.itemProperties != null)
        {
            _masked.creatureAnimator.SetBool("GrabValidated", true);
            _masked.creatureAnimator.SetBool("cancelHolding", false);
            _masked.creatureAnimator.SetBool("HandsOut", false);

            if (twoHanded)
            {
                _masked.creatureAnimator.ResetTrigger("SwitchHoldAnimationTwoHanded");
                _masked.creatureAnimator.SetTrigger("SwitchHoldAnimationTwoHanded");
            }
            else
            {
                _masked.creatureAnimator.ResetTrigger("SwitchHoldAnimation");
                _masked.creatureAnimator.SetTrigger("SwitchHoldAnimation");
            }

            if (!string.IsNullOrEmpty(scrap.itemProperties.grabAnim))
            {
                try
                {
                    _masked.creatureAnimator.SetBool(scrap.itemProperties.grabAnim, true);
                }
                catch { }
            }
        }

        // Hide visual tool while carrying real scrap so mimic never holds double items!
        var holder = GetComponent<MaskedHeldItemHolder>();
        if (holder != null)
        {
            holder.SetHeldToolActive(false);
            holder.UpdateAnimationLayers(hasRealScrap: true, isAggressive: false);
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[ScrapManager] Attached primary item '{scrap.itemProperties?.itemName ?? "Item"}' to '{(holdBone != null ? holdBone.name : "null")}' (twoHanded={twoHanded}, grabAnim='{scrap.itemProperties?.grabAnim ?? "none"}').");
    }


    public void ExecuteGrabLocally(GrabbableObject scrap)
    {
        if (scrap == null || IsPlayerBodyOrRagdoll(scrap) || IsKeyItem(scrap)) return;
        if (IsItemTwoHanded(scrap))
        {
            // Two-handed item: pocket any existing carried items so meshes are concealed and items aren't leaked!
            for (int i = 0; i < CarriedItems.Count; i++)
            {
                var prev = CarriedItems[i];
                if (prev != null && prev != scrap)
                {
                    prev.isPocketed = true;
                    prev.EnableItemMeshes(false);
                    Transform beltBone = _beltBone ?? transform;
                    prev.parentObject = beltBone;
                    prev.transform.SetParent(beltBone, false);
                }
            }
            CarriedItems.Remove(scrap);
            CarriedItems.Insert(0, scrap);
            AttachPrimaryItem(scrap);
            return;
        }

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

        if (_masked?.creatureAnimator != null && scrap.itemProperties != null && !string.IsNullOrEmpty(scrap.itemProperties.grabAnim))
        {
            try { _masked.creatureAnimator.SetBool(scrap.itemProperties.grabAnim, false); } catch { }
        }

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
    /// Broadcasts the inventory swap across the network so all clients see the mimic swap items.
    /// </summary>
    public bool CycleInventory(bool syncToNetwork = true)
    {
        if (CarriedItems.Count <= 1 || _masked == null) return false;

        // Two-handed items cannot be cycled away from — player must hold it in both hands!
        if (HeldScrap != null && HeldScrap.itemProperties != null && HeldScrap.itemProperties.twoHanded)
            return false;

        CycleInventoryLocally();

        if (syncToNetwork && _masked.NetworkObject != null)
        {
            PhoneyNetworkManager.Instance.BroadcastItemCycle(_masked.NetworkObject.NetworkObjectId);
        }

        return true;
    }

    /// <summary>
    /// Executes the hotbar cycle locally on this client: rotates inventory list,
    /// hides the old primary mesh, and attaches the new primary item in hands.
    /// </summary>
    public void CycleInventoryLocally()
    {
        if (CarriedItems.Count <= 1 || _masked == null) return;

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
    /// Applies scrap value reduction, HUD hover tooltip, and scan node updates locally.
    /// Safe for both server and receiving clients without breaking ScriptableObject asset references.
    /// Ensures scan node header, subText ("Value: $XX"), scrapValue, and nodeType (2 = Scrap)
    /// remain 100% visible and readable to players scanning the item.
    /// </summary>
    public static void ApplyTaintLocally(GrabbableObject scrap, int newValue, string displayName)
    {
        if (scrap == null) return;

        if (string.IsNullOrEmpty(displayName))
        {
            displayName = scrap.itemProperties?.itemName ?? "Item";
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
                    scanNode.headerText = displayName;
                    if (isScrap)
                    {
                        scanNode.scrapValue = newValue;
                        scanNode.subText = $"Value: ${newValue}";
                        scanNode.nodeType = 2; // Always enforce scrap node type so scanner displays green box & value!
                    }
                }
            }
        }

        scrap.customGrabTooltip = $"Grab {displayName} : [E]";
    }
}
