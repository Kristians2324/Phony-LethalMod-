using System;
using System.Text.RegularExpressions;
using Phoney.Vault;
using UnityEngine;

namespace Phoney.AI;

public struct DialogueDecision
{
    public bool IsAccusation;
    public string MatchedAccusation;
    public RecordedClip? ResponseClip;
    public float ResponseDelay;
}

public class DialogueBrain
{
    private static DialogueBrain? _instance;
    public static DialogueBrain Instance => _instance ??= new DialogueBrain();

    // ── Fast Accusation Pre-Filter Keywords ──────────────────────────────────
    private static readonly string[] FastAccusationKeywords = new[]
    {
        "mimic", "phony", "phoney", "fake", "impostor", "imposter", "masked", "not real", "aren't real"
    };

    private static readonly string[] ItemExclusionKeywords = new[]
    {
        "scrap", "item", "door", "loot", "shotgun", "shovel", "flashlight", "walkie", "apparatus", "money", "body", "corpse", "terminal"
    };

    // ── Pre-compiled Accusation Patterns ─────────────────────────────────────
    private static readonly Regex[] AccusationPatterns = new[]
    {
        // 1. Direct address: "you're a mimic", "you are a mimic", "are you a mimic", "you're fake", "you're a phony"
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
    /// Ultra-fast check if a player's speech accuses or exposes the mimic.
    /// Exits in sub-microseconds if no trigger keywords are present.
    /// </summary>
    public bool IsAccusingMimic(string? transcript, out string matchedReason)
    {
        matchedReason = string.Empty;
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        string normalized = transcript.ToLowerInvariant();

        // High-speed keyword pre-filter: 98% of normal conversation skips all regexes instantly!
        bool hasKeyword = false;
        for (int i = 0; i < FastAccusationKeywords.Length; i++)
        {
            if (normalized.Contains(FastAccusationKeywords[i]))
            {
                hasKeyword = true;
                break;
            }
        }
        if (!hasKeyword) return false;

        // Keyword present: evaluate compiled patterns
        for (int i = 0; i < AccusationPatterns.Length; i++)
        {
            var match = AccusationPatterns[i].Match(transcript);
            if (match.Success)
            {
                // Disambiguation: If the utterance is talking about items, scrap, doors, equipment, etc.
                // (e.g. "not real scrap", "fake item", "fake door", "not a real shotgun"), DO NOT treat it as accusing the mimic!
                bool refersToItem = false;
                for (int k = 0; k < ItemExclusionKeywords.Length; k++)
                {
                    if (normalized.Contains(ItemExclusionKeywords[k]))
                    {
                        refersToItem = true;
                        break;
                    }
                }

                if (refersToItem && !normalized.Contains("you") && !normalized.Contains("u r"))
                {
                    // Discussing inanimate object rather than accusing teammate
                    continue;
                }

                matchedReason = match.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unified cognitive evaluation: checks for accusations FIRST (for instant strike reaction),
    /// then selects a snappy, concise dialogue response with an authentic human delay.
    /// </summary>
    public DialogueDecision EvaluateSpeech(
        ulong impersonatedPlayerSteamId,
        string incomingPlayerTranscript,
        bool allowProfanity)
    {
        var decision = new DialogueDecision
        {
            IsAccusation = false,
            MatchedAccusation = string.Empty,
            ResponseClip = null,
            ResponseDelay = UnityEngine.Random.Range(0.35f, 0.70f) // Authentic human reaction delay
        };

        if (string.IsNullOrWhiteSpace(incomingPlayerTranscript))
            return decision;

        // Step 1: Instant Mimic Discovery / Accusation check!
        if (IsAccusingMimic(incomingPlayerTranscript, out string reason))
        {
            decision.IsAccusation = true;
            decision.MatchedAccusation = reason;
            decision.ResponseDelay = 0f;
            return decision;
        }

        // Step 2: Streamlined dialogue selection
        decision.ResponseClip = ChooseResponse(
            impersonatedPlayerSteamId,
            incomingPlayerTranscript,
            allowProfanity,
            out decision.ResponseDelay);

        return decision;
    }

    /// <summary>
    /// Selects the best contextual response clip from ClipVault with authentic intent matching and human delay.
    /// </summary>
    public RecordedClip? ChooseResponse(
        ulong impersonatedPlayerSteamId,
        string incomingPlayerTranscript,
        bool allowProfanity,
        out float responseDelaySeconds)
    {
        // Authentic human conversational hesitation (0.35s - 0.70s)
        float minDelay = 0.35f;
        float maxDelay = PhoneyPlugin.ResponseDelaySeconds != null ? Mathf.Max(0.45f, PhoneyPlugin.ResponseDelaySeconds.Value) : 0.70f;
        responseDelaySeconds = UnityEngine.Random.Range(minDelay, maxDelay);

        if (string.IsNullOrWhiteSpace(incomingPlayerTranscript))
            return null;

        string normalized = incomingPlayerTranscript.ToLowerInvariant().Trim();
        SemanticIntent desiredReplyIntent;

        // Refined intent routing:
        if (normalized.Contains("hey") || normalized.Contains("hello") || normalized.Contains("hi") ||
            normalized.Contains("yo") || normalized.Contains("sup") || normalized.Contains("what's up"))
        {
            float roll = UnityEngine.Random.value;
            desiredReplyIntent = roll < 0.60f ? SemanticIntent.Greeting : (roll < 0.85f ? SemanticIntent.RawBanter : SemanticIntent.Affirmative);
        }
        else if (normalized.Contains("where are you") || normalized.Contains("where you at") || normalized.Contains("where is") ||
                 normalized.Contains("over here") || normalized.Contains("come here") || normalized.Contains("follow me") || normalized.Contains("this way"))
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.30f ? SemanticIntent.Location : SemanticIntent.Affirmative;
        }
        else if (normalized.Contains("scrap") || normalized.Contains("apparatus") || normalized.Contains("item") ||
                 normalized.Contains("loot") || normalized.Contains("engine") || normalized.Contains("bottle") || normalized.Contains("valuable"))
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.40f ? SemanticIntent.LootScrap : SemanticIntent.Affirmative;
        }
        else if (normalized.Contains("run") || normalized.Contains("monster") || normalized.Contains("dog") ||
                 normalized.Contains("bracken") || normalized.Contains("spider") || normalized.Contains("turret") ||
                 normalized.Contains("look out") || normalized.Contains("help") || normalized.Contains("danger"))
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.35f ? SemanticIntent.WarningPanic : SemanticIntent.RawBanter;
        }
        else if (normalized.Contains("fuck") || normalized.Contains("shit") || normalized.Contains("damn") || normalized.Contains("bitch") || normalized.Contains("bro"))
        {
            desiredReplyIntent = SemanticIntent.RawBanter;
        }
        else if (normalized.Contains("?") || normalized.StartsWith("is ") || normalized.StartsWith("are ") ||
                 normalized.StartsWith("did ") || normalized.StartsWith("can ") || normalized.StartsWith("should "))
        {
            float qRoll = UnityEngine.Random.value;
            desiredReplyIntent = qRoll < 0.55f ? SemanticIntent.Affirmative : (qRoll < 0.85f ? SemanticIntent.Negative : SemanticIntent.RawBanter);
        }
        else
        {
            desiredReplyIntent = UnityEngine.Random.value < 0.55f ? SemanticIntent.RawBanter : SemanticIntent.Affirmative;
        }

        PhoneyPlugin.Logger.LogInfo($"[DialogueBrain] Incoming: \"{incomingPlayerTranscript}\" -> Target Intent: {desiredReplyIntent} (Human response delay: {responseDelaySeconds:F2}s)");

        return ClipVault.Instance.FindBestResponse(
            impersonatedPlayerSteamId,
            desiredReplyIntent,
            incomingPlayerTranscript,
            allowProfanity);
    }
}
