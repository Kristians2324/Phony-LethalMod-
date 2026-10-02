using HarmonyLib;
using Phoney.Debug;

namespace Phoney.Patches;

/// <summary>
/// Intercepts chat messages to support slash commands.
/// Provides a 100% reliable alternative to physical F-keys.
/// </summary>
[HarmonyPatch(typeof(HUDManager), "AddTextToChatOnServer")]
public static class ChatCommandPatch
{
    [HarmonyPrefix]
    public static bool Prefix(string chatMessage)
    {
        if (string.IsNullOrWhiteSpace(chatMessage)) return true;

        string trimmed = chatMessage.Trim();
        PhoneyPlugin.Logger.LogInfo($"[ChatCmd] Chat message intercepted: '{trimmed}'");

        if (!trimmed.StartsWith("/") && !trimmed.StartsWith("!"))
        {
            PhoneyPlugin.Logger.LogInfo("[ChatCmd]   Not a command — passing through normally.");
            return true;
        }

        string cmd = trimmed.Substring(1).Trim().ToLowerInvariant();
        PhoneyPlugin.Logger.LogInfo($"[ChatCmd]   Parsed command: '{cmd}'");

        // Support /phoney <cmd> prefix
        if (cmd.StartsWith("phoney"))
        {
            cmd = cmd.Substring(6).Trim();
            if (string.IsNullOrEmpty(cmd)) cmd = "help";
            PhoneyPlugin.Logger.LogInfo($"[ChatCmd]   Stripped 'phoney' prefix → '{cmd}'");
        }

        switch (cmd)
        {
            case "spawn":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing SpawnTestMasked");
                PhoneyTestMode.SpawnTestMasked();
                return false;

            case "stats":
            case "status":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing DumpStats");
                PhoneyTestMode.DumpStats();
                return false;

            case "cycle":
            case "phase":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing CycleNearestMaskedPhase");
                PhoneyTestMode.CycleNearestMaskedPhase();
                return false;

            case "inject":
            case "clips":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing InjectSyntheticClips");
                PhoneyTestMode.InjectSyntheticClips();
                return false;

            case "reveal":
            case "blood":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing TriggerNearestBloodyReveal");
                PhoneyTestMode.TriggerNearestBloodyReveal();
                return false;

            case "help":
            case "test":
                PhoneyPlugin.Logger.LogInfo("[ChatCmd]   → Executing PrintHelp");
                PhoneyTestMode.PrintHelp();
                return false;

            default:
                PhoneyPlugin.Logger.LogInfo($"[ChatCmd]   Unknown command '{cmd}' — passing to server chat.");
                return true;
        }
    }
}
