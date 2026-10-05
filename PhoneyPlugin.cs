using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Phoney.AI;
using Phoney.Core;
using Phoney.Debug;
using Phoney.Patches;

namespace Phoney;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class PhoneyPlugin : BaseUnityPlugin
{
    public const string PluginGuid    = "com.user.phoney";
    public const string PluginName    = "Phoney";
    public const string PluginVersion = "1.0.0";

    public static PhoneyPlugin Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger { get; private set; } = null!;
    internal static Harmony? Harmony { get; set; }

    // ── General ──────────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  EnableMimicAI          { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableUnfilteredBanter { get; private set; } = null!;
    public static ConfigEntry<float> Aggressiveness         { get; private set; } = null!;
    public static ConfigEntry<float> ResponseDelaySeconds   { get; private set; } = null!;
    public static ConfigEntry<bool>  MaskedOnly             { get; private set; } = null!;

    // ── Cosmetic ─────────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  HideMask                  { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableHeldItems            { get; private set; } = null!;
    public static ConfigEntry<float> HeldItemFlashlightChance   { get; private set; } = null!;
    public static ConfigEntry<float> HeldItemWalkieChance       { get; private set; } = null!;
    public static ConfigEntry<float> HeldItemWalkieScale        { get; private set; } = null!;
    public static ConfigEntry<float> HeldItemFlashlightScale    { get; private set; } = null!;
    public static ConfigEntry<float> HeldItemScrapScale         { get; private set; } = null!;

    // ── VR deception ─────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  VRFriendlyDeception        { get; private set; } = null!;

    // ── Bloody Reveal ────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  EnableBloodyReveal         { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableRevealSpasm          { get; private set; } = null!;
    public static ConfigEntry<float> BloodIntensity             { get; private set; } = null!;

    // ── Deceptive AI ─────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  EnableDeceptiveAI       { get; private set; } = null!;
    public static ConfigEntry<float> AmbushDistanceThreshold { get; private set; } = null!;
    public static ConfigEntry<bool>  AllowTacticalRetreat    { get; private set; } = null!;
    public static ConfigEntry<float> LostLineOfSightTimeout  { get; private set; } = null!;
    public static ConfigEntry<float> MaxChaseRange           { get; private set; } = null!;
    public static ConfigEntry<float> ParanoiaIntervalSeconds { get; private set; } = null!;
    public static ConfigEntry<float> InitialHostilityChance  { get; private set; } = null!;
    public static ConfigEntry<float> HostilityChanceIncrement{ get; private set; } = null!;
    public static ConfigEntry<bool>  EnableScrapLooting          { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableMimicTouchReduction   { get; private set; } = null!;
    public static ConfigEntry<int>   MimicTouchValueReduction    { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableVoiceAccusationAggression { get; private set; } = null!;
    public static ConfigEntry<bool>  EnableDemonicAttackVoice       { get; private set; } = null!;
    public static ConfigEntry<float> DemonicVoicePitch              { get; private set; } = null!;
    public static ConfigEntry<float> DemonicDistortionLevel         { get; private set; } = null!;
    public static ConfigEntry<float> DemonicTauntInterval           { get; private set; } = null!;
    public static ConfigEntry<float> AmbushSprintSpeed              { get; private set; } = null!;
    public static ConfigEntry<float> AmbushJogSpeed                 { get; private set; } = null!;
    public static ConfigEntry<int>   MaxCarriedScrapCount           { get; private set; } = null!;
    public static ConfigEntry<float> HaulToShipChance               { get; private set; } = null!;
    public static ConfigEntry<float> HaulLootOutsideChance           { get; private set; } = null!;

    // ── Spawning ──────────────────────────────────────────────────────────────
    public static ConfigEntry<bool>  EnableCrossMoonSpawning { get; private set; } = null!;
    public static ConfigEntry<float> MoonSpawnChance         { get; private set; } = null!;
    public static ConfigEntry<int>   MoonSpawnRarity         { get; private set; } = null!;
    public static ConfigEntry<bool>  MaskedOnlySpawns        { get; private set; } = null!;
    public static ConfigEntry<int>   MaxMimicCount           { get; private set; } = null!;

    // ── Testing / Debug ───────────────────────────────────────────────────────
    public static ConfigEntry<bool>  TestModeEnabled         { get; private set; } = null!;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private void Awake()
    {
        Logger   = base.Logger;
        Instance = this;

        Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Logger.LogInfo($"  PHONEY v{PluginVersion} — STARTING UP");
        Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        Logger.LogInfo("[STARTUP] Step 1/6 — Binding config...");
        try
        {
            BindConfig();
            Logger.LogInfo("[STARTUP] Config bound OK.");
            Logger.LogInfo($"[STARTUP]   EnableMimicAI        = {EnableMimicAI.Value}");
            Logger.LogInfo($"[STARTUP]   EnableDeceptiveAI    = {EnableDeceptiveAI.Value}");
            Logger.LogInfo($"[STARTUP]   EnableBloodyReveal   = {EnableBloodyReveal.Value}");
            Logger.LogInfo($"[STARTUP]   EnableRevealSpasm    = {EnableRevealSpasm.Value}");
            Logger.LogInfo($"[STARTUP]   EnableHeldItems      = {EnableHeldItems.Value}");
            Logger.LogInfo($"[STARTUP]   HaulLootOutsideChance= {HaulLootOutsideChance.Value:P0}");
            Logger.LogInfo($"[STARTUP]   HideMask             = {HideMask.Value}");
            Logger.LogInfo($"[STARTUP]   EnableCrossMoonSpawn = {EnableCrossMoonSpawning.Value}");
            Logger.LogInfo($"[STARTUP]   MaskedOnlySpawns     = {MaskedOnlySpawns.Value}");
            Logger.LogInfo($"[STARTUP]   MoonSpawnChance      = {MoonSpawnChance.Value}%");
            Logger.LogInfo($"[STARTUP]   MoonSpawnRarity      = {MoonSpawnRarity.Value}");
            Logger.LogInfo($"[STARTUP]   TestModeEnabled      = {TestModeEnabled.Value}");
            Logger.LogInfo($"[STARTUP]   AllowTacticalRetreat = {AllowTacticalRetreat.Value}");
            Logger.LogInfo($"[STARTUP]   EnableDemonicVoice   = {EnableDemonicAttackVoice.Value} (Pitch={DemonicVoicePitch.Value:F2}, Distort={DemonicDistortionLevel.Value:F2})");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED to bind config: {ex}");
        }

        Logger.LogInfo("[STARTUP] Step 2/6 — Setting up native DLL path...");
        try
        {
            SetupNativeDllPath();
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED native DLL setup: {ex}");
        }

        Logger.LogInfo("[STARTUP] Step 3/6 — Initializing MainThreadDispatcher...");
        try
        {
            MainThreadDispatcher.Initialize();
            Logger.LogInfo("[STARTUP] MainThreadDispatcher OK.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED MainThreadDispatcher: {ex}");
        }

        Logger.LogInfo("[STARTUP] Step 4/6 — Initializing PhoneyTestMode (keyboard + chat commands)...");
        try
        {
            PhoneyTestMode.Initialize();
            Logger.LogInfo("[STARTUP] PhoneyTestMode OK.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED PhoneyTestMode.Initialize: {ex}");
        }

        Logger.LogInfo("[STARTUP] Step 5/6 — Starting Whisper speech recognizer...");
        try
        {
            SpeechTranscriber.Instance.InitializeAsync();
            Logger.LogInfo("[STARTUP] Whisper init started in background.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED Whisper init: {ex}");
        }

        Logger.LogInfo("[STARTUP] Step 6/6 — Applying Harmony patches...");
        try
        {
            Patch();
            Logger.LogInfo("[STARTUP] Harmony patches applied.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[STARTUP] FAILED to apply patches: {ex}");
        }

        Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Logger.LogInfo($"  PHONEY v{PluginVersion} READY — The mimics are listening.");
        Logger.LogInfo("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
    }

    private void BindConfig()
    {
        EnableMimicAI = Config.Bind(
            "General", "EnableMimicAI", true,
            "Enable AI-driven conversational mimicry on Masked enemies.");

        EnableUnfilteredBanter = Config.Bind(
            "General", "EnableUnfilteredBanter", true,
            "Preserve raw, unfiltered speech — profanity, screams, and dark banter all intact.");

        Aggressiveness = Config.Bind(
            "Behavior", "Aggressiveness", 0.7f,
            "Frequency multiplier for proactive wandering banter (0.1 = almost silent, 1.0 = chatty).");

        ResponseDelaySeconds = Config.Bind(
            "Behavior", "ResponseDelaySeconds", 0.7f,
            "Human-like hesitation delay (seconds) before the mimic answers your voice.");

        MaskedOnly = Config.Bind(
            "Behavior", "MaskedOnly", true,
            "Restrict conversational mimicry to Masked enemies only.");

        HideMask = Config.Bind(
            "Cosmetic", "HideMask", true,
            "Hide the white theatrical mask from Masked enemies so they look like real players. " +
            "The mask-eye glow is re-enabled during the Phase 3 ambush for dramatic effect.");

        EnableHeldItems = Config.Bind(
            "Cosmetic", "EnableHeldItems", true,
            "Give Masked enemies a visual flashlight or walkie-talkie prop so they look like they're carrying gear.\n" +
            "Items are purely visual — they will not drop on death and are not tracked by the game.\n" +
            "The flashlight will actually cast real light making the Masked even more convincing.");

        HeldItemFlashlightChance = Config.Bind(
            "Cosmetic", "HeldItemFlashlightChance", 40f,
            "Percentage chance (0–100) a Masked enemy spawns holding a flashlight.");

        HeldItemWalkieChance = Config.Bind(
            "Cosmetic", "HeldItemWalkieChance", 30f,
            "Percentage chance (0–100) a Masked enemy spawns with a walkie-talkie clipped to its chest.\n" +
            "If the mimicked player was holding a walkie, this chance is overridden to 80%%.");

        HeldItemWalkieScale = Config.Bind(
            "Cosmetic", "HeldItemWalkieScale", 0.038f,
            "Local scale multiplier for the walkie-talkie prop on the Masked chest (default: 0.038).");

        HeldItemFlashlightScale = Config.Bind(
            "Cosmetic", "HeldItemFlashlightScale", 0.050f,
            "Local scale multiplier for the flashlight prop in the Masked right hand (default: 0.050).");

        HeldItemScrapScale = Config.Bind(
            "Cosmetic", "HeldItemScrapScale", 0.065f,
            "Local scale multiplier for scrap loot carried by the Masked (default: 0.065).");

        VRFriendlyDeception = Config.Bind(
            "Cosmetic", "VRFriendlyDeception", true,
            "Adds subtle head micro-movements (VR-style head tracking glances, natural micro-bob) to Masked enemies.\n" +
            "In VR, real players move their head constantly in small ways. Vanilla Masked stand stock-still.\n" +
            "Works with or without LCVR installed — LCVR is only used to detect whether the session is in VR.");

        EnableBloodyReveal = Config.Bind(
            "Cosmetic", "EnableBloodyReveal", true,
            "When the mimic reveals its true killer self in Phase 3 (Ambush Strike), its suit and face\n" +
            "become violently drenched in dark blood, emitting blood flood particles and bone-snap gore audio.");

        EnableRevealSpasm = Config.Bind(
            "Cosmetic", "EnableRevealSpasm", true,
            "Play an unnatural demonic neck-snap and violent bodily spasm animation upon revealing.");

        BloodIntensity = Config.Bind(
            "Cosmetic", "BloodIntensity", 1.0f,
            "Multiplier for the darkness and intensity of the blood-drenched appearance (0.1 = faint stains, 1.0 = soaked in blood).");

        EnableDeceptiveAI = Config.Bind(
            "DeceptiveAI", "EnableDeceptiveAI", true,
            "Overhaul Masked AI into a realistic deceptive crewmate that loots rooms, hauls scrap to entrance, and escalates paranoia.");

        AmbushDistanceThreshold = Config.Bind(
            "DeceptiveAI", "AmbushDistanceThreshold", 16.0f,
            "Distance in metres for the mimic's active aggro range detection to trigger an ambush attack (default: 16.0m).");

        AllowTacticalRetreat = Config.Bind(
            "DeceptiveAI", "AllowTacticalRetreat", true,
            "Allow the mimic to flee into darkness and reset its disguise if a player escapes far away.");

        LostLineOfSightTimeout = Config.Bind(
            "DeceptiveAI", "LostLineOfSightTimeout", 18.0f,
            "Duration in seconds of broken line of sight before an aggroed mimic gives up pursuit, runs back to the facility, and returns to normal (default: 18.0s).");

        MaxChaseRange = Config.Bind(
            "DeceptiveAI", "MaxChaseRange", 85.0f,
            "Maximum distance in metres before a mimic gives up chasing an escaped player when line of sight is broken (default: 85.0m inside, 140.0m outside).");

        ParanoiaIntervalSeconds = Config.Bind(
            "DeceptiveAI", "ParanoiaIntervalSeconds", 120.0f,
            "Interval in seconds (default: 120s / 2 minutes) for each paranoia hostility check.");

        InitialHostilityChance = Config.Bind(
            "DeceptiveAI", "InitialHostilityChance", 0.40f,
            "Base percentage chance (0.0 - 1.0) on the first 2-minute mark for the mimic to become hostile (default: 0.40 = 40%).");

        HostilityChanceIncrement = Config.Bind(
            "DeceptiveAI", "HostilityChanceIncrement", 0.25f,
            "How much the hostility probability increases every 2 minutes if the mimic rolls peaceful (default: 0.25 = +25%).");

        EnableScrapLooting = Config.Bind(
            "DeceptiveAI", "EnableScrapLooting", true,
            "Allow mimics to scan for real scrap items in facility rooms, pick them up, and haul them to the main entrance like a crewmate.");

        EnableMimicTouchReduction = Config.Bind(
            "DeceptiveAI", "EnableMimicTouchReduction", true,
            "Reduce the value of loot touched and carried by the mimic, as a subtle hint that it was handled by an impostor.");

        MimicTouchValueReduction = Config.Bind(
            "DeceptiveAI", "MimicTouchValueReduction", 20,
            "Flat reduction in scrap value applied when a mimic touches scrap (default: 20 value loss).");

        EnableVoiceAccusationAggression = Config.Bind(
            "DeceptiveAI", "EnableVoiceAccusationAggression", true,
            "Immediately triggers aggressive ambush if a nearby player exposes or accuses the mimic in voice chat (e.g. 'that guy is a mimic', 'you're a mimic', 'are you a mimic?').");

        EnableDemonicAttackVoice = Config.Bind(
            "Voice", "EnableDemonicAttackVoice", true,
            "Distort the mimic's voice (pitch down, overdrive distortion, chorus modulation, and cavernous echo)\n" +
            "when attacking in Ambush Strike mode, making the impersonated voice sound demonic.\n" +
            "Restores natural voice when calm and undercover.");

        DemonicVoicePitch = Config.Bind(
            "Voice", "DemonicVoicePitch", 0.72f,
            "Pitch multiplier applied to the impersonated player's voice during attack mode (default: 0.72). Lower = deeper/demonic.");

        DemonicDistortionLevel = Config.Bind(
            "Voice", "DemonicDistortionLevel", 0.55f,
            "Distortion filter intensity applied to the voice during attack mode (0.1 = subtle grit, 0.55 = heavy demonic overdrive, 1.0 = maximum).");

        DemonicTauntInterval = Config.Bind(
            "Voice", "DemonicTauntInterval", 22.0f,
            "Average cooldown in seconds between pursuit demonic vocalizations during an ambush chase (default: 22.0s).");

        AmbushSprintSpeed = Config.Bind(
            "DeceptiveAI", "AmbushSprintSpeed", 6.8f,
            "Sprint speed of the mimic during ambush pursuit (player sprint: ~10.3 m/s, default: 6.8f). Allows sprinting players to outrun the mimic, but outpaces walking players.");

        AmbushJogSpeed = Config.Bind(
            "DeceptiveAI", "AmbushJogSpeed", 3.8f,
            "Reaction-window jog speed during ambush (player walk: ~4.6 m/s, default: 3.8f). Gives players a window to gain distance and recover stamina.");

        MaxCarriedScrapCount = Config.Bind(
            "DeceptiveAI", "MaxCarriedScrapCount", 4,
            "Maximum number of scrap items the mimic can carry simultaneously (default: 4, matching player inventory slots).");

        HaulToShipChance = Config.Bind(
            "DeceptiveAI", "HaulToShipChance", 0.15f,
            "Chance (0.0 to 1.0) that a mimic hauling loot outside takes it all the way to the Ship rather than dropping near the entrance door (default: 0.15).");

        HaulLootOutsideChance = Config.Bind(
            "DeceptiveAI", "HaulLootOutsideChance", 0.15f,
            "Chance (0.0 to 1.0) that a mimic carrying loot inside the facility decides to take it outside rather than staying inside near the player (default: 0.15 = 15%).");

        EnableCrossMoonSpawning = Config.Bind(
            "Spawning", "EnableCrossMoonSpawning", true,
            "Inject Masked enemies onto moons where they don't normally spawn (Assurance, March, Offense…).");

        MoonSpawnChance = Config.Bind(
            "Spawning", "MoonSpawnChance", 100f,
            "Percentage chance (0–100) that Masked enemies are injected onto any given moon. 100 = always.");

        MoonSpawnRarity = Config.Bind(
            "Spawning", "MoonSpawnRarity", 300,
            "Rarity weight for Masked enemies in the spawn table.\n" +
            "This is ALSO used to boost Masked on moons where they already spawn natively (e.g. Titan).\n" +
            "Vanilla Titan has Masked at ~5. Setting this to 300 basically guarantees one every round.\n" +
            "Range: 1–300.");

        MaskedOnlySpawns = Config.Bind(
            "Spawning", "MaskedOnlySpawns", true,
            "When true, ALL non-Masked enemies are removed from every moon's spawn table.\n" +
            "Only Masked (mimic) enemies will spawn. Use this while testing Phoney.");

        MaxMimicCount = Config.Bind(
            "Spawning", "MaxMimicCount", 2,
            "Hard limit for the maximum number of living mimics that can exist simultaneously on any moon (default: 2).");

        TestModeEnabled = Config.Bind(
            "Testing", "TestModeEnabled", true,
            "Enable debug hotkeys for testing Phoney (ENABLED by default).\n" +
            "  F6 = Force cycle AI Phase on closest Masked (Undercover -> Luring -> Ambush -> Retreat)\n" +
            "  F7 = Spawn a Masked enemy 4 m ahead (host only)\n" +
            "  F8 = Print vault stats and active Masked info to HUD + BepInEx console\n" +
            "  F9 = Inject 6 synthetic voice clips so Masked enemies can speak immediately\n" +
            "Set to false if you want to disable hotkeys.");
    }

    private void SetupNativeDllPath()
    {
        try
        {
            string pluginFolder  = Path.GetDirectoryName(Info.Location) ?? ".";
            string runtimeFolder = Path.Combine(pluginFolder, "runtimes", "win-x64");
            bool   useRuntime    = Directory.Exists(runtimeFolder);
            SetDllDirectory(useRuntime ? runtimeFolder : pluginFolder);
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = useRuntime ? runtimeFolder : pluginFolder;
            Logger.LogInfo($"[STARTUP] Native DLL path: {(useRuntime ? runtimeFolder : pluginFolder)}");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[STARTUP] Could not configure native DLL path: {ex.Message}");
        }
    }

    internal static void Patch()
    {
        Harmony ??= new Harmony(PluginGuid);
        Logger.LogInfo("[STARTUP] Registering Harmony patches class-by-class...");
        Type[] patchClasses = new Type[]
        {
            typeof(ChatCommandPatch),
            typeof(RoundLifecyclePatch),
            typeof(GameNetworkManagerPatch),
            typeof(VoiceChatPatch),
            typeof(MoonSpawnPatch),
            typeof(MaskedEnemyPatch)
        };

        foreach (var cls in patchClasses)
        {
            try
            {
                Harmony.CreateClassProcessor(cls).Patch();
                Logger.LogInfo($"[STARTUP] Harmony patched '{cls.Name}' successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[STARTUP] FAILED to patch '{cls.Name}': {ex}");
            }
        }
    }

    internal static void Unpatch()
    {
        Logger.LogInfo("[SHUTDOWN] Removing Phoney Harmony patches...");
        Harmony?.UnpatchSelf();
        Logger.LogInfo("[SHUTDOWN] Patches removed.");
    }

    private void Update()
    {
        MainThreadDispatcher.DrainQueue();
        Audio.AudioCaptureManager.Instance.UpdateMainThreadState();
        if (EnableHeldItems.Value)
        {
            AI.MaskedHeldItemManager.TrackAllLivingPlayers();
        }
    }


    private void OnDestroy()
    {
        Logger.LogInfo("[SHUTDOWN] PhoneyPlugin.OnDestroy called.");
        try { Audio.AudioCaptureManager.Instance.UnsubscribeDissonance(); } catch (Exception ex) { Logger.LogWarning($"[SHUTDOWN] Dissonance unsubscribe error: {ex.Message}"); }
        try { SpeechTranscriber.Instance.Dispose(); }         catch (Exception ex) { Logger.LogWarning($"[SHUTDOWN] Whisper dispose error: {ex.Message}"); }
        try { Network.PhoneyNetworkManager.Instance.Unregister(); } catch (Exception ex) { Logger.LogWarning($"[SHUTDOWN] NetworkManager unregister error: {ex.Message}"); }
        Unpatch();
    }
}
