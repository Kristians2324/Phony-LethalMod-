using System;
using Phoney.Vault;
using UnityEngine;

namespace Phoney.AI;

public class DialogueBrain
{
    private static DialogueBrain? _instance;
    public static DialogueBrain Instance => _instance ??= new DialogueBrain();

    /// <summary>
    /// Evaluates what a nearby player said, and queries the ClipVault for the most convincing, deceptive response clip.
    /// Responds immediately and selects diverse responses (greetings, funny crew jokes, banter, acknowledgments).
    /// </summary>
    public RecordedClip? ChooseResponse(
        ulong impersonatedPlayerSteamId,
        string incomingPlayerTranscript,
        bool allowProfanity,
        out float responseDelaySeconds)
    {
        // Near-instant response (0.08s - 0.25s) so the mimic feels live and responsive in voice chat
        responseDelaySeconds = UnityEngine.Random.Range(0.08f, 0.25f);

        if (string.IsNullOrWhiteSpace(incomingPlayerTranscript))
        {
            return null;
        }

        SemanticIntent incomingIntent = IntentClassifier.Classify(
            incomingPlayerTranscript,
            out bool containsProfanity,
            out bool isQuestion);

        SemanticIntent desiredReplyIntent;
        string normalized = incomingPlayerTranscript.ToLowerInvariant().Trim();

        // 1. Identity / Greeting phrases ("hey", "hello", "hi", "yo", "sup", "is that you", "who's there")
        if (normalized.Contains("hey") || normalized.Contains("hello") || normalized.Contains("hi") ||
            normalized.Contains("yo") || normalized.Contains("sup") || normalized.Contains("who") ||
            normalized.Contains("is that you") || normalized.Contains("you there") ||
            incomingIntent == SemanticIntent.Greeting)
        {
            float roll = UnityEngine.Random.value;
            if (roll < 0.45f)
            {
                // Reply with a reciprocal greeting ("Hey", "Yo", "Hello")
                desiredReplyIntent = SemanticIntent.Greeting;
            }
            else if (roll < 0.75f)
            {
                // Reply with a funny crew joke, quip, or unfiltered banter
                desiredReplyIntent = SemanticIntent.RawBanter;
            }
            else
            {
                // Reply with conversational acknowledgment ("Yeah?", "What's up?")
                desiredReplyIntent = SemanticIntent.Affirmative;
            }
        }
        // 2. Where questions ("where are you?", "where is it?") -> Answer with location or banter
        else if (normalized.Contains("where"))
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.35f ? SemanticIntent.Location : SemanticIntent.RawBanter;
        }
        // 3. Loot / Item questions ("Did you find scrap?", "Got apparatus?") -> Answer with Loot, Affirmative, or Banter
        else if (normalized.Contains("scrap") || normalized.Contains("apparatus") || normalized.Contains("item") || incomingIntent == SemanticIntent.LootScrap)
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.5f ? SemanticIntent.LootScrap : SemanticIntent.Affirmative;
        }
        // 4. Panic / Danger screams ("Run!", "Monster!") -> Panic or Banter
        else if (incomingIntent == SemanticIntent.WarningPanic)
        {
            desiredReplyIntent = UnityEngine.Random.value > 0.4f ? SemanticIntent.WarningPanic : SemanticIntent.RawBanter;
        }
        // 5. Profanity / Jokes / Banter -> Match energy with unfiltered dark humor & banter
        else if (containsProfanity || incomingIntent == SemanticIntent.RawBanter)
        {
            desiredReplyIntent = SemanticIntent.RawBanter;
        }
        // 6. Generic questions -> Answer with Affirmative, Negative, or a witty Banter remark
        else if (isQuestion)
        {
            float qRoll = UnityEngine.Random.value;
            if (qRoll < 0.45f) desiredReplyIntent = SemanticIntent.Affirmative;
            else if (qRoll < 0.80f) desiredReplyIntent = SemanticIntent.Negative;
            else desiredReplyIntent = SemanticIntent.RawBanter;
        }
        else
        {
            // General conversational speech / jokes / praise -> Reply with banter, greeting, or affirmative
            float gRoll = UnityEngine.Random.value;
            if (gRoll < 0.50f) desiredReplyIntent = SemanticIntent.RawBanter;
            else if (gRoll < 0.80f) desiredReplyIntent = SemanticIntent.Affirmative;
            else desiredReplyIntent = SemanticIntent.Greeting;
        }

        PhoneyPlugin.Logger.LogInfo($"[DialogueBrain] Incoming: \"{incomingPlayerTranscript}\" -> Target Intent: {desiredReplyIntent} (Instant response delay: {responseDelaySeconds:F2}s)");

        return ClipVault.Instance.FindBestResponse(
            impersonatedPlayerSteamId,
            desiredReplyIntent,
            incomingPlayerTranscript,
            allowProfanity);
    }
}
