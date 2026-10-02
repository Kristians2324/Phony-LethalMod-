using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using GameNetcodeStuff;
using Phoney.AI;
using Phoney.Audio;
using Phoney.Vault;
using Unity.Netcode;
using UnityEngine;

namespace Phoney.Debug;

/// <summary>
/// Persistent MonoBehaviour — provides in-game keyboard shortcuts and chat commands.
/// Controls: F6=Cycle, F7=Spawn, F8=Stats, F9=Inject, F10=Reveal
/// Chat: /cycle /spawn /stats /inject /reveal /help
/// </summary>
public class PhoneyTestMode : MonoBehaviour
{
    private static PhoneyTestMode? _instance;

    // ── Win32 raw hardware polling ─────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F6  = 0x75;
    private const int VK_F7  = 0x76;
    private const int VK_F8  = 0x77;
    private const int VK_F9  = 0x78;
    private const int VK_F10 = 0x79;

    private bool _f6Held;
    private bool _f7Held;
    private bool _f8Held;
    private bool _f9Held;
    private bool _f10Held;

    // Heartbeat timer
    private float _heartbeatTimer;
    private int   _updateCallCount;

    public static void Initialize()
    {
        if (_instance != null)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] Already initialized — skipping duplicate Initialize call.");
            return;
        }

        PhoneyPlugin.Logger.LogInfo("[TestMode] Creating persistent GameObject '[Phoney] TestMode'...");
        try
        {
            var go = new GameObject("[Phoney] TestMode");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<PhoneyTestMode>();
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** INITIALIZED SUCCESSFULLY ***");
            PhoneyPlugin.Logger.LogInfo("[TestMode] Win32 GetAsyncKeyState polling ACTIVE.");
            PhoneyPlugin.Logger.LogInfo("[TestMode] Keys: F6=Cycle, F7=Spawn, F8=Stats, F9=Inject, F10=Reveal");
            PhoneyPlugin.Logger.LogInfo("[TestMode] Chat: /cycle  /spawn  /stats  /inject  /reveal  /help");
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogError($"[TestMode] FAILED to initialize: {ex}");
        }
    }

    private void Awake()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] MonoBehaviour.Awake() called — component is alive.");
    }

    private void Start()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] MonoBehaviour.Start() called — entering Update loop.");
    }

    private static PlayerControllerB? GetLocalPlayer()
        => GameNetworkManager.Instance?.localPlayerController
        ?? StartOfRound.Instance?.localPlayerController;

    // ── Main polling loop ──────────────────────────────────────────────────────

    private void Update()
    {
        _updateCallCount++;

        // Heartbeat every 5 seconds so we know Update() is alive
        _heartbeatTimer -= Time.unscaledDeltaTime;
        if (_heartbeatTimer <= 0f)
        {
            _heartbeatTimer = 5f;
            PhoneyPlugin.Logger.LogInfo($"[TestMode] ♥ Update heartbeat #{_updateCallCount / 5} — key polling alive. Scene: '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");
        }

        // Read raw hardware key states directly from OS
        bool f6Now, f7Now, f8Now, f9Now, f10Now;
        try
        {
            f6Now  = (GetAsyncKeyState(VK_F6)  & 0x8000) != 0;
            f7Now  = (GetAsyncKeyState(VK_F7)  & 0x8000) != 0;
            f8Now  = (GetAsyncKeyState(VK_F8)  & 0x8000) != 0;
            f9Now  = (GetAsyncKeyState(VK_F9)  & 0x8000) != 0;
            f10Now = (GetAsyncKeyState(VK_F10) & 0x8000) != 0;
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogError($"[TestMode] GetAsyncKeyState THREW EXCEPTION: {ex.Message}");
            return;
        }

        // Dispatch on leading edge (key just pressed, not held)
        if (f6Now && !_f6Held)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** F6 DETECTED *** → Cycling nearest Masked phase.");
            CycleNearestMaskedPhase();
        }
        if (f7Now && !_f7Held)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** F7 DETECTED *** → Spawning test Masked.");
            SpawnTestMasked();
        }
        if (f8Now && !_f8Held)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** F8 DETECTED *** → Dumping stats.");
            DumpStats();
        }
        if (f9Now && !_f9Held)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** F9 DETECTED *** → Injecting voice clips.");
            InjectSyntheticClips();
        }
        if (f10Now && !_f10Held)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] *** F10 DETECTED *** → Triggering bloody reveal.");
            TriggerNearestBloodyReveal();
        }

        _f6Held  = f6Now;
        _f7Held  = f7Now;
        _f8Held  = f8Now;
        _f9Held  = f9Now;
        _f10Held = f10Now;
    }

    // ─── F6 / /cycle: Cycle AI Phase ─────────────────────────────────────────

    public static void CycleNearestMaskedPhase()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] CycleNearestMaskedPhase: scanning for alive Masked...");
        var localPlayer = GetLocalPlayer();

        var all = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>();
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   FindObjectsOfType<MaskedPlayerEnemy> returned {all.Length} instances.");

        var aliveMasked = all
            .Where(m => m != null && !m.isEnemyDead)
            .OrderBy(m => localPlayer != null ? Vector3.Distance(m.transform.position, localPlayer.transform.position) : 0f)
            .FirstOrDefault();

        if (aliveMasked == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   No alive Masked found!");
            SendFeedback("No alive Masked found. Spawn one first with F7 or /spawn", isWarning: true);
            return;
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Found alive Masked: '{aliveMasked.gameObject.name}'");

        var ai = aliveMasked.GetComponent<PhoneyDeceptiveAI>();
        if (ai == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   PhoneyDeceptiveAI component NOT FOUND on this Masked.");
            SendFeedback("Found Masked but PhoneyDeceptiveAI is not attached — was it spawned by Phoney?", isWarning: true);
            return;
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Current phase: {ai.CurrentPhase}");

        MimicPhase nextPhase = ai.CurrentPhase switch
        {
            MimicPhase.UndercoverLooting => MimicPhase.LuringFollower,
            MimicPhase.LuringFollower    => MimicPhase.AmbushStrike,
            MimicPhase.AmbushStrike      => MimicPhase.TacticalRetreat,
            MimicPhase.TacticalRetreat   => MimicPhase.UndercoverLooting,
            _                            => MimicPhase.UndercoverLooting
        };

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Transitioning {ai.CurrentPhase} → {nextPhase}");
        ai.TransitionTo(nextPhase);
        SendFeedback($"{aliveMasked.gameObject.name}: {ai.CurrentPhase}");
    }

    // ─── F7 / /spawn: Spawn Masked ───────────────────────────────────────────

    public static void SpawnTestMasked()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] SpawnTestMasked called.");

        var localPlayer = GetLocalPlayer();
        if (localPlayer == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   GetLocalPlayer() returned NULL.");
            SendFeedback("Local player not found — join or host a game first.", isWarning: true);
            return;
        }
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   localPlayer = '{localPlayer.playerUsername}'");

        var nm = NetworkManager.Singleton;
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   NetworkManager: IsHost={nm?.IsHost} IsServer={nm?.IsServer} IsClient={nm?.IsClient}");

        if (nm == null || (!nm.IsHost && !nm.IsServer))
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   Not host/server — cannot spawn.");
            SendFeedback("Only the lobby HOST can spawn enemies!", isWarning: true);
            return;
        }

        if (RoundManager.Instance == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   RoundManager.Instance is NULL.");
            SendFeedback("RoundManager not ready yet.", isWarning: true);
            return;
        }

        if (StartOfRound.Instance == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   StartOfRound.Instance is NULL.");
            SendFeedback("StartOfRound not ready.", isWarning: true);
            return;
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   inShipPhase = {StartOfRound.Instance.inShipPhase}");
        if (StartOfRound.Instance.inShipPhase)
        {
            SendFeedback("Still in orbit! Land on a moon first.", isWarning: true);
            return;
        }

        PhoneyPlugin.Logger.LogInfo("[TestMode]   Finding Masked EnemyType...");
        var maskedType = FindMaskedEnemyType();
        if (maskedType == null)
        {
            PhoneyPlugin.Logger.LogError("[TestMode]   Masked EnemyType NOT FOUND — cannot spawn.");
            SendFeedback("Masked EnemyType not found in game assets.", isWarning: true);
            return;
        }
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Found EnemyType: '{maskedType.enemyName}'");

        Vector3 desiredPos = localPlayer.transform.position + localPlayer.transform.forward * 3f;
        Vector3 spawnPos   = RoundManager.Instance.GetNavMeshPosition(desiredPos, default, 10f);
        bool    gotNavMesh = RoundManager.Instance.GotNavMeshPositionResult;
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   desiredPos={desiredPos} spawnPos={spawnPos} gotNavMesh={gotNavMesh}");

        if (!gotNavMesh)
        {
            spawnPos = desiredPos + Vector3.up * 0.1f;
            PhoneyPlugin.Logger.LogWarning("[TestMode]   NavMesh not found — using raw position.");
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Calling SpawnEnemyGameObject at {spawnPos}...");
        try
        {
            var netRef = RoundManager.Instance.SpawnEnemyGameObject(spawnPos, localPlayer.transform.eulerAngles.y + 180f, -1, maskedType);
            if (netRef.TryGet(out NetworkObject netObj))
            {
                PhoneyPlugin.Logger.LogInfo($"[TestMode]   NetworkObject obtained: '{netObj.gameObject.name}'");
                var masked = netObj.GetComponent<MaskedPlayerEnemy>();
                if (masked != null)
                {
                    masked.SetSuit(localPlayer.currentSuitID);
                    masked.mimickingPlayer = localPlayer;
                    masked.SetEnemyOutside(!localPlayer.isInsideFactory);
                    masked.SetVisibilityOfMaskedEnemy();
                    PhoneyPlugin.Logger.LogInfo($"[TestMode]   Masked configured → suit={localPlayer.currentSuitID} outside={!localPlayer.isInsideFactory}");
                }
                else
                {
                    PhoneyPlugin.Logger.LogWarning("[TestMode]   MaskedPlayerEnemy component not found on spawned object!");
                }
            }
            else
            {
                PhoneyPlugin.Logger.LogWarning("[TestMode]   netRef.TryGet returned false — spawn may have failed.");
            }
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogError($"[TestMode]   SpawnEnemyGameObject THREW EXCEPTION: {ex}");
            SendFeedback($"Spawn failed: {ex.Message}", isWarning: true);
            return;
        }

        SendFeedback("Spawned Masked imposter 3m ahead!");
    }

    // ─── F8 / /stats ─────────────────────────────────────────────────────────

    public static void DumpStats()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] DumpStats called.");

        int  clipCount   = ClipVault.Instance.TotalClipCount;
        bool whisperInit = SpeechTranscriber.Instance.IsInitialized;
        var  localPlayer = GetLocalPlayer();

        var activeMasked = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>()
            .Where(m => m != null && !m.isEnemyDead).ToArray();

        var nm = NetworkManager.Singleton;

        PhoneyPlugin.Logger.LogInfo("══════════════════════════════════════════");
        PhoneyPlugin.Logger.LogInfo("  PHONEY STATS DUMP");
        PhoneyPlugin.Logger.LogInfo("══════════════════════════════════════════");
        PhoneyPlugin.Logger.LogInfo($"  Local player    : {localPlayer?.playerUsername ?? "NULL"}");
        PhoneyPlugin.Logger.LogInfo($"  IsHost/Server   : {nm?.IsHost == true || nm?.IsServer == true}");
        PhoneyPlugin.Logger.LogInfo($"  IsClient        : {nm?.IsClient}");
        PhoneyPlugin.Logger.LogInfo($"  inShipPhase     : {StartOfRound.Instance?.inShipPhase}");
        PhoneyPlugin.Logger.LogInfo($"  Scene           : {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}");
        PhoneyPlugin.Logger.LogInfo($"  ClipVault clips : {clipCount}");
        PhoneyPlugin.Logger.LogInfo($"  Whisper ready   : {whisperInit}");
        PhoneyPlugin.Logger.LogInfo($"  Active Masked   : {activeMasked.Length}");
        PhoneyPlugin.Logger.LogInfo($"  TestModeEnabled : {PhoneyPlugin.TestModeEnabled.Value}");
        PhoneyPlugin.Logger.LogInfo($"  MaskedOnlySpawn : {PhoneyPlugin.MaskedOnlySpawns.Value}");
        PhoneyPlugin.Logger.LogInfo($"  MoonSpawnChance : {PhoneyPlugin.MoonSpawnChance.Value}%");
        PhoneyPlugin.Logger.LogInfo($"  MoonSpawnRarity : {PhoneyPlugin.MoonSpawnRarity.Value}");

        foreach (var m in activeMasked)
        {
            var ai     = m.GetComponent<PhoneyDeceptiveAI>();
            var emit   = m.GetComponent<PhoneyVoiceEmitter>();
            var bloody = m.GetComponent<PhoneyBloodyReveal>();
            float dist = localPlayer != null ? Vector3.Distance(m.transform.position, localPlayer.transform.position) : -1f;

            PhoneyPlugin.Logger.LogInfo($"  [{m.gameObject.name}]");
            PhoneyPlugin.Logger.LogInfo($"    Phase        = {(ai != null ? ai.CurrentPhase.ToString() : "NO PhoneyDeceptiveAI")}");
            PhoneyPlugin.Logger.LogInfo($"    Impersonates = '{(emit != null ? emit.ImpersonatedPlayerName : "NO PhoneyVoiceEmitter")}'");
            PhoneyPlugin.Logger.LogInfo($"    Revealed     = {(bloody != null ? bloody.IsRevealed.ToString() : "NO PhoneyBloodyReveal")}");
            PhoneyPlugin.Logger.LogInfo($"    Distance     = {dist:F1}m");
        }
        PhoneyPlugin.Logger.LogInfo("══════════════════════════════════════════");

        string hudMsg = $"Clips:{clipCount} Whisper:{(whisperInit ? "OK" : "Init")} Masked:{activeMasked.Length} Host:{nm?.IsHost == true}";
        SendFeedback(hudMsg);
    }

    // ─── F9 / /inject ────────────────────────────────────────────────────────

    public static void InjectSyntheticClips()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] InjectSyntheticClips called.");
        var localPlayer = GetLocalPlayer();

        var targets = StartOfRound.Instance?.allPlayerScripts?
            .Where(p => p != null && p.isPlayerControlled && p.playerSteamId != 0)
            .ToList();

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Controlled players found: {targets?.Count ?? 0}");

        if (targets == null || targets.Count == 0)
        {
            targets = new List<PlayerControllerB>();
            if (localPlayer != null)
            {
                targets.Add(localPlayer);
                PhoneyPlugin.Logger.LogInfo($"[TestMode]   Falling back to local player: '{localPlayer.playerUsername}'");
            }
        }

        var phrases = new (string Text, SemanticIntent Intent)[]
        {
            ("Hey where are you guys?",          SemanticIntent.Location),
            ("I'm over here at the entrance!",   SemanticIntent.Location),
            ("Yeah I found some scrap.",          SemanticIntent.LootScrap),
            ("Did you hear that? Hello?",         SemanticIntent.Greeting),
            ("What the hell was that noise?!",    SemanticIntent.WarningPanic),
            ("Holy shit there's something here!", SemanticIntent.WarningPanic),
        };

        int total = 0;
        foreach (var player in targets)
        {
            PhoneyPlugin.Logger.LogInfo($"[TestMode]   Injecting {phrases.Length} clips for '{player.playerUsername}' (steamId={player.playerSteamId})");
            foreach (var (text, intent) in phrases)
            {
                float[] samples = GenerateToneSamples(440f, 0.8f, 48000, 1);
                var clip = new RecordedClip(
                    player.playerSteamId, player.playerUsername, samples, 48000, 1,
                    Time.time - UnityEngine.Random.Range(5f, 30f));

                clip.Transcript        = text;
                clip.Intent            = intent;
                clip.ContainsProfanity = text.Contains("shit") || text.Contains("hell");
                clip.IsQuestion        = text.EndsWith("?");
                ClipVault.Instance.AddClip(clip);
                total++;
            }
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Total clips injected: {total}. Vault total: {ClipVault.Instance.TotalClipCount}");
        SendFeedback($"Injected {total} synthetic voice clips! Vault total: {ClipVault.Instance.TotalClipCount}");
    }

    // ─── F10 / /reveal ───────────────────────────────────────────────────────

    public static void TriggerNearestBloodyReveal()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] TriggerNearestBloodyReveal called.");
        var localPlayer = GetLocalPlayer();

        var all = UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>();
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Total MaskedPlayerEnemy in scene: {all.Length}");

        var aliveMasked = all
            .Where(m => m != null && !m.isEnemyDead)
            .OrderBy(m => localPlayer != null ? Vector3.Distance(m.transform.position, localPlayer.transform.position) : 0f)
            .FirstOrDefault();

        if (aliveMasked == null)
        {
            PhoneyPlugin.Logger.LogWarning("[TestMode]   No alive Masked found to reveal.");
            SendFeedback("No alive Masked found. Use F7 or /spawn first.", isWarning: true);
            return;
        }

        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Target: '{aliveMasked.gameObject.name}'");

        var ai = aliveMasked.GetComponent<PhoneyDeceptiveAI>();
        if (ai != null)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode] Triggering instant ambush via PhoneyDeceptiveAI...");
            ai.TriggerInstantAmbush(localPlayer);
            SendFeedback($"AMBUSH + DEMONIC ATTACK triggered on {aliveMasked.gameObject.name}!");
            return;
        }

        var bloody = aliveMasked.GetComponent<PhoneyBloodyReveal>();
        if (bloody == null)
        {
            PhoneyPlugin.Logger.LogInfo("[TestMode]   PhoneyBloodyReveal not found — adding component now.");
            bloody = aliveMasked.gameObject.AddComponent<PhoneyBloodyReveal>();
            bloody.Initialize(aliveMasked);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo($"[TestMode]   PhoneyBloodyReveal already attached. IsRevealed={bloody.IsRevealed}");
        }

        PhoneyPlugin.Logger.LogInfo("[TestMode]   Calling TriggerBloodyReveal()...");
        bloody.TriggerBloodyReveal();
        SendFeedback($"REVEAL triggered on {aliveMasked.gameObject.name}!");
    }

    // ─── /help ────────────────────────────────────────────────────────────────

    public static void PrintHelp()
    {
        SendFeedback("F6=Cycle  F7=Spawn  F8=Stats  F9=Inject  F10=Reveal");
        PhoneyPlugin.Logger.LogInfo("[TestMode] Commands: /cycle /spawn /stats /inject /reveal /help");
    }

    // ─── Feedback ────────────────────────────────────────────────────────────

    private static void SendFeedback(string message, bool isWarning = false)
    {
        try { HUDManager.Instance?.DisplayTip("Phoney", message, isWarning, false, "LC_Tip1"); } catch { }
        try { HUDManager.Instance?.AddChatMessage(message, "Phoney", -1, false); }               catch { }
        PhoneyPlugin.Logger.LogInfo($"[TestMode] FEEDBACK: {message}");
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private static float[] GenerateToneSamples(float frequency, float durationSecs, int sampleRate, int channels)
    {
        int count   = (int)(durationSecs * sampleRate * channels);
        float[] samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = 0.08f * Mathf.Sin(2f * Mathf.PI * frequency * i / (sampleRate * channels));
        return samples;
    }

    private static EnemyType? FindMaskedEnemyType()
    {
        PhoneyPlugin.Logger.LogInfo("[TestMode] FindMaskedEnemyType: searching Resources...");
        var allTypes = Resources.FindObjectsOfTypeAll<EnemyType>();
        PhoneyPlugin.Logger.LogInfo($"[TestMode]   Found {allTypes.Length} EnemyType assets in Resources.");

        var masked = allTypes.FirstOrDefault(t =>
            t != null && (
                (t.enemyPrefab != null && t.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null) ||
                (t.enemyName   != null && t.enemyName.IndexOf("masked", StringComparison.OrdinalIgnoreCase) >= 0)
            ));

        if (masked != null)
        {
            PhoneyPlugin.Logger.LogInfo($"[TestMode]   Found in Resources: '{masked.enemyName}'");
            return masked;
        }

        PhoneyPlugin.Logger.LogWarning("[TestMode]   Not in Resources — scanning moon tables...");
        if (StartOfRound.Instance?.levels == null)
        {
            PhoneyPlugin.Logger.LogError("[TestMode]   StartOfRound.Instance.levels is NULL.");
            return null;
        }

        foreach (var level in StartOfRound.Instance.levels)
        {
            if (level?.Enemies == null) continue;
            foreach (var entry in level.Enemies)
            {
                if (entry?.enemyType != null && (
                    (entry.enemyType.enemyPrefab != null && entry.enemyType.enemyPrefab.GetComponent<MaskedPlayerEnemy>() != null) ||
                    (entry.enemyType.enemyName   != null && entry.enemyType.enemyName.IndexOf("masked", StringComparison.OrdinalIgnoreCase) >= 0)
                ))
                {
                    PhoneyPlugin.Logger.LogInfo($"[TestMode]   Found in moon table '{level.PlanetName}': '{entry.enemyType.enemyName}'");
                    return entry.enemyType;
                }
            }
        }

        PhoneyPlugin.Logger.LogError("[TestMode]   Masked EnemyType NOT FOUND anywhere.");
        return null;
    }
}
