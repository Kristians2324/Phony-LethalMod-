using HarmonyLib;
using Phoney.Audio;
using Phoney.Network;
using Phoney.Vault;

namespace Phoney.Patches;

[HarmonyPatch(typeof(StartOfRound))]
public class RoundLifecyclePatch
{
    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix()
    {
        PhoneyPlugin.Logger.LogInfo("[Lifecycle] StartOfRound.Start() fired — registering network handlers.");
        PhoneyNetworkManager.Instance.TryRegisterHandlers();
        AudioCaptureManager.Instance.TrySubscribeToDissonance();
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] allPlayerScripts count: {StartOfRound.Instance?.allPlayerScripts?.Length ?? -1}");
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] levels count: {StartOfRound.Instance?.levels?.Length ?? -1}");
    }

    [HarmonyPatch("ShipHasLeft")]
    [HarmonyPostfix]
    private static void ShipHasLeftPostfix()
    {
        PhoneyPlugin.Logger.LogInfo("[Lifecycle] ShipHasLeft fired — clearing voice emitters and scrap registry.");
        AudioCaptureManager.Instance.Clear();
        Phoney.AI.MaskedScrapManager.GloballyProcessedScrapIds.Clear();
        Phoney.AI.MaskedScrapManager.TaintedScrapIds.Clear();
        Phoney.AI.MaskedHeldItemManager.LastKnownPlayerEquipment.Clear();
    }


    [HarmonyPatch("StartGame")]
    [HarmonyPostfix]
    private static void StartGamePostfix()
    {
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] StartGame fired — new expedition starting.");
        AudioCaptureManager.Instance.TrySubscribeToDissonance();
        Phoney.AI.MaskedScrapManager.GloballyProcessedScrapIds.Clear();
        Phoney.AI.MaskedScrapManager.TaintedScrapIds.Clear();
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] Vault holds {ClipVault.Instance.TotalClipCount} clips.");
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] inShipPhase = {StartOfRound.Instance?.inShipPhase}");
        PhoneyPlugin.Logger.LogInfo($"[Lifecycle] currentLevel = {StartOfRound.Instance?.currentLevel?.PlanetName ?? "NULL"}");
    }

    [HarmonyPatch("ReviveDeadPlayers")]
    [HarmonyPostfix]
    private static void ReviveDeadPlayersPostfix()
    {
        PhoneyPlugin.Logger.LogInfo("[Lifecycle] ReviveDeadPlayers fired — new round beginning.");
    }
}

/// <summary>
/// Registers network handlers on client connect.
/// </summary>
[HarmonyPatch(typeof(GameNetworkManager))]
public class GameNetworkManagerPatch
{
    [HarmonyPatch("Start")]
    [HarmonyPostfix]
    private static void StartPostfix()
    {
        PhoneyPlugin.Logger.LogInfo("[Lifecycle] GameNetworkManager.Start() fired — registering network handlers.");
        PhoneyNetworkManager.Instance.TryRegisterHandlers();
    }

    [HarmonyPatch("Disconnect")]
    [HarmonyPostfix]
    private static void DisconnectPostfix()
    {
        PhoneyPlugin.Logger.LogInfo("[Lifecycle] GameNetworkManager.Disconnect() fired — session ending.");
    }
}
