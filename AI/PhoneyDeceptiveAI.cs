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
    private float _ambientChatterTimer;

    // ── Paranoia & Hostility Escalation System ─────────────────────────────
    private float _nextParanoiaCheckTime;
    private float _hostilityChance;
    private bool  _isHostilePrimed;

    // ── Undercover Sub-state Machine ─────────────────────────────────────────
    private UndercoverSubState _subState = UndercoverSubState.SeekingPlayer;
    private float              _subStateTimer;
    private Vector3            _currentRoomTarget;
    private GrabbableObject?   _targetScrap;
    private ScrapDeliveryPlan  _deliveryPlan = ScrapDeliveryPlan.None;
    private Vector3            _scrapDropTarget;
    private bool               _hasGreetedPlayer;
    private float              _greetingResetTime;

    private const float WalkSpeed              = 2.4f;
    private const float UndercoverSprintSpeed  = 4.4f;
    public float AmbushJogSpeed    => PhoneyPlugin.AmbushJogSpeed != null ? PhoneyPlugin.AmbushJogSpeed.Value : 2.5f;
    public float AmbushSprintSpeed => PhoneyPlugin.AmbushSprintSpeed != null ? PhoneyPlugin.AmbushSprintSpeed.Value : 4.4f;

    private float _stamina       = 1.0f;
    private bool  _isSprinting   = false;
    private float _ambushStamina = 1.0f;
    private bool  _isAmbushBurst = false;
    private float _nextPursuitTauntTime;
    private float _inspectNextTurnTime;
    private Quaternion _inspectTargetRotation;

    // ─── Mineshaft Elevator Integration ───────────────────────────────────────

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

        _hostilityChance = Mathf.Clamp01(PhoneyPlugin.InitialHostilityChance.Value);
        _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
        _isHostilePrimed = false;
        _ambientChatterTimer = Time.time + UnityEngine.Random.Range(30f, 45f);

        _subState = UndercoverSubState.SeekingPlayer;
        TransitionTo(MimicPhase.UndercoverLooting);

        PhoneyPlugin.Logger.LogInfo(
            $"[DeceptiveAI] '{maskedEnemy.gameObject.name}' initialized. Paranoia timer: {PhoneyPlugin.ParanoiaIntervalSeconds.Value:F0}s (initial chance: {_hostilityChance:P0}).");
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
                if (isServer)
                {
                    if (Masked.handsOut)  { Masked.handsOut  = false; Masked.SetHandsOutServerRpc(false); }
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

                _subState = UndercoverSubState.SearchingRooms;
                PickRoomNearPlayer(TargetPlayer);
                Masked.SetDestinationToPosition(_currentRoomTarget);
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

                // Drop any held scrap immediately when revealing true form
                _scrapManager?.DropHeldScrapImmediately();

                if (TargetPlayer == null || TargetPlayer.isPlayerDead || !Masked.PlayerIsTargetable(TargetPlayer))
                {
                    TargetPlayer = GetClosestLivingPlayer(out _);
                }

                if (TargetPlayer != null)
                {
                    Masked.targetPlayer = TargetPlayer;
                    Masked.movingTowardsTargetPlayer = true;
                    Masked.SetMovingTowardsTargetPlayer(TargetPlayer);
                    Masked.SetDestinationToPosition(TargetPlayer.transform.position);
                }

                _ambushStamina = 1.0f;
                _isAmbushBurst = false;

                NavMeshUtil.SafeSetStopped(Masked.agent, false);
                NavMeshUtil.SafeSetSpeed(Masked.agent, AmbushJogSpeed);
                if (Masked.agent != null && Masked.agent.isOnNavMesh)
                {
                    Masked.agent.stoppingDistance = 0f;
                }
                if (isServer)
                {
                    if (!Masked.handsOut) { Masked.handsOut = true; Masked.SetHandsOutServerRpc(true); }
                    if (Masked.running)   { Masked.running  = false; Masked.SetRunningServerRpc(false); }
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
                if (isServer)
                {
                    if (Masked.handsOut)  { Masked.handsOut = false; Masked.SetHandsOutServerRpc(false); }
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

        // ── Paranoia Escalation Timer (2-Minute Intervals) ────────────────────
        if (CurrentPhase != MimicPhase.AmbushStrike && CurrentPhase != MimicPhase.TacticalRetreat)
        {
            if (!_isHostilePrimed && Time.time >= _nextParanoiaCheckTime)
            {
                float roll = UnityEngine.Random.value;
                if (roll < _hostilityChance)
                {
                    _isHostilePrimed = true;
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' HOSTILITY PRIMED! (Rolled {roll:P0} < {_hostilityChance:P0}). Will strike when player within {PhoneyPlugin.AmbushDistanceThreshold.Value:F1}m!");
                }
                else
                {
                    _hostilityChance = Mathf.Clamp01(_hostilityChance + PhoneyPlugin.HostilityChanceIncrement.Value);
                    _nextParanoiaCheckTime = Time.time + PhoneyPlugin.ParanoiaIntervalSeconds.Value;
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' rolled peaceful (Rolled {roll:P0} >= {_hostilityChance:P0}). Escalated chance: {_hostilityChance:P0}. Next roll in {PhoneyPlugin.ParanoiaIntervalSeconds.Value:F0}s.");
                }
            }

            // ── Proximity Strike Trigger (Only when Primed AND <= 3.0m away) ───
            if (_isHostilePrimed)
            {
                PlayerControllerB? closePlayer = GetClosestLivingPlayer(out float playerDist);
                if (closePlayer != null && playerDist <= PhoneyPlugin.AmbushDistanceThreshold.Value)
                {
                    PhoneyPlugin.Logger.LogInfo(
                        $"[DeceptiveAI] '{Masked.gameObject.name}' BOOM! Player '{closePlayer.playerUsername}' is {playerDist:F1}m away (<= {PhoneyPlugin.AmbushDistanceThreshold.Value:F1}m) — AMBUSH STRIKE!");
                    TargetPlayer = closePlayer;
                    TransitionTo(MimicPhase.AmbushStrike);
                }
            }
        }
    }

    // ─── Main AI Interval (Called via DoAIInterval HarmonyPrefix) ─────────────

    public bool CustomDoAIInterval()
    {
        if (Masked == null || Masked.isEnemyDead || !PhoneyPlugin.EnableDeceptiveAI.Value)
            return false;

        if (Masked.inKillAnimation) return false;

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

    // ─── Phase 1: Realistic Undercover Scrap Looting & Room Searching ─────────

    private void UpdateUndercover()
    {
        if (Masked == null) return;

        PlayerControllerB? player = TargetPlayer != null && !TargetPlayer.isPlayerDead && Masked.PlayerIsTargetable(TargetPlayer)
            ? TargetPlayer
            : GetClosestLivingPlayer(out _);

        if (player != null)
        {
            TargetPlayer = player;
            // Never set Masked.targetPlayer during undercover mode!
            // When targetPlayer is set, vanilla Update forces model rotation towards targetPlayer,
            // making the mimic walk backwards and stare upwards!
            Masked.targetPlayer = null;
            Masked.movingTowardsTargetPlayer = false;
        }

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

        // Weight carry penalty: heavy scrap visibly and significantly slows the mimic down, exactly like real players
        _scrapManager ??= GetComponent<MaskedScrapManager>();
        float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
        // In Lethal Company, extraWeight = totalWeight - 1.0f.
        // HUD displays (extraWeight * 105) lbs.
        // 0 lbs = 0.0f, 15 lbs = 0.14f, 30 lbs = 0.28f, 50 lbs = 0.48f, 80 lbs = 0.76f
        float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);

        // Realistic weight factor: 15 lbs -> ~0.76x, 30 lbs -> ~0.61x, 50 lbs -> ~0.48x, 80 lbs -> ~0.37x
        float weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 2.2f), 0.22f, 1.0f);

        // If carrying heavy scrap (> 25 lbs / extraWeight > 0.24f), players cannot sustain sprinting
        bool isHeavy = extraWeight > 0.24f;

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
            float drainMultiplier = 1.0f + extraWeight * 3.5f;
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

        _scrapManager ??= GetComponent<MaskedScrapManager>();
        float totalWeight = _scrapManager != null ? _scrapManager.TotalCarryWeight : 1.0f;
        float extraWeight = Mathf.Max(0f, totalWeight - 1.0f);
        float weightFactor = Mathf.Clamp(1.0f / (1.0f + extraWeight * 2.2f), 0.22f, 1.0f);

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
                    Masked.UseElevator(targetOnUpper);
                    return;
                }
                else if (Masked.IsInsideMineshaftElevator(transform.position) && !elevator.elevatorFinishedMoving)
                {
                    return;
                }
            }
        }

        Masked!.SetDestinationToPosition(player.transform.position);
        UpdateStaminaAndSpeed(player.transform.position);
        float dist = Vector3.Distance(transform.position, player.transform.position);

        if (dist <= 14f && HasLineOfSight(player))
        {
            _subState = UndercoverSubState.Greeting;
            _subStateTimer = Time.time + 2.0f;

            NavMeshUtil.SafeSetStopped(Masked.agent, true);
            NavMeshUtil.SafeSetVelocity(Masked.agent, Vector3.zero);

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
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked!.SetDestinationToPosition(_currentRoomTarget);
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
                    Masked.UseElevator(goUp: false);
                    return;
                }
            }
        }

        // Check if there is real reachable scrap lying nearby
        if (PhoneyPlugin.EnableScrapLooting.Value && _scrapManager != null && _scrapManager.CanPickUpMoreScrap())
        {
            var scrap = _scrapManager.FindNearbyReachableScrap(20f);
            if (scrap != null)
            {
                _targetScrap = scrap;
                _subState = UndercoverSubState.ApproachingScrap;
                Masked!.SetDestinationToPosition(scrap.transform.position);
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
                        Masked.UseElevator(goUp: false);
                        return;
                    }
                }
            }
        }

        // Tethering to crewmate: don't wander off to the opposite end of the facility
        if (player != null)
        {
            float distToPlayer = Vector3.Distance(transform.position, player.transform.position);

            if (distToPlayer > 28f)
            {
                // Catch up towards player's general vicinity
                PickRoomNearPlayer(player);
                Masked!.SetDestinationToPosition(_currentRoomTarget);
                UpdateStaminaAndSpeed(_currentRoomTarget);
                return;
            }
            if (distToPlayer < 2.0f)
            {
                // Polite space
                Vector3 away = (transform.position - player.transform.position).normalized;
                Masked!.SetDestinationToPosition(transform.position + away * 2.5f);
                UpdateStaminaAndSpeed(transform.position + away * 2.5f);
                return;
            }
        }

        // Room navigation: check if arrived at the chosen room
        float distToRoom = Vector3.Distance(transform.position, _currentRoomTarget);
        if (distToRoom <= 2.2f)
        {
            _subState = UndercoverSubState.InspectingRoom;
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
            Masked!.SetDestinationToPosition(_currentRoomTarget);
            UpdateStaminaAndSpeed(_currentRoomTarget);
        }
    }

    // ── Sub-state 4: Inspecting Room ──────────────────────────────────────────

    private void UpdateInspectingRoom(PlayerControllerB? player)
    {
        // Human-like inspection: pause and look at points of interest instead of continuous oscillating rotation
        if (Time.time >= _inspectNextTurnTime)
        {
            _inspectNextTurnTime = Time.time + UnityEngine.Random.Range(1.3f, 2.5f);
            float turnAngle = UnityEngine.Random.Range(30f, 65f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            _inspectTargetRotation = transform.rotation * Quaternion.Euler(0f, turnAngle, 0f);
        }
        transform.rotation = Quaternion.RotateTowards(transform.rotation, _inspectTargetRotation, 75f * Time.deltaTime);

        // While inspecting shelves/corners, keep eyes open for scrap
        if (PhoneyPlugin.EnableScrapLooting.Value && _scrapManager != null && _scrapManager.CanPickUpMoreScrap())
        {
            var scrap = _scrapManager.FindNearbyReachableScrap(16f);
            if (scrap != null)
            {
                NavMeshUtil.SafeSetStopped(Masked!.agent, false);
                _targetScrap = scrap;
                _subState = UndercoverSubState.ApproachingScrap;
                Masked.SetDestinationToPosition(scrap.transform.position);
                return;
            }
        }

        if (Time.time >= _subStateTimer && !_performingCrouch)
        {
            NavMeshUtil.SafeSetStopped(Masked!.agent, false);
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked.SetDestinationToPosition(_currentRoomTarget);
        }
    }

    // ── Sub-state 5: Approaching Scrap ────────────────────────────────────────

    private void UpdateApproachingScrap(PlayerControllerB? player)
    {
        if (_isPickingUpScrap) return;

        if (_targetScrap == null || _targetScrap.isHeld || _targetScrap.isPocketed || _targetScrap.deactivated ||
            (_targetScrap.NetworkObject != null && MaskedScrapManager.GloballyProcessedScrapIds.Contains(_targetScrap.NetworkObject.NetworkObjectId)) ||
            MaskedScrapManager.IsNearEntranceOrShip(_targetScrap.transform.position))
        {
            NavMeshUtil.SafeSetStopped(Masked!.agent, false);
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked!.SetDestinationToPosition(_currentRoomTarget);
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
            Masked!.SetDestinationToPosition(_targetScrap.transform.position);
        }
    }

    // ── Sub-state 6: Hauling Scrap (Door Transition, Ship, or Disrupt Drop) ──

    private void UpdateHaulingScrapToEntrance(PlayerControllerB? player)
    {
        if (Masked == null) return;

        if (_scrapManager == null || !_scrapManager.HasHeldScrap)
        {
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked.SetDestinationToPosition(_currentRoomTarget);
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
                                _subState = UndercoverSubState.SearchingRooms;
                                PickRoomNearPlayer(player);
                                Masked.SetDestinationToPosition(_currentRoomTarget);
                                return;
                            }
                            else
                            {
                                Masked.SetDestinationToPosition(elevatorBottomPos);
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
                                    Masked.UseElevator(goUp: true);
                                    return;
                                }
                            }
                            else
                            {
                                Masked.UseElevator(goUp: true);
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

            if ((distToDoor <= 3.2f || (Masked.agent != null && Masked.agent.isOnNavMesh && !Masked.agent.pathPending && Masked.agent.remainingDistance <= 0.8f && distToDoor <= 5.0f)) && interiorDoor != null)
            {
                // Step through door into the outside world!
                TeleportThroughDoor(interiorDoor, toOutside: true);

                // Now outside: choose our delivery plan!
                float roll = UnityEngine.Random.value;
                if (roll < 0.40f)
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
                else if (roll < 0.80f)
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

                Masked.SetDestinationToPosition(_scrapDropTarget);
            }
            else
            {
                Masked.SetDestinationToPosition(doorPos);
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
            if (!_performingCrouch) StartFriendlyCrouch();

            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' successfully dropped scrap outside via {_deliveryPlan}!");

            if (_deliveryPlan == ScrapDeliveryPlan.DropAtOutsideDoor)
            {
                // 65% chance to immediately go back inside the facility to find more loot
                if (UnityEngine.Random.value < 0.65f)
                {
                    _subState = UndercoverSubState.ReturningToFacility;
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    if (outDoor != null) Masked.SetDestinationToPosition(MaskedScrapManager.GetDoorPosition(outDoor));
                    return;
                }
            }
            else
            {
                // After delivering to Ship or random drop: 50% chance to return inside to loot more
                if (UnityEngine.Random.value < 0.50f)
                {
                    _subState = UndercoverSubState.ReturningToFacility;
                    var outDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
                    if (outDoor != null) Masked.SetDestinationToPosition(MaskedScrapManager.GetDoorPosition(outDoor));
                    return;
                }
            }

            // Otherwise, remain outside to scout/loot/stalk
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked.SetDestinationToPosition(_currentRoomTarget);
        }
        else
        {
            Masked.SetDestinationToPosition(_scrapDropTarget);
        }
    }

    // ── Sub-state 7: Returning to Facility Through Door ───────────────────────

    private void UpdateReturningToFacility(PlayerControllerB? player)
    {
        if (Masked == null) return;

        var outsideDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: true);
        if (outsideDoor == null)
        {
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(player);
            Masked.SetDestinationToPosition(_currentRoomTarget);
            return;
        }

        Vector3 doorPos = MaskedScrapManager.GetDoorPosition(outsideDoor);
        UpdateStaminaAndSpeed(doorPos);

        float dist = Vector3.Distance(transform.position, doorPos);
        if (dist <= 2.6f)
        {
            TeleportThroughDoor(outsideDoor, toOutside: false);
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(TargetPlayer);
            Masked.SetDestinationToPosition(_currentRoomTarget);
            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' returned through door into facility! Searching rooms.");

            // In Mineshaft, if returning inside and we are on the upper entrance floor, ride elevator down into mine!
            if (IsMineshaftDungeon() && IsOnMineshaftUpperFloor())
            {
                Masked.UseElevator(goUp: false);
            }
        }
        else
        {
            Masked.SetDestinationToPosition(doorPos);
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
        if (_scrapManager != null && _scrapManager.HeldScrap != null)
        {
            _scrapManager.HeldScrap.isInFactory = !toOutside;
            _scrapManager.HeldScrap.isInElevator = false;
        }

        PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' stepped through door → toOutside={toOutside} at {exitPos} (isOnNavMesh={Masked.agent?.isOnNavMesh})");
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

        if (TargetPlayer == null || TargetPlayer.isPlayerDead || !Masked.PlayerIsTargetable(TargetPlayer))
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
                    Masked.UseElevator(targetOnUpper);
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
            if (isServer)
            {
                if (!Masked.handsOut) { Masked.handsOut = true; Masked.SetHandsOutServerRpc(true); }
                if (Masked.running)   { Masked.running  = false; Masked.SetRunningServerRpc(false); }
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
                }
            }
            else
            {
                // Drop to recovery jog when stamina runs out
                if (_ambushStamina <= 0.10f)
                {
                    _isAmbushBurst = false;
                    if (isServer) { Masked.running = false; Masked.SetRunningServerRpc(false); }
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
            if (isServer && !Masked.handsOut)
            {
                Masked.handsOut = true;
                Masked.SetHandsOutServerRpc(true);
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

        // If player manages to break away (> 28m) or chase exceeds 16s with dist > 18m:
        // Mimic tactically retreats and reverts back to deceptive undercover mode!
        if (dist > 28f || (elapsed > 16f && dist > 18f))
        {
            PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}': Player escaped pursuit ({dist:F1}m > 28m) — resetting to deceptive undercover state.");
            TransitionTo(PhoneyPlugin.AllowTacticalRetreat.Value
                ? MimicPhase.TacticalRetreat
                : MimicPhase.UndercoverLooting);
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
        FindFleeDestination();

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
                $"[DeceptiveAI] '{Masked.gameObject.name}' triggered INSTANT AMBUSH against '{(TargetPlayer != null ? TargetPlayer.playerUsername : "closest player")}'!");
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
            $"[DeceptiveAI] '{Masked.gameObject.name}' struck by {(playerWhoHit != null ? $"'{playerWhoHit.playerUsername}'" : "player")} — INSTANT AGGRESSION!");

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

        // Choose nodes that are in rooms 10m - 26m from the player
        var nearby = nodes
            .Where(n => {
                float d = Vector3.Distance(n.transform.position, center);
                return d >= 10f && d <= 26f;
            })
            .ToList();

        if (nearby.Count > 0)
        {
            _currentRoomTarget = nearby[UnityEngine.Random.Range(0, nearby.Count)].transform.position;
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
            if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
            // Respect interior vs exterior separation
            if (!Masked.PlayerIsTargetable(p)) continue;

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
            $"[DeceptiveAI] '{Masked.gameObject.name}' shifting disguise to player: '{chosenPlayer.playerUsername}' (SteamID {chosenPlayer.playerSteamId}, Dead={chosenPlayer.isPlayerDead}, Suit {chosenPlayer.currentSuitID}, Clips={ClipVault.Instance.GetClipCountForPlayer(chosenPlayer.playerSteamId)})!");

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
                    _subState = UndercoverSubState.ApproachingScrap;
                    Masked.SetDestinationToPosition(nextScrap.transform.position);
                    _isPickingUpScrap = false;
                    yield break;
                }
            }

            if (Masked!.isOutside)
            {
                // Already outside when picking up scrap!
                // 70% chance to haul back to ship, 30% chance to disrupt the crew with random drop
                if (UnityEngine.Random.value < 0.70f)
                {
                    _deliveryPlan = ScrapDeliveryPlan.HaulToShip;
                    _scrapDropTarget = StartOfRound.Instance?.shipDoorAudioSource != null
                        ? StartOfRound.Instance.shipDoorAudioSource.transform.position
                        : transform.position;
                }
                else
                {
                    _deliveryPlan = ScrapDeliveryPlan.DisruptRandomDrop;
                    _scrapDropTarget = PickRandomDisruptNode();
                }

                _subState = UndercoverSubState.HaulingScrapToEntrance;
                Masked.SetDestinationToPosition(_scrapDropTarget);
                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' picked up scrap outside! Hauling via {_deliveryPlan}.");
            }
            else
            {
                // Inside facility: head to interior exit door to take it outside!
                var interiorDoor = MaskedScrapManager.FindDoor(wantEntranceToBuilding: false);
                _scrapDropTarget = interiorDoor != null 
                    ? MaskedScrapManager.GetDoorPosition(interiorDoor) 
                    : _scrapManager!.GetEntranceDropPosition();

                _subState = UndercoverSubState.HaulingScrapToEntrance;
                Masked.SetDestinationToPosition(_scrapDropTarget);
                PhoneyPlugin.Logger.LogInfo($"[DeceptiveAI] '{Masked.gameObject.name}' picked up scrap inside! Hauling to exit door to take outside.");
            }
        }
        else
        {
            _subState = UndercoverSubState.SearchingRooms;
            PickRoomNearPlayer(TargetPlayer);
            Masked!.SetDestinationToPosition(_currentRoomTarget);
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
}
