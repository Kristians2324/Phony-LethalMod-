using GameNetcodeStuff;
using HarmonyLib;
using Phoney.Audio;

namespace Phoney.Patches;

[HarmonyPatch(typeof(PlayerControllerB))]
public class VoiceChatPatch
{
    [HarmonyPatch("Update")]
    [HarmonyPostfix]
    private static void UpdatePostfix(PlayerControllerB __instance)
    {
        if (__instance == null || !__instance.isPlayerControlled) return;

        // Try subscribing to Dissonance local mic capture if not already subscribed
        AudioCaptureManager.Instance.TrySubscribeToDissonance();

        // Ensure voice audio listener is bound once currentVoiceChatAudioSource is ready (for remote players)
        if (__instance.currentVoiceChatAudioSource != null)
        {
            AudioCaptureManager.Instance.BindPlayerVoice(__instance);
        }
    }
}
