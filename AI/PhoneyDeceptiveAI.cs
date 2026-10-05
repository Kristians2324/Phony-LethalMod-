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
    private MaskedHeldItemHolder? _itemHolder;

    private float _phaseEnteredTime;
    private float _crouchCooldownTime;
    private float _lastBumpTime = -99f;
    private bool  _performingCrouch;
    private bool  _isPickingUpScrap;
    private bool  _isUsingDoor;
    private bool  _isTraversingOffMeshLink;
    public  bool  IsClimbingLadder { get; private set; } = false;
    private RuntimeAnimatorController? _originalAnimatorController;
    private float _lastSeenPlayerTime;
    public  bool  IsPerformingFriendlyCrouch => _performingCrouch || _isPickingUpScrap;
    public  bool  IsUsingDoor => _isUsingDoor;
    public  Vector3 DoorInteractionTarget { get; private set; } = Vector3.zero;
    private float _ambientChatterTimer;

    // ── Paranoia & Hostility Escalation System ─────────────────────────────
    private float _nextParanoiaCheckTime;
    private float _hostilityChance;
    private bool  _isHostilePrimed;
    private float _hostilePrimedTime;
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
    private float   _attendPlayerCooldown;
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

    private const float WalkSpeed              = 4.6f;
    private const float UndercoverSprintSpeed  = 9.8f;
    public float AmbushJogSpeed    => PhoneyPlugin.AmbushJogSpeed != null ? PhoneyPlugin.AmbushJogSpeed.Value : 3.8f;
    public float AmbushSprintSpeed => PhoneyPlugin.AmbushSprintSpeed != null ? PhoneyPlugin.AmbushSprintSpeed.Value : 6.8f;

    private float _stamina       = 1.0f;
    private bool  _isSprinting   = false;
    public bool   IsSprinting    => _isSprinting;
    private bool  _networkSyncedRunning = false;
    public bool   NetworkSyncedRunning
    {
        get => _networkSyncedRunning;
        set => _networkSyncedRunning = value;
    }
    private bool    _lastSyncedRunning    = false;
    private Vector3 _currentNavDestination;
    private bool    _hasNavDestination    = false;
    private float   _sprintBurstEndTime;
    private float   _sprintBreatherUntil;

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
    /// Checks whether it is late in the expedition day (after ~3:30 PM, normalizedTimeOfDay >= 0.55f).
    /// Real crewmates only haul scrap back to the ship late in the day. Early and mid day, they drop loot at the entrance door.
    /// </summary>
    public static bool IsLateInExpeditionDay()
    {
        return TimeOfDay.Instance != null && TimeOfDay.Instance.normalizedTimeOfDay >= 0.55f;
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
        _itemHolder   = maskedEnemy.GetComponent<MaskedHeldItemHolder>();
        _originalAnimatorController = maskedEnemy.creatureAnimator?.runtimeAnimatorController;

        if (maskedEnemy.agent != null)
        {
            maskedEnemy.agent.autoTraverseOffMeshLink = false;
            maskedEnemy.agent.acceleration = 28f;
            maskedEnemy.agent.angularSpeed = 600f;
        }

        _hostilityChance = Mathf.Clamp01(Mathf.Max(0.40f, PhoneyPlugin.InitialHostilityChance.Value));
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
            SetDestinationSafe(bestPlayer.transform.position);
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
                    if (Masked.running)   { Masked.running   = false; Masked.SetRunningClientRpc(false);  }
                    if (Masked.crouching) { Masked.crouching = false; Masked.SetCrouchingClientRpc(false); }
                }
                _isSprinting = false;
                _lastSyncedRunning = false;
                _networkSyncedRunning = false;
                _stamina = 1.0f;
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
                _hostilityChance = Mathf.Clamp01(Mathf.Max(0.40f, PhoneyPlugin.InitialHostilityChance.Value));
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
                if (TargetPlayer == null || !IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: false))
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
                    if (Masked.running) { Masked.running = false; Masked.SetRunningClientRpc(false); }
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
                    if (!Masked.running)  { Masked.running  = true;  Masked.SetRunningClientRpc(true);   }
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
                if (_isHostilePrimed)
                {
                    // Already primed and stalking target; push next check forward without resetting hostility
                    _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                    _lastParanoiaLogTime = Time.time;
                }
                else
                {
                    float roll = UnityEngine.Random.value;
                    if (roll < _hostilityChance)
                    {
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' 2-MINUTE ATTACK ROLL: Rolled {roll:P1} < chance {_hostilityChance:P1} -> HOSTILE! Checking player proximity...");

                        PlayerControllerB? target = TargetPlayer != null && IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: false)
                            ? TargetPlayer
                            : GetClosestLivingPlayer(out _);

                        // If living player is in ambush position/range, launch ambush strike immediately!
                        if (target != null && IsNearPlayerForAmbush(target, out string reason))
                        {
                            PhoneyPlugin.Logger.LogInfo(
                                $"[DeceptiveAI] '{Masked.gameObject.name}' Target player '{target.playerUsername}' is in ambush position ({reason}) — launching AMBUSH STRIKE!");
                            TargetPlayer = target;
                            _isHostilePrimed = false;
                            TransitionTo(MimicPhase.AmbushStrike);
                        }
                        else
                        {
                            // Target is further away or across environment boundary — prime hostility so mimic stalks and strikes as soon as it gets near!
                            _isHostilePrimed = true;
                            _hostilePrimedTime = Time.time;
                            _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                            _lastParanoiaLogTime = Time.time;
                            if (target != null)
                            {
                                TargetPlayer = target;
                                SetSubState(UndercoverSubState.SeekingPlayer, $"Hostility primed ({_hostilityChance:P0}) — stalking '{target.playerUsername}'");
                                SetDestinationSafe(target.transform.position);
                            }
                            PhoneyPlugin.Logger.LogInfo(
                                $"[DeceptiveAI] '{Masked.gameObject.name}' 2-minute attack roll hostile! Target is not yet in ambush position. Hostility PRIMED to strike when near player. Stalking '{target?.playerUsername ?? "crewmate"}'.");
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
            }

            // ── Hostility Primed Check (From 2-Minute Timer Roll) ─────────────────
            // When hostility has been primed by the 2-minute roll, strike as soon as the mimic is in ambush range of a player!
            if (_isHostilePrimed)
            {
                PlayerControllerB? closePlayer = TargetPlayer != null && IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: false)
                    ? TargetPlayer
                    : GetClosestLivingPlayer(out float _);

                if (closePlayer != null)
                {
                    float playerDist = Vector3.Distance(transform.position, closePlayer.transform.position);

                    // Throttled heartbeat log for primed status (every 10s)
                    if (Time.time >= _lastPrimedStatusLogTime + 10f)
                    {
                        _lastPrimedStatusLogTime = Time.time;
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' Hostility is PRIMED: stalking '{closePlayer.playerUsername}' (dist: {playerDist:F1}m, sameEnv={IsSameEnvironment(closePlayer)}).");
                    }

                    bool isGreetingActive = _subState == UndercoverSubState.Greeting && _performingCrouch;
                    if (!isGreetingActive && IsNearPlayerForAmbush(closePlayer, out string primedReason))
                    {
                        PhoneyPlugin.Logger.LogInfo(
                            $"[DeceptiveAI] '{Masked.gameObject.name}' Primed hostility triggered ({primedReason}) on '{closePlayer.playerUsername}' — launching AMBUSH STRIKE!");
                        TargetPlayer = closePlayer;
                        _isHostilePrimed = false;
                        TransitionTo(MimicPhase.AmbushStrike);
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
        _currentNavDestination = pos;
        _hasNavDestination = true;
        if (Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            if (!_isUsingDoor && !_performingCrouch && !_isPickingUpScrap && !_isTraversingOffMeshLink)
            {
                NavMeshUtil.SafeSetStopped(Masked.agent, false);
            }
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
            player = (TargetPlayer != null && IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: true))
                ? TargetPlayer
                : GetClosestLivingFacilityPlayer();

            // If no living crewmate is inside the facility, fall back to tracking crewmates outside
            // so the mimic knows where the crew went and can follow them through the exit door!
            if (player == null)
            {
                player = (TargetPlayer != null && IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: false))
                    ? TargetPlayer
                    : GetClosestLivingPlayer(out _, requireSameEnvironment: false);
            }
        }
        else
        {
            // Outside: prioritize crewmates outside, or follow inside if no one outside
            player = (TargetPlayer != null && IsPlayerValidTarget(TargetPlayer, requireSameEnvironment: true))
                ? TargetPlayer
                : GetClosestLivingPlayer(out _, requireSameEnvironment: false);
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

        if (_isHostilePrimed && player != null)
        {
            // Hostility primed from 2-minute roll: actively stalk player to strike!
            UpdateSeekingPlayer(player);
            return;
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
        _currentNavDestination = targetDestination;
        _hasNavDestination = true;
    }

    /// <summary>
    /// Enforces human-like speeds, stamina pacing, carry-weight penalties, and running animations every single frame.
    /// Called from MaskedEnemyPatch.UpdatePostfix to prevent vanilla MaskedPlayerEnemy.Update() from wiping out speed modifiers and running flags.
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

        // Maintain snappy player-like acceleration and turning every frame
        Masked.agent.acceleration = 28f;
        if (!_isUsingDoor && !_isTraversingOffMeshLink)
        {
            Masked.agent.angularSpeed = 600f;
        }

        bool isServer = NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true;

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
            weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 0.35f), 0.70f, 1.0f);
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

            if (elapsed < 1.6f)
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
            bool isBlocked = Masked.agent.isStopped || _performingCrouch || _isPickingUpScrap;

            if (isBlocked)
            {
                _isSprinting = false;
                // Fast recovery while paused, crouching, or picking up loot
                _stamina = Mathf.Clamp01(_stamina + Time.deltaTime * 0.48f);
                targetSpeed = 0f;
            }
            else
            {
                // Dynamic distance check:
                float distToDest = _hasNavDestination
                    ? Vector3.Distance(transform.position, _currentNavDestination)
                    : (Masked.agent.hasPath && !Masked.agent.pathPending ? Masked.agent.remainingDistance : Vector3.Distance(transform.position, Masked.destination));

                bool isHeavy = extraWeight > 0.85f;
                bool isMoving = Masked.agent.velocity.sqrMagnitude > 0.15f;

                if (isServer)
                {
                    if (!_isSprinting)
                    {
                        // Start sprinting if target is far enough away (> 2.0m), has stamina (> 0.20), not heavily burdened, not on breather, and moving
                        if (distToDest > 2.0f && _stamina > 0.20f && !isHeavy && Time.time >= _sprintBreatherUntil && isMoving)
                        {
                            _isSprinting = true;
                            _sprintBurstEndTime = Time.time + UnityEngine.Random.Range(3.5f, 6.5f);
                        }
                    }
                    else
                    {
                        // Stop sprinting if low stamina (<= 0.05), arrived near target (< 1.2m), heavy scrap, or burst timer expired
                        if (_stamina <= 0.05f || distToDest < 1.2f || isHeavy || Time.time >= _sprintBurstEndTime)
                        {
                            _isSprinting = false;
                            // If burst ended naturally and still traveling to a distant destination, take a short breather walk
                            if (distToDest > 3.0f && _stamina > 0.15f)
                            {
                                _sprintBreatherUntil = Time.time + UnityEngine.Random.Range(0.6f, 1.2f);
                            }
                        }
                    }

                    // Smooth frame-by-frame stamina simulation
                    if (_isSprinting)
                    {
                        float drainMultiplier = 1.0f + extraWeight * 1.2f;
                        _stamina = Mathf.Clamp01(_stamina - Time.deltaTime * 0.09f * drainMultiplier);
                    }
                    else
                    {
                        float recoverRate = isMoving ? 0.30f : 0.48f;
                        _stamina = Mathf.Clamp01(_stamina + Time.deltaTime * recoverRate);
                    }
                }

                if (_isSprinting)
                {
                    targetSpeed = UndercoverSprintSpeed * weightFactor;
                }
                else
                {
                    targetSpeed = WalkSpeed * weightFactor;
                }
            }
        }

        NavMeshUtil.SafeSetSpeed(Masked.agent, targetSpeed);

        // Enforce Running animation flag and client RPC synchronization
        if (CurrentPhase != MimicPhase.AmbushStrike)
        {
            bool moving = Masked.agent.velocity.sqrMagnitude > 0.20f && !Masked.agent.isStopped;
            bool shouldRun = (isServer ? _isSprinting : _networkSyncedRunning) && moving && !_performingCrouch && !_isPickingUpScrap;

            Masked.running = shouldRun;
            if (Masked.creatureAnimator != null)
            {
                Masked.creatureAnimator.SetBool("Running", shouldRun);
            }

            if (isServer && _lastSyncedRunning != shouldRun)
            {
                _lastSyncedRunning = shouldRun;
                Masked.SetRunningClientRpc(shouldRun);
            }
        }
        else
        {
            bool moving = Masked.agent.velocity.sqrMagnitude > 0.20f && !Masked.agent.isStopped;
            bool shouldRun = (isServer ? _isAmbushBurst : _networkSyncedRunning) && moving;

            Masked.running = shouldRun;
            if (Masked.creatureAnimator != null)
            {
                Masked.creatureAnimator.SetBool("Running", shouldRun);
            }

            if (isServer && _lastSyncedRunning != shouldRun)
            {
                _lastSyncedRunning = shouldRun;
                Masked.SetRunningClientRpc(shouldRun);
            }
        }

        // Synchronize authentic player holding animation layers & eliminate zombie arms
        _itemHolder ??= GetComponent<MaskedHeldItemHolder>();
        bool hasRealScrap = _scrapManager != null && _scrapManager.HasHeldScrap;
        bool isAggressive = CurrentPhase == MimicPhase.AmbushStrike;

        if (_itemHolder != null)
        {
            if (hasRealScrap)
            {
                _itemHolder.SetHeldToolActive(false);
            }
            else if (!isAggressive)
            {
                // Only restore held tool if not in an active transition that requested it hidden (ladder or door)
                if (!_isTraversingOffMeshLink && !_isUsingDoor)
                {
                    _itemHolder.SetHeldToolActive(true);
                }
            }
            _itemHolder.UpdateAnimationLayers(hasRealScrap, isAggressive);
        }
        else if (!isAggressive)
        {
            Masked.handsOut = false;
            if (Masked.creatureAnimator != null)
            {
                Masked.creatureAnimator.SetBool("HandsOut", false);
            }
        }


        // Smoothly align body and head facing with actual NavMesh velocity to eliminate awkward side-walking
        if (Masked.agent.velocity.sqrMagnitude > 0.15f)
        {
            Vector3 moveDir = Masked.agent.velocity;
            moveDir.y = 0;
            if (moveDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(moveDir), Time.deltaTime * 14f);
            }

            // In non-aggressive phases, keep head gaze strictly forward along velocity vector
            if (CurrentPhase != MimicPhase.AmbushStrike)
            {
                Masked.stareAtTransform = null;
                Masked.lookAtPositionTimer = 0f;
                if (Masked.headTiltTarget != null)
                {
                    Vector3 forwardHead = transform.position + transform.forward * 8f + Vector3.up * 1.4f;
                    Masked.headTiltTarget.LookAt(forwardHead);
                    Masked.headTiltTarget.localEulerAngles = new Vector3(Masked.headTiltTarget.localEulerAngles.x, 0f, 0f);
                }
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

        // If player is in a different environment (e.g. inside while mimic is outside, or vice versa):
        if (Masked != null && player.isInsideFactory != !Masked.isOutside)
        {
            var door = MaskedScrapManager.FindClosestDoor(transform.position, wantEntranceToBuilding: Masked.isOutside);
            if (door != null)
            {
                Vector3 doorNavPos = MaskedScrapManager.GetDoorNavPosition(door);
                Vector3 doorInteractPos = MaskedScrapManager.GetDoorInteractPosition(door);
                float distToDoor = Vector3.Distance(transform.position, doorNavPos);
                bool arrived = distToDoor <= 1.75f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 1.0f);

                if (distToDoor <= 4.0f)
                {
                    Masked.stareAtTransform = null;
                    Masked.LookAtPosition(doorInteractPos, 0.6f);
                }

                if (arrived && !_isUsingDoor)
                {
                    StartCoroutine(DoorTransitionRoutine(door, toOutside: !Masked.isOutside, onComplete: () =>
                    {
                        SetSubState(UndercoverSubState.SeekingPlayer, "Crossed door into player's area");
                    }));
                    return;
                }
                else
                {
                    SetDestinationSafe(doorNavPos);
                    UpdateStaminaAndSpeed(doorNavPos);
                    return;
                }
            }
        }

        // Head toward the player until we are back inside their companion band.
        SetDestinationSafe(player.transform.position);
        UpdateStaminaAndSpeed(player.transform.position);

        if (_isHostilePrimed)
        {
            // Hostility primed: maintain pursuit towards the player until ambush strike triggers!
            return;
        }

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

        // Wait until both timer and crouch animation finish (with safety timeout) before going off to work
        if ((Time.time >= _subStateTimer && !_performingCrouch) || Time.time >= _subStateTimer + 1.5f)
        {
            _performingCrouch = false;
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            SetSubState(UndercoverSubState.SearchingRooms, "Greeting complete");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
        }
    }

    // ── Sub-state 3: Searching Rooms for Scrap ────────────────────────────────

    private void UpdateSearchingRooms(PlayerControllerB? player)
    {
        if (_isHostilePrimed && player != null)
        {
            SetSubState(UndercoverSubState.SeekingPlayer, "Hostility primed — stalking crewmate to strike");
            SetDestinationSafe(player.transform.position);
            UpdateStaminaAndSpeed(player.transform.position);
            return;
        }

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
                SetDestinationSafe(player!.transform.position);
                UpdateStaminaAndSpeed(player.transform.position);
                return;
            }

            // Current room target has drifted out of the player's band (they walked off) or we're past the soft
            // limit: commit to a NEW room inside the band. Throttled so the target isn't re-rolled every AI tick.
            bool roomOutOfBand = Vector3.Distance(_currentRoomTarget, player!.transform.position) > softLimit;
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
        else if (player != null && !IsSameEnvironment(player))
        {
            // Companion is in a DIFFERENT environment (e.g. crewmate went outside, or mimic is outside while crewmate inside):
            // Real crewmates don't wander empty rooms indefinitely while their companion is elsewhere!
            // If the mimic has collected loot, head through the door to deliver; otherwise seek companion!
            if (_scrapManager != null && _scrapManager.HasHeldScrap)
            {
                SetSubState(UndercoverSubState.HaulingScrapToEntrance, "Crewmate is in different environment and mimic has loot — heading to door");
                var door = MaskedScrapManager.FindClosestDoor(transform.position, wantEntranceToBuilding: Masked!.isOutside);
                if (door != null) SetDestinationSafe(MaskedScrapManager.GetDoorNavPosition(door));
                return;
            }
            else
            {
                SetSubState(UndercoverSubState.SeekingPlayer, "Crewmate is in different environment — moving to door to rejoin them");
                var door = MaskedScrapManager.FindClosestDoor(transform.position, wantEntranceToBuilding: Masked!.isOutside);
                if (door != null) SetDestinationSafe(MaskedScrapManager.GetDoorNavPosition(door));
                return;
            }
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
            Vector3 doorNavPos = interiorDoor != null ? MaskedScrapManager.GetDoorNavPosition(interiorDoor) : _scrapDropTarget;
            Vector3 doorInteractPos = interiorDoor != null ? MaskedScrapManager.GetDoorInteractPosition(interiorDoor) : doorNavPos;

            UpdateStaminaAndSpeed(doorNavPos);
            float distToDoor = Vector3.Distance(transform.position, doorNavPos);

            if (distToDoor <= 4.0f && interiorDoor != null)
            {
                Masked.stareAtTransform = null;
                Masked.LookAtPosition(doorInteractPos, 0.6f);
            }

            bool arrivedAtDoor = distToDoor <= 1.75f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 1.0f);
            if (arrivedAtDoor && interiorDoor != null && !_isUsingDoor)
            {
                StartCoroutine(DoorTransitionRoutine(interiorDoor, toOutside: true, onComplete: () =>
                {
                    // Now outside: choose our delivery plan!
                    // Real players drop loot right outside the door (catwalk/landing) to quickly head back in.
                    // Haul to ship is ONLY allowed late in the day (after 3:30 PM, normalizedTimeOfDay >= 0.55f),
                    // and even then at a low chance (max 15-18%). Early/mid day, real players NEVER haul to ship individually.
                    bool isLateInDay = IsLateInExpeditionDay();
                    float shipChance = isLateInDay ? Mathf.Clamp(PhoneyPlugin.HaulToShipChance.Value, 0.05f, 0.18f) : 0f;
                    float disruptChance = 0.12f;
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
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped outside! Delivery plan: DropAtOutsideDoor at {_scrapDropTarget} (isLateInDay={isLateInDay})");
                    }
                    else if (isLateInDay && roll < doorChance + shipChance)
                    {
                        // Option 2: Haul all the way to the Ship (LATE IN THE DAY ONLY)!
                        _deliveryPlan = ScrapDeliveryPlan.HaulToShip;
                        _scrapDropTarget = StartOfRound.Instance?.shipDoorAudioSource != null
                            ? StartOfRound.Instance.shipDoorAudioSource.transform.position
                            : transform.position;
                        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped outside! Delivery plan: HaulToShip (late in day, normalizedTime={TimeOfDay.Instance?.normalizedTimeOfDay:P0})");
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
                SetDestinationSafe(doorNavPos);
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
                    Masked.SetCrouchingClientRpc(false);
                }
                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, UndercoverSprintSpeed);
                _isSprinting = true;

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
                Masked.SetCrouchingClientRpc(false);
            }
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            NavMeshUtil.SafeSetSpeed(Masked.agent, UndercoverSprintSpeed);
            _isSprinting = true;
        }

        var outsideDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
        if (outsideDoor == null)
        {
            SetSubState(UndercoverSubState.SearchingRooms, "Outside entrance door not found");
            PickRoomNearPlayer(player);
            SetDestinationSafe(_currentRoomTarget);
            return;
        }

        Vector3 doorNavPos = MaskedScrapManager.GetDoorNavPosition(outsideDoor);
        Vector3 doorInteractPos = MaskedScrapManager.GetDoorInteractPosition(outsideDoor);
        UpdateStaminaAndSpeed(doorNavPos);

        float dist = Vector3.Distance(transform.position, doorNavPos);
        if (dist <= 4.0f && outsideDoor != null)
        {
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorInteractPos, 0.6f);
        }

        bool arrivedAtDoor = dist <= 1.75f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 1.0f);
        if (arrivedAtDoor && outsideDoor != null && !_isUsingDoor)
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
            SetDestinationSafe(doorNavPos);
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

        // Real players look where they are walking/running! Never glance sideways while actively moving!
        if (Masked.agent != null && Masked.agent.velocity.sqrMagnitude > 0.15f) return;

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

        // Safe landing position on destination NavMesh
        Vector3 exitPos = targetPoint.position;
        if (NavMesh.SamplePosition(exitPos, out var navHit, 3.5f, NavMesh.AllAreas))
        {
            exitPos = navHit.position;
        }
        else if (RoundManager.Instance != null)
        {
            exitPos = RoundManager.Instance.GetNavMeshPosition(exitPos);
        }

        // Face mimic in the landing orientation away from door threshold
        Vector3 forwardDir = targetPoint.forward;
        forwardDir.y = 0;
        if (forwardDir != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(forwardDir.normalized);
        }

        // Cleanly unbind agent from current NavMesh surface before switching environments
        if (Masked.agent != null)
        {
            Masked.agent.enabled = false;
        }
        transform.position = exitPos;
        Masked.serverPosition = exitPos;

        // Synchronize teleportation across network
        try
        {
            if (Masked.IsOwner)
            {
                Masked.TeleportMaskedEnemyAndSync(exitPos, setOutside: toOutside);
            }
            else
            {
                Masked.TeleportMaskedEnemy(exitPos, setOutside: toOutside);
                Masked.TeleportMaskedEnemyServerRpc(exitPos, setOutside: toOutside);
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[Door] TeleportMaskedEnemy caught: {ex.Message}");
        }

        // Re-enable NavMeshAgent on destination NavMesh and warp to position
        if (Masked.agent != null)
        {
            Masked.agent.enabled = true;
            NavMeshUtil.SafeWarp(Masked.agent, exitPos);
            Masked.agent.ResetPath();
        }

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

        Vector3 doorNavPos = MaskedScrapManager.GetDoorNavPosition(door);
        Vector3 doorInteractPos = MaskedScrapManager.GetDoorInteractPosition(door);
        DoorInteractionTarget = doorInteractPos;
        PhoneyNetworkManager.Instance.SyncDoorInteraction(Masked, doorInteractPos, true);

        float distToNav = Vector3.Distance(transform.position, doorNavPos);
        bool isAggressive = CurrentPhase == MimicPhase.AmbushStrike;
        PhoneyPlugin.Logger.LogInfo(
            $"[Door] '{Masked.gameObject.name}' approaching door '{door.gameObject.name}' (distance: {distToNav:F2}m, toOutside: {toOutside}, aggressive={isAggressive}). Initiating interaction.");

        // Walk directly towards doorNavPos until within reach (<= 1.35m) or path completed
        if (distToNav > 1.35f && Masked.agent != null && Masked.agent.isOnNavMesh)
        {
            SetDestinationSafe(doorNavPos);
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
            NavMeshUtil.SafeSetSpeed(Masked.agent, isAggressive ? AmbushSprintSpeed : WalkSpeed);
            float approachTimeout = isAggressive ? 1.5f : 3.5f;
            float elapsedApproach = 0f;
            while (Vector3.Distance(transform.position, doorNavPos) > 1.35f && elapsedApproach < approachTimeout)
            {
                if (Masked == null || Masked.isEnemyDead)
                {
                    _isUsingDoor = false;
                    DoorInteractionTarget = Vector3.zero;
                    PhoneyNetworkManager.Instance.SyncDoorInteraction(Masked, Vector3.zero, false);
                    yield break;
                }
                if (Masked.agent != null && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 0.6f)
                {
                    break;
                }
                elapsedApproach += Time.deltaTime;
                Masked.stareAtTransform = null;
                Masked.LookAtPosition(doorInteractPos, 0.5f);
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
        float turnTime = isAggressive ? 0.08f : 0.35f;
        float elapsedTurn = 0f;
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' turning to face door handle at {doorInteractPos} (turn time: {turnTime:F2}s).");
        while (elapsedTurn < turnTime)
        {
            if (Masked == null || Masked.isEnemyDead)
            {
                _isUsingDoor = false;
                DoorInteractionTarget = Vector3.zero;
                PhoneyNetworkManager.Instance.SyncDoorInteraction(Masked, Vector3.zero, false);
                yield break;
            }
            elapsedTurn += Time.deltaTime;
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorInteractPos, 0.5f);
            Vector3 lookDir = (doorInteractPos - transform.position).normalized;
            lookDir.y = 0;
            if (lookDir != Vector3.zero)
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 20f);
            }
            yield return null;
        }

        // 2. Realistic player interact hold delay:
        // Undercover: 1.0s - 1.3s hold with creak open.
        // Aggressive: 0.15s instant burst through!
        float holdDuration = isAggressive ? 0.15f : UnityEngine.Random.Range(1.0f, 1.3f);
        float elapsedHold = 0f;
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked.gameObject.name}' holding door handle (hold duration: {holdDuration:F2}s, aggressive={isAggressive})...");

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
                PhoneyNetworkManager.Instance.SyncDoorInteraction(Masked, Vector3.zero, false);
                yield break;
            }
            elapsedHold += Time.deltaTime;
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(doorInteractPos, 0.5f);
            Vector3 lookDir = (doorInteractPos - transform.position).normalized;
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
            door.FinishOpeningEntrance(true);
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogDebug($"[Door] FinishOpeningEntrance caught: {ex.Message}");
            if (door.exitScript == null) door.FindExitPoint();
            try { door.PlayAudioAtTeleportPositions(); } catch { }
        }

        // 4. Perform the teleport through the door
        DoorInteractionTarget = Vector3.zero;
        TeleportThroughDoor(door, toOutside);

        // 5. Brief post-door pause (0.1s aggressive, 0.35s undercover)
        yield return new WaitForSeconds(isAggressive ? 0.1f : 0.35f);

        if (Masked?.agent != null)
        {
            NavMeshUtil.SafeSetStopped(Masked.agent, false);
        }
        _isUsingDoor = false;
        DoorInteractionTarget = Vector3.zero;
        PhoneyNetworkManager.Instance.SyncDoorInteraction(Masked, Vector3.zero, false);
        PhoneyPlugin.Logger.LogInfo($"[Door] '{Masked?.gameObject.name}' door transition complete. Resuming navigation.");

        onComplete?.Invoke();
    }

    private static InteractTrigger? FindNearbyLadderTrigger(Vector3 pos, float radius = 3.5f)
    {
        try
        {
            // First: Fast local search using OverlapSphere to avoid scanning entire scene
            var colliders = Physics.OverlapSphere(pos, radius);
            if (colliders != null && colliders.Length > 0)
            {
                InteractTrigger? bestLocal = null;
                float bestDist = radius;
                for (int i = 0; i < colliders.Length; i++)
                {
                    var col = colliders[i];
                    if (col == null) continue;
                    var trigger = col.GetComponent<InteractTrigger>() ?? col.GetComponentInParent<InteractTrigger>();
                    if (trigger != null && trigger.isLadder)
                    {
                        float d = Vector3.Distance(trigger.transform.position, pos);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestLocal = trigger;
                        }
                    }
                }
                if (bestLocal != null) return bestLocal;
            }

            // Fallback: full scene scan if no trigger found in local colliders
            var triggers = UnityEngine.Object.FindObjectsOfType<InteractTrigger>();
            if (triggers != null)
            {
                InteractTrigger? best = null;
                float bestDist = radius;
                for (int i = 0; i < triggers.Length; i++)
                {
                    var t = triggers[i];
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
        var toolHolder = GetComponent<MaskedHeldItemHolder>();
        bool isHoldingVisualTwoHanded = toolHolder != null && toolHolder.IsTwoHanded && toolHolder.HasHeldTool && toolHolder.HeldToolProp != null && toolHolder.HeldToolProp.activeSelf;
        bool hasTwoHanded = isHoldingTwoHanded || isCarryingTwoHanded || isHoldingVisualTwoHanded;

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
                        $"[Ladder] '{Masked.gameObject.name}' reached ladder with two-handed item '{_scrapManager?.HeldScrap?.itemProperties?.itemName ?? (isHoldingVisualTwoHanded ? "VisualWeapon" : "Item")}' (DeltaY={deltaY:F2}m). Enforcing realistic player mechanics.");

                    if (deltaY < -1.0f)
                    {
                        // Real player behavior: players strictly CANNOT descend ladders with two-handed items!
                        PhoneyPlugin.Logger.LogInfo(
                            $"[Ladder] '{Masked.gameObject.name}' refuses downward ladder traversal while holding two-handed item (DeltaY={deltaY:F2}m). Aborting descent to match authentic player rules.");

                        // If holding real scrap, drop it on the floor at the upper landing approach
                        if (_scrapManager != null && _scrapManager.HasHeldScrap)
                        {
                            _scrapManager.DropRealScrap(startPos, isElevatorStaged: false, isLadderStaged: true);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Ladder] '{Masked.gameObject.name}' dropped two-handed scrap at ladder landing {startPos}.");
                        }

                        // Align facing away from ladder rungs
                        Vector3 retreatDir = -ladderFaceDir;
                        retreatDir.y = 0;
                        if (retreatDir.sqrMagnitude > 0.01f) retreatDir.Normalize();
                        else retreatDir = -transform.forward;

                        Vector3 safePos = startPos + retreatDir * 1.2f;
                        if (NavMesh.SamplePosition(safePos, out var navHit, 2.0f, NavMesh.AllAreas))
                        {
                            safePos = navHit.position;
                        }

                        transform.rotation = Quaternion.LookRotation(retreatDir);
                        transform.position = safePos;
                        Masked.serverPosition = safePos;

                        if (Masked.agent != null)
                        {
                            Masked.agent.updatePosition = true;
                            Masked.agent.updateRotation = true;
                            NavMeshUtil.SafeWarp(Masked.agent, safePos);
                            Masked.agent.ResetPath();
                        }

                        PickAlternativeUpperFloorTarget();
                        yield break;
                    }
                    else
                    {
                        // deltaY > 0: Ascending ladder.
                        // Visual two-handed weapons cannot be dropped, so abort climb up as well!
                        if (isHoldingVisualTwoHanded)
                        {
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Ladder] '{Masked.gameObject.name}' holding visual two-handed item, cannot ascend ladder (DeltaY={deltaY:F2}m). Aborting climb.");

                            Vector3 retreatDir = -ladderFaceDir;
                            retreatDir.y = 0;
                            if (retreatDir.sqrMagnitude > 0.01f) retreatDir.Normalize();
                            else retreatDir = -transform.forward;

                            Vector3 safePos = startPos + retreatDir * 1.2f;
                            if (NavMesh.SamplePosition(safePos, out var navHit, 2.0f, NavMesh.AllAreas))
                            {
                                safePos = navHit.position;
                            }

                            transform.rotation = Quaternion.LookRotation(retreatDir);
                            transform.position = safePos;
                            Masked.serverPosition = safePos;

                            if (Masked.agent != null)
                            {
                                Masked.agent.updatePosition = true;
                                Masked.agent.updateRotation = true;
                                NavMeshUtil.SafeWarp(Masked.agent, safePos);
                                Masked.agent.ResetPath();
                            }

                            PickAlternativeUpperFloorTarget();
                            yield break;
                        }

                        // Real scrap ascending: drop at bottom of ladder approach before climbing up
                        droppedLadderScrap = _scrapManager?.HeldScrap;
                        if (droppedLadderScrap != null)
                        {
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

                            Vector3 dropPos = startPos;
                            _scrapManager?.DropRealScrap(dropPos, isElevatorStaged: false, isLadderStaged: true);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Ladder] '{Masked.gameObject.name}' dropped two-handed item at ladder approach {dropPos} before ascending ladder!");
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


                // Traversal along ladder shaft
                bool isAggressive = CurrentPhase == MimicPhase.AmbushStrike;
                float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
                float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);
                float weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 0.40f), 0.50f, 1.0f);
                float baseClimb = isAggressive ? 3.8f : 5.6f;
                float climbSpeed = baseClimb * weightFactor;
                float climbDuration = Mathf.Max(0.20f, absDeltaY / climbSpeed);

                // Step smoothly onto ladder rungs (no instant snap!)
                float alignDuration = 0.10f;
                float elapsedAlign = 0f;
                Vector3 initPos = transform.position;
                Quaternion initRot = transform.rotation;
                Quaternion targetRot = Quaternion.LookRotation(ladderFaceDir);

                // Activate authentic player ladder climbing animation
                StartClimbingLadder(deltaY, weightFactor);
                if (Masked.NetworkObject != null)
                {
                    PhoneyNetworkManager.Instance.BroadcastLadderSync(Masked.NetworkObject.NetworkObjectId, true, deltaY, weightFactor);
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
                    UpdateClimbingLadder(deltaY, weightFactor);
                    yield return null;
                }

                transform.position = climbStart;
                Masked.serverPosition = climbStart;
                transform.rotation = targetRot;

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
                    UpdateClimbingLadder(deltaY, weightFactor);
                    yield return null;
                }

                transform.position = climbEnd;
                Masked.serverPosition = climbEnd;

                // Exit ladder climbing state
                StopClimbingLadder();
                if (Masked.NetworkObject != null)
                {
                    PhoneyNetworkManager.Instance.BroadcastLadderSync(Masked.NetworkObject.NetworkObjectId, false, 0f, 0f);
                }

                // Smooth step off ladder onto destination landing (no instant snap!)
                float stepOffDuration = 0.10f;
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
                bool isAggressive = CurrentPhase == MimicPhase.AmbushStrike;
                float speed = isAggressive ? AmbushSprintSpeed : UndercoverSprintSpeed;
                float duration = Mathf.Max(0.18f, Vector3.Distance(startPos, endPos) / speed);
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
        }
        finally
        {
            StopClimbingLadder();
            if (heldOneHanded != null && heldOneHanded.isHeld)
            {
                heldOneHanded.EnableItemMeshes(true);
            }
            var ladderHolder = GetComponent<MaskedHeldItemHolder>();
            if (ladderHolder != null)
            {
                ladderHolder.RebindLayers();
                bool hasScrap = _scrapManager != null && _scrapManager.HasHeldScrap;
                ladderHolder.SetHeldToolActive(!hasScrap);
                ladderHolder.UpdateAnimationLayers(hasScrap, isAggressive: CurrentPhase == MimicPhase.AmbushStrike);
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

    /// <summary>
    /// Swaps the mimic's animator controller to StartOfRound.Instance.otherClientsAnimatorController
    /// and activates the authentic player SpecialAnimations ladder climbing state and parameters.
    /// </summary>
    public void StartClimbingLadder(float deltaY, float speedMultiplier = 1f)
    {
        if (Masked?.creatureAnimator == null) return;

        if (_originalAnimatorController == null)
        {
            _originalAnimatorController = Masked.creatureAnimator.runtimeAnimatorController;
        }

        if (StartOfRound.Instance?.otherClientsAnimatorController != null &&
            Masked.creatureAnimator.runtimeAnimatorController != StartOfRound.Instance.otherClientsAnimatorController)
        {
            Masked.creatureAnimator.runtimeAnimatorController = StartOfRound.Instance.otherClientsAnimatorController;
        }

        IsClimbingLadder = true;

        if (Masked.lookRig1 != null) Masked.lookRig1.weight = 0f;
        if (Masked.lookRig2 != null) Masked.lookRig2.weight = 0f;

        int specialLayer = Masked.creatureAnimator.GetLayerIndex("SpecialAnimations");
        if (specialLayer >= 0)
        {
            Masked.creatureAnimator.SetLayerWeight(specialLayer, 1.0f);
        }

        Masked.creatureAnimator.SetBool("crouching", false);
        Masked.creatureAnimator.SetBool("ClimbingLadder", true);
        Masked.creatureAnimator.SetTrigger("EnterLadder");
        Masked.creatureAnimator.SetBool("Walking", true);

        float animSpeed = (deltaY >= 0 ? 1.45f : -1.45f) * Mathf.Clamp(speedMultiplier, 0.6f, 2.0f);
        Masked.creatureAnimator.SetFloat("animationSpeed", animSpeed);

        if (specialLayer >= 0)
        {
            Masked.creatureAnimator.CrossFadeInFixedTime("ClimbLadder", 0.08f, specialLayer);
        }
        Masked.creatureAnimator.CrossFadeInFixedTime("ClimbLadder", 0.08f, 0);
    }

    /// <summary>
    /// Maintains the active climbing pose, speeds, and layer weights every frame of ladder traversal.
    /// </summary>
    public void UpdateClimbingLadder(float deltaY, float speedMultiplier = 1f)
    {
        if (!IsClimbingLadder || Masked?.creatureAnimator == null) return;

        if (Masked.lookRig1 != null) Masked.lookRig1.weight = 0f;
        if (Masked.lookRig2 != null) Masked.lookRig2.weight = 0f;

        int specialLayer = Masked.creatureAnimator.GetLayerIndex("SpecialAnimations");
        if (specialLayer >= 0)
        {
            Masked.creatureAnimator.SetLayerWeight(specialLayer, 1.0f);
        }

        Masked.creatureAnimator.SetBool("ClimbingLadder", true);
        Masked.creatureAnimator.SetBool("Walking", true);
        float animSpeed = (deltaY >= 0 ? 1.45f : -1.45f) * Mathf.Clamp(speedMultiplier, 0.6f, 2.0f);
        Masked.creatureAnimator.SetFloat("animationSpeed", animSpeed);
    }

    /// <summary>
    /// Cleanly exits the ladder climbing state and restores the mimic's original animator controller.
    /// </summary>
    public void StopClimbingLadder()
    {
        if (!IsClimbingLadder) return;
        IsClimbingLadder = false;

        if (Masked?.creatureAnimator != null)
        {
            Masked.creatureAnimator.SetBool("ClimbingLadder", false);
            Masked.creatureAnimator.SetBool("Walking", false);
            Masked.creatureAnimator.SetFloat("animationSpeed", 0f);

            int specialLayer = Masked.creatureAnimator.GetLayerIndex("SpecialAnimations");
            if (specialLayer >= 0)
            {
                Masked.creatureAnimator.SetTrigger("SA_stopAnimation");
                Masked.creatureAnimator.SetLayerWeight(specialLayer, 0f);
            }

            if (_originalAnimatorController != null &&
                Masked.creatureAnimator.runtimeAnimatorController != _originalAnimatorController)
            {
                Masked.creatureAnimator.runtimeAnimatorController = _originalAnimatorController;
            }
        }

        if (Masked?.lookRig1 != null) Masked.lookRig1.weight = 0.452f;
        if (Masked?.lookRig2 != null) Masked.lookRig2.weight = 1f;

        var holder = GetComponent<MaskedHeldItemHolder>();
        holder?.RebindLayers();
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

        if (TargetPlayer == null || TargetPlayer.isPlayerDead || !TargetPlayer.isPlayerControlled)
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

        // ── Cross-Environment Door Pursuit ───────────────────────────────────
        // When the player flees to the outside (or inside), CHASE THEM THROUGH THE DOOR!
        bool differentSide = TargetPlayer.isInsideFactory != (!Masked.isOutside);
        if (differentSide)
        {
            if (_isUsingDoor) return;

            bool mimicIsInside = !Masked.isOutside;
            var chaseDoor = MaskedScrapManager.FindClosestDoor(transform.position, wantEntranceToBuilding: !mimicIsInside);
            if (chaseDoor != null)
            {
                Vector3 doorNavPos = MaskedScrapManager.GetDoorNavPosition(chaseDoor);
                Vector3 doorInteractPos = MaskedScrapManager.GetDoorInteractPosition(chaseDoor);
                float distToDoor = Vector3.Distance(transform.position, doorNavPos);
                bool arrived = distToDoor <= 1.85f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 1.2f);

                if (arrived)
                {
                    // At the door — burst through it to chase the player!
                    PhoneyPlugin.Logger.LogInfo(
                        $"[Pursuit] '{Masked.gameObject.name}' CHASING player '{TargetPlayer.playerUsername}' through door to {(mimicIsInside ? "outside" : "inside")}!");
                    StartCoroutine(DoorTransitionRoutine(chaseDoor, toOutside: mimicIsInside, onComplete: () =>
                    {
                        // Resumed chase on the other side!
                        _lastSeenPlayerTime = Time.time;
                        _hadLOSLastFrame = true;
                        if (TargetPlayer != null)
                        {
                            Masked.targetPlayer = TargetPlayer;
                            Masked.movingTowardsTargetPlayer = true;
                            Masked.SetMovingTowardsTargetPlayer(TargetPlayer);
                            Masked.SetDestinationToPosition(TargetPlayer.transform.position);
                            PhoneyPlugin.Logger.LogInfo(
                                $"[Pursuit] '{Masked.gameObject.name}' emerged through door! Resuming pursuit of '{TargetPlayer.playerUsername}'.");
                        }
                    }));
                    return;
                }
                else
                {
                    // Sprint to the door
                    Masked.targetPlayer = null;
                    Masked.movingTowardsTargetPlayer = false;
                    SetDestinationSafe(doorNavPos);
                    NavMeshUtil.SafeSetStopped(Masked.agent, false);
                    NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushSprintSpeed);
                    if (distToDoor <= 4.0f)
                    {
                        Masked.stareAtTransform = null;
                        Masked.LookAtPosition(doorInteractPos, 0.6f);
                    }
                }
            }

            // Only give up if the player has been on the other side for 35+ seconds
            float crossTime = Time.time - _lastSeenPlayerTime;
            if (crossTime >= 35.0f)
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}': Player '{TargetPlayer.playerUsername}' escaped through door and mimic couldn't follow in time ({crossTime:F1}s >= 35s). Giving up.");
                Masked.targetPlayer = null;
                Masked.movingTowardsTargetPlayer = false;
                TargetPlayer = null;
                TransitionTo(MimicPhase.UndercoverLooting);
                return;
            }
            return; // Don't run same-side pursuit while navigating to door
        }

        // ── Same-Side Pursuit ────────────────────────────────────────────────
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

        // Phase 2: Reaction window jog with hands outstretched (~0.5s jog after 1.1s transform = elapsed < 1.6f)
        // Mimic lunges forward with hands outstretched at jog speed, giving the player time to turn and sprint!
        if (elapsed < 1.6f)
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
                Masked.SetRunningClientRpc(false);
            }
            _ambushStamina = 1.0f;
            _isAmbushBurst = false;
        }
        else
        {
            // Phase 3: Relentless pursuit with burst sprinting & stamina pacing
            // Sprints in bursts, draining stamina, then falling back to recovery jog
            if (!_isAmbushBurst)
            {
                // Enter sprint burst once stamina recovers to at least 45%
                if (_ambushStamina >= 0.45f)
                {
                    _isAmbushBurst = true;
                    if (isServer) { Masked.running = true; Masked.SetRunningClientRpc(true); }
                    PhoneyPlugin.Logger.LogInfo($"[Pursuit] '{Masked.gameObject.name}' entered burst sprint! Speed: {AmbushSprintSpeed:F2} m/s (stamina: {_ambushStamina:P0}).");
                }
            }
            else
            {
                // Drop to recovery jog when stamina runs out
                if (_ambushStamina <= 0.08f)
                {
                    _isAmbushBurst = false;
                    if (isServer) { Masked.running = false; Masked.SetRunningClientRpc(false); }
                    PhoneyPlugin.Logger.LogInfo($"[Pursuit] '{Masked.gameObject.name}' sprint stamina depleted ({_ambushStamina:P0}), dropping to recovery jog. Speed: {AmbushJogSpeed:F2} m/s.");
                }
            }

            if (_isAmbushBurst)
            {
                // Sprint burst: drains stamina over ~4.5 seconds
                _ambushStamina = Mathf.Clamp01(_ambushStamina - Time.deltaTime * 0.22f);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushSprintSpeed);
            }
            else
            {
                // Recovery jog: catches breath over ~2.4 seconds
                _ambushStamina = Mathf.Clamp01(_ambushStamina + Time.deltaTime * 0.38f);
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
            : 18.0f;

        float maxRange = PhoneyPlugin.MaxChaseRange != null
            ? PhoneyPlugin.MaxChaseRange.Value
            : 85.0f;
        if (Masked.isOutside)
        {
            maxRange = Mathf.Max(maxRange, 140.0f);
        }

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
                    $"[Pursuit] '{Masked.gameObject.name}' LOST line of sight to '{TargetPlayer.playerUsername}'! (Distance: {dist:F1}m, break timeout: {timeout:F1}s, max range: {maxRange:F1}m). Searching last known trajectory.");
                _hadLOSLastFrame = false;
                _lastLOSLogTime = Time.time;
            }
            else if (Time.time >= _lastLOSLogTime + 2.5f)
            {
                _lastLOSLogTime = Time.time;
                float currentBroken = Time.time - _lastSeenPlayerTime;
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}': Hunting '{TargetPlayer.playerUsername}' without LOS ({currentBroken:F1}s / {timeout:F1}s, dist: {dist:F1}m / {maxRange:F1}m).");
            }
        }

        float timeWithoutLOS = Time.time - _lastSeenPlayerTime;

        // Dynamic re-targeting: If primary target has broken line of sight for >= 3.0s,
        // but another living crewmate is closer or in clear line of sight, switch pursuit to them!
        if (!hasLOS && timeWithoutLOS >= 3.0f)
        {
            var altTarget = GetClosestLivingPlayer(out float altDist, requireSameEnvironment: true);
            if (altTarget != null && altTarget != TargetPlayer && (altDist <= 16.0f || HasLineOfSight(altTarget)))
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[Pursuit] '{Masked.gameObject.name}' lost sight of '{TargetPlayer.playerUsername}' for {timeWithoutLOS:F1}s, but acquired nearby target '{altTarget.playerUsername}' (dist: {altDist:F1}m, hasLOS: {HasLineOfSight(altTarget)})! Retargeting.");
                TargetPlayer = altTarget;
                Masked.targetPlayer = altTarget;
                Masked.SetMovingTowardsTargetPlayer(altTarget);
                Masked.SetDestinationToPosition(altTarget.transform.position);
                _lastSeenPlayerTime = Time.time;
                _hadLOSLastFrame = true;
                timeWithoutLOS = 0f;
                hasLOS = true;
            }
        }

        // Escape conditions:
        // 1. If mimic HAS line of sight: it will NEVER give up pursuit based on distance alone,
        //    unless at extreme planetary limits (> 160m outside).
        // 2. If line of sight is BROKEN:
        //    a. Full grace period elapsed (default >= 18.0s continuous broken LOS).
        //    b. OR player managed to get beyond maxRange (85m inside / 140m outside) AND stayed hidden for >= 10.0s.
        bool lostLOSLongEnough = !hasLOS && timeWithoutLOS >= timeout;
        bool brokenAway = !hasLOS && dist > maxRange && timeWithoutLOS >= 10.0f;
        bool extremeDistance = dist > (Masked.isOutside ? 160.0f : 100.0f);

        if (lostLOSLongEnough || brokenAway || extremeDistance)
        {
            string escapeReason = lostLOSLongEnough ? $"LOS broken continuously for {timeWithoutLOS:F1}s >= {timeout:F1}s" :
                                 (brokenAway ? $"Player escaped beyond pursuit range ({dist:F1}m > {maxRange:F1}m) and hidden for {timeWithoutLOS:F1}s >= 10.0s" :
                                 $"Extreme distance ({dist:F1}m > {(Masked.isOutside ? 160 : 100)}m)");

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
                Vector3 doorNavPos = MaskedScrapManager.GetDoorNavPosition(outDoor);
                Masked.SetDestinationToPosition(doorNavPos);
                if (Vector3.Distance(transform.position, doorNavPos) <= 2.5f && !_isUsingDoor)
                {
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}': Reached entrance door during retreat — entering facility and returning to normal.");
                    StartCoroutine(DoorTransitionRoutine(outDoor, toOutside: false, onComplete: () =>
                    {
                        TransitionTo(MimicPhase.UndercoverLooting);
                    }));
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

        Vector3 center = (player != null && IsSameEnvironment(player)) ? player.transform.position : transform.position;

        // When inside facility and accompanying a crewmate, roam in a realistic squadmate band (6m - 20m)
        // so the mimic explores ahead/behind/nearby without crowding (personal space >= 1.8m).
        bool isInside = !Masked.isOutside;
        float minDist = isInside ? 6.0f : 8.0f;
        float maxDist = isInside ? 20.0f : 28.0f;

        // Discard candidate nodes that are too close to recent targets to prevent repetitive jitter/pacing
        var candidates = new List<Vector3>(16);
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null) continue;
            Vector3 pos = n.transform.position;
            float d = Vector3.Distance(pos, center);
            if (d < minDist || d > maxDist) continue;

            bool tooClose = false;
            foreach (var recent in _recentRoomTargets)
            {
                if (Vector3.Distance(pos, recent) < 5.0f)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose)
            {
                candidates.Add(pos);
            }
        }

        if (candidates.Count == 0)
        {
            // If all filtered out by history, relax the history check
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n == null) continue;
                Vector3 pos = n.transform.position;
                float d = Vector3.Distance(pos, center);
                if (d >= minDist && d <= maxDist)
                {
                    candidates.Add(pos);
                }
            }
        }

        Vector3 chosen;
        if (candidates.Count > 0)
        {
            chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }
        else
        {
            // Fallback: sample NavMesh in the band around center
            Vector2 circle = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(minDist, maxDist);
            Vector3 samplePos = center + new Vector3(circle.x, 0, circle.y);
            if (NavMesh.SamplePosition(samplePos, out var hit, 6.0f, NavMesh.AllAreas))
            {
                chosen = hit.position;
            }
            else
            {
                var fallback = nodes.Where(n => Vector3.Distance(n.transform.position, center) < 32f).ToList();
                chosen = (fallback.Count > 0 ? fallback[UnityEngine.Random.Range(0, fallback.Count)] : nodes[0]).transform.position;
            }
        }

        _currentRoomTarget = chosen;
        _recentRoomTargets.Enqueue(chosen);
        while (_recentRoomTargets.Count > 4)
        {
            _recentRoomTargets.Dequeue();
        }
    }

    private void PickAlternativeUpperFloorTarget()
    {
        _targetScrap = null;
        SetSubState(UndercoverSubState.SearchingRooms, "Aborted ladder traversal with two-handed item");
        PlayerControllerB? player = TargetPlayer ?? GetClosestLivingPlayer(out _);
        PickRoomNearPlayer(player);
        SetDestinationSafe(_currentRoomTarget);
    }

    public bool IsSameEnvironment(PlayerControllerB? player)
    {
        if (player == null || Masked == null) return false;
        return player.isInsideFactory == !Masked.isOutside;
    }

    public float LeashDistance(PlayerControllerB? player)
    {
        if (player == null || Masked == null) return float.MaxValue;
        return Vector3.Distance(transform.position, player.transform.position);
    }

    public void GetLeashBand(PlayerControllerB? player, out float minFollow, out float comfortMax, out float softLimit, out float hardLimit)
    {
        if (Masked != null && !Masked.isOutside)
        {
            minFollow = 5.0f;
            comfortMax = 15.0f;
            softLimit = 22.0f;
            hardLimit = 32.0f;
        }
        else
        {
            minFollow = 7.0f;
            comfortMax = 20.0f;
            softLimit = 30.0f;
            hardLimit = 42.0f;
        }
    }

    public Vector3 GetScrapLeashCenter(PlayerControllerB? player, out float scrapLeashRadius)
    {
        if (player != null && IsSameEnvironment(player))
        {
            scrapLeashRadius = 22.0f;
            return player.transform.position;
        }
        scrapLeashRadius = 20.0f;
        return transform.position;
    }

    public bool UpdateAttendPlayer(PlayerControllerB? player)
    {
        if (player == null || Masked == null || _isUsingDoor || _isTraversingOffMeshLink || _isPickingUpScrap)
            return false;

        // If player is close, occasionally glance at them naturally without stopping or locking movement
        if (Time.time < _attendPlayerUntil)
        {
            Masked.stareAtTransform = null;
            Masked.LookAtPosition(player.gameplayCamera != null ? player.gameplayCamera.transform.position : (player.transform.position + Vector3.up * 1.5f), 0.4f);
            return false; // Don't block state machine updates! Let mimic keep moving and looting!
        }

        // Trigger brief head glance if player is close (< 3.8m), has LOS, and is looking at the mimic
        if (IsSameEnvironment(player) && Vector3.Distance(transform.position, player.transform.position) <= 3.8f && HasLineOfSight(player))
        {
            Vector3 toMimic = (transform.position - player.transform.position).normalized;
            Vector3 playerLook = player.gameplayCamera != null ? player.gameplayCamera.transform.forward : player.transform.forward;
            bool playerLookingAtMimic = Vector3.Dot(playerLook, toMimic) > 0.65f;

            if (playerLookingAtMimic && Time.time >= _attendPlayerCooldown)
            {
                _attendPlayerCooldown = Time.time + UnityEngine.Random.Range(8.0f, 15.0f);
                _attendPlayerUntil = Time.time + UnityEngine.Random.Range(1.0f, 2.0f);
                Masked.stareAtTransform = null;
                Masked.LookAtPosition(player.gameplayCamera != null ? player.gameplayCamera.transform.position : (player.transform.position + Vector3.up * 1.5f), 0.4f);
            }
        }

        return false;
    }

    public void SetDoorInteraction(Vector3 doorPos, bool isUsingDoor)
    {
        _isUsingDoor = isUsingDoor;
        DoorInteractionTarget = doorPos;
        if (Masked != null && isUsingDoor)
        {
            Masked.stareAtTransform = null;
            Masked.targetPlayer = null;
        }
    }

    /// <summary>
    /// Checks whether a target player is in ambush position / range.
    /// Evaluates proximity (<= aggroRange), line of sight (<= 14m), back turned (<= 12m),
    /// physical collision (<= 2.2m), and stalking patience timeout.
    /// </summary>
    public bool IsNearPlayerForAmbush(PlayerControllerB? target, out string reason)
    {
        reason = string.Empty;
        if (target == null || target.isPlayerDead || Masked == null) return false;
        if (!IsSameEnvironment(target)) return false;

        float dist = Vector3.Distance(transform.position, target.transform.position);
        float aggroRange = PhoneyPlugin.AmbushDistanceThreshold != null 
            ? Mathf.Max(12.0f, PhoneyPlugin.AmbushDistanceThreshold.Value) 
            : 16.0f;

        // 1. Direct physical collision / close contact (< 2.4m)
        if (dist <= 2.4f)
        {
            reason = $"Physical contact ({dist:F1}m <= 2.4m)";
            return true;
        }

        // 2. Proximity threshold (within room / corridor range <= aggroRange)
        if (dist <= aggroRange)
        {
            reason = $"Within proximity range ({dist:F1}m <= {aggroRange:F1}m)";
            return true;
        }

        // 3. Clear line of sight visual contact (24m inside, 38m outside)
        float losVisualRange = Masked.isOutside ? 38.0f : 24.0f;
        if (dist <= losVisualRange && HasLineOfSight(target))
        {
            reason = $"Line of sight visual contact ({dist:F1}m <= {losVisualRange:F0}m)";
            return true;
        }

        // 4. Backstab: Player has back turned to mimic (16m inside, 26m outside)
        float backstabRange = Masked.isOutside ? 26.0f : 16.0f;
        if (dist <= backstabRange)
        {
            Vector3 toMimic = (transform.position - target.transform.position).normalized;
            Vector3 playerFacing = target.gameplayCamera != null ? target.gameplayCamera.transform.forward : target.transform.forward;
            float facingDot = Vector3.Dot(playerFacing, toMimic);

            if (facingDot < -0.1f)
            {
                reason = $"Back turned on mimic ({dist:F1}m, dot={facingDot:F2})";
                return true;
            }
        }

        // 5. Stalking patience timeout: Mimic has stalked in primed state for > 12s within 24m (or 35m outside)
        float stalkRange = Masked.isOutside ? 35.0f : 24.0f;
        if (_isHostilePrimed && (Time.time - _hostilePrimedTime > 12f) && dist <= stalkRange)
        {
            reason = $"Stalking patience elapsed ({Time.time - _hostilePrimedTime:F0}s > 12s, dist={dist:F1}m)";
            return true;
        }

        return false;
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
    /// Checks whether a player is a valid, living, controllable human player.
    /// Can optionally restrict to the same interior/exterior environment.
    /// Avoids vanilla Mineshaft elevator/start tile lockout bugs where PlayerIsTargetable returns false for valid players.
    /// </summary>
    public bool IsPlayerValidTarget(PlayerControllerB? player, bool requireSameEnvironment = false)
    {
        if (player == null || !player.isPlayerControlled || player.isPlayerDead) return false;
        if (Masked == null) return false;

        if (requireSameEnvironment)
        {
            bool playerInside = player.isInsideFactory;
            bool mimicInside  = !Masked.isOutside;
            return playerInside == mimicInside;
        }

        return true;
    }

    private bool HasLineOfSight(PlayerControllerB player)
    {
        if (player == null || Masked == null) return false;
        Vector3 eyePos = transform.position + Vector3.up * 1.5f;
        Vector3 playerPos = player.transform.position + Vector3.up * 1.5f;
        return !Physics.Linecast(eyePos, playerPos, StartOfRound.Instance.collidersAndRoomMaskAndDefault);
    }

    /// <summary>
    /// Gets the closest living controllable player.
    /// Prefers players in the same environment (interior vs exterior),
    /// but seamlessly falls back to players in the other environment so mimics
    /// track crewmates and follow them through facility doors.
    /// </summary>
    private PlayerControllerB? GetClosestLivingPlayer(out float closestDistance, bool requireSameEnvironment = false)
    {
        closestDistance = float.MaxValue;
        PlayerControllerB? closest = null;
        var players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || Masked == null) return null;

        // Pass 1: Try finding closest player in the same environment first
        foreach (var p in players)
        {
            if (!IsPlayerValidTarget(p, requireSameEnvironment: true)) continue;

            float d = Vector3.Distance(transform.position, p.transform.position);
            if (d < closestDistance) { closestDistance = d; closest = p; }
        }

        if (closest != null || requireSameEnvironment) return closest;

        // Pass 2: If no living players in same environment, find closest living player anywhere on moon
        closestDistance = float.MaxValue;
        foreach (var p in players)
        {
            if (!IsPlayerValidTarget(p, requireSameEnvironment: false)) continue;

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
            if (isServer) { Masked.crouching = true; Masked.SetCrouchingClientRpc(true); }
            yield return new WaitForSeconds(0.28f);
            if (Masked == null) break;
            if (isServer) { Masked.crouching = false; Masked.SetCrouchingClientRpc(false); }
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
            Masked.SetCrouchingClientRpc(true);
        }

        // Bend down to reach item on the floor
        yield return new WaitForSeconds(0.42f);

        bool pickedUp = _scrapManager != null && _scrapManager.PickUpRealScrap(scrap);

        // Brief pause to grasp and secure the item
        yield return new WaitForSeconds(0.18f);

        if (isServer && Masked != null)
        {
            Masked.crouching = false;
            Masked.SetCrouchingClientRpc(false);
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
                // Real players only haul to ship late in the day (after 3:30 PM, normalizedTimeOfDay >= 0.55f)
                bool isLateInDay = IsLateInExpeditionDay();
                float shipChance = isLateInDay ? Mathf.Clamp(PhoneyPlugin.HaulToShipChance.Value, 0.05f, 0.18f) : 0f;
                float outRoll = UnityEngine.Random.value;
                if (isLateInDay && outRoll < shipChance)
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
                // If inventory is full (can't carry more) or holding a two-handed item, real players haul outside!
                // Otherwise roll HaulLootOutsideChance (~15% default).
                bool inventoryFull = _scrapManager != null && !_scrapManager.CanPickUpMoreScrap();
                bool twoHanded = _scrapManager != null && _scrapManager.IsHoldingTwoHanded;
                float haulChance = (inventoryFull || twoHanded) ? 1.0f : Mathf.Clamp01(PhoneyPlugin.HaulLootOutsideChance.Value);

                if (UnityEngine.Random.value < haulChance)
                {
                    var interiorDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: false);
                    _scrapDropTarget = interiorDoor != null 
                        ? MaskedScrapManager.GetDoorNavPosition(interiorDoor) 
                        : _scrapManager!.GetEntranceDropPosition();

                    SetSubState(UndercoverSubState.HaulingScrapToEntrance, $"Picked up scrap inside (full={inventoryFull}, twoHanded={twoHanded}), hauling to exit door to take outside");
                    SetDestinationSafe(_scrapDropTarget);
                    PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' hauling scrap outside! (invFull={inventoryFull}, twoHanded={twoHanded}, rollChance={haulChance:P0})");
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
