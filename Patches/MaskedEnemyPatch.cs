using System.Linq;
using GameNetcodeStuff;
using HarmonyLib;
using Phoney.AI;
using Phoney.Audio;
using Phoney.Compat;
using Phoney.Core;
using Phoney.Vault;
using Unity.Netcode;
using UnityEngine;

namespace Phoney.Patches;

[HarmonyPatch(typeof(MaskedPlayerEnemy))]
public class MaskedEnemyPatch
{
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix(MaskedPlayerEnemy __instance)
    {
        PhoneyPlugin.Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] MaskedPlayerEnemy.Start() FIRED → '{__instance?.gameObject?.name ?? "NULL"}'");

        if (__instance == null)
        {
            PhoneyPlugin.Logger.LogError("[MaskedPatch] __instance is NULL — aborting patch.");
            return;
        }

        if (!PhoneyPlugin.EnableMimicAI.Value)
        {
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch] EnableMimicAI = false — skipping all Phoney setup.");
            return;
        }

        // ── Enforce hard limit on active mimics ───────────────────────────────
        int livingMimics = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>()
            .Count(m => m != null && !m.isEnemyDead);
        if (livingMimics > PhoneyPlugin.MaxMimicCount.Value)
        {
            PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Mimic limit reached ({livingMimics}/{PhoneyPlugin.MaxMimicCount.Value}) — despawning excess mimic '{__instance.gameObject.name}'.");
            if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
            {
                __instance.KillEnemyServerRpc(false);
                __instance.gameObject.SetActive(false);
            }
            return;
        }

        // ── 1. Pick impersonation target ──────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo("[MaskedPatch] Step 1: Finding impersonation target...");
        PlayerControllerB? targetPlayer = __instance.mimickingPlayer;

        if (targetPlayer == null || ((!targetPlayer.isPlayerControlled && !targetPlayer.isPlayerDead) && targetPlayer != StartOfRound.Instance?.localPlayerController))
        {
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   mimickingPlayer is null/invalid — scanning players (living and dead)...");
            var candidates = StartOfRound.Instance?.allPlayerScripts?
                .Where(p => p != null && (p.isPlayerControlled || p.isPlayerDead || p == StartOfRound.Instance.localPlayerController) && p.playerSteamId != 0)
                .ToList();

            PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Eligible players found: {candidates?.Count ?? 0}");
            if (candidates != null && candidates.Count > 0)
            {
                // Prefer players who have recorded voice clips in ClipVault (living or dead!)
                var withClips = candidates.Where(p => ClipVault.Instance.GetClipCountForPlayer(p.playerSteamId) > 0).ToList();
                targetPlayer = withClips.Count > 0
                    ? withClips[UnityEngine.Random.Range(0, withClips.Count)]
                    : candidates[UnityEngine.Random.Range(0, candidates.Count)];
                PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Selected target: '{targetPlayer.playerUsername}' (steamId={targetPlayer.playerSteamId}, dead={targetPlayer.isPlayerDead}, clips={ClipVault.Instance.GetClipCountForPlayer(targetPlayer.playerSteamId)})");
            }
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   mimickingPlayer already set: '{targetPlayer.playerUsername}'");
        }

        if (targetPlayer == null)
        {
            PhoneyPlugin.Logger.LogWarning("[MaskedPatch]   No valid target player found — aborting setup.");
            return;
        }

        // ── 2. Enforce suit ────────────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo("[MaskedPatch] Step 2: Setting suit...");
        if (__instance.mimickingPlayer != targetPlayer)
        {
            __instance.mimickingPlayer = targetPlayer;
            __instance.SetSuit(targetPlayer.currentSuitID);
            PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Suit set to suitID={targetPlayer.currentSuitID}");
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   Suit already correct — no change.");
        }

        // ── 3. MoreCompany cosmetics ───────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 3: MoreCompany cosmetics (present={MoreCompanyCompat.IsPresent})...");
        MoreCompanyCompat.CopyCosmetics(targetPlayer, __instance);

        // ── 4. Hide mask ───────────────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 4: HideMask = {PhoneyPlugin.HideMask.Value}...");
        if (PhoneyPlugin.HideMask.Value)
        {
            HideMaskOnEnemy(__instance);
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   Mask hidden.");
        }

        // ── 5. PhoneyVoiceEmitter ──────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo("[MaskedPatch] Step 5: Attaching PhoneyVoiceEmitter...");
        var emitter = __instance.gameObject.GetComponent<PhoneyVoiceEmitter>()
                      ?? __instance.gameObject.AddComponent<PhoneyVoiceEmitter>();
        emitter.Initialize(__instance, targetPlayer);
        AudioCaptureManager.Instance.RegisterEmitter(emitter);
        PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   PhoneyVoiceEmitter attached and registered.");

        // ── 6. Held props & Scrap Manager ──────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 6: EnableHeldItems = {PhoneyPlugin.EnableHeldItems.Value}...");
        var (holder, rightHandBone) = MaskedHeldItemManager.TryEquipItems(__instance, targetPlayer);
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   holder={(holder != null ? "created" : "none")} rightHandBone={(rightHandBone != null ? rightHandBone.name : "none")}");

        var scrapManager = __instance.gameObject.GetComponent<MaskedScrapManager>()
                           ?? __instance.gameObject.AddComponent<MaskedScrapManager>();
        scrapManager.Initialize(__instance, rightHandBone);
        PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   MaskedScrapManager attached.");

        // ── 7. Bloody Reveal ─────────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 7: EnableBloodyReveal = {PhoneyPlugin.EnableBloodyReveal.Value}...");
        PhoneyBloodyReveal? bloody = null;
        if (PhoneyPlugin.EnableBloodyReveal.Value)
        {
            bloody = __instance.gameObject.GetComponent<PhoneyBloodyReveal>()
                     ?? __instance.gameObject.AddComponent<PhoneyBloodyReveal>();
            bloody.Initialize(__instance);
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   PhoneyBloodyReveal attached and initialized.");
        }

        // ── 8. PhoneyDeceptiveAI ───────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 8: DeceptiveAI = {PhoneyPlugin.EnableDeceptiveAI.Value}...");
        PhoneyDeceptiveAI? deceptiveAI = null;
        if (PhoneyPlugin.EnableDeceptiveAI.Value)
        {
            deceptiveAI = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>()
                          ?? __instance.gameObject.AddComponent<PhoneyDeceptiveAI>();
            deceptiveAI.Initialize(__instance, emitter);
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   PhoneyDeceptiveAI attached. Starting in UndercoverLooting.");
        }

        // ── 9. MaskedPropSwitcher ─────────────────────────────────────────────
        if (PhoneyPlugin.EnableHeldItems.Value && deceptiveAI != null)
        {
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch] Step 9: Attaching MaskedPropSwitcher...");
            var switcher = __instance.gameObject.GetComponent<MaskedPropSwitcher>()
                           ?? __instance.gameObject.AddComponent<MaskedPropSwitcher>();
            switcher.Initialize(__instance, deceptiveAI, holder?.HeldToolProp, rightHandBone);
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   MaskedPropSwitcher attached.");
        }


        // ── 10. VR deception ───────────────────────────────────────────────────
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] Step 10: VRFriendlyDeception = {PhoneyPlugin.VRFriendlyDeception.Value}...");
        if (PhoneyPlugin.VRFriendlyDeception.Value)
        {
            var vrBehaviour = __instance.gameObject.GetComponent<VRDeceptionBehaviour>()
                              ?? __instance.gameObject.AddComponent<VRDeceptionBehaviour>();
            vrBehaviour.Initialize(__instance);
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   VRDeceptionBehaviour attached.");
        }

        __instance.handsOut = false;
        if (__instance.creatureAnimator != null)
        {
            __instance.creatureAnimator.SetBool("HandsOut", false);
        }
        if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
        {
            __instance.SetHandsOutClientRpc(false);
        }

        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] SETUP COMPLETE for '{__instance.gameObject.name}' → impersonating '{targetPlayer.playerUsername}'");
        PhoneyPlugin.Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    [HarmonyPatch("Update")]
    [HarmonyPrefix]
    private static void UpdatePrefix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null) return;

        // State 0 is standard movement. Prevent vanilla State 1 (stopAndStareTimer)
        // from zeroing agent speed every frame, and prevent State 2 from causing
        // the mimic to abandon players and walk outside to hide in the ship next to the terminal.
        __instance.stopAndStareTimer = -999f;
        __instance.interestInShipCooldown = -9999f;
        __instance.currentBehaviourStateIndex = 0;
        __instance.previousBehaviourState = 0;

        if (ai.CurrentPhase != MimicPhase.AmbushStrike || ai.IsUsingDoor)
        {
            __instance.handsOut = false;
            if (__instance.stareAtTransform != null)
            {
                __instance.stareAtTransform = null;
            }
        }

        if (ai.CurrentPhase == MimicPhase.AmbushStrike)
        {
            var reveal = __instance.gameObject.GetComponent<PhoneyBloodyReveal>();
            if (reveal != null && reveal.IsTransforming)
            {
                NavMeshUtil.SafeSetStopped(__instance.agent, true);
                NavMeshUtil.SafeSetVelocity(__instance.agent, Vector3.zero);
                return;
            }
        }
    }

    [HarmonyPatch("Update")]
    [HarmonyPostfix]
    private static void UpdatePostfix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai != null)
        {
            ai.EnforceHumanSpeedAndMovement();

            if (ai.IsUsingDoor && __instance.stareAtTransform != null)
            {
                __instance.stareAtTransform = null;
            }

            // Clear vanilla crouching if the mimic is not performing a deceptive crouch greeting or scrap pickup.
            // Vanilla State 2 sets crouching = true when near insideShipPositions, freezing the mimic next to the terminal.
            // Undercover mimics must NEVER crouch inside or near the ship.
            bool nearShip = MaskedScrapManager.IsNearShip(__instance.transform.position, 8.0f)
                || (StartOfRound.Instance?.shipBounds != null && StartOfRound.Instance.shipBounds.bounds.Contains(__instance.transform.position));
            if ((!ai.IsPerformingFriendlyCrouch || nearShip) && __instance.crouching)
            {
                __instance.crouching = false;
                if (NetworkManager.Singleton?.IsServer == true || NetworkManager.Singleton?.IsHost == true)
                {
                    __instance.SetCrouchingServerRpc(false);
                }
            }

            // Synchronize authentic player holding animation layers & eliminate zombie arms
            var holder = __instance.GetComponent<MaskedHeldItemHolder>();
            var scrapManager = __instance.GetComponent<MaskedScrapManager>();
            bool hasRealScrap = scrapManager != null && scrapManager.HasHeldScrap;
            bool isAggressive = ai.CurrentPhase == MimicPhase.AmbushStrike;
            if (holder != null)
            {
                holder.UpdateAnimationLayers(hasRealScrap, isAggressive);
            }
            else if (!isAggressive)
            {
                __instance.handsOut = false;
                if (__instance.creatureAnimator != null)
                {
                    __instance.creatureAnimator.SetBool("HandsOut", false);
                }
            }
        }
    }

    // ─── LookAtFocusedPosition ────────────────────────────────────────────────

    [HarmonyPatch("LookAtFocusedPosition")]
    [HarmonyPrefix]
    private static bool LookAtFocusedPositionPrefix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null) return true;

        // 1. Strict Door Gaze Lock: While using a door, keep body and head gaze locked on the door trigger!
        // Never allow vanilla head-tracking or stareAtTransform to turn the mimic toward nearby players.
        if (ai.IsUsingDoor && ai.DoorInteractionTarget != Vector3.zero)
        {
            __instance.stareAtTransform = null;
            if (__instance.agent != null)
            {
                __instance.agent.angularSpeed = 0f;
            }

            Vector3 target = ai.DoorInteractionTarget;
            Vector3 bodyDir = (target - __instance.transform.position).normalized;
            bodyDir.y = 0;
            if (bodyDir != Vector3.zero)
            {
                __instance.transform.rotation = Quaternion.LookRotation(bodyDir);
                __instance.transform.eulerAngles = new Vector3(0f, __instance.transform.eulerAngles.y, 0f);
            }

            if (__instance.headTiltTarget != null)
            {
                __instance.headTiltTarget.LookAt(target);
                __instance.headTiltTarget.localEulerAngles = new Vector3(__instance.headTiltTarget.localEulerAngles.x, 0f, 0f);
            }
            return false;
        }

        // 2. Clear vanilla's persistent stareAtTransform in non-aggressive phases so mimic doesn't zombie-stare
        if (ai.CurrentPhase != MimicPhase.AmbushStrike && __instance.stareAtTransform != null)
        {
            __instance.stareAtTransform = null;
        }

        return true;
    }

    // ─── LookAtPlayerClientRpc ────────────────────────────────────────────────

    [HarmonyPatch("LookAtPlayerClientRpc")]
    [HarmonyPrefix]
    private static bool LookAtPlayerClientRpcPrefix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null) return true;

        // Suppress client RPC from forcing stareAtTransform when undercover or using doors
        if (ai.IsUsingDoor || ai.CurrentPhase != MimicPhase.AmbushStrike)
        {
            return false;
        }
        return true;
    }

    // ─── SetHandsOut RPC Suppression ──────────────────────────────────────────

    [HarmonyPatch("SetHandsOutClientRpc")]
    [HarmonyPrefix]
    private static bool SetHandsOutClientRpcPrefix(MaskedPlayerEnemy __instance, ref bool setOut)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai != null && ai.CurrentPhase != MimicPhase.AmbushStrike)
        {
            setOut = false;
            __instance.handsOut = false;
            if (__instance.creatureAnimator != null)
            {
                __instance.creatureAnimator.SetBool("HandsOut", false);
            }
            return false;
        }
        return true;
    }

    [HarmonyPatch("SetHandsOutServerRpc")]
    [HarmonyPrefix]
    private static bool SetHandsOutServerRpcPrefix(MaskedPlayerEnemy __instance, ref bool setOut)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;
        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai != null && ai.CurrentPhase != MimicPhase.AmbushStrike)
        {
            setOut = false;
            __instance.handsOut = false;
            return false;
        }
        return true;
    }

    // ─── ChooseShipHidingSpot ─────────────────────────────────────────────────

    [HarmonyPatch("ChooseShipHidingSpot")]
    [HarmonyPrefix]
    private static bool ChooseShipHidingSpotPrefix(MaskedPlayerEnemy __instance)
    {
        // When DeceptiveAI is enabled, completely block vanilla ship hiding behavior.
        // Vanilla ChooseShipHidingSpot linecasts from ship door and sets destination to insideShipPositions
        // right behind the terminal, freezing the mimic in a permanent crouch there.
        if (__instance != null && PhoneyPlugin.EnableDeceptiveAI.Value)
        {
            return false;
        }
        return true;
    }

    // ─── DoAIInterval ─────────────────────────────────────────────────────────

    [HarmonyPatch("DoAIInterval")]
    [HarmonyPrefix]
    private static bool DoAIIntervalPrefix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;

        if (__instance.elevatorScript == null)
        {
            __instance.elevatorScript = RoundManager.Instance != null && RoundManager.Instance.currentMineshaftElevator != null
                ? RoundManager.Instance.currentMineshaftElevator
                : UnityEngine.Object.FindObjectOfType<MineshaftElevatorController>();
        }

        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null)
        {
            // Only log this once so it doesn't spam
            return true;
        }
        if (ai.CustomDoAIInterval()) return false;
        return true;
    }

    // ─── OnCollideWithPlayer ──────────────────────────────────────────────────

    [HarmonyPatch("OnCollideWithPlayer")]
    [HarmonyPrefix]
    private static bool OnCollideWithPlayerPrefix(MaskedPlayerEnemy __instance, Collider other)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return true;

        var ai = __instance.gameObject.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null)
        {
            PhoneyPlugin.Logger.LogWarning($"[MaskedPatch] OnCollideWithPlayer: No PhoneyDeceptiveAI on '{__instance.gameObject.name}' — vanilla kill allowed.");
            return true;
        }

        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] OnCollideWithPlayer: '{__instance.gameObject.name}' in phase {ai.CurrentPhase}");

        if (ai.CurrentPhase == MimicPhase.UndercoverLooting ||
            ai.CurrentPhase == MimicPhase.LuringFollower)
        {
            var player = other.GetComponent<PlayerControllerB>()
                         ?? other.GetComponentInParent<PlayerControllerB>();
            if (player != null && !player.isPlayerDead)
            {
                PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Phase {ai.CurrentPhase} — suppressing kill. Bumped by '{player.playerUsername}'.");
                ai.OnPlayerBumpedInto(player);
            }
            return false; // No kill
        }

        if (ai.CurrentPhase == MimicPhase.TacticalRetreat)
        {
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   Phase TacticalRetreat — suppressing kill (fleeing).");
            return false;
        }

        if (ai.CurrentPhase == MimicPhase.AmbushStrike)
        {
            var reveal = __instance.gameObject.GetComponent<PhoneyBloodyReveal>();
            if (reveal != null)
            {
                if (!reveal.IsRevealed)
                    reveal.TriggerBloodyReveal();

                // If currently undergoing the 1.1s transformation convulsion, suppress instant kill
                // so the player gets a jump-scare reaction window!
                if (reveal.IsTransforming)
                {
                    return false;
                }
            }

            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   Phase AmbushStrike — ALLOWING vanilla kill.");
            return true;
        }

        return true;
    }

    // ─── HitEnemy / KillEnemy ────────────────────────────────────────────────

    [HarmonyPatch("HitEnemy")]
    [HarmonyPrefix]
    private static void HitEnemyPrefix(MaskedPlayerEnemy __instance, PlayerControllerB? playerWhoHit = null)
    {
        if (__instance == null || !PhoneyPlugin.EnableDeceptiveAI.Value) return;
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] HitEnemy on '{__instance.gameObject.name}' by {(playerWhoHit != null ? playerWhoHit.playerUsername : "unknown")} — triggering OnDamagedByPlayer.");
        __instance.gameObject.GetComponent<PhoneyDeceptiveAI>()?.OnDamagedByPlayer(playerWhoHit);
    }

    [HarmonyPatch("KillEnemy")]
    [HarmonyPrefix]
    private static void KillEnemyPrefix(MaskedPlayerEnemy __instance)
    {
        if (__instance == null) return;
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch] KillEnemy on '{__instance.gameObject.name}' — unregistering emitter, destroying visual props, and dropping scrap.");
        __instance.GetComponent<MaskedHeldItemHolder>()?.DestroyAllProps();
        __instance.GetComponent<MaskedScrapManager>()?.DropHeldScrapImmediately();
        var emitter = __instance.gameObject.GetComponent<PhoneyVoiceEmitter>();
        if (emitter != null)
        {
            emitter.StopSpeaking();
            emitter.SetDemonicMode(false, syncToNetwork: false);
            AudioCaptureManager.Instance.UnregisterEmitter(emitter);
        }
    }


    // ─── Mask hiding ─────────────────────────────────────────────────────────

    private static void HideMaskOnEnemy(MaskedPlayerEnemy masked)
    {
        int hidden = 0;
        if (masked.maskTypes != null && masked.maskTypes.Length > 0)
        {
            foreach (var m in masked.maskTypes)
                if (m != null) { m.SetActive(false); hidden++; }
        }
        PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Hidden {hidden} mask GameObjects.");

        if (masked.maskEyesGlowLight != null)
        {
            masked.maskEyesGlowLight.enabled = false;
            PhoneyPlugin.Logger.LogInfo("[MaskedPatch]   maskEyesGlowLight disabled.");
        }
        else
        {
            PhoneyPlugin.Logger.LogWarning("[MaskedPatch]   maskEyesGlowLight is NULL — cannot disable.");
        }

        if (masked.maskEyesGlow != null)
        {
            int eyeCount = 0;
            foreach (var r in masked.maskEyesGlow)
                if (r != null) { r.enabled = false; eyeCount++; }
            PhoneyPlugin.Logger.LogInfo($"[MaskedPatch]   Disabled {eyeCount} maskEyesGlow renderers.");
        }
    }
}
