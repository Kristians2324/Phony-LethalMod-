using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Phoney.Audio;
using Phoney.Compat;
using Phoney.Core;
using Phoney.Network;
using Phoney.Vault;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Phoney.AI;

public enum MimicPhase
{
    UndercoverLooting,  // Phase 1: Pretending to be crewmate, searching rooms, hauling scrap
    LuringFollower,     // Phase 2: Companion pacing (legacy fallback)
    AmbushStrike,       // Phase 3: Violent sprint chase and kill
    TacticalRetreat     // Phase 4: Flee into darkness, reset disguise
}

public enum UndercoverSubState
{
    SeekingPlayer,          // Spawning: pathing toward a crewmate to make first contact
    Greeting,               // Saying hello once / friendly crouch nod to acknowledge teammate
    SearchingRooms,         // Moving through rooms in the player's general area (10-26m away)
    InspectingRoom,         // Pausing at a room corner or shelf (2-4s) pretending to look for loot
    ApproachingScrap,       // Walking directly up to spotted real scrap on the floor
    HaulingScrapToEntrance, // Carrying real scrap in hands toward exit door, ship, or disrupt location
    ReturningToFacility     // Walking back to the exterior door to return inside after outside delivery
}

public enum ScrapDeliveryPlan
{
    None,
    DropAtOutsideDoor,      // Exit facility, drop loot right outside the door (catwalk/landing)
    HaulToShip,             // Haul loot across the moon to the ship drop zone
    DisruptRandomDrop       // Trek to a random spot far away outside to disrupt the crew
}

public class PhoneyDeceptiveAI : MonoBehaviour
{
    public MaskedPlayerEnemy? Masked       { get; private set; }
    public MimicPhase CurrentPhase         { get; private set; } = MimicPhase.UndercoverLooting;
    public PlayerControllerB? TargetPlayer { get; private set; }

    private PhoneyVoiceEmitter? _voiceEmitter;
    private MaskedScrapManager? _scrapManager;
    private PhoneyBloodyReveal? _bloodyReveal;

    private float _phaseEnteredTime;
    private float _crouchCooldownTime;
    private float _lastBumpTime = -99f;
    private bool  _performingCrouch;
    private bool  _isPickingUpScrap;
    private bool  _isUsingDoor;
    private bool  _isTraversingOffMeshLink;
    private float _lastSeenPlayerTime;
    public  bool  IsPerformingFriendlyCrouch => _performingCrouch || _isPickingUpScrap;
    public  bool  IsUsingDoor => _isUsingDoor;
    public  Vector3 DoorInteractionTarget { get; private set; } = Vector3.zero;
    private float _ambientChatterTimer;

    // ── Paranoia & Hostility Escalation System ─────────────────────────────
    private float _nextParanoiaCheckTime;
    private float _hostilityChance;
    private bool  _isHostilePrimed;
    private float _lastParanoiaLogTime;
    private float _lastPrimedStatusLogTime;

    // ── Undercover Sub-state Machine ─────────────────────────────────────────
    private UndercoverSubState _subState = UndercoverSubState.SeekingPlayer;
    private float              _subStateTimer;
    private Vector3            _currentRoomTarget;
    private GrabbableObject?   _targetScrap;
    private ScrapDeliveryPlan  _deliveryPlan = ScrapDeliveryPlan.None;
    private Vector3            _scrapDropTarget;
    private bool               _hasGreetedPlayer;
    private float              _greetingResetTime;

    // ── Companion Leash ──────────────────────────────────────────────────────
    // The followed player carries an invisible band around them. The mimic loots and
    // explores freely inside the band, drifts back when it leaves the soft limit, and only
    // actively chases the player past the hard limit. Room targets are committed (not
    // re-rolled every AI tick) so movement is smooth instead of jittering onto the player.
    private const float LeashPersonalSpace = 1.8f;
    private float   _nextLeashRepickTime;
    private float   _nextPersonalSpaceTime;
    private float   _attendPlayerUntil;
    private readonly Queue<Vector3> _recentRoomTargets = new();

    private void SetSubState(UndercoverSubState next, string reason = "")
    {
        if (_subState != next)
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[DeceptiveAI] '{Masked?.gameObject.name}' SubState: {_subState} → {next}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}");
            _subState = next;
        }
    }

    private const float WalkSpeed              = 3.2f;
    private const float UndercoverSprintSpeed  = 5.2f;
    public float AmbushJogSpeed    => PhoneyPlugin.AmbushJogSpeed != null ? PhoneyPlugin.AmbushJogSpeed.Value : 2.95f;
    public float AmbushSprintSpeed => PhoneyPlugin.AmbushSprintSpeed != null ? PhoneyPlugin.AmbushSprintSpeed.Value : 4.95f;

    private float _stamina       = 1.0f;
    private bool  _isSprinting   = false;
    private float _ambushStamina = 1.0f;
    private bool  _isAmbushBurst = false;
    private float _nextPursuitTauntTime;
    private float _inspectNextTurnTime;
    private Quaternion _inspectTargetRotation;
    private bool  _hadLOSLastFrame = true;
    private float _lastLOSLogTime;
    private float _nextHeadGlanceTime;
    private float _nextHotbarCycleTime;

    // ─── Mineshaft Elevator Integration ───────────────────────────────────────

    private float _elevatorCooldownTime;

    /// <summary>
    /// Wrapper around Masked.UseElevator that prevents spam-calling every frame.
    /// Enforces a 5-second cooldown between elevator requests.
    /// </summary>
    private void SafeUseElevator(bool goUp)
    {
        if (Masked == null) return;
        if (Time.time < _elevatorCooldownTime) return;
        _elevatorCooldownTime = Time.time + 5.0f;
        PhoneyPlugin.Logger.LogInfo(
            $"[DeceptiveAI] '{Masked.gameObject.name}' requesting elevator (goUp={goUp}). Next request allowed in 5s.");
        Masked.UseElevator(goUp);
    }

    /// <summary>
    /// Checks if the current map is the Mineshaft dungeon flow (dungeon type 4).
    /// </summary>
    public static bool IsMineshaftDungeon()
    {
        return RoundManager.Instance != null && RoundManager.Instance.currentDungeonType == 4;
    }

    /// <summary>
    /// Gets the active Mineshaft elevator controller for the current map, if any.
    /// </summary>
    public MineshaftElevatorController? GetMineshaftElevator()
    {
        if (Masked != null && Masked.elevatorScript != null)
            return Masked.elevatorScript;

        var elev = RoundManager.Instance != null ? RoundManager.Instance.currentMineshaftElevator : null;
        if (elev == null)
        {
            elev = UnityEngine.Object.FindObjectOfType<MineshaftElevatorController>();
        }

        if (Masked != null && elev != null)
        {
            Masked.elevatorScript = elev;
        }

        return elev;
    }

    /// <summary>
    /// Checks if the mimic is currently on the upper floor (near the main entrance elevator room).
    /// </summary>
    public bool IsOnMineshaftUpperFloor()
    {
        var elevator = GetMineshaftElevator();
        if (elevator != null && elevator.elevatorTopPoint != null && elevator.elevatorBottomPoint != null)
        {
            return Vector3.Distance(transform.position, elevator.elevatorTopPoint.position) < 20f
                   || transform.position.y > (elevator.elevatorBottomPoint.position.y + 25f);
        }
        return transform.position.y > -50f;
    }

    // ─── Initialization ───────────────────────────────────────────────────────

    public void Initialize(MaskedPlayerEnemy maskedEnemy, PhoneyVoiceEmitter emitter)
    {
        Masked        = maskedEnemy;
        _voiceEmitter = emitter;
        _scrapManager = maskedEnemy.GetComponent<MaskedScrapManager>();
        _bloodyReveal = maskedEnemy.GetComponent<PhoneyBloodyReveal>();

        if (maskedEnemy.agent != null)
        {
            maskedEnemy.agent.autoTraverseOffMeshLink = false;
        }

        _hostilityChance = Mathf.Clamp01(PhoneyPlugin.InitialHostilityChance.Value);
        _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
        _isHostilePrimed = false;
        _ambientChatterTimer = Time.time + UnityEngine.Random.Range(30f, 45f);

        SetSubState(UndercoverSubState.SeekingPlayer, "Initial setup");
        TransitionTo(MimicPhase.UndercoverLooting);

        maskedEnemy.handsOut = false;
        if (maskedEnemy.creatureAnimator != null)
        {
            maskedEnemy.creatureAnimator.SetBool("HandsOut", false);
        }
        if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
        {
            maskedEnemy.SetHandsOutClientRpc(false);
        }

        // Immediately scan for the closest player to stalk from frame 1!
        var bestPlayer = GetClosestLivingPlayer(out float initDist);
        if (bestPlayer != null)
        {
            TargetPlayer = bestPlayer;
            maskedEnemy.SetDestinationToPosition(bestPlayer.transform.position);
            PhoneyPlugin.Logger.LogInfo(
                $"[DeceptiveAI] '{maskedEnemy.gameObject.name}' locked onto player '{bestPlayer.playerUsername}' (dist: {initDist:F1}m) from spawn! Sprinting to stalk target.");
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[DeceptiveAI] '{maskedEnemy.gameObject.name}' initialized. Paranoia timer: {PhoneyPlugin.ParanoiaIntervalSeconds.Value:F0}s (initial chance: {_hostilityChance:P0}). WalkSpeed: {WalkSpeed:F1}, SprintSpeed: {UndercoverSprintSpeed:F1}.");
    }

    // ─── Phase Transitions ────────────────────────────────────────────────────

    public void TransitionTo(MimicPhase next)
    {
        if (CurrentPhase == next && Time.time - _phaseEnteredTime < 1.0f) return;

        MimicPhase previous = CurrentPhase;
        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked?.gameObject.name}': {CurrentPhase} → {next}");
        CurrentPhase      = next;
        _phaseEnteredTime = Time.time;

        if (Masked == null) return;
        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;

        switch (next)
        {
            case MimicPhase.UndercoverLooting:
                Masked.currentBehaviourStateIndex = 0;
                Masked.stopAndStareTimer = -999f;
                Masked.movingTowardsTargetPlayer = false;

                // Calm down: ensure demonic voice mode is off and natural voice is active
                _voiceEmitter?.SetDemonicMode(false);

                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, WalkSpeed);
                Masked.handsOut = false;
                if (Masked.creatureAnimator != null) Masked.creatureAnimator.SetBool("HandsOut", false);
                if (isServer)
                {
                    Masked.SetHandsOutClientRpc(false);
                    if (Masked.running)   { Masked.running   = false; Masked.SetRunningServerRpc(false);  }
                    if (Masked.crouching) { Masked.crouching = false; Masked.SetCrouchingServerRpc(false); }
                }
                SetGlow(false);

                // If returning from pursuit or retreat, take on the appearance and voice of a DIFFERENT player!
                if (previous == MimicPhase.TacticalRetreat || previous == MimicPhase.AmbushStrike)
                {
                    ReDisguiseAsNewPlayer();
                }
                else
                {
                    _bloodyReveal?.ResetDisguise();
                }

                // Reset paranoia timer & hostility back to initial cycle
                _isHostilePrimed = false;
                _hostilityChance = Mathf.Clamp01(PhoneyPlugin.InitialHostilityChance.Value);
                _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                _lastParanoiaLogTime = Time.time;

                if (Masked.isOutside)
                {
                    SetSubState(UndercoverSubState.ReturningToFacility, "Outside after reset - heading back inside");
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    if (outDoor != null)
                    {
                        Vector3 doorPos = MaskedScrapManager.GetDoorPosition(outDoor);
                        Masked.SetDestinationToPosition(doorPos);
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' is outside after losing pursuit — running back to facility entrance at {doorPos}!");
                    }
                }
                else
                {
                    SetSubState(UndercoverSubState.SearchingRooms, "Inside facility - searching for rooms/scrap");
                    PickRoomNearPlayer(TargetPlayer);
                    Masked.SetDestinationToPosition(_currentRoomTarget);
                }
                break;

            case MimicPhase.LuringFollower:
                Masked.currentBehaviourStateIndex = 0;
                Masked.stopAndStareTimer = -999f;
                Masked.movingTowardsTargetPlayer = false;
                _voiceEmitter?.SetDemonicMode(false);
                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, WalkSpeed);
                SetGlow(false);
                break;

            case MimicPhase.AmbushStrike:
                Masked.currentBehaviourStateIndex = 0;
                Masked.stopAndStareTimer = -999f;

                // Stop any friendly banter immediately and turn on demonic audio distortion!
                _voiceEmitter?.StopSpeaking();
                _voiceEmitter?.SetDemonicMode(true);

                // Drop ALL held and pocketed scrap immediately when revealing true form!
                // Weight must be fully zeroed so pursuit speed is unaffected by loot
                _scrapManager ??= Masked.GetComponent<MaskedScrapManager>();
                if (_scrapManager != null && _scrapManager.CarriedCount > 0)
                {
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Pursuit] '{Masked.gameObject.name}' dropping ALL {_scrapManager.CarriedCount} carried item(s) on aggro! " +
                        $"Weight before drop: {_scrapManager.TotalCarryWeight:F2}x.");
                    _scrapManager.DropHeldScrapImmediately();
                }
                _isPickingUpScrap = false;
                _isUsingDoor = false;
                DoorInteractionTarget = Vector3.zero;
                _isHostilePrimed = false;
                _lastSeenPlayerTime = Time.time;
                _hadLOSLastFrame = true;
                _lastLOSLogTime = Time.time;
                if (TargetPlayer == null || !IsPlayerValidTarget(TargetPlayer))
                {
                    TargetPlayer = GetClosestLivingPlayer(out _);
                }

                if (TargetPlayer != null)
                {
                    Masked.targetPlayer = TargetPlayer;
                    Masked.movingTowardsTargetPlayer = true;
                    Masked.SetMovingTowardsTargetPlayer(TargetPlayer);
                    Masked.SetDestinationToPosition(TargetPlayer.transform.position);

                    float distToPlayer = Vector3.Distance(transform.position, TargetPlayer.transform.position);
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Pursuit] AMBUSH PURSUIT STARTED! Target: '{TargetPlayer.playerUsername}' (dist: {distToPlayer:F1}m, insideFactory: {TargetPlayer.isInsideFactory}, mimicOutside: {Masked.isOutside}, JogSpeed: {AmbushJogSpeed:F2} m/s, SprintSpeed: {AmbushSprintSpeed:F2} m/s).");
                }

                _ambushStamina = 1.0f;
                _isAmbushBurst = false;

                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushJogSpeed);
                if (Masked.agent != null && Masked.agent.isOnNavMesh)
                {
                    Masked.agent.stoppingDistance = 0f;
                }

                // Ambush mode: hide visual props, zero holding layers, trigger zombie arms
                var holder = GetComponent<MaskedHeldItemHolder>();
                if (holder != null)
                {
                    holder.SetHeldToolActive(false);
                    holder.SetWalkieActive(false);
                    holder.UpdateAnimationLayers(hasRealScrap: false, isAggressive: true);
                }
                else
                {
                    Masked.handsOut = true;
                    if (Masked.creatureAnimator != null) Masked.creatureAnimator.SetBool("HandsOut", true);
                }

                if (isServer)
                {
                    Masked.SetHandsOutClientRpc(true);
                    if (Masked.running) { Masked.running = false; Masked.SetRunningServerRpc(false); }
                }
                SetGlow(true);
                TriggerAmbushScream();
                _nextPursuitTauntTime = Time.time + UnityEngine.Random.Range(8.0f, 15.0f);
                _bloodyReveal ??= Masked.GetComponent<PhoneyBloodyReveal>();
                if (PhoneyPlugin.EnableBloodyReveal.Value)
                    _bloodyReveal?.TriggerBloodyReveal();
                break;


            case MimicPhase.TacticalRetreat:
                Masked.currentBehaviourStateIndex = 0;
                Masked.stopAndStareTimer = -999f;
                Masked.movingTowardsTargetPlayer = false;

                // Calm down: restore voice back to normal and stop any ongoing pursuit taunt
                _voiceEmitter?.SetDemonicMode(false);
                _voiceEmitter?.StopSpeaking();

                _scrapManager ??= Masked.GetComponent<MaskedScrapManager>();
                _scrapManager?.DropHeldScrapImmediately();

                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushSprintSpeed);
                Masked.handsOut = false;
                if (Masked.creatureAnimator != null) Masked.creatureAnimator.SetBool("HandsOut", false);
                if (isServer)
                {
                    Masked.SetHandsOutClientRpc(false);
                    if (!Masked.running)  { Masked.running  = true;  Masked.SetRunningServerRpc(true);   }
                }
                SetGlow(false);
                FindFleeDestination();
                break;
        }
    }

    // ─── Frame-by-Frame Update (Paranoia Timer & 3m Proximity Trigger) ─────────

    private void Update()
    {
        if (Masked == null || Masked.isEnemyDead || !PhoneyPlugin.EnableDeceptiveAI.Value) return;

        // When inside Mineshaft elevator, smoothly update position with elevator car
        if (IsMineshaftDungeon() && Masked.IsInsideMineshaftElevator(transform.position))
        {
            Masked.MoveWithMineshaftElevator();
        }

        bool isHostOrServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;
        if (!isHostOrServer) return;

        // Ensure manual off-mesh link traversal is active so mimic climbs ladders instead of snapping/gliding
        if (Masked.agent != null && Masked.agent.autoTraverseOffMeshLink)
        {
            Masked.agent.autoTraverseOffMeshLink = false;
        }

        // Realistic ladder / link traversal handling
        if (Masked.agent != null && Masked.agent.isOnOffMeshLink && !_isTraversingOffMeshLink)
        {
            StartCoroutine(TraverseOffMeshLinkRoutine());
        }

        // ── Paranoia Escalation Timer (2-Minute Intervals) ────────────────────
        if (CurrentPhase != MimicPhase.AmbushStrike && CurrentPhase != MimicPhase.TacticalRetreat)
        {
            // Periodic countdown heartbeat log (every 30 seconds)
            if (Time.time >= _lastParanoiaLogTime + 30f)
            {
                _lastParanoiaLogTime = Time.time;
                float rem = Mathf.Max(0f, _nextParanoiaCheckTime - Time.time);
                PhoneyPlugin.Logger.LogInfo(
                    $"[DeceptiveAI] '{Masked.gameObject.name}' Paranoia status: {rem:F0}s remaining until next 2-min roll. Hostility chance: {_hostilityChance:P0}, Primed: {_isHostilePrimed}, SubState: {_subState}.");
            }

            if (Time.time >= _nextParanoiaCheckTime)
            {
                float roll = UnityEngine.Random.value;
                if (roll < _hostilityChance)
                {
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' 2-MINUTE ATTACK ROLL: Rolled {roll:P1} < chance {_hostilityChance:P1} -> HOSTILE! Checking player proximity...");

                    float aggroRange = PhoneyPlugin.AmbushDistanceThreshold != null
                        ? PhoneyPlugin.AmbushDistanceThreshold.Value
                        : 5.5f;

                    PlayerControllerB? target = TargetPlayer != null && IsPlayerValidTarget(TargetPlayer)
                        ? TargetPlayer
                        : GetClosestLivingPlayer(out _);

                    // If a target player is already nearby or visible, launch ambush attack immediately!
                    if (target != null && (Vector3.Distance(transform.position, target.transform.position) <= aggroRange || HasLineOfSight(target)))
                    {
                        float dist = Vector3.Distance(transform.position, target.transform.position);
                        bool los = HasLineOfSight(target);
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' Target player '{target.playerUsername}' is nearby/visible (dist: {dist:F1}m <= {aggroRange:F1}m, LOS: {los}) — AMBUSH STRIKE!");
                        TargetPlayer = target;
                        _isHostilePrimed = false;
                        TransitionTo(MimicPhase.AmbushStrike);
                    }
                    else
                    {
                        // No player is immediately present: prime hostility so the mimic attacks the moment a player enters aggro range
                        _isHostilePrimed = true;
                        _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                        _lastParanoiaLogTime = Time.time;
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' 2-minute attack roll hostile, but no player currently in range ({aggroRange:F1}m) or line of sight. Hostility primed! Next roll in {PhoneyPlugin.ParanoiaIntervalSeconds.Value:F0}s.");
                    }
                }
                else
                {
                    float oldChance = _hostilityChance;
                    _hostilityChance = Mathf.Clamp01(_hostilityChance + PhoneyPlugin.HostilityChanceIncrement.Value);
                    _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                    _lastParanoiaLogTime = Time.time;
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' 2-MINUTE ATTACK ROLL: Rolled {roll:P1} >= {oldChance:P1} -> PEACEFUL. Hostility chance escalated: {oldChance:P1} -> {_hostilityChance:P1} (+{PhoneyPlugin.HostilityChanceIncrement.Value:P1}). Next roll in {PhoneyPlugin.ParanoiaIntervalSeconds.Value:F0}s.");
                }
            }

            // ── Hostility Primed Check (From 2-Minute Timer Roll) ─────────────────
            // A mimic ONLY attacks when the 2-minute hostility roll has succeeded!
            // It NEVER attacks just because a player walks near it while peaceful.
            if (_isHostilePrimed)
            {
                PlayerControllerB? closePlayer = GetClosestLivingPlayer(out float playerDist);
                float aggroRange = PhoneyPlugin.AmbushDistanceThreshold != null
                    ? PhoneyPlugin.AmbushDistanceThreshold.Value
                    : 5.5f;

                // Throttled heartbeat log for primed status (every 10s)
                if (Time.time >= _lastPrimedStatusLogTime + 10f)
                {
                    _lastPrimedStatusLogTime = Time.time;
                    string pInfo = closePlayer != null ? $"'{closePlayer.playerUsername}' at {playerDist:F1}m" : "none in area";
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' Hostility is PRIMED: waiting for player in aggro range ({aggroRange:F1}m) or LOS (closest: {pInfo}).");
                }

                if (closePlayer != null)
                {
                    bool isInAggroRange = playerDist <= aggroRange;
                    bool isGreetingActive = _subState == UndercoverSubState.Greeting && _performingCrouch;
                    bool los = HasLineOfSight(closePlayer);

                    if (!isGreetingActive && (isInAggroRange || (playerDist <= 10f && los)))
                    {
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' Primed hostility triggered! Player '{closePlayer.playerUsername}' in range ({playerDist:F1}m <= {aggroRange:F1}m, LOS: {los}) — AMBUSH STRIKE!");
                        TargetPlayer = closePlayer;
                        _isHostilePrimed = false;
                    }
                }
            }
        }

        // Realistic player behavior during undercover stages:
        // Natural procedural head glances and hotbar inventory cycling!
        if (CurrentPhase == MimicPhase.UndercoverLooting || CurrentPhase == MimicPhase.LuringFollower)
        {
            UpdateHumanHeadGlancing(TargetPlayer);
            UpdateHotbarCycling();
        }
    }

    // ─── Main AI Interval (Called via DoAIInterval HarmonyPrefix) ─────────────

    public bool CustomDoAIInterval()
    {
        if (Masked == null || Masked.isEnemyDead || !PhoneyPlugin.EnableDeceptiveAI.Value)
            return false;

        if (Masked.inKillAnimation) return false;

        // Keep NavMeshAgent actively synchronized with Masked.destination every AI interval
        if (Masked.moveTowardsDestination && Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            NavMeshUtil.SafeSetDestination(Masked.agent, Masked.destination);
        }

        if (IsMineshaftDungeon() && Masked.elevatorScript == null)
        {
            Masked.elevatorScript = GetMineshaftElevator();
        }

        switch (CurrentPhase)
        {
            case MimicPhase.UndercoverLooting:
            case MimicPhase.LuringFollower:
                UpdateUndercover();
                break;

            case MimicPhase.AmbushStrike:
                UpdateAmbush();
                break;

            case MimicPhase.TacticalRetreat:
                UpdateRetreat();
                break;
        }

        return true;
    }

    /// <summary>
    /// Safely updates destination for both the Masked enemy and its NavMeshAgent,
    /// ensuring navigation stays active even when vanilla DoAIInterval is overridden.
    /// </summary>
    public void SetDestinationSafe(Vector3 pos)
    {
        if (Masked == null) return;
        Masked.SetDestinationToPosition(pos);
        if (Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            NavMeshUtil.SafeSetDestination(Masked.agent, pos);
        }
    }

    /// <summary>
    /// Scans for the closest living crewmate who is currently inside the facility.
    /// </summary>
    private PlayerControllerB? GetClosestLivingFacilityPlayer()
    {
        var allPlayers = StartOfRound.Instance?.allPlayerScripts;
        if (allPlayers == null || Masked == null) return null;
        PlayerControllerB? best = null;
        float bestDist = float.MaxValue;
        foreach (var p in allPlayers)
        {
            if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
            if (!p.isInsideFactory) continue;
            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }
        return best;
    }

    // ─── Phase 1: Realistic Undercover Scrap Looting & Room Searching ─────────

    private void UpdateUndercover()
    {
        if (Masked == null) return;
        if (_isUsingDoor) return;

        PlayerControllerB? player;
        if (!Masked.isOutside)
        {
            // Inside facility: prioritize crewmate who is also inside the facility!
            player = (TargetPlayer != null && IsPlayerValidTarget(TargetPlayer) && TargetPlayer.isInsideFactory)
                ? TargetPlayer
                : GetClosestLivingFacilityPlayer();
        }
        else
        {
            player = TargetPlayer != null && IsPlayerValidTarget(TargetPlayer)
                ? TargetPlayer
                : GetClosestLivingPlayer(out _);
        }

        if (player != null)
        {
            TargetPlayer = player;
            // Never set Masked.targetPlayer during undercover mode!
            // When targetPlayer is set, vanilla Update forces model rotation towards targetPlayer,
            // making the mimic walk backwards and stare upwards!
            Masked.targetPlayer = null;
            Masked.movingTowardsTargetPlayer = false;
        }

        if (UpdateAttendPlayer(player)) return;

        switch (_subState)
        {
            case UndercoverSubState.SeekingPlayer:
                UpdateSeekingPlayer(player);
                break;

            case UndercoverSubState.Greeting:
                UpdateGreeting(player);
                break;

            case UndercoverSubState.SearchingRooms:
                UpdateSearchingRooms(player);
                break;

            case UndercoverSubState.InspectingRoom:
                UpdateInspectingRoom(player);
                break;

            case UndercoverSubState.ApproachingScrap:
                UpdateApproachingScrap(player);
                break;

            case UndercoverSubState.HaulingScrapToEntrance:
                UpdateHaulingScrapToEntrance(player);
                break;

            case UndercoverSubState.ReturningToFacility:
                UpdateReturningToFacility(player);
                break;
        }

        // Ambient proactive banter: mimic occasionally speaks unfiltered comments while exploring near player
        if (Time.time >= _ambientChatterTimer)
        {
            _ambientChatterTimer = Time.time + UnityEngine.Random.Range(35f, 50f);
            if (player != null && Vector3.Distance(transform.position, player.transform.position) <= 16f &&
                _voiceEmitter != null && _voiceEmitter.CanSpeak && UnityEngine.Random.value < 0.40f)
            {
                var clip = ClipVault.Instance.FindProactiveClip(_voiceEmitter.ImpersonatedSteamId);
                if (clip != null)
                {
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] Ambient exploration chatter near player: \"{clip.Transcript}\"");
                    ClipVault.Instance.RecordClipPlayed(clip);
                    PhoneyNetworkManager.Instance.SyncAndPlayClip(Masked, _voiceEmitter, clip, 0.2f);
                }
            }
        }
    }

    // ── Player-like Stamina & Weight System ───────────────────────────────────

    private void UpdateStaminaAndSpeed(Vector3 targetDestination)
    {
        if (Masked == null || Masked.agent == null) return;
        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;

        // Weight carry penalty: calibrated to match Lethal Company player feel.
        // Two egg beaters (~28 lbs, extraWeight ~0.28) slows down by only ~16% (0.84x), not crawl.
        // Heavy scrap like Cash Register (84 lbs) or Gold Bar (77 lbs) slows down noticeably (~0.65x).
        _scrapManager ??= GetComponent<MaskedScrapManager>();
        float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
        // In Lethal Company, extraWeight = totalWeight - 1.0f.
        // HUD displays (extraWeight * 105) lbs.
        // 0 lbs = 0.0f, 15 lbs = 0.14f, 30 lbs = 0.28f, 50 lbs = 0.48f, 80 lbs = 0.76f
        float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);

        // Realistic and sensible weight curve:
        // 0 lbs (0.00) -> 1.00x
        // 15 lbs (0.14) -> 0.91x
        // 30 lbs (0.28, 2 egg beaters) -> 0.84x (can still sprint easily!)
        // 50 lbs (0.48, engine/axle) -> 0.75x
        // 80 lbs (0.76, gold bar/register) -> 0.65x
        // Floor clamped at 0.40x so mimic never becomes immobile
        float weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 0.70f), 0.40f, 1.0f);

        // Only truly heavy scrap (> 60 lbs / extraWeight > 0.58f) prevents sustained sprinting
        bool isHeavy = extraWeight > 0.58f;

        float distToDest = Vector3.Distance(transform.position, targetDestination);

        if (Masked.agent != null && Masked.agent.isOnNavMesh && Masked.agent.isStopped)
        {
            _stamina = Mathf.Clamp01(_stamina + Time.deltaTime * 0.12f);
            if (_isSprinting)
            {
                _isSprinting = false;
                if (isServer) { Masked.running = false; Masked.SetRunningServerRpc(false); }
            }
            return;
        }

        // Sprint to destination if far enough away, having sufficient stamina, and not overburdened with heavy loot
        if (!_isSprinting && distToDest > 6.0f && _stamina > 0.45f && !isHeavy)
        {
            _isSprinting = true;
            if (isServer) { Masked.running = true; Masked.SetRunningServerRpc(true); }
        }
        else if (_isSprinting && (_stamina <= 0.08f || distToDest < 2.5f || isHeavy))
        {
            _isSprinting = false;
            if (isServer) { Masked.running = false; Masked.SetRunningServerRpc(false); }
        }

        if (_isSprinting)
        {
            float drainMultiplier = 1.0f + extraWeight * 2.0f;
            _stamina = Mathf.Clamp01(_stamina - Time.deltaTime * 0.14f * drainMultiplier);
            NavMeshUtil.SafeSetSpeed(Masked.agent, UndercoverSprintSpeed * weightFactor);
        }
        else
        {
            _stamina = Mathf.Clamp01(_stamina + Time.deltaTime * 0.07f);
            NavMeshUtil.SafeSetSpeed(Masked.agent, WalkSpeed * weightFactor);
        }

        // Align body facing direction to actual movement velocity to eliminate looking sideways / backwards
        if (Masked.agent != null && Masked.agent.isOnNavMesh && Masked.agent.velocity.sqrMagnitude > 0.15f)
        {
            Vector3 moveDir = Masked.agent.velocity;
            moveDir.y = 0;
            if (moveDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), Time.deltaTime * 8f);
            }
        }
    }

    /// <summary>
    /// Enforces human-like speeds, stamina pacing, and carry-weight penalties every single frame.
    /// Called from MaskedEnemyPatch.UpdatePostfix to prevent vanilla MaskedPlayerEnemy.Update() from wiping out speed modifiers.
    /// </summary>
    public void EnforceHumanSpeedAndMovement()
    {
        if (Masked == null || Masked.isEnemyDead || Masked.agent == null || !Masked.agent.isOnNavMesh) return;

        if (_isTraversingOffMeshLink)
        {
            // TraverseOffMeshLinkRoutine directly controls movement, speed, and facing
            return;
        }

        if (_isUsingDoor)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, true);
            NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);
            NavMeshUtil.SafeSetSpeed(Masked.agent, 0f);
            return;
        }

        _scrapManager ??= GetComponent<MaskedScrapManager>();
        float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
        float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);

        // During AmbushStrike: weight penalty is COMPLETELY IGNORED!
        // The mimic drops all items on aggro, but even if something remained, pursuit speed must be full.
        float weightFactor;
        if (CurrentPhase == MimicPhase.AmbushStrike || CurrentPhase == MimicPhase.TacticalRetreat)
        {
            weightFactor = 1.0f;
        }
        else
        {
            weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 0.70f), 0.40f, 1.0f);
        }

        float targetSpeed;

        if (CurrentPhase == MimicPhase.AmbushStrike)
        {
            var reveal = _bloodyReveal ?? GetComponent<PhoneyBloodyReveal>();
            if (reveal != null && reveal.IsTransforming)
            {
                NavMeshUtil.SafeSetStopped(Masked.agent, true);
                NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);
                return;
            }

            float elapsed = Time.time - _phaseEnteredTime;
            float sprintBase = AmbushSprintSpeed;
            float jogBase = AmbushJogSpeed;

            if (elapsed < 3.7f)
            {
                targetSpeed = jogBase * weightFactor;
            }
            else if (_isAmbushBurst)
            {
                targetSpeed = sprintBase * weightFactor;
            }
            else
            {
                targetSpeed = jogBase * weightFactor;
            }
        }
        else
        {
            if (Masked.agent.isStopped || _performingCrouch || _isPickingUpScrap)
            {
                targetSpeed = 0f;
            }
            else if (_isSprinting)
            {
                targetSpeed = UndercoverSprintSpeed * weightFactor;
            }
            else
            {
                targetSpeed = WalkSpeed * weightFactor;
            }
        }

        NavMeshUtil.SafeSetSpeed(Masked.agent, targetSpeed);

        // Synchronize authentic player holding animation layers & eliminate zombie arms
        var holder = GetComponent<MaskedHeldItemHolder>();
        bool hasRealScrap = _scrapManager != null && _scrapManager.HasHeldScrap;
        bool isAggressive = CurrentPhase == MimicPhase.AmbushStrike;

        if (holder != null)
        {
            if (hasRealScrap)
            {
                holder.SetHeldToolActive(false);
            }
            else if (!isAggressive)
            {
                holder.SetHeldToolActive(true);
            }
            holder.UpdateAnimationLayers(hasRealScrap, isAggressive);
        }
        else if (!isAggressive)
        {
            Masked.handsOut = false;
            if (Masked.creatureAnimator != null)
            {
                Masked.creatureAnimator.SetBool("HandsOut", false);
            }
        }


        // Smoothly align facing with actual NavMesh velocity to eliminate awkward side-walking
        if (Masked.agent.velocity.sqrMagnitude > 0.15f)
        {
            Vector3 moveDir = Masked.agent.velocity;
            moveDir.y = 0;
            if (moveDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), Time.deltaTime * 9f);
            }
        }
    }

    // ── Sub-state 1: Seeking Player ───────────────────────────────────────────

    private void UpdateSeekingPlayer(PlayerControllerB? player)
    {
        // On spawn / re-entry: scan ALL living players and pick the best target to stalk ASAP
        var allPlayers = StartOfRound.Instance?.allPlayerScripts;
        if (allPlayers != null && Masked != null)
        {
            PlayerControllerB? bestSameEnv = null;
            float bestSameEnvDist = float.MaxValue;
            PlayerControllerB? bestAny = null;
            float bestAnyDist = float.MaxValue;

            foreach (var p in allPlayers)
            {
                if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
                float d = Vector3.Distance(transform.position, p.transform.position);

                // Track absolute closest
                if (d < bestAnyDist) { bestAnyDist = d; bestAny = p; }

                // Track closest in same environment (inside/outside)
                bool sameEnv = p.isInsideFactory == !Masked.isOutside;
                if (sameEnv && d < bestSameEnvDist) { bestSameEnvDist = d; bestSameEnv = p; }
            }

            // Prefer same-environment player; fall back to absolute closest
            var chosen = bestSameEnv ?? bestAny;
            if (chosen != null && chosen != TargetPlayer)
            {
                TargetPlayer = chosen;
                player = chosen;
                PhoneyPlugin.Logger.LogInfo(
                    $"[DeceptiveAI] '{Masked.gameObject.name}' scanned all players on spawn. " +
                    $"Stalking '{chosen.playerUsername}' (dist: {Vector3.Distance(transform.position, chosen.transform.position):F1}m, " +
                    $"sameEnv: {(bestSameEnv != null ? "yes" : "no — cross-environment fallback")}).");
            }
        }

        if (player == null)
        {
            WanderToRandomNode();
            return;
        }

        // In Mineshaft: if player is on the other floor, take the elevator!
        if (IsMineshaftDungeon() && player.isInsideFactory && !Masked!.isOutside)
        {
            var elevator = GetMineshaftElevator();
            if (elevator != null && elevator.elevatorBottomPoint != null)
            {
                bool targetOnUpper = player.transform.position.y > (elevator.elevatorBottomPoint.position.y + 25f);
                bool mimicOnUpper = IsOnMineshaftUpperFloor();

                if (targetOnUpper != mimicOnUpper)
                {
                    SafeUseElevator(targetOnUpper);
                    return;
                }
                else if (Masked.IsInsideMineshaftElevator(transform.position) && !elevator.elevatorFinishedMoving)
                {
                    return;
                }
            }
        }

        // Head toward the player until we are back inside their companion band.
        SetDestinationSafe(player.transform.position);
        UpdateStaminaAndSpeed(player.transform.position);
        float dist = LeashDistance(player);
        GetLeashBand(player, out _, out float comfortMax, out _, out _);

        if (dist <= comfortMax && HasLineOfSight(player))
        {
            bool shouldGreet = !_hasGreetedPlayer;
            if (!shouldGreet)
            {
                // Already said hi recently — just slot back in and keep looting nearby.
                SetSubState(UndercoverSubState.SearchingRooms, $"Rejoined '{player.playerUsername}' ({dist:F1}m)");
                PickRoomNearPlayer(player);
                SetDestinationSafe(_currentRoomTarget);
                return;
            }

            SetSubState(UndercoverSubState.Greeting, $"Spotted player '{player.playerUsername}' with LOS (dist: {dist:F1}m)");
            _subStateTimer = Time.time + UnityEngine.Random.Range(1.2f, 2.0f);

            NavMeshUtil.SafeSetStopped(Masked?.agent, true);
            NavMeshUtil.SafeSetVelocity(Masked?.agent, Vector3.zero);

            if (!_hasGreetedPlayer)
            {
                _hasGreetedPlayer = true;
                _greetingResetTime = Time.time + 60f;

                _voiceEmitter?.TryPlayEncounterGreeting();
                StartCoroutine(FriendlyCrouchGreeting());
            }
        }
    }

    // ── Sub-state 2: Greeting ─────────────────────────────────────────────────

    private void UpdateGreeting(PlayerControllerB? player)
    {
        // Stand completely still facing the player during the greeting
        NavMeshUtil.SafeSetStopped(Masked!.agent, true);
        NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);

        if (player != null)
        {
            Vector3 lookDir = (player.transform.position - transform.position).normalized;
            lookDir.y = 0;
            if (lookDir != Vector3.zero)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 6f);
        }

        // Wait until both timer and crouch animation finish before going off to work
        if (Time.time >= _subStateTimer && !_performingCrouch)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            SetSubState(UndercoverSubState.SearchingRooms, "Greeting complete");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
        }
    }

    // ── Sub-state 3: Searching Rooms for Scrap ────────────────────────────────

    private void UpdateSearchingRooms(PlayerControllerB? player)
    {
        if (_scrapManager == null) _scrapManager = GetComponent<MaskedScrapManager>();

        // In Mineshaft: if on upper floor and player is down in the mine, or no loot on upper floor, take elevator down!
        if (IsMineshaftDungeon() && !Masked!.isOutside && IsOnMineshaftUpperFloor())
        {
            var elevator = GetMineshaftElevator();
            if (elevator != null && elevator.elevatorBottomPoint != null)
            {
                bool playerIsDown = player != null && player.isInsideFactory && player.transform.position.y < (elevator.elevatorBottomPoint.position.y + 25f);
                if (playerIsDown)
                {
                    SafeUseElevator(goUp: false);
                    return;
                }
            }
        }

        // Check if there is real reachable scrap lying nearby
        if (PhoneyPlugin.EnableScrapLooting.Value && _scrapManager != null && _scrapManager.CanPickUpMoreScrap())
        {
            var scrap = _scrapManager.FindNearbyReachableScrap(20f, false, GetScrapLeashCenter(player, out float scrapLeashRadius), scrapLeashRadius);
            if (scrap != null)
            {
                _targetScrap = scrap;
                SetSubState(UndercoverSubState.ApproachingScrap, $"Spotted scrap '{scrap.itemProperties?.itemName ?? "Item"}' ({_scrapManager.CarriedCount + 1}/{PhoneyPlugin.MaxCarriedScrapCount.Value})");
                SetDestinationSafe(scrap.transform.position);
                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked!.gameObject.name}' spotted scrap '{scrap.itemProperties?.itemName ?? "Item"}' ({_scrapManager.CarriedCount + 1}/{PhoneyPlugin.MaxCarriedScrapCount.Value}) — pathing to pick it up!");
                return;
            }
            else if (IsMineshaftDungeon() && !Masked!.isOutside && IsOnMineshaftUpperFloor())
            {
                // No scrap on upper floor! Take elevator down into the mines to search for scrap!
                var elevator = GetMineshaftElevator();
                if (elevator != null)
                {
                    if (Masked.IsInsideMineshaftElevator(transform.position))
                    {
                        if (!elevator.elevatorFinishedMoving) return;
                    }
                    else
                    {
                        SafeUseElevator(goUp: false);
                        return;
                    }
                }
            }
        }

        // Anti-ship camping/getting stuck: if outside and near or inside the ship (and not actively hauling loot to the ship),
        // real crewmates NEVER loiter in the ship next to the terminal; they return into the facility to loot!
        if (Masked != null && Masked.isOutside && MaskedScrapManager.IsNearShip(transform.position, 7.5f) && _deliveryPlan != ScrapDeliveryPlan.HaulToShip)
        {
            SetSubState(UndercoverSubState.ReturningToFacility, "Near ship outside, heading back to facility entrance");
            var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
            if (outDoor != null) SetDestinationSafe(MaskedScrapManager.GetDoorPosition(outDoor));
            return;
        }

        // If outside with no held scrap and our crewmate is inside the facility:
        // Always head back inside through the door to stick together!
        if (Masked != null && Masked.isOutside && player != null && player.isInsideFactory && (_scrapManager == null || !_scrapManager.HasHeldScrap))
        {
            SetSubState(UndercoverSubState.ReturningToFacility, "Crewmate is inside facility, returning through door to rejoin them");
            var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
            if (outDoor != null) SetDestinationSafe(MaskedScrapManager.GetDoorPosition(outDoor));
            return;
        }

        // Companion leash: roam and loot freely inside the player's band, drift back when outside it.
        if (player != null && IsSameEnvironment(player))
        {
            float leash = LeashDistance(player);
            GetLeashBand(player, out _, out _, out float softLimit, out float hardLimit);

            if (leash > hardLimit)
            {
                SetSubState(UndercoverSubState.SeekingPlayer, $"Player beyond hard leash ({leash:F1}m > {hardLimit:F0}m), catching up");
                SetDestinationSafe(player.transform.position);
                UpdateStaminaAndSpeed(player.transform.position);
                return;
            }

            // Current room target has drifted out of the player's band (they walked off) or we're past the soft
            // limit: commit to a NEW room inside the band. Throttled so the target isn't re-rolled every AI tick.
            bool roomOutOfBand = Vector3.Distance(_currentRoomTarget, player.transform.position) > softLimit;
            if ((leash > softLimit || roomOutOfBand) && Time.time >= _nextLeashRepickTime)
            {
                _nextLeashRepickTime = Time.time + 2.0f;
                PickRoomNearPlayer(player);
            }

            // Too close and not being addressed: wander off to another spot in the band rather than hovering.
            if (leash < LeashPersonalSpace && Time.time >= _nextPersonalSpaceTime)
            {
                _nextPersonalSpaceTime = Time.time + 4.0f;
                PickRoomNearPlayer(player);
            }
        }
        else if (player != null && LeashDistance(player) > 30f && Time.time >= _nextLeashRepickTime)
        {
            // Different environment (e.g. mimic outside, player inside) with no loot run active — drift toward their area.
            _nextLeashRepickTime = Time.time + 3.0f;
            PickRoomNearPlayer(player);
        }

        // Room navigation: check if arrived at the chosen room
        float distToRoom = Vector3.Distance(transform.position, _currentRoomTarget);
        if (distToRoom <= 2.2f)
        {
            SetSubState(UndercoverSubState.InspectingRoom, $"Arrived at target room ({distToRoom:F1}m)");
            _subStateTimer = Time.time + UnityEngine.Random.Range(2.8f, 5.0f);
            _inspectNextTurnTime = Time.time + UnityEngine.Random.Range(1.0f, 1.8f);
            _inspectTargetRotation = transform.rotation;
            NavMeshUtil.SafeSetStopped(Masked!.agent, true);

            // 35% chance to crouch inspect lower shelves/counters
            if (UnityEngine.Random.value < 0.35f && !_performingCrouch)
            {
                StartFriendlyCrouch();
            }
        }
        else
        {
            SetDestinationSafe(_currentRoomTarget);
            UpdateStaminaAndSpeed(_currentRoomTarget);
        }
    }

    // ── Sub-state 4: Inspecting Room ──────────────────────────────────────────

    private void UpdateInspectingRoom(PlayerControllerB? player)
    {
        // If inside facility and player moves away (> 13m), break inspection early to stick with them!
        if (!Masked!.isOutside && player != null && player.isInsideFactory && !_performingCrouch)
        {
            float pDist = Vector3.Distance(transform.position, player.transform.position);
            if (pDist > 13.0f)
            {
                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                SetSubState(UndercoverSubState.SearchingRooms, $"Player moving away ({pDist:F1}m), breaking inspection to follow");
                PickRoomNearPlayer(player);
                SetDestinationSafe(_currentRoomTarget);
                return;
            }
        }

        // Human-like inspection: pause and look at points of interest instead of continuous oscillating rotation
        if (Time.time >= _inspectNextTurnTime)
        {
            _inspectNextTurnTime = Time.time + UnityEngine.Random.Range(1.3f, 2.5f);
            float turnAngle = UnityEngine.Random.Range(30f, 65f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            _inspectTargetRotation = transform.rotation * Quaternion.Euler(0f, turnAngle, 0f);
        }
        transform.rotation = Quaternion.RotateTowards(transform.rotation, _inspectTargetRotation, 75f * Time.deltaTime);

        // Disruptive behavior: 25% chance while inspecting a room corner inside facility to drop/stash carried scrap
        if (!Masked.isOutside && _scrapManager != null && _scrapManager.HasHeldScrap && UnityEngine.Random.value < 0.25f && !_performingCrouch)
        {
            Vector3 stashPos = transform.position + transform.forward * 0.8f;
            _scrapManager.DropRealScrap(stashPos);
            StartFriendlyCrouch();
            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' disruptively stashed carried scrap inside room corner at {stashPos}.");
        }

        // While inspecting shelves/corners, keep eyes open for scrap
        if (PhoneyPlugin.EnableScrapLooting.Value && _scrapManager != null && _scrapManager.CanPickUpMoreScrap())
        {
            var scrap = _scrapManager.FindNearbyReachableScrap(16f);
            if (scrap != null)
            {
                NavMeshUtil.SafeSetStopped(Masked!.agent, false);
                _targetScrap = scrap;
                SetSubState(UndercoverSubState.ApproachingScrap, $"Spotted scrap '{scrap.itemProperties?.itemName ?? "Item"}' while inspecting room");
                SetDestinationSafe(scrap.transform.position);
                return;
            }
        }

        if (Time.time >= _subStateTimer && !_performingCrouch)
        {
            NavMeshUtil.SafeSetStopped(Masked!.agent, false);
            SetSubState(UndercoverSubState.SearchingRooms, "Inspection finished, picking next room");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
        }
    }

    // ── Sub-state 5: Approaching Scrap ────────────────────────────────────────

    private void UpdateApproachingScrap(PlayerControllerB? player)
    {
        if (_isPickingUpScrap) return;

        if (_targetScrap == null || _targetScrap.isHeld || _targetScrap.isPocketed || _targetScrap.deactivated ||
            MaskedScrapManager.IsKeyItem(_targetScrap) ||
            (_targetScrap.NetworkObject != null && MaskedScrapManager.GloballyProcessedScrapIds.Contains(_targetScrap.NetworkObject.NetworkObjectId)) ||
            MaskedScrapManager.IsNearEntranceOrShip(_targetScrap.transform.position))
        {
            NavMeshUtil.SafeSetStopped(Masked!.agent, false);
            SetSubState(UndercoverSubState.SearchingRooms, "Target scrap invalid or already taken");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
            return;
        }

        UpdateStaminaAndSpeed(_targetScrap.transform.position);

        float dist = Vector3.Distance(transform.position, _targetScrap.transform.position);
        if (dist <= 1.6f)
        {
            StartCoroutine(HumanPickUpScrapRoutine(_targetScrap));
        }
        else
        {
            SetDestinationSafe(_targetScrap.transform.position);
        }
    }

    // ── Sub-state 6: Hauling Scrap (Door Transition, Ship, or Disrupt Drop) ──

    private void UpdateHaulingScrapToEntrance(PlayerControllerB? player)
    {
        if (Masked == null) return;

        if (_scrapManager == null || !_scrapManager.HasHeldScrap)
        {
            SetSubState(UndercoverSubState.SearchingRooms, "No scrap held, returning to search");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
            return;
        }

        // 1. If inside facility, walk to the interior exit door to take the scrap outside!
        if (!Masked.isOutside)
        {
            if (IsMineshaftDungeon())
            {
                var elevator = GetMineshaftElevator();
                if (elevator != null)
                {
                    // Ensure Masked.elevatorScript is always synced
                    if (Masked.elevatorScript == null) Masked.elevatorScript = elevator;

                    // Check if we are at the bottom of the mineshaft (in the mine)
                    if (!IsOnMineshaftUpperFloor())
                    {
                        Vector3 elevatorBottomPos = elevator.elevatorBottomPoint != null
                            ? elevator.elevatorBottomPoint.position
                            : elevator.transform.position;

                        float distToElevator = Vector3.Distance(transform.position, elevatorBottomPos);
                        UpdateStaminaAndSpeed(elevatorBottomPos);

                        int stagedCount = MaskedScrapManager.CountScrapNearElevatorBottom(elevatorBottomPos, 9.0f);
                        int totalElevatorLoot = stagedCount + (_scrapManager != null && _scrapManager.HasHeldScrap ? 1 : 0);

                        // Put loot in the elevator shaft, and until there's a decent amount of loot (2+), keep gathering!
                        // Only stage if total gathered loot is < 2 AND we are holding scrap to stage!
                        if (totalElevatorLoot < 2 && _scrapManager != null && _scrapManager.HasHeldScrap)
                        {
                            if (distToElevator <= 3.8f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 1.0f && distToElevator <= 6.0f))
                            {
                                Vector3 dropPos = transform.position + transform.forward * 0.8f;
                                _scrapManager.DropRealScrap(dropPos, isElevatorStaged: true);
                                if (!_performingCrouch) StartFriendlyCrouch();

                                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' staged loot at Mineshaft elevator bottom ({stagedCount + 1}/2 staged). Heading back into mine tunnels for more loot!");
                                SetSubState(UndercoverSubState.SearchingRooms, $"Staged loot at elevator bottom ({stagedCount + 1}/2 staged)");
                                PickRoomNearPlayer(player);
                                SetDestinationSafe(_currentRoomTarget);
                                return;
                            }
                            else
                            {
                                SetDestinationSafe(elevatorBottomPos);
                                return;
                            }
                        }
                        else
                        {
                            // Decent amount of loot reached (totalElevatorLoot >= 2)! Ride the elevator UP to the surface!
                            if (Masked.IsInsideMineshaftElevator(transform.position))
                            {
                                // Inside elevator car! If elevator finished moving and we are at the top, step out!
                                if (elevator.elevatorFinishedMoving && IsOnMineshaftUpperFloor())
                                {
                                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' arrived at upper floor via elevator! Proceeding to exit door.");
                                    // Fall through to normal exit door hauling
                                }
                                else
                                {
                                    // While moving inside car, just ride along smoothly
                                    if (!elevator.elevatorFinishedMoving) return;
                                    SafeUseElevator(goUp: true);
                                    return;
                                }
                            }
                            else
                            {
                                SafeUseElevator(goUp: true);
                                return;
                            }
                        }
                    }
                    else
                    {
                        // On upper floor of mineshaft: if currently inside elevator car, wait until finished moving
                        if (Masked.IsInsideMineshaftElevator(transform.position) && !elevator.elevatorFinishedMoving)
                        {
                            return;
                        }
                    }
                }
            }

            var interiorDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: false);
            Vector3 doorPos = interiorDoor != null ? MaskedScrapManager.GetDoorPosition(interiorDoor) : _scrapDropTarget;

            UpdateStaminaAndSpeed(doorPos);
            float distToDoor = Vector3.Distance(transform.position, doorPos);

            if (distToDoor <= 4.0f && interiorDoor != null)
            {
                Masked.stareAtTransform = null;
                Masked.LookAtPosition(doorPos, 0.6f);
            }

            if (distToDoor <= 1.25f && interiorDoor != null)
            {
                StartCoroutine(DoorTransitionRoutine(interiorDoor, toOutside: true, onComplete: () =>
                {
                    // Now outside: choose our delivery plan!
                    // Real players overwhelmingly drop loot right outside the door (catwalk/landing) to quickly head back in.
                    // Haul to ship is kept low (~15%) so mimics act like normal looters.
                    float shipChance = Mathf.Clamp01(PhoneyPlugin.HaulToShipChance.Value);
                    float disruptChance = 0.15f;
                    float doorChance = Mathf.Max(0.10f, 1.0f - (shipChance + disruptChance));
                    float roll = UnityEngine.Random.value;

                    if (roll < doorChance)
                    {
                        // Option 1: Drop outside near the main entrance door (walk 4m out on catwalk/ground away from wall)
                        _deliveryPlan = ScrapDeliveryPlan.DropAtOutsideDoor;
                        var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                        Vector3 fwd = outDoor != null && outDoor.entrancePoint != null ? outDoor.entrancePoint.forward : transform.forward;
                        _scrapDropTarget = transform.position + fwd * 4.0f;
                        if (NavMesh.SamplePosition(_scrapDropTarget, out var dropHit, 3.0f, NavMesh.AllAreas))
                        {
                            _scrapDropTarget = dropHit.position;
                        }
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped outside! Delivery plan: DropAtOutsideDoor at {_scrapDropTarget}");
                    }
                    else if (roll < doorChance + shipChance)
                    {
                        // Option 2: Haul all the way to the Ship!
                        _deliveryPlan = ScrapDeliveryPlan.HaulToShip;
                        _scrapDropTarget = StartOfRound.Instance?.shipDoorAudioSource != null
                            ? StartOfRound.Instance.shipDoorAudioSource.transform.position
                            : transform.position;
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped outside! Delivery plan: HaulToShip");
                    }
                    else
                    {
                        // Option 3: Drop somewhere random outside to disrupt the crew!
                        _deliveryPlan = ScrapDeliveryPlan.DisruptRandomDrop;
                        _scrapDropTarget = PickRandomDisruptNode();
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped outside! Delivery plan: DisruptRandomDrop at {_scrapDropTarget}");
                    }

                    SetDestinationSafe(_scrapDropTarget);
                }));
            }
            else
            {
                SetDestinationSafe(doorPos);
            }
            return;
        }

        // 2. Currently outside carrying scrap: navigate to our chosen drop target!
        UpdateStaminaAndSpeed(_scrapDropTarget);

        float distToTarget = Vector3.Distance(transform.position, _scrapDropTarget);
        float dropThreshold = (_deliveryPlan == ScrapDeliveryPlan.HaulToShip) ? 3.5f : 2.5f;

        if (distToTarget <= dropThreshold || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 0.8f))
        {
            _scrapManager.DropRealScrap(_scrapDropTarget);

            if (_deliveryPlan == ScrapDeliveryPlan.HaulToShip)
            {
                // CRITICAL ANTI-STUCK: NEVER crouch inside or near the ship!
                // Clear any crouch state, unstop agent, sprint, and immediately run back to the entrance door.
                _performingCrouch = false;
                Masked.crouching = false;
                if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
                {
                    Masked.SetCrouchingServerRpc(false);
                }
                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, UndercoverSprintSpeed);

                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' dropped scrap at ship! Immediately returning to facility entrance.");
                SetSubState(UndercoverSubState.ReturningToFacility, "Delivered scrap to ship, returning to facility to loot more");
                var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                if (outDoor != null)
                {
                    SetDestinationSafe(MaskedScrapManager.GetDoorPosition(outDoor));
                }
                return;
            }

            if (!_performingCrouch) StartFriendlyCrouch();

            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' successfully dropped scrap outside via {_deliveryPlan}!");

            if (_deliveryPlan == ScrapDeliveryPlan.DropAtOutsideDoor)
            {
                // 65% chance to immediately go back inside the facility to find more loot
                if (UnityEngine.Random.value < 0.65f)
                {
                    SetSubState(UndercoverSubState.ReturningToFacility, "Dropped scrap at outside door, returning inside to loot more");
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    if (outDoor != null) SetDestinationSafe(MaskedScrapManager.GetDoorPosition(outDoor));
                    return;
                }
            }
            else
            {
                // After random drop: 50% chance to return inside to loot more
                if (UnityEngine.Random.value < 0.50f)
                {
                    SetSubState(UndercoverSubState.ReturningToFacility, "Disrupt drop complete, returning inside facility to loot more");
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    if (outDoor != null) SetDestinationSafe(MaskedScrapManager.GetDoorPosition(outDoor));
                    return;
                }
            }

            // Otherwise, remain outside to scout/loot/stalk
            SetSubState(UndercoverSubState.SearchingRooms, "Remaining outside to scout/loot");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
        }
        else
        {
            SetDestinationSafe(_scrapDropTarget);
        }
    }

    // ── Sub-state 7: Returning to Facility Through Door ───────────────────────

    private void UpdateReturningToFacility(PlayerControllerB? player)
    {
        if (Masked == null) return;
        if (_isUsingDoor) return;

        // Anti-ship stuck safeguard: if inside or near the ship, force unstopped, clear crouching, sprint out!
        bool inShip = (StartOfRound.Instance?.shipBounds != null && StartOfRound.Instance.shipBounds.bounds.Contains(transform.position))
                      || MaskedScrapManager.IsNearShip(transform.position, 7.5f);
        if (inShip)
        {
            _performingCrouch = false;
            Masked.crouching = false;
            if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
            {
                Masked.SetCrouchingServerRpc(false);
            }
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            NavMeshUtil.SafeSetSpeed(Masked.agent, UndercoverSprintSpeed);
        }

        var outsideDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
        if (outsideDoor == null)
        {
            SetSubState(UndercoverSubState.SearchingRooms, "Outside entrance door not found");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
            return;
        }

        Vector3 doorPos = MaskedScrapManager.GetDoorPosition(outsideDoor);
        UpdateStaminaAndSpeed(doorPos);

        float dist = Vector3.Distance(transform.position, doorPos);
        if (dist <= 4.0f && outsideDoor != null)
        {
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorPos, 0.6f);
        }

        if (dist <= 1.25f && outsideDoor != null)
        {
            StartCoroutine(DoorTransitionRoutine(outsideDoor, toOutside: false, onComplete: () =>
            {
                SetSubState(UndercoverSubState.SearchingRooms, "Returned through door into facility");
                PickRoomNearPlayer(TargetPlayer);
                SetDestinationSafe(_currentRoomTarget);
                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' returned through door into facility! Searching rooms.");

                // In Mineshaft, if returning inside and we are on the upper entrance floor, ride elevator down into mine!
                if (IsMineshaftDungeon() && IsOnMineshaftUpperFloor())
                {
                    SafeUseElevator(goUp: false);
                }
            }));
        }
        else
        {
            SetDestinationSafe(doorPos);
        }
    }

    // ── Human-like Behavior Helpers: Head Glancing & Hotbar Cycling ───────────

    /// <summary>
    /// Mimics real player neck movement: occasionally casts glances at doorways, loot,
    /// room corners, or nearby teammates while the body walks along the NavMesh.
    /// </summary>
    private void UpdateHumanHeadGlancing(PlayerControllerB? player)
    {
        if (Masked == null || _isUsingDoor || _isTraversingOffMeshLink) return;
        if (DoorInteractionTarget != Vector3.zero) return;
        if (MaskedScrapManager.IsNearEntranceDoor(transform.position, 4.0f)) return;

        if (Time.time >= _nextHeadGlanceTime)
        {
            _nextHeadGlanceTime = Time.time + UnityEngine.Random.Range(3.5f, 7.0f);

            // Real player glancing behavior:
            // 35% chance: glance towards nearby teammate (if within 16m)
            // 45% chance: glance towards room interest (shelf, corner, floor, door)
            // 20% chance: look forward along path
            float roll = UnityEngine.Random.value;
            Vector3 glancePos;

            if (roll < 0.35f && player != null && Vector3.Distance(transform.position, player.transform.position) <= 16f)
            {
                // Glance at teammate's upper body / head
                glancePos = player.transform.position + Vector3.up * 1.5f;
            }
            else if (roll < 0.80f)
            {
                // Glance to left or right side of movement direction (+/- 35 to 65 degrees)
                float angle = UnityEngine.Random.Range(35f, 65f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
                Vector3 glanceDir = Quaternion.Euler(0f, angle, 0f) * transform.forward;
                float glanceDist = UnityEngine.Random.Range(4f, 10f);
                float glanceHeight = UnityEngine.Random.Range(0.4f, 1.8f); // table height to eye level
                glancePos = transform.position + glanceDir * glanceDist + Vector3.up * glanceHeight;
            }
            else
            {
                // Look straight ahead along movement direction
                glancePos = transform.position + transform.forward * 12f + Vector3.up * 1.4f;
            }

            try
            {
                Masked.LookAtPosition(glancePos);
            }
            catch { }
        }
    }

    /// <summary>
    /// Mimics real player hotbar swapping: periodically cycles through carried 1-handed items.
    /// Does NOT cycle if holding a two-handed item (in vanilla, two-handed items lock hotbar cycling).
    /// </summary>
    private void UpdateHotbarCycling()
    {
        if (Masked == null) return;
        _scrapManager ??= GetComponent<MaskedScrapManager>();
        if (_scrapManager == null) return;
        if (_isUsingDoor || _isTraversingOffMeshLink || _isPickingUpScrap) return;

        if (Time.time >= _nextHotbarCycleTime)
        {
            _nextHotbarCycleTime = Time.time + UnityEngine.Random.Range(14f, 28f);

            // Strict two-handed rules: if holding a two-handed item, NEVER cycle away from it!
            // Only cycle if carrying multiple one-handed items.
            if (_scrapManager.CarriedCount > 1 && (_scrapManager.HeldScrap?.itemProperties?.twoHanded != true))
            {
                _scrapManager.CycleInventory();
            }
        }
    }

    private void TeleportThroughDoor(EntranceTeleport? door, bool toOutside)
    {
        if (Masked == null) return;
        door ??= MaskedScrapManager.FindDoor(!toOutside);
        if (door == null) return;

        if (door.exitScript == null) door.FindExitPoint();

        var exitDoor = door.exitScript ?? MaskedScrapManager.FindDoor(toOutside);
        Transform targetPoint = exitDoor != null && exitDoor.entrancePoint != null
            ? exitDoor.entrancePoint
            : (exitDoor != null ? exitDoor.transform : door.transform);

        // Project outward vector away from door threshold to prevent wall clipping
        Vector3 forwardDir = targetPoint.forward;
        Vector3 exitPos = targetPoint.position + forwardDir * 1.8f;

        if (NavMesh.SamplePosition(exitPos, out var navHit, 2.5f, NavMesh.AllAreas))
        {
            exitPos = navHit.position;
        }
        else if (NavMesh.SamplePosition(targetPoint.position, out navHit, 1.5f, NavMesh.AllAreas))
        {
            exitPos = navHit.position;
        }

        // Face mimic away from the door wall into open walkable space
        if (forwardDir != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(forwardDir);
        }

        Masked.TeleportMaskedEnemyAndSync(exitPos, setOutside: toOutside);

        // Explicitly warp agent and synchronize position & state
        NavMeshUtil.SafeWarp(Masked.agent, exitPos);
        Masked.serverPosition = exitPos;
        Masked.SetEnemyOutside(toOutside);

        // Update held scrap status if carrying any
        if (_scrapManager != null)
        {
            foreach (var item in _scrapManager.CarriedItems)
            {
                if (item != null)
                {
                    item.isInFactory = !toOutside;
                    item.isInElevator = false;
                }
            }
        }

        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' stepped through door → toOutside={toOutside} at {exitPos} (facing: {forwardDir}, isOnNavMesh={Masked.agent?.isOnNavMesh})");
    }

    private IEnumerator DoorTransitionRoutine(EntranceTeleport door, bool toOutside, Action onComplete)
    {
        if (Masked == null) yield break;
        _isUsingDoor = true;

        Vector3 doorPos = MaskedScrapManager.GetDoorPosition(door);
        DoorInteractionTarget = doorPos;
        float distToDoor = Vector3.Distance(transform.position, doorPos);
        PhoneyPlugin.Logger.LogInfo(
            $"[Door] '{Masked.gameObject.name}' approaching door '{door.gameObject.name}' (distance: {distToDoor:F2}m, toOutside: {toOutside}). Initiating interaction.");

        // Realism enforcement: Real players stand within ~1.2m of the door handle.
        // If the mimic is further than 1.25m, walk directly towards doorPos until <= 1.25m (with timeout).
        if (distToDoor > 1.25f && Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            SetDestinationSafe(doorPos);
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            NavMeshUtil.SafeSetSpeed(Masked.agent, WalkSpeed);
            float approachTimeout = 2.5f;
            float elapsedApproach = 0f;
            while (Vector3.Distance(transform.position, doorPos) > 1.25f && elapsedApproach < approachTimeout)
            {
                if (Masked == null || Masked.isEnemyDead)
                {
                    _isUsingDoor = false;
                    DoorInteractionTarget = Vector3.zero;
                    yield break;
                }
                elapsedApproach += Time.deltaTime;
                Masked.stareAtTransform = null;
                Masked.LookAtPosition(doorPos, 0.5f);
                yield return null;
            }
        }

        if (Masked.agent != null)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, true);
            NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);
            NavMeshUtil.SafeSetSpeed(Masked.agent, 0f);
        }

        // 1. Turn smoothly to face the door handle / threshold
        float turnTime = 0.35f;
        float elapsedTurn = 0f;
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' turning to face door handle at {doorPos} (turn time: {turnTime:F2}s).");
        while (elapsedTurn < turnTime)
        {
            if (Masked == null || Masked.isEnemyDead)
            {
                _isUsingDoor = false;
                DoorInteractionTarget = Vector3.zero;
                yield break;
            }
            elapsedTurn += Time.deltaTime;
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorPos, 0.5f);
            Vector3 lookDir = (doorPos - transform.position).normalized;
            lookDir.y = 0;
            if (lookDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 15f);
            }
            yield return null;
        }

        // 2. Realistic player interact hold delay (1.15s - 1.45s)
        float holdDuration = UnityEngine.Random.Range(1.15f, 1.45f);
        float elapsedHold = 0f;
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' holding door handle (hold duration: {holdDuration:F2}s, simulating player [E] hold)...");

        // CRITICAL REALISM: Start opening the door!
        // This triggers the door animator to crack open slightly and plays the creak SFX,
        // exactly like when a real player holds [E] on the entrance door!
        try
        {
            door.StartOpeningEntrance();
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[Door] StartOpeningEntrance caught: {ex.Message}");
        }

        while (elapsedHold < holdDuration)
        {
            if (Masked == null || Masked.isEnemyDead)
            {
                try { door.FinishOpeningEntrance(); } catch { }
                _isUsingDoor = false;
                DoorInteractionTarget = Vector3.zero;
                yield break;
            }
            elapsedHold += Time.deltaTime;
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorPos, 0.5f);
            Vector3 lookDir = (doorPos - transform.position).normalized;
            lookDir.y = 0;
            if (lookDir != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(lookDir);
            }
            yield return null;
        }

        // 3. Complete door opening animation & sound
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' finishing door opening transition.");
        try
        {
            door.FinishOpeningEntrance();
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[Door] FinishOpeningEntrance caught: {ex.Message}");
            if (door.exitScript == null) door.FindExitPoint();
            door.PlayAudioAtTeleportPositions();
        }

        // 4. Perform the teleport through the door
        DoorInteractionTarget = Vector3.zero;
        TeleportThroughDoor(door, toOutside);

        // 5. Brief post-door pause (0.35s) simulating player screen fade / loading recovery
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' teleported through door! Pausing 0.35s for screen fade recovery.");
        yield return new WaitForSeconds(0.35f);

        if (Masked?.agent != null)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
        }
        _isUsingDoor = false;
        DoorInteractionTarget = Vector3.zero;
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked?.gameObject.name}' door transition complete. Resuming navigation.");

        onComplete?.Invoke();
    }

    private static InteractTrigger? FindNearbyLadderTrigger(Vector3 pos, float radius = 3.5f)
    {
        try
        {
            var triggers = UnityEngine.Object.FindObjectsOfType<InteractTrigger>();
            if (triggers != null)
            {
                InteractTrigger? best = null;
                float bestDist = radius;
                foreach (var t in triggers)
                {
                    if (t == null || !t.isLadder) continue;
                    float d = Vector3.Distance(t.transform.position, pos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = t;
                    }
                }
                return best;
            }
        }
        catch { }
        return null;
    }

    private IEnumerator TraverseOffMeshLinkRoutine()
    {
        if (Masked?.agent == null) yield break;
        _isTraversingOffMeshLink = true;

        OffMeshLinkData linkData = Masked.agent.currentOffMeshLinkData;
        if (!linkData.valid)
        {
            try
            {
                if (Masked.agent.isOnOffMeshLink) Masked.agent.CompleteOffMeshLink();
            }
            catch { }
            _isTraversingOffMeshLink = false;
            yield break;
        }

        Vector3 startPos = transform.position;
        Vector3 endPos = linkData.endPos + Vector3.up * Masked.agent.baseOffset;

        float deltaY = endPos.y - startPos.y;
        float absDeltaY = Mathf.Abs(deltaY);
        bool isVerticalClimb = absDeltaY > 1.2f;

        PhoneyPlugin.Logger.LogInfo(
            $"[Ladder] '{Masked.gameObject.name}' off-mesh link detected! Start: {startPos}, End: {endPos}, DeltaY: {deltaY:F2}m (vertical climb: {isVerticalClimb}).");

        _scrapManager ??= GetComponent<MaskedScrapManager>();
        bool isHoldingTwoHanded = _scrapManager != null && _scrapManager.IsHoldingTwoHanded;
        bool isCarryingTwoHanded = _scrapManager != null && _scrapManager.IsCarryingTwoHanded;
        bool hasTwoHanded = isHoldingTwoHanded || isCarryingTwoHanded;

        GrabbableObject? droppedLadderScrap = null;
        GrabbableObject? heldOneHanded = (!hasTwoHanded && _scrapManager != null) ? _scrapManager.HeldScrap : null;

        // Disable NavMeshAgent's internal position and rotation updates during off-mesh link traversal.
        Masked.agent.updatePosition = false;
        Masked.agent.updateRotation = false;
        NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);

        try
        {
            if (isVerticalClimb)
            {
                InteractTrigger? ladderTrigger = FindNearbyLadderTrigger(linkData.startPos, 3.5f) ?? FindNearbyLadderTrigger(linkData.endPos, 3.5f);

                // Determine facing direction towards the ladder rungs
                Vector3 ladderFaceDir = Vector3.zero;
                if (ladderTrigger?.ladderPlayerPositionNode != null)
                {
                    ladderFaceDir = ladderTrigger.ladderPlayerPositionNode.forward;
                }
                else if (ladderTrigger != null)
                {
                    ladderFaceDir = -ladderTrigger.transform.forward;
                }
                else
                {
                    // Check horizontal rays around linkData.startPos to find ladder wall surface
                    int layerMask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                    Vector3[] directions = new Vector3[] { transform.forward, -transform.forward, transform.right, -transform.right };
                    foreach (var dir in directions)
                    {
                        if (Physics.Raycast(linkData.startPos + Vector3.up * 0.5f, dir, out RaycastHit wallHit, 1.2f, layerMask, QueryTriggerInteraction.Ignore))
                        {
                            ladderFaceDir = -wallHit.normal;
                            break;
                        }
                    }

                    if (ladderFaceDir == Vector3.zero)
                    {
                        ladderFaceDir = (deltaY > 0 ? (linkData.endPos - linkData.startPos) : (linkData.startPos - linkData.endPos));
                        ladderFaceDir.y = 0;
                        if (ladderFaceDir.sqrMagnitude > 0.01f) ladderFaceDir.Normalize();
                        else ladderFaceDir = transform.forward;
                    }
                }

                // Ladder rung climb endpoints
                Vector3 climbStart = linkData.startPos;
                Vector3 climbEnd = linkData.endPos + Vector3.up * Masked.agent.baseOffset;
                if (ladderTrigger != null)
                {
                    if (deltaY < 0)
                    {
                        climbStart = ladderTrigger.topOfLadderPosition != null ? ladderTrigger.topOfLadderPosition.position : linkData.startPos;
                        climbEnd = ladderTrigger.bottomOfLadderPosition != null ? ladderTrigger.bottomOfLadderPosition.position : linkData.endPos;
                    }
                    else
                    {
                        climbStart = ladderTrigger.bottomOfLadderPosition != null ? ladderTrigger.bottomOfLadderPosition.position : linkData.startPos;
                        climbEnd = ladderTrigger.topOfLadderPosition != null ? ladderTrigger.topOfLadderPosition.position : linkData.endPos;
                    }
                }

                // ── Two-Handed Restriction ────────────────────────────────────────
                // Real players CANNOT climb or go down ladders while holding a two-handed item!
                if (hasTwoHanded)
                {
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Ladder] '{Masked.gameObject.name}' reached ladder with two-handed item '{_scrapManager?.HeldScrap?.itemProperties?.itemName ?? "Item"}' (DeltaY={deltaY:F2}m). Enforcing realistic player mechanics.");

                    if (deltaY < -1.2f)
                    {
                        // Descending ladder:
                        // Real player behavior: drop the two-handed item down to the lower landing before descending!
                        droppedLadderScrap = _scrapManager?.HeldScrap;
                        if (droppedLadderScrap != null)
                        {
                            // Align facing toward ladder before dropping
                            float turnTime = 0.2f;
                            float elapsedTurn = 0f;
                            Quaternion startRot = transform.rotation;
                            Quaternion dropRot = Quaternion.LookRotation(ladderFaceDir);
                            while (elapsedTurn < turnTime)
                            {
                                if (Masked == null || Masked.isEnemyDead) yield break;
                                elapsedTurn += Time.deltaTime;
                                transform.rotation = Quaternion.Slerp(startRot, dropRot, elapsedTurn / turnTime);
                                yield return null;
                            }

                            // Drop the scrap down to endPos (lower landing)
                            _scrapManager?.DropRealScrap(endPos, isElevatorStaged: false, isLadderStaged: true);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Ladder] '{Masked.gameObject.name}' dropped two-handed item down to lower floor at {endPos} before climbing down!");
                            yield return new WaitForSeconds(0.35f);
                        }
                    }
                    else
                    {
                        // Ascending ladder:
                        // Real player behavior: players cannot climb up ladders with two-handed items. Drop it at the base!
                        droppedLadderScrap = _scrapManager?.HeldScrap;
                        if (droppedLadderScrap != null)
                        {
                            _scrapManager?.DropRealScrap(startPos, isElevatorStaged: false, isLadderStaged: true);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Ladder] '{Masked.gameObject.name}' dropped two-handed item at ladder base {startPos} before climbing up!");
                            yield return new WaitForSeconds(0.35f);
                        }
                    }
                }
                else if (heldOneHanded != null)
                {
                    // For one-handed items, hide item mesh while on ladder so hands are realistically gripping rungs
                    heldOneHanded.EnableItemMeshes(false);
                }

                // Hide visual tool while climbing ladder so hands realistically grip rungs
                var ladderHolder = GetComponent<MaskedHeldItemHolder>();
                ladderHolder?.SetHeldToolActive(false);


                // Step smoothly onto ladder rungs (no instant snap!)
                float alignDuration = 0.22f;
                float elapsedAlign = 0f;
                Vector3 initPos = transform.position;
                Quaternion initRot = transform.rotation;
                Quaternion targetRot = Quaternion.LookRotation(ladderFaceDir);

                if (Masked.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("crouching", false);
                    Masked.creatureAnimator.SetBool("IsMoving", false);
                    Masked.creatureAnimator.SetFloat("VelocityZ", 0f);
                    Masked.creatureAnimator.SetFloat("VelocityX", 0f);
                    Masked.creatureAnimator.SetBool("Running", false);
                    Masked.creatureAnimator.SetTrigger("EnterLadder");
                }

                while (elapsedAlign < alignDuration)
                {
                    if (Masked == null || Masked.isEnemyDead) yield break;
                    elapsedAlign += Time.deltaTime;
                    float tAlign = Mathf.Clamp01(elapsedAlign / alignDuration);
                    float smoothT = Mathf.SmoothStep(0f, 1f, tAlign);

                    Vector3 p = Vector3.Lerp(initPos, climbStart, smoothT);
                    transform.position = p;
                    Masked.serverPosition = p;
                    transform.rotation = Quaternion.Slerp(initRot, targetRot, smoothT);
                    yield return null;
                }

                transform.position = climbStart;
                Masked.serverPosition = climbStart;
                transform.rotation = targetRot;

                // Enter climbing animation state
                if (Masked.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("ClimbingLadder", true);
                }

                // Traversal along ladder shaft
                float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
                float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);
                float weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 0.70f), 0.40f, 1.0f);
                float climbSpeed = 3.8f * weightFactor;
                float climbDuration = Mathf.Max(0.4f, absDeltaY / climbSpeed);

                PhoneyPlugin.Logger.LogInfo(
                    $"[Ladder] '{Masked.gameObject.name}' climbing {(deltaY > 0 ? "UP" : "DOWN")} ladder: speed={climbSpeed:F2} m/s, duration={climbDuration:F2}s, weightFactor={weightFactor:F2}, items={_scrapManager?.CarriedCount ?? 0}.");

                float elapsed = 0f;
                while (elapsed < climbDuration)
                {
                    if (Masked == null || Masked.isEnemyDead) yield break;
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / climbDuration);

                    Vector3 curPos = Vector3.Lerp(climbStart, climbEnd, t);
                    transform.position = curPos;
                    Masked.serverPosition = curPos;
                    transform.rotation = targetRot;
                    yield return null;
                }

                transform.position = climbEnd;
                Masked.serverPosition = climbEnd;

                // Exit ladder climbing state
                if (Masked.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("ClimbingLadder", false);
                }

                // Smooth step off ladder onto destination landing (no instant snap!)
                float stepOffDuration = 0.20f;
                float elapsedStepOff = 0f;
                Vector3 stepOffStart = transform.position;
                Quaternion stepOffRot = transform.rotation;
                Vector3 exitDir = endPos - climbEnd;
                exitDir.y = 0;
                Quaternion exitRot = exitDir.sqrMagnitude > 0.05f ? Quaternion.LookRotation(exitDir.normalized) : targetRot;

                while (elapsedStepOff < stepOffDuration)
                {
                    if (Masked == null || Masked.isEnemyDead) yield break;
                    elapsedStepOff += Time.deltaTime;
                    float tStep = Mathf.Clamp01(elapsedStepOff / stepOffDuration);
                    float smoothStep = Mathf.SmoothStep(0f, 1f, tStep);

                    Vector3 p = Vector3.Lerp(stepOffStart, endPos, smoothStep);
                    transform.position = p;
                    Masked.serverPosition = p;
                    transform.rotation = Quaternion.Slerp(stepOffRot, exitRot, smoothStep);
                    yield return null;
                }

                transform.position = endPos;
                Masked.serverPosition = endPos;
                PhoneyPlugin.Logger.LogInfo($"[Ladder] '{Masked.gameObject.name}' finished vertical ladder climb at {endPos}.");
            }
            else
            {
                // Horizontal hop / small drop across gap
                float speed = WalkSpeed;
                float duration = Mathf.Max(0.25f, Vector3.Distance(startPos, endPos) / speed);
                PhoneyPlugin.Logger.LogInfo(
                    $"[Ladder] '{Masked?.gameObject.name ?? "Mimic"}' traversing horizontal link: dist={Vector3.Distance(startPos, endPos):F2}m, duration={duration:F2}s.");
                float elapsed = 0f;

                Vector3 moveDir = (endPos - startPos).normalized;
                moveDir.y = 0;
                if (moveDir != Vector3.zero)
                {
                    transform.rotation = Quaternion.LookRotation(moveDir);
                }

                if (Masked != null && Masked.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("IsMoving", true);
                    Masked.creatureAnimator.SetFloat("VelocityZ", 1.0f);
                }

                while (elapsed < duration)
                {
                    if (Masked == null || Masked.isEnemyDead) yield break;
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);

                    Vector3 curPos = Vector3.Lerp(startPos, endPos, t);
                    transform.position = curPos;
                    Masked.serverPosition = curPos;

                    yield return null;
                }

                PhoneyPlugin.Logger.LogInfo($"[Ladder] '{Masked?.gameObject.name}' finished horizontal link at {endPos}.");
            }

            // Restore held one-handed item mesh if hidden
            if (heldOneHanded != null && heldOneHanded.isHeld)
            {
                heldOneHanded.EnableItemMeshes(true);
            }

            // Safely complete OffMeshLink and warp
            if (Masked != null && !Masked.isEnemyDead)
            {
                transform.position = endPos;
                Masked.serverPosition = endPos;

                try
                {
                    if (Masked.agent != null)
                    {
                        if (Masked.agent.isOnOffMeshLink)
                        {
                            Masked.agent.CompleteOffMeshLink();
                        }
                        NavMeshUtil.SafeWarp(Masked.agent, endPos);
                        Masked.agent.updatePosition = true;
                        Masked.agent.updateRotation = true;
                    }
                }
                catch (Exception ex)
                {
                    PhoneyPlugin.Logger.LogDebug($"[Ladder] CompleteOffMeshLink error: {ex.Message}");
                }

                if (Masked.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("IsMoving", false);
                    Masked.creatureAnimator.SetFloat("VelocityZ", 0f);
                }
            }

            // If a two-handed item was dropped down the ladder, retrieve it from the floor now!
            if (droppedLadderScrap != null && deltaY < -1.2f && !droppedLadderScrap.isHeld && _scrapManager != null)
            {
                if (Masked?.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("crouching", true);
                }
                yield return new WaitForSeconds(0.35f);

                if (Masked != null && !Masked.isEnemyDead && droppedLadderScrap != null && !droppedLadderScrap.isHeld)
                {
                    _scrapManager.PickUpRealScrap(droppedLadderScrap);
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Ladder] '{Masked.gameObject.name}' retrieved dropped two-handed item '{droppedLadderScrap.itemProperties?.itemName ?? "Item"}' at base of ladder!");
                }

                if (Masked?.creatureAnimator != null)
                {
                    Masked.creatureAnimator.SetBool("crouching", false);
                }
                yield return new WaitForSeconds(0.15f);
            }
        }
        finally
        {
            if (heldOneHanded != null && heldOneHanded.isHeld)
            {
                heldOneHanded.EnableItemMeshes(true);
            }
            var ladderHolder = GetComponent<MaskedHeldItemHolder>();
            if (ladderHolder != null)
            {
                bool hasScrap = _scrapManager != null && _scrapManager.HasHeldScrap;
                ladderHolder.SetHeldToolActive(!hasScrap);
                ladderHolder.UpdateAnimationLayers(hasScrap, isAggressive: CurrentPhase == MimicPhase.AmbushStrike);
            }
            if (Masked?.creatureAnimator != null)
            {
                Masked.creatureAnimator.SetBool("ClimbingLadder", false);
            }

            if (Masked?.agent != null)
            {
                Masked.agent.updatePosition = true;
                Masked.agent.updateRotation = true;
            }
            _isTraversingOffMeshLink = false;
            PhoneyPlugin.Logger.LogInfo($"[Ladder] '{Masked?.gameObject.name}' off-mesh link completed. Resumed standard NavMesh pathing.");
        }
    }

    private Vector3 PickRandomDisruptNode()
    {
        var nodes = RoundManager.Instance?.outsideAINodes;
        if (nodes != null && nodes.Length > 0)
        {
            // Pick a node 20m - 50m away from the entrance / ship to hide loot
            Vector3 center = transform.position;
            var candidates = nodes
                .Where(n => {
                    float d = Vector3.Distance(n.transform.position, center);
                    return d >= 20f && d <= 55f;
                })
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[UnityEngine.Random.Range(0, candidates.Count)].transform.position;
            }
            return nodes[UnityEngine.Random.Range(0, nodes.Length)].transform.position;
        }
        return transform.position + UnityEngine.Random.insideUnitSphere * 25f;
    }

    // ─── Phase 3: Ambush Strike (Relentless Pursuit) ──────────────────────────

    private void UpdateAmbush()
    {
        if (Masked == null) return;

        if (TargetPlayer == null || !IsPlayerValidTarget(TargetPlayer))
        {
            TargetPlayer = GetClosestLivingPlayer(out _);
            if (TargetPlayer == null)
            {
                TransitionTo(MimicPhase.UndercoverLooting);
                return;
            }
        }

        // In Mineshaft: if target player is on the other floor, take the elevator!
        if (IsMineshaftDungeon() && TargetPlayer.isInsideFactory && !Masked.isOutside)
        {
            var elevator = GetMineshaftElevator();
            if (elevator != null && elevator.elevatorBottomPoint != null)
            {
                bool targetOnUpper = TargetPlayer.transform.position.y > (elevator.elevatorBottomPoint.position.y + 25f);
                bool mimicOnUpper = IsOnMineshaftUpperFloor();

                if (targetOnUpper != mimicOnUpper)
                {
                    SafeUseElevator(targetOnUpper);
                    return;
                }
                else if (Masked.IsInsideMineshaftElevator(transform.position) && !elevator.elevatorFinishedMoving)
                {
                    return;
                }
            }
        }

        Masked.currentBehaviourStateIndex = 0;
        Masked.stopAndStareTimer = -999f;
        Masked.targetPlayer = TargetPlayer;
        Masked.movingTowardsTargetPlayer = true;

        Masked.SetMovingTowardsTargetPlayer(TargetPlayer);
        Masked.SetDestinationToPosition(TargetPlayer.transform.position);

        float dist    = Vector3.Distance(transform.position, TargetPlayer.transform.position);
        float elapsed = Time.time - _phaseEnteredTime;
        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;

        // Phase 1: Standstill during violent transformation freeze (1.1s handled by PhoneyBloodyReveal)
        if (_bloodyReveal != null && _bloodyReveal.IsTransforming)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, true);
            NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);
            return;
        }

        NavMeshUtil.SafeSetStopped(Masked.agent, false);
        if (Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            Masked.agent.stoppingDistance = 0f;
        }

        // Phase 2: Reaction window jog with hands outstretched (~2.6s jog after 1.1s transform = elapsed < 3.7s)
        // Mimic eerily jogs towards the player with hands outstretched, giving player time to turn and flee!
        if (elapsed < 3.7f)
        {
            NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushJogSpeed);
            if (!Masked.handsOut)
            {
                Masked.handsOut = true;
                if (Masked.creatureAnimator != null) Masked.creatureAnimator.SetBool("HandsOut", true);
                if (isServer) Masked.SetHandsOutClientRpc(true);
            }
            if (isServer && Masked.running)
            {
                Masked.running = false;
                Masked.SetRunningServerRpc(false);
            }
            _ambushStamina = 1.0f;
            _isAmbushBurst = false;
        }
        else
        {
            // Phase 3: Relentless pursuit with burst sprinting & stamina pacing
            // Sprints in bursts like a real player, draining stamina, then falling back to recovery jog
            if (!_isAmbushBurst)
            {
                // Enter sprint burst once stamina recovers to at least 50%
                if (_ambushStamina >= 0.50f)
                {
                    _isAmbushBurst = true;
                    if (isServer) { Masked.running = true; Masked.SetRunningServerRpc(true); }
                    PhoneyPlugin.Logger.LogInfo($"[Pursuit] '{Masked.gameObject.name}' entered burst sprint! Speed: {AmbushSprintSpeed:F2} m/s (stamina: {_ambushStamina:P0}).");
                }
            }
            else
            {
                // Drop to recovery jog when stamina runs out
                if (_ambushStamina <= 0.10f)
                {
                    _isAmbushBurst = false;
                    if (isServer) { Masked.running = false; Masked.SetRunningServerRpc(false); }
                    PhoneyPlugin.Logger.LogInfo($"[Pursuit] '{Masked.gameObject.name}' sprint stamina depleted ({_ambushStamina:P0}), dropping to recovery jog. Speed: {AmbushJogSpeed:F2} m/s.");
                }
            }

            if (_isAmbushBurst)
            {
                // Sprint burst: drains stamina over ~3.2 seconds
                _ambushStamina = Mathf.Clamp01(_ambushStamina - Time.deltaTime * 0.28f);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushSprintSpeed);
            }
            else
            {
                // Recovery jog: catches breath over ~2.6 seconds
                _ambushStamina = Mathf.Clamp01(_ambushStamina + Time.deltaTime * 0.35f);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushJogSpeed);
            }

            // Always maintain hands outstretched in pursuit
            if (!Masked.handsOut)
            {
                Masked.handsOut = true;
                if (Masked.creatureAnimator != null) Masked.creatureAnimator.SetBool("HandsOut", true);
                if (isServer) Masked.SetHandsOutClientRpc(true);
            }
        }

        // Body facing direction: smoothly face movement velocity to prevent looking sideways/backwards
        if (Masked.agent != null && Masked.agent.velocity.sqrMagnitude > 0.15f)
        {
            Vector3 moveDir = Masked.agent.velocity;
            moveDir.y = 0;
            if (moveDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), Time.deltaTime * 10f);
            }
        }

        float timeout = PhoneyPlugin.LostLineOfSightTimeout != null
            ? PhoneyPlugin.LostLineOfSightTimeout.Value
            : 7.0f;

        // Track line-of-sight: when line of sight is broken, timeWithoutLOS counts up
        bool hasLOS = HasLineOfSight(TargetPlayer);
        if (hasLOS)
        {
            if (!_hadLOSLastFrame)
            {
                float brokenDuration = Time.time - _lastSeenPlayerTime;
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}' REGAINED line of sight to '{TargetPlayer.playerUsername}'! (Was broken for {brokenDuration:F1}s, distance: {dist:F1}m).");
                _hadLOSLastFrame = true;
            }
            _lastSeenPlayerTime = Time.time;
        }
        else
        {
            if (_hadLOSLastFrame)
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}' LOST line of sight to '{TargetPlayer.playerUsername}'! (Distance: {dist:F1}m, break timeout: {timeout:F1}s). Starting escape countdown.");
                _hadLOSLastFrame = false;
                _lastLOSLogTime = Time.time;
            }
            else if (Time.time >= _lastLOSLogTime + 1.5f)
            {
                _lastLOSLogTime = Time.time;
                float currentBroken = Time.time - _lastSeenPlayerTime;
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}': Line of sight broken with '{TargetPlayer.playerUsername}' for {currentBroken:F1}s / {timeout:F1}s (dist: {dist:F1}m, differentSide: {TargetPlayer.isInsideFactory != (!Masked.isOutside)}).");
            }
        }

        float timeWithoutLOS = Time.time - _lastSeenPlayerTime;

        // Escape conditions:
        // 1. Broken line of sight for long enough (default >= 7.0s continuously).
        // 2. OR player maintains distance (> 25m) without line of sight for >= 3.5s.
        // 3. OR player sprinted far away and broke away completely (> 45m).
        bool differentSide = TargetPlayer.isInsideFactory != (!Masked.isOutside);
        bool lostLOSLongEnough = timeWithoutLOS >= timeout;
        bool lostLOSFarAway = dist > 25.0f && timeWithoutLOS >= 3.5f && !differentSide;
        bool brokenAway = dist > 45.0f && !differentSide;

        // When the player crosses to the other side (went through a door), CHASE THEM THROUGH!
        // The mimic should follow aggressively, not give up just because the player used a door.
        if (differentSide && !_isUsingDoor)
        {
            // Find the door that leads to the player's side and run to it
            bool playerIsOutside = !TargetPlayer.isInsideFactory;
            var chaseDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: !playerIsOutside);
            if (chaseDoor != null)
            {
                Vector3 doorPos = MaskedScrapManager.GetDoorPosition(chaseDoor);
                float distToDoor = Vector3.Distance(transform.position, doorPos);

                if (distToDoor <= 3.5f)
                {
                    Masked.stareAtTransform = null;
                    Masked.LookAtPosition(doorPos, 0.6f);
                }

                if (distToDoor <= 1.35f)
                {
                    // At the door — burst through it to chase the player!
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Pursuit] '{Masked.gameObject.name}' CHASING player '{TargetPlayer.playerUsername}' through door to {(playerIsOutside ? "outside" : "inside")}!");
                    StartCoroutine(DoorTransitionRoutine(chaseDoor, toOutside: playerIsOutside, onComplete: () =>
                    {
                        // Resumed chase on the other side!
                        _lastSeenPlayerTime = Time.time; // Reset LOS timer after door transition
                        if (TargetPlayer != null)
                        {
                            Masked.SetDestinationToPosition(TargetPlayer.transform.position);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Pursuit] '{Masked.gameObject.name}' emerged through door! Resuming pursuit of '{TargetPlayer.playerUsername}'.");
                        }
                    }));
                }
                else
                {
                    // Sprint to the door
                    Masked.SetDestinationToPosition(doorPos);
                    NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushSprintSpeed);
                }
            }

            // Only give up if the player has been on the other side for 12+ seconds
            // (more than enough time for the mimic to reach and use the door)
            if (timeWithoutLOS >= 12.0f)
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}': Player '{TargetPlayer.playerUsername}' escaped through door and mimic couldn't follow in time ({timeWithoutLOS:F1}s >= 12s). Giving up.");
                Masked.targetPlayer = null;
                Masked.movingTowardsTargetPlayer = false;
                TargetPlayer = null;
                TransitionTo(MimicPhase.UndercoverLooting);
                return;
            }
            return; // Don't check other escape conditions while chasing through door
        }

        if (lostLOSLongEnough || lostLOSFarAway || brokenAway)
        {
            string escapeReason = lostLOSLongEnough ? $"LOS broken for {timeWithoutLOS:F1}s >= {timeout:F1}s" :
                                 (lostLOSFarAway ? $"LOS broken ({timeWithoutLOS:F1}s >= 3.5s) and player maintained distance ({dist:F1}m > 25m)" :
                                 $"Player sprinted away ({dist:F1}m > 45m)");

            PhoneyPlugin.Logger.LogInfo(
                $"[Pursuit] '{Masked.gameObject.name}': Player '{TargetPlayer.playerUsername}' ESCAPED! Reason: {escapeReason}. Ending pursuit, re-disguising, and returning to normal!");

            Masked.targetPlayer = null;
            Masked.movingTowardsTargetPlayer = false;
            TargetPlayer = null;

            TransitionTo(MimicPhase.UndercoverLooting);
            return;
        }

        // Periodic demonic pursuit taunt / distorted shouts while chasing! Spaced out to not be overly spammy!
        if (isServer && Time.time >= _nextPursuitTauntTime && _voiceEmitter != null && !_voiceEmitter.IsSpeaking)
        {
            float interval = PhoneyPlugin.DemonicTauntInterval != null ? PhoneyPlugin.DemonicTauntInterval.Value : 22.0f;
            _nextPursuitTauntTime = Time.time + UnityEngine.Random.Range(interval * 0.85f, interval * 1.25f);
            TriggerPursuitTaunt();
        }
    }

    // ─── Phase 4: Tactical Retreat ────────────────────────────────────────────

    private void UpdateRetreat()
    {
        if (Masked == null) return;

        if (Masked.isOutside)
        {
            var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
            if (outDoor != null)
            {
                Vector3 doorPos = MaskedScrapManager.GetDoorPosition(outDoor);
                Masked.SetDestinationToPosition(doorPos);
                if (Vector3.Distance(transform.position, doorPos) <= 3.5f)
                {
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}': Reached entrance door during retreat — entering facility and returning to normal.");
                    TransitionTo(MimicPhase.UndercoverLooting);
                    return;
                }
            }
        }
        else
        {
            FindFleeDestination();
        }

        if (Time.time - _phaseEnteredTime > 8f)
        {
            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}': Retreat complete — returning to deceptive undercover mode.");
            TransitionTo(MimicPhase.UndercoverLooting);
        }
    }

    // ─── External Damage & Bumping ────────────────────────────────────────────

    /// <summary>
    /// Instantly blows the mimic's cover and launches an aggressive ambush against the specified player.
    /// Triggered by taking damage from a player or hearing callouts like "he's a mimic" / "are you a mimic?".
    /// </summary>
    public void TriggerInstantAmbush(PlayerControllerB? targetPlayer = null)
    {
        if (Masked == null || Masked.isEnemyDead) return;

        if (targetPlayer != null && !targetPlayer.isPlayerDead)
        {
            TargetPlayer = targetPlayer;
        }
        else if (TargetPlayer == null || TargetPlayer.isPlayerDead)
        {
            TargetPlayer = GetClosestLivingPlayer(out _);
        }

        if (CurrentPhase != MimicPhase.AmbushStrike)
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[Combat] '{Masked.gameObject.name}' triggered INSTANT AMBUSH against '{(TargetPlayer != null ? TargetPlayer.playerUsername : "closest player")}'!");
            TransitionTo(MimicPhase.AmbushStrike);
        }
        else if (targetPlayer != null)
        {
            // Already aggressive: switch pursuit target to this player
            Masked.targetPlayer = targetPlayer;
            Masked.SetMovingTowardsTargetPlayer(targetPlayer);
        }
    }

    /// <summary>
    /// Triggered when the mimic takes damage from any player (shovel, stop sign, shotgun, etc.).
    /// Instantly becomes aggressive and targets the attacker!
    /// </summary>
    public void OnDamagedByPlayer(PlayerControllerB? playerWhoHit = null)
    {
        if (Masked == null || Masked.isEnemyDead) return;

        PhoneyPlugin.Logger.LogInfo(
            $"[Combat] '{Masked.gameObject.name}' struck by {(playerWhoHit != null ? $"'{playerWhoHit.playerUsername}'" : "a player")} — cover blown, launching instant ambush!");

        TriggerInstantAmbush(playerWhoHit);
    }

    public void OnPlayerBumpedInto(PlayerControllerB bumpingPlayer)
    {
        if (bumpingPlayer == null || bumpingPlayer.isPlayerDead) return;

        // If hostility was already primed and the player physically walks into the mimic: STRIKE!
        if (_isHostilePrimed)
        {
            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked?.gameObject.name}' bumped by '{bumpingPlayer.playerUsername}' while primed — AMBUSH STRIKE!");
            TargetPlayer = bumpingPlayer;
            TransitionTo(MimicPhase.AmbushStrike);
            return;
        }

        if (Time.time - _lastBumpTime < 3f) return;
        _lastBumpTime = Time.time;

        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked?.gameObject.name}' bumped by '{bumpingPlayer.playerUsername}' — polite crouch greeting.");
        StartFriendlyCrouch();

        if (Masked?.agent != null)
        {
            Vector3 awayDir = (transform.position - bumpingPlayer.transform.position).normalized;
            Masked.agent.Move(awayDir * 0.7f);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void PickRoomNearPlayer(PlayerControllerB? player)
    {
        if (Masked == null) return;

        var nodes = Masked.isOutside
            ? RoundManager.Instance?.outsideAINodes
            : RoundManager.Instance?.insideAINodes;

        if (nodes == null || nodes.Length == 0)
        {
            _currentRoomTarget = transform.position + UnityEngine.Random.insideUnitSphere * 15f;
            return;
        }

        Vector3 center = player != null ? player.transform.position : transform.position;

        // When inside facility and accompanying a crewmate, prioritize rooms/hallways close to the player (3.5m - 12m)
        // so the mimic feels like a loyal, observant squadmate exploring alongside them!
        bool stayClose = !Masked.isOutside && player != null && player.isInsideFactory;
        float minDist = stayClose ? 3.5f : 10f;
        float maxDist = stayClose ? 12.0f : 26f;

        var nearby = nodes
            .Where(n => {
                float d = Vector3.Distance(n.transform.position, center);
                return d >= minDist && d <= maxDist;
            })
            .ToList();

        if (nearby.Count > 0)
        {
            _currentRoomTarget = nearby[UnityEngine.Random.Range(0, nearby.Count)].transform.position;
        }
        else if (stayClose)
        {
            // Fallback for staying close: sample a position 4m-6m around player on NavMesh
            Vector3 offset = UnityEngine.Random.insideUnitSphere * 5.0f;
            offset.y = 0;
            Vector3 samplePos = center + offset;
            if (NavMesh.SamplePosition(samplePos, out var hit, 5.0f, NavMesh.AllAreas))
            {
                _currentRoomTarget = hit.position;
            }
            else
            {
                var fallback = nodes.Where(n => Vector3.Distance(n.transform.position, center) < 20f).ToList();
                _currentRoomTarget = (fallback.Count > 0 ? fallback[UnityEngine.Random.Range(0, fallback.Count)] : nodes[0]).transform.position;
            }
        }
        else
        {
            var fallback = nodes.Where(n => Vector3.Distance(n.transform.position, transform.position) < 32f).ToList();
            _currentRoomTarget = (fallback.Count > 0 ? fallback[UnityEngine.Random.Range(0, fallback.Count)] : nodes[0]).transform.position;
        }
    }

    private void TriggerAmbushScream()
    {
        if (_voiceEmitter == null || _voiceEmitter.ImpersonatedSteamId == 0 || Masked == null) return;
        var nm = NetworkManager.Singleton;
        if (nm == null || (!nm.IsServer && !nm.IsHost)) return;

        var clip = ClipVault.Instance.FindAttackVocalizationClip(_voiceEmitter.ImpersonatedSteamId);
        if (clip != null)
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[DeceptiveAI] Mimic '{Masked.gameObject.name}' emitting demonic ambush reveal shout: \"{clip.Transcript}\"");
            ClipVault.Instance.RecordClipPlayed(clip);
            PhoneyNetworkManager.Instance.SyncAndPlayClip(Masked, _voiceEmitter, clip, 0f);
        }
    }

    private void TriggerPursuitTaunt()
    {
        if (_voiceEmitter == null || _voiceEmitter.ImpersonatedSteamId == 0 || Masked == null) return;
        var nm = NetworkManager.Singleton;
        if (nm == null || (!nm.IsServer && !nm.IsHost)) return;

        var clip = ClipVault.Instance.FindAttackVocalizationClip(_voiceEmitter.ImpersonatedSteamId);
        if (clip != null)
        {
            PhoneyPlugin.Logger.LogInfo(
                $"[DeceptiveAI] Mimic '{Masked.gameObject.name}' emitting demonic pursuit taunt: \"{clip.Transcript}\"");
            ClipVault.Instance.RecordClipPlayed(clip);
            PhoneyNetworkManager.Instance.SyncAndPlayClip(Masked, _voiceEmitter, clip, 0f);
        }
    }

    /// <summary>
    /// Checks whether a player is a valid, living, controllable target on the same interior/exterior level as the mimic.
    /// Avoids vanilla Mineshaft elevator/start tile lockout bugs where PlayerIsTargetable returns false for valid players.
    /// </summary>
    public bool IsPlayerValidTarget(PlayerControllerB? player)
    {
        if (player == null || !player.isPlayerControlled || player.isPlayerDead) return false;
        if (Masked == null) return false;

        // Interior vs exterior match: mimic and player must both be inside or both be outside
        bool playerInside = player.isInsideFactory;
        bool mimicInside  = !Masked.isOutside;
        return playerInside == mimicInside;
    }

    private bool HasLineOfSight(PlayerControllerB player)
    {
        if (player == null || Masked == null) return false;
        Vector3 eyePos = transform.position + Vector3.up * 1.5f;
        Vector3 playerPos = player.transform.position + Vector3.up * 1.5f;
        return !Physics.Linecast(eyePos, playerPos, StartOfRound.Instance.collidersAndRoomMaskAndDefault);
    }

    private PlayerControllerB? GetClosestLivingPlayer(out float closestDistance)
    {
        closestDistance = float.MaxValue;
        PlayerControllerB? closest = null;
        var players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || Masked == null) return null;

        foreach (var p in players)
        {
            if (!IsPlayerValidTarget(p)) continue;

            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < closestDistance) { closestDistance = d; closest = p; }
        }
        return closest;
    }

    private void WanderToRandomNode()
    {
        if (Masked == null) return;
        var nodes = Masked.isOutside
            ? RoundManager.Instance?.outsideAINodes
            : RoundManager.Instance?.insideAINodes;

        if (nodes != null && nodes.Length > 0)
        {
            var nearby = nodes.Where(n => Vector3.Distance(n.transform.position, transform.position) < 35f).ToList();
            var target = (nearby.Count > 0 ? nearby[UnityEngine.Random.Range(0, nearby.Count)] : nodes[UnityEngine.Random.Range(0, nodes.Length)]).transform.position;
            Masked.SetDestinationToPosition(target);
        }
    }

    private void FindFleeDestination()
    {
        if (Masked == null) return;

        if (Masked.isOutside)
        {
            var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
            if (outDoor != null)
            {
                Masked.SetDestinationToPosition(MaskedScrapManager.GetDoorPosition(outDoor));
                return;
            }
        }

        var nodes = Masked.isOutside
            ? RoundManager.Instance?.outsideAINodes
            : RoundManager.Instance?.insideAINodes;

        if (nodes == null || nodes.Length == 0) return;

        Vector3 playerPos = TargetPlayer != null ? TargetPlayer.transform.position : transform.position;
        Vector3 fleeNode = nodes
            .OrderByDescending(n => Vector3.Distance(n.transform.position, playerPos))
            .First().transform.position;

        Masked.SetDestinationToPosition(fleeNode);
    }

    /// <summary>
    /// Shifts the mimic's identity to a different player in the lobby when returning to undercover mode.
    /// Clones the new player's MoreCompany cosmetics, changes suit, updates voice emitter, and cleans disguise.
    /// </summary>
    public void ReDisguiseAsNewPlayer()
    {
        if (Masked == null) return;

        var allPlayers = StartOfRound.Instance?.allPlayerScripts;
        if (allPlayers == null || allPlayers.Length == 0) return;

        // Include both living and dead teammates
        var candidates = allPlayers
            .Where(p => p != null && (p.isPlayerControlled || p.isPlayerDead || p == StartOfRound.Instance?.localPlayerController) && p.playerSteamId != 0)
            .ToList();

        if (candidates.Count == 0) return;

        ulong currentId = _voiceEmitter != null ? _voiceEmitter.ImpersonatedSteamId : 0;
        var differentPlayers = candidates.Where(p => p.playerSteamId != currentId).ToList();

        // Prefer players with voice clips in the vault (even dead teammates)
        var pool = differentPlayers.Count > 0 ? differentPlayers : candidates;
        var withClips = pool.Where(p => ClipVault.Instance.GetClipCountForPlayer(p.playerSteamId) > 0).ToList();

        PlayerControllerB chosenPlayer = withClips.Count > 0
            ? withClips[UnityEngine.Random.Range(0, withClips.Count)]
            : pool[UnityEngine.Random.Range(0, pool.Count)];

        PhoneyPlugin.Logger.LogInfo(
            $"[Disguise] '{Masked.gameObject.name}' shifting disguise to player: '{chosenPlayer.playerUsername}' (SteamID {chosenPlayer.playerSteamId}, Dead={chosenPlayer.isPlayerDead}, Suit {chosenPlayer.currentSuitID}, Clips={ClipVault.Instance.GetClipCountForPlayer(chosenPlayer.playerSteamId)})!");

        // 1. Update mimickingPlayer reference & suit
        Masked.mimickingPlayer = chosenPlayer;
        Masked.SetSuit(chosenPlayer.currentSuitID);

        // 2. Clone new MoreCompany cosmetics
        MoreCompanyCompat.CopyCosmetics(chosenPlayer, Masked);

        // 3. Shift voice emitter identity to new player
        _voiceEmitter?.SetImpersonatedPlayer(chosenPlayer);

        // 4. Reset bloody transformation & disguise state
        _bloodyReveal?.ResetDisguise();

        // 5. Reset greeting state so the mimic introduces its new identity
        _hasGreetedPlayer = false;
        _greetingResetTime = 0f;

        // 6. Refresh equipment to mirror the newly mimicked player (or dead teammate)
        MaskedHeldItemManager.RefreshPlayerEquipment(Masked, chosenPlayer);
    }

    public void StartFriendlyCrouch()
    {
        if (!_performingCrouch && Time.time > _crouchCooldownTime)
        {
            StartCoroutine(FriendlyCrouchGreeting());
        }
    }

    private IEnumerator FriendlyCrouchGreeting()
    {
        _performingCrouch = true;
        _crouchCooldownTime = Time.time + 15f;

        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;

        for (int i = 0; i < 2; i++)
        {
            if (Masked == null) break;
            if (isServer) { Masked.crouching = true; Masked.SetCrouchingServerRpc(true); }
            yield return new WaitForSeconds(0.28f);
            if (Masked == null) break;
            if (isServer) { Masked.crouching = false; Masked.SetCrouchingServerRpc(false); }
            yield return new WaitForSeconds(0.28f);
        }

        _performingCrouch = false;
    }

    private IEnumerator HumanPickUpScrapRoutine(GrabbableObject scrap)
    {
        _isPickingUpScrap = true;
        NavMeshUtil.SafeSetStopped(Masked?.agent, true);
        NavMeshUtil.SafeSetVelocity(Masked?.agent, Vector3.zero);

        // Hide held tool before bending down to pick up scrap
        var scrapHolder = GetComponent<MaskedHeldItemHolder>();
        scrapHolder?.SetHeldToolActive(false);

        // Smoothly face scrap directly
        Vector3 dir = (scrap.transform.position - transform.position).normalized;
        dir.y = 0;
        if (dir != Vector3.zero)
            transform.rotation = Quaternion.LookRotation(dir);


        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;
        if (isServer && Masked != null)
        {
            Masked.crouching = true;
            Masked.SetCrouchingServerRpc(true);
        }

        // Bend down to reach item on the floor
        yield return new WaitForSeconds(0.42f);

        bool pickedUp = _scrapManager != null && _scrapManager.PickUpRealScrap(scrap);

        // Brief pause to grasp and secure the item
        yield return new WaitForSeconds(0.18f);

        if (isServer && Masked != null)
        {
            Masked.crouching = false;
            Masked.SetCrouchingServerRpc(false);
        }
        NavMeshUtil.SafeSetStopped(Masked?.agent, false);

        // 35% chance to drop ambient chatter upon discovering and picking up scrap
        if (UnityEngine.Random.value < 0.35f && _voiceEmitter != null && _voiceEmitter.CanSpeak)
        {
            var clip = ClipVault.Instance.FindProactiveClip(_voiceEmitter.ImpersonatedSteamId);
            if (clip != null)
            {
                ClipVault.Instance.RecordClipPlayed(clip);
                PhoneyNetworkManager.Instance.SyncAndPlayClip(Masked!, _voiceEmitter, clip, 0.2f);
            }
        }

        if (pickedUp)
        {
            // If the mimic has inventory room to carry more items, check if another piece of scrap is nearby!
            if (_scrapManager != null && _scrapManager.CanPickUpMoreScrap())
            {
                var nextScrap = _scrapManager.FindNearbyReachableScrap(14f, forceScan: true);
                if (nextScrap != null)
                {
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked!.gameObject.name}' has inventory room ({_scrapManager.CarriedCount}/{PhoneyPlugin.MaxCarriedScrapCount.Value}) and spotted nearby scrap '{nextScrap.itemProperties?.itemName}' — picking it up too!");
                    _targetScrap = nextScrap;
                    SetSubState(UndercoverSubState.ApproachingScrap, $"Spotted next nearby scrap '{nextScrap.itemProperties?.itemName}' for multi-carry ({_scrapManager.CarriedCount}/{PhoneyPlugin.MaxCarriedScrapCount.Value})");
                    SetDestinationSafe(nextScrap.transform.position);
                    _isPickingUpScrap = false;
                    yield break;
                }
            }

            if (Masked!.isOutside)
            {
                // Already outside when picking up scrap!
                // Low chance to haul back to ship (~15%), higher chance to drop at door pile or disrupt
                float shipChance = Mathf.Clamp01(PhoneyPlugin.HaulToShipChance.Value);
                float outRoll = UnityEngine.Random.value;
                if (outRoll < shipChance)
                {
                    _deliveryPlan = ScrapDeliveryPlan.HaulToShip;
                    _scrapDropTarget = StartOfRound.Instance?.shipDoorAudioSource != null
                        ? StartOfRound.Instance.shipDoorAudioSource.transform.position
                        : transform.position;
                }
                else if (outRoll < shipChance + 0.55f)
                {
                    _deliveryPlan = ScrapDeliveryPlan.DropAtOutsideDoor;
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    Vector3 fwd = outDoor != null && outDoor.entrancePoint != null ? outDoor.entrancePoint.forward : transform.forward;
                    _scrapDropTarget = transform.position + fwd * 4.0f;
                    if (NavMesh.SamplePosition(_scrapDropTarget, out var dropHit, 3.0f, NavMesh.AllAreas))
                    {
                        _scrapDropTarget = dropHit.position;
                    }
                }
                else
                {
                    _deliveryPlan = ScrapDeliveryPlan.DisruptRandomDrop;
                    _scrapDropTarget = PickRandomDisruptNode();
                }

                SetSubState(UndercoverSubState.HaulingScrapToEntrance, $"Picked up scrap outside, hauling via {_deliveryPlan}");
                SetDestinationSafe(_scrapDropTarget);
                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' picked up scrap outside! Hauling via {_deliveryPlan}.");
            }
            else
            {
                // Inside facility:
                // Only a small chance (~15% default) to take loot outside!
                // The overwhelming majority of the time (~85%), stay inside near the player, holding scrap or stashing disruptively!
                float haulChance = Mathf.Clamp01(PhoneyPlugin.HaulLootOutsideChance.Value);
                if (UnityEngine.Random.value < haulChance)
                {
                    var interiorDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: false);
                    _scrapDropTarget = interiorDoor != null 
                        ? MaskedScrapManager.GetDoorPosition(interiorDoor) 
                        : _scrapManager!.GetEntranceDropPosition();

                    SetSubState(UndercoverSubState.HaulingScrapToEntrance, "Picked up scrap inside, hauling to exit door to take outside");
                    SetDestinationSafe(_scrapDropTarget);
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' rolled {haulChance:P0} chance: Hauling scrap outside!");
                }
                else
                {
                    SetSubState(UndercoverSubState.SearchingRooms, "Picked up scrap inside, continuing to search/hang around crewmate");
                    PickRoomNearPlayer(TargetPlayer);
                    SetDestinationSafe(_currentRoomTarget);
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' picked up scrap inside — keeping it in inventory and sticking with crewmate!");
                }
            }
        }
        else
        {
            SetSubState(UndercoverSubState.SearchingRooms, "Scrap pickup failed");
            PickRoomNearPlayer(TargetPlayer);
            SetDestinationSafe(_currentRoomTarget);
        }

        _isPickingUpScrap = false;
    }

    private void SetGlow(bool on)
    {
        if (Masked?.maskEyesGlowLight != null)
            Masked.maskEyesGlowLight.enabled = on;

        if (Masked?.maskEyesGlow != null)
            foreach (var r in Masked.maskEyesGlow)
                if (r != null) r.enabled = on;
    }

    private void OnDestroy()
    {
        try
        {
            if (Masked?.agent != null && Masked.agent.isOnOffMeshLink)
            {
                Masked.agent.CompleteOffMeshLink();
            }
        }
        catch { }
    }
}
