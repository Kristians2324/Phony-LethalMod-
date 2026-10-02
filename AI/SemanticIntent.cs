using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Phoney.AI;

public enum SemanticIntent
{
    Unknown,
    Greeting,
    Affirmative,
    Negative,
    Location,
    LootScrap,
    Question,
    WarningPanic,
    RawBanter
}

public static class IntentClassifier
{
    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey", "yo", "hello", "hi", "sup", "who's there", "who is that", "is that you", "anyone there", "anybody", "howdy", "morning"
    };

    private static readonly HashSet<string> Affirmatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "yup", "sure", "ok", "okay", "alright", "uh-huh", "mhm", "right", "got it", "i see you", "coming",
        "nice", "good", "great", "cool", "sweet", "thanks", "thank", "awesome", "perfect", "well done", "good job", "nice job", "yessir", "fair"
    };

    private static readonly HashSet<string> Negatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nope", "nah", "don't", "dont", "stop", "never", "can't", "cant", "not", "wait", "hold on"
    };

    private static readonly HashSet<string> Locations = new(StringComparer.OrdinalIgnoreCase)
    {
        "here", "over here", "behind", "back", "inside", "outside", "room", "door", "fire exit", "entrance",
        "ship", "hallway", "hall", "pipes", "basement", "stairs", "tunnel", "bridge", "elevator", "vents"
    };

    private static readonly HashSet<string> Loot = new(StringComparer.OrdinalIgnoreCase)
    {
        "scrap", "item", "apparatus", "engine", "gear", "money", "loot", "carry", "grab", "found", "two handed",
        "quota", "take", "axle", "cash", "bottle", "sheet metal", "rubber duck"
    };

    private static readonly HashSet<string> Panics = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "monster", "brute", "jester", "dog", "turret", "landmine", "mine", "get out", "help",
        "behind you", "look out", "watch out", "die", "dead", "killing", "screaming", "giant"
    };

    private static readonly HashSet<string> Profanities = new(StringComparer.OrdinalIgnoreCase)
    {
        "fuck", "fucking", "shit", "bitch", "damn", "ass", "asshole", "hell", "wtf", "stfu", "shut up", "dick", "pussy"
    };

    /// <summary>
    /// Classifies transcript text into a primary SemanticIntent.
    /// Does NOT filter or censor profanity - uncensored speech is preserved for authentic mimicry.
    /// </summary>
    public static SemanticIntent Classify(string text, out bool containsProfanity, out bool isQuestion)
    {
        containsProfanity = false;
        isQuestion = false;

        if (string.IsNullOrWhiteSpace(text))
            return SemanticIntent.Unknown;

        string normalized = text.Trim().ToLowerInvariant();
        isQuestion = normalized.Contains("?") || 
                     normalized.StartsWith("where") || 
                     normalized.StartsWith("who") || 
                     normalized.StartsWith("what") || 
                     normalized.StartsWith("is that") || 
                     normalized.StartsWith("are you") || 
                     normalized.StartsWith("did you");

        string[] words = Regex.Split(normalized, @"\W+").Where(w => !string.IsNullOrEmpty(w)).ToArray();

        // Check profanity presence
        foreach (var word in words)
        {
            if (Profanities.Contains(word))
            {
                containsProfanity = true;
                break;
            }
        }

        // Score categories
        int greetingScore = CountMatches(words, normalized, Greetings);
        int affScore = CountMatches(words, normalized, Affirmatives);
        int negScore = CountMatches(words, normalized, Negatives);
        int locScore = CountMatches(words, normalized, Locations);
        int lootScore = CountMatches(words, normalized, Loot);
        int panicScore = CountMatches(words, normalized, Panics);

        // Highest score wins
        int maxScore = 0;
        SemanticIntent bestIntent = SemanticIntent.Unknown;

        void CheckScore(int score, SemanticIntent intent)
        {
            if (score > maxScore)
            {
                maxScore = score;
                bestIntent = intent;
            }
        }

        CheckScore(panicScore, SemanticIntent.WarningPanic);
        CheckScore(locScore, SemanticIntent.Location);
        CheckScore(lootScore, SemanticIntent.LootScrap);
        CheckScore(affScore, SemanticIntent.Affirmative);
        CheckScore(negScore, SemanticIntent.Negative);
        CheckScore(greetingScore, SemanticIntent.Greeting);

        if (bestIntent == SemanticIntent.Unknown)
        {
            if (isQuestion) return SemanticIntent.Question;
            if (containsProfanity) return SemanticIntent.RawBanter;
        }

        return bestIntent;
    }

    private static int CountMatches(string[] words, string fullText, HashSet<string> dictionary)
    {
        int count = 0;
        foreach (var word in words)
        {
            if (dictionary.Contains(word)) count++;
        }
        foreach (var phrase in dictionary)
        {
            if (phrase.Contains(' ') && fullText.Contains(phrase)) count += 2;
        }
        return count;
    }
}
