using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Phoney.AI;
using UnityEngine;

namespace Phoney.Vault;

public class ClipVault
{
    private static ClipVault? _instance;
    public static ClipVault Instance => _instance ??= new ClipVault();

    private readonly ConcurrentDictionary<ulong, List<RecordedClip>> _playerClips = new();
    private readonly object _lock = new();

    public int TotalClipCount
    {
        get
        {
            lock (_lock)
                return _playerClips.Values.Sum(list => list.Count);
        }
    }

    // ─── Storing ──────────────────────────────────────────────────────────────

    public void AddClip(RecordedClip clip)
    {
        lock (_lock)
        {
            if (!_playerClips.TryGetValue(clip.PlayerSteamId, out var list))
            {
                list = new List<RecordedClip>();
                _playerClips[clip.PlayerSteamId] = list;
            }

            // Cap at 40 clips per player to cap memory
            if (list.Count >= 40)
            {
                list[0].DisposeClip();
                list.RemoveAt(0);
            }

            list.Add(clip);
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[ClipVault] Indexed '{clip.PlayerName}' [{clip.DurationSeconds:F1}s]: \"{clip.Transcript}\" (Intent:{clip.Intent} Profanity:{clip.ContainsProfanity})");
    }

    private readonly ConcurrentDictionary<string, float> _recentlyPlayed = new();
    private readonly Queue<string> _recentClipsHistory = new();

    public void RecordClipPlayed(RecordedClip clip)
    {
        if (clip != null && !string.IsNullOrWhiteSpace(clip.Transcript))
        {
            string key = clip.Transcript.Trim().ToLowerInvariant();
            _recentlyPlayed[key] = Time.time;
            lock (_lock)
            {
                _recentClipsHistory.Enqueue(key);
                while (_recentClipsHistory.Count > 6)
                {
                    _recentClipsHistory.Dequeue();
                }
            }
        }
    }

    // ─── Lookup: contextual response ──────────────────────────────────────────

    public RecordedClip? FindBestResponse(
        ulong targetPlayerId,
        SemanticIntent desiredIntent,
        string incomingQuestion,
        bool allowProfanity)
    {
        lock (_lock)
        {
            var clips = GetClipsForPlayer(targetPlayerId);
            if (clips == null || clips.Count == 0) return null;

            var scored = clips
                .Select(c => (Clip: c, Score: ScoreClip(c, desiredIntent, incomingQuestion, allowProfanity)))
                .Where(x => x.Score > -500f) // Filter out hard anti-parroting rejections
                .OrderByDescending(x => x.Score)
                .ToList();

            // Diversity & Variety: pick among top-tier candidates (within 22 points of the top score)
            // with recency-weighted preference so recent dialogue is prioritized over ancient clips from early in the game!
            // (Removed Take(4) which previously truncated candidate lists and locked selection to the oldest clips!)
            if (scored.Count > 0)
            {
                float bestScore = scored[0].Score;
                var topCandidates = scored
                    .Where(x => x.Score >= bestScore - 22f)
                    .Select(x => x.Clip)
                    .ToList();

                return PickRecencyWeighted(topCandidates);
            }

            // Fallback: if all clips were heavily penalized by recent playback, pick any clip that isn't a direct parrot
            var fallback = clips.Where(c => !IsParrotOf(c.Transcript, incomingQuestion)).ToList();
            if (fallback.Count > 0)
            {
                return PickRecencyWeighted(fallback);
            }

            return null;
        }
    }

    // ─── Lookup: proactive wandering banter ───────────────────────────────────

    /// <summary>Returns true if the vault has at least one clip for the given Steam ID.</summary>
    public bool HasClipsFor(ulong steamId)
    {
        lock (_lock)
            return _playerClips.TryGetValue(steamId, out var list) && list.Count > 0;
    }

    public RecordedClip? FindProactiveClip(ulong targetPlayerId)
    {
        lock (_lock)
        {
            var clips = GetClipsForPlayer(targetPlayerId);
            if (clips == null || clips.Count == 0) return null;

            // Proactive wandering chatter: any clip from this player that isn't a scream!
            // No filter — dark jokes, friend banter, swearing, or comments are all fair game!
            var candid = clips.Where(c => c.Intent != SemanticIntent.WarningPanic).ToList();
            if (candid.Count == 0) return null;

            // Filter out recently played clips (history check + 75s cooldown)
            var fresh = candid
                .Where(c => {
                    string key = c.Transcript.Trim().ToLowerInvariant();
                    if (_recentClipsHistory.Contains(key)) return false;
                    return !_recentlyPlayed.TryGetValue(key, out float t) || Time.time - t > 75f;
                })
                .ToList();

            var pool = fresh.Count > 0 ? fresh : candid;
            return PickRecencyWeighted(pool);
        }
    }

    /// <summary>Finds a proactive clip matching a specific intent, e.g. SemanticIntent.Greeting.</summary>
    public RecordedClip? FindProactiveClip(ulong targetPlayerId, SemanticIntent preferredIntent)
    {
        lock (_lock)
        {
            var clips = GetClipsForPlayer(targetPlayerId);
            if (clips == null || clips.Count == 0) return null;

            var matching = clips.Where(c => c.Intent == preferredIntent).ToList();
            if (matching.Count > 0)
            {
                var fresh = matching
                    .Where(c => {
                        string key = c.Transcript.Trim().ToLowerInvariant();
                        if (_recentClipsHistory.Contains(key)) return false;
                        return !_recentlyPlayed.TryGetValue(key, out float t) || Time.time - t > 75f;
                    })
                    .ToList();
                var pool = fresh.Count > 0 ? fresh : matching;

                // When preferred intent is Greeting, but greeting clips are ancient (> 240s old from ship landing),
                // allow picking recent friendly banter / affirmative clips from the last 3 minutes so the mimic doesn't
                // sound stuck in the dropship repeating round-start hellos!
                if (preferredIntent == SemanticIntent.Greeting)
                {
                    bool allAncient = pool.All(c => Time.time - c.RecordedTimestamp > 240f);
                    if (allAncient)
                    {
                        var recentBanter = clips
                            .Where(c => (c.Intent == SemanticIntent.RawBanter || c.Intent == SemanticIntent.Affirmative || c.ContainsProfanity)
                                     && (Time.time - c.RecordedTimestamp) < 180f)
                            .Where(c => {
                                string key = c.Transcript.Trim().ToLowerInvariant();
                                return !_recentClipsHistory.Contains(key) && (!_recentlyPlayed.TryGetValue(key, out float t) || Time.time - t > 60f);
                            })
                            .ToList();

                        if (recentBanter.Count > 0 && UnityEngine.Random.value < 0.50f)
                        {
                            return PickRecencyWeighted(recentBanter);
                        }
                    }
                }

                return PickRecencyWeighted(pool);
            }
            else
            {
                // Fallback if no matching clips for this intent
                var fallback = clips.Where(c => c.Intent != SemanticIntent.WarningPanic).ToList();
                if (fallback.Count > 0)
                    return PickRecencyWeighted(fallback);
            }

            return null;
        }
    }

    /// <summary>
    /// Finds a voice clip for aggressive attack vocalizations in Ambush Strike mode.
    /// Prefers screams, panic callouts, and urgent warnings, but gracefully falls back to ANY clip from the player.
    /// When processed through demonic audio filters, even everyday statements sound deeply terrifying.
    /// </summary>
    public RecordedClip? FindAttackVocalizationClip(ulong targetPlayerId)
    {
        lock (_lock)
        {
            var clips = GetClipsForPlayer(targetPlayerId);
            if (clips == null || clips.Count == 0)
            {
                // If this player has no clips, check any clips in the entire vault as emergency fallback
                var anyClips = _playerClips.Values.SelectMany(v => v).ToList();
                if (anyClips.Count == 0) return null;
                return PickRecencyWeighted(anyClips);
            }

            // 1. Try panic / scream / urgent warning clips first
            var panicClips = clips
                .Where(c => c.Intent == SemanticIntent.WarningPanic ||
                            c.Transcript.IndexOf("scream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("help", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("no", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("die", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("fuck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Transcript.IndexOf("shit", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (panicClips.Count > 0)
            {
                var freshPanic = panicClips
                    .Where(c => !_recentlyPlayed.TryGetValue(c.Transcript.Trim().ToLowerInvariant(), out float t) || Time.time - t > 20f)
                    .ToList();
                var pool = freshPanic.Count > 0 ? freshPanic : panicClips;
                return PickRecencyWeighted(pool);
            }

            // 2. Otherwise pick ANY fresh clip from this player
            var freshGeneral = clips
                .Where(c => !_recentlyPlayed.TryGetValue(c.Transcript.Trim().ToLowerInvariant(), out float t) || Time.time - t > 15f)
                .ToList();
            if (freshGeneral.Count > 0)
                return PickRecencyWeighted(freshGeneral);

            // 3. Fallback: any clip from this player
            return PickRecencyWeighted(clips);
        }
    }

    // ─── Lookup: by timestamp (for network sync on receiving clients) ─────────

    /// <summary>
    /// Finds the clip from <paramref name="speakerSteamId"/> whose recorded timestamp is
    /// closest to <paramref name="referenceTimestamp"/>. Falls back to intent-based search
    /// if no close match exists (tolerance: ±4 seconds).
    /// </summary>
    public RecordedClip? FindByTimestamp(ulong speakerSteamId, float referenceTimestamp, SemanticIntent fallbackIntent)
    {
        lock (_lock)
        {
            var clips = GetClipsForPlayer(speakerSteamId);
            if (clips == null || clips.Count == 0) return null;

            var nearest = clips
                .Select(c => (Clip: c, Dist: Math.Abs(c.RecordedTimestamp - referenceTimestamp)))
                .OrderBy(x => x.Dist)
                .FirstOrDefault();

            if (nearest.Clip != null && nearest.Dist <= 4.0f)
                return nearest.Clip;

            return FindBestResponse(speakerSteamId, fallbackIntent, string.Empty, allowProfanity: true);
        }
    }

    // ─── Scoring ──────────────────────────────────────────────────────────────

    private static bool IsParrotOf(string? clipTranscript, string? incomingQuestion)
    {
        if (string.IsNullOrWhiteSpace(incomingQuestion) || string.IsNullOrWhiteSpace(clipTranscript)) return false;
        string cleanIncoming = new string(incomingQuestion.Where(c => !char.IsPunctuation(c)).ToArray()).Trim().ToLowerInvariant();
        string cleanClip = new string(clipTranscript.Where(c => !char.IsPunctuation(c)).ToArray()).Trim().ToLowerInvariant();

        // Allow reciprocal greetings - humans naturally greet back!
        if (cleanClip is "hello" or "hey" or "hi" or "yo" or "sup")
            return false;

        return cleanClip == cleanIncoming || 
               (cleanIncoming.Length >= 6 && cleanClip.StartsWith(cleanIncoming)) ||
               (cleanClip.Length >= 6 && cleanIncoming.StartsWith(cleanClip));
    }

    private float ScoreClip(RecordedClip clip, SemanticIntent desiredIntent, string incomingQuestion, bool allowProfanity)
    {
        // Anti-Parroting: if clip is identical or directly repeats what the player just said, strongly reject!
        if (IsParrotOf(clip.Transcript, incomingQuestion))
        {
            return -999f;
        }

        // Base score for any authentic voice clip recorded from the crew
        float score = 25f;

        if (clip.Intent == desiredIntent && desiredIntent != SemanticIntent.Unknown)
        {
            score += 50f;
        }
        else if (desiredIntent == SemanticIntent.Greeting)
        {
            if (clip.Intent == SemanticIntent.Greeting) score += 50f;
            else if (clip.Intent == SemanticIntent.RawBanter || clip.ContainsProfanity) score += 40f;
            else if (clip.Intent == SemanticIntent.Affirmative) score += 35f;
        }
        else if (desiredIntent == SemanticIntent.RawBanter)
        {
            if (clip.Intent == SemanticIntent.RawBanter || clip.ContainsProfanity) score += 50f;
            else if (clip.Intent == SemanticIntent.Affirmative) score += 30f;
        }
        else if (desiredIntent == SemanticIntent.Affirmative)
        {
            if (clip.Intent == SemanticIntent.Affirmative) score += 50f;
            else if (clip.Intent == SemanticIntent.Greeting) score += 35f;
            else if (clip.Intent == SemanticIntent.RawBanter || clip.ContainsProfanity) score += 30f;
        }

        // Do not use panic screams for normal conversational replies
        if (desiredIntent != SemanticIntent.WarningPanic && clip.Intent == SemanticIntent.WarningPanic)
        {
            return -999f;
        }

        if (clip.IsQuestion)
            score -= 10f;

        // Prefer punchy, concise dialogue (0.7s - 3.2s) so the mimic responds quickly like a real teammate
        if (clip.DurationSeconds >= 0.7f && clip.DurationSeconds <= 3.2f)
        {
            score += 25f;
        }
        else if (clip.DurationSeconds > 4.5f)
        {
            score -= 25f; // Penalize long rambling clips
        }

        // Unfiltered: profanity and dark humor score high as genuine, authentic friend speech
        if (clip.ContainsProfanity)
            score += 18f;

        // Anti-Repetition: if this clip was played in the last 6 lines, heavily penalize it (-300f)!
        if (!string.IsNullOrWhiteSpace(clip.Transcript))
        {
            string key = clip.Transcript.Trim().ToLowerInvariant();
            lock (_lock)
            {
                if (_recentClipsHistory.Contains(key))
                {
                    score -= 300f;
                }
            }

            if (_recentlyPlayed.TryGetValue(key, out float lastPlayed))
            {
                float timeSincePlayed = Time.time - lastPlayed;
                if (timeSincePlayed < 75f)
                {
                    score -= Mathf.Lerp(150f, 0f, timeSincePlayed / 75f);
                }
            }
        }

        float age = Mathf.Max(0f, Time.time - clip.RecordedTimestamp);
        // Recency boost: strong gradient favoring recent dialogue over ancient clips from round start
        if (age < 45f) score += 40f;         // Just spoken in the last 45 seconds (extremely fresh!)
        else if (age < 120f) score += 30f;   // Spoken in the last 2 minutes
        else if (age < 240f) score += 20f;   // Spoken in the last 4 minutes
        else if (age < 420f) score += 10f;   // Spoken in the last 7 minutes
        else if (age < 600f) score += 5f;    // Spoken in the last 10 minutes

        score += UnityEngine.Random.Range(0f, 6f);

        return score;
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Selects a clip from the candidate list with weighted preference for recently recorded audio.
    /// Clips recorded in the last 1–3 minutes have the highest weight, while older clips from early
    /// in the expedition still have a chance to be chosen for natural variety.
    /// </summary>
    private static RecordedClip PickRecencyWeighted(IReadOnlyList<RecordedClip> candidates)
    {
        if (candidates.Count == 0) throw new ArgumentException("Candidate list cannot be empty.", nameof(candidates));
        if (candidates.Count == 1) return candidates[0];

        float now = Time.time;
        float totalWeight = 0f;
        Span<float> weights = stackalloc float[Math.Min(candidates.Count, 128)];
        float[]? heapWeights = candidates.Count > 128 ? new float[candidates.Count] : null;
        var activeWeights = heapWeights ?? weights[..candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            float age = Mathf.Max(0f, now - candidates[i].RecordedTimestamp);
            // Half-life recency curve: a clip from 60s ago has ~4x weight of a clip from 300s ago
            // Floor weight is 0.15f so older authentic clips can still occasionally be selected for variety
            float w = Mathf.Max(0.15f, 1.0f / (1.0f + age / 90.0f));
            activeWeights[i] = w;
            totalWeight += w;
        }

        float roll = UnityEngine.Random.value * totalWeight;
        float accum = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            accum += activeWeights[i];
            if (roll <= accum)
                return candidates[i];
        }

        return candidates[^1];
    }

    public int GetClipCountForPlayer(ulong steamId)
    {
        lock (_lock)
        {
            return _playerClips.TryGetValue(steamId, out var list) ? list.Count : 0;
        }
    }

    private List<RecordedClip>? GetClipsForPlayer(ulong steamId)
    {
        if (_playerClips.TryGetValue(steamId, out var list) && list.Count > 0)
            return list;

        // If steamId is the local player, also check if clips were stored under key 0
        var localPlayer = Audio.AudioCaptureManager.Instance.CachedLocalPlayer;
        if (localPlayer != null && steamId == localPlayer.playerSteamId && _playerClips.TryGetValue(0, out var zeroList) && zeroList.Count > 0)
        {
            return zeroList;
        }

        // Fallback: pick the player with the MOST RECENT clip, NOT the oldest!
        return _playerClips.Values
            .Where(c => c.Count > 0)
            .OrderByDescending(l => l.Max(clip => clip.RecordedTimestamp))
            .FirstOrDefault();
    }

    // ─── Cleanup & Lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Prunes stale clips at the end of an expedition so the mimic doesn't indefinitely replay
    /// voice lines from hours ago or previous moons. Keeps up to 15 most recent clips per player.
    /// </summary>
    public void PruneForNewExpedition()
    {
        lock (_lock)
        {
            float now = Time.time;
            foreach (var kvp in _playerClips)
            {
                var list = kvp.Value;
                // Remove clips older than 20 minutes (1200 seconds)
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (now - list[i].RecordedTimestamp > 1200f && list.Count > 8)
                    {
                        list[i].DisposeClip();
                        list.RemoveAt(i);
                    }
                }

                // Keep at most 15 most recent clips
                while (list.Count > 15)
                {
                    list[0].DisposeClip();
                    list.RemoveAt(0);
                }
            }

            _recentClipsHistory.Clear();
            PhoneyPlugin.Logger.LogInfo($"[ClipVault] Pruned vault for new expedition. Total clips remaining: {TotalClipCount}");
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var list in _playerClips.Values)
            {
                foreach (var c in list) c.DisposeClip();
                list.Clear();
            }
            _playerClips.Clear();
            _recentClipsHistory.Clear();
            PhoneyPlugin.Logger.LogInfo("[ClipVault] Cleared all stored voice clips.");
        }
    }
}
