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
    private static readonly Regex[] AccusationPatterns = new[]
    {
        // 1. Direct address: "you're a mimic", "you are a mimic", "are you a mimic", "you are the mimic", "are you fake", "you're fake", "you're a phony"
        new Regex(@"\b(?:you(?:['\s]?re|\s+are)|u\s+r|are\s+you)\b.*?\b(?:a\s+|the\s+)?(?:mimic|masked|fake|impostor|imposter|phony|phoney)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bare\s+you\s+real\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b(?:you(?:['\s]?re|\s+are)|u\s+r)?\s*(?:a\s+|the\s+)?(?:dirty\s+|fucking\s+|total\s+|big\s+)?(?:phony|phoney)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 2. Demonstrative / Third-person pointing: "that guy is a mimic", "he's a mimic", "that's a mimic", "it's a mimic", "this guy's a mimic", "there's a mimic", "he's a phony"
        new Regex(@"\b(?:that\s+guy|this\s+guy|that\s+one|this\s+one|that|this|he|she|it|they|there)(?:['\s]?(?:s|re)|\s+(?:is|are))\b.*?\b(?:a\s+|the\s+)?(?:mimic|masked|fake|impostor|imposter|phony|phoney)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 3. Questions pointing at the entity: "is that a mimic", "is he a mimic", "is it a mimic", "is that guy a mimic", "is there a mimic", "is that a phony"
        new Regex(@"\b(?:is\s+(?:that|this|he|she|it|that\s+guy|this\s+guy|there))\b.*?\b(?:a\s+|the\s+)?(?:mimic|masked|fake|impostor|imposter|phony|phoney)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 4. Commands / shouting directed at the mimic: "get out of here mimic", "get away mimic", "back off mimic", "leave me alone mimic", "run it's a mimic", "get away phony"
        new Regex(@"\b(?:get\s+out(?:\s+of\s+here)?|get\s+away(?:\s+from)?|stay\s+away|back\s+off|leave\s+me\s+alone|fuck\s+off|run\s+it['\s]?s)\b.*?\b(?:a\s+|that\s+)?(?:mimic|masked|phony|phoney)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 5. Direct callouts / combat commands: "fucking mimic", "found the mimic", "caught the mimic", "spotted the mimic", "kill the mimic", "kill the phony"
        new Regex(@"\b(?:fucking|found|caught|spotted|kill)\s+(?:the\s+|a\s+)?(?:mimic|masked|phony|phoney|impostor|imposter)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 6. Calling out impostor disguise: "you're not [real]", "that's not real", "he's not real"
        new Regex(@"\b(?:you(?:['\s]?re|\s+are)|that(?:['\s]?s|\s+is)|he(?:['\s]?s|\s+is))\s+not\s+real\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Checks if a player's transcribed speech is accusing, discovering, or pointing out that the entity is a mimic.
    /// Excludes general plural references like "look out for mimics" or "are there mimics".
    /// </summary>
    public static bool IsExposingOrAccusingMimic(string? transcript, out string matchedReason)
    {
        matchedReason = string.Empty;
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        string text = transcript.Trim();

        foreach (var pattern in AccusationPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
            {
                matchedReason = match.Value;
                return true;
            }
        }

        return false;
    }
}
