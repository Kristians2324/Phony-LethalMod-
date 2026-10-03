using System;
using System.Text.RegularExpressions;

namespace Phoney.AI;

/// <summary>
/// Detects voice chat keywords and phrases where players expose, accuse, or confront the mimic.
/// 
/// Supported phrases include:
///   - Direct second-person accusations: "you're a mimic", "you are a mimic", "are you a mimic?", "you're the mimic", "are you fake"
///   - Demonstrative / third-person callouts: "that guy's a mimic", "he's a mimic", "that's a mimic", "it's a mimic", "this guy's a mimic", "there's a mimic"
///   - Questions pointing at the entity: "is that a mimic", "is he a mimic", "is that guy a mimic", "is this a mimic"
///   - Shouts / commands directed at it: "get out of here mimic", "get away mimic", "back off mimic", "leave me alone mimic", "run it's a mimic", "get away from that mimic"
///   - Combat callouts: "fucking mimic", "kill the mimic", "found the mimic", "caught the mimic"
///   - Disguise doubt: "you are not real", "that's not real", "he's not real"
///   - Masked equivalents: "you're a masked", "that's a masked", "he's a masked", "are you a masked"
/// 
/// General plural discussion (e.g. "look out for mimics", "there are mimics on this moon", "check for mimics") does NOT trigger.
/// </summary>
public static class MimicDiscoveryDetector
{
    /// <summary>
    /// Checks if a player's transcribed speech is accusing, discovering, or pointing out that the entity is a mimic.
    /// Unified inside DialogueBrain with sub-microsecond pre-filter and compiled pattern checks.
    /// </summary>
    public static bool IsExposingOrAccusingMimic(string? transcript, out string matchedReason)
    {
        return DialogueBrain.Instance.IsAccusingMimic(transcript, out matchedReason);
    }
}
