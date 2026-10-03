using System.Collections;
using GameNetcodeStuff;
using Phoney.AI;
using Phoney.Network;
using Phoney.Vault;
using Unity.Netcode;
using UnityEngine;

namespace Phoney.Audio;

public class PhoneyVoiceEmitter : MonoBehaviour
{
    public MaskedPlayerEnemy? MaskedEnemy { get; private set; }
    public ulong ImpersonatedSteamId { get; private set; }
    public string ImpersonatedPlayerName { get; private set; } = "Crewmate";

    public bool IsSpeaking => _isSpeaking;
    public bool CanSpeak => !_isSpeaking && Time.time >= _reactiveSpeechCooldown;

    private AudioSource? _voiceAudioSource;
    private AudioDistortionFilter? _distortionFilter;
    private AudioChorusFilter? _chorusFilter;
    private AudioEchoFilter? _echoFilter;
    private bool _isSpeaking;
    private float _nextProactiveChatTime;
    private float _reactiveSpeechCooldown;
    private bool _isDemonicMode;

    public bool IsDemonicMode => _isDemonicMode;

    // ─── Initialization ───────────────────────────────────────────────────────

    public void Initialize(MaskedPlayerEnemy maskedEnemy, PlayerControllerB targetPlayer)
    {
        MaskedEnemy = maskedEnemy;
        ImpersonatedSteamId = targetPlayer.playerSteamId;
        ImpersonatedPlayerName = targetPlayer.playerUsername;

        // Dedicated voice AudioSource so we do NOT interfere with creatureVoice or inherit loop=true
        var voiceObj = new GameObject("PhoneyVoiceAudio");
        voiceObj.transform.SetParent(maskedEnemy.transform, false);
        voiceObj.transform.localPosition = Vector3.up * 1.5f; // Mouth/head level
        _voiceAudioSource = voiceObj.AddComponent<AudioSource>();
        _voiceAudioSource.spatialBlend = 1.0f;
        _voiceAudioSource.minDistance = 2.5f;
        _voiceAudioSource.maxDistance = 28f;
        _voiceAudioSource.rolloffMode = AudioRolloffMode.Linear;
        _voiceAudioSource.playOnAwake = false;
        _voiceAudioSource.loop = false;

        // Demonic Audio DSP filters (active only during AmbushStrike attack phase)
        _distortionFilter = voiceObj.AddComponent<AudioDistortionFilter>();
        _distortionFilter.distortionLevel = 0.55f;
        _distortionFilter.enabled = false;

        _chorusFilter = voiceObj.AddComponent<AudioChorusFilter>();
        _chorusFilter.dryMix = 0.9f;
        _chorusFilter.wetMix1 = 0.70f;
        _chorusFilter.wetMix2 = 0.55f;
        _chorusFilter.wetMix3 = 0.40f;
        _chorusFilter.delay = 35f;
        _chorusFilter.rate = 0.8f;
        _chorusFilter.depth = 0.35f;
        _chorusFilter.enabled = false;

        _echoFilter = voiceObj.AddComponent<AudioEchoFilter>();
        _echoFilter.delay = 140f;
        _echoFilter.decayRatio = 0.45f;
        _echoFilter.wetMix = 0.35f;
        _echoFilter.dryMix = 1.0f;
        _echoFilter.enabled = false;

        _nextProactiveChatTime = Time.time + UnityEngine.Random.Range(15f, 35f);
        _reactiveSpeechCooldown = 0f;
        _isDemonicMode = false;
        PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter] Impersonating '{ImpersonatedPlayerName}' ({ImpersonatedSteamId}) with dedicated AudioSource");
    }

    /// <summary>
    /// Shifts impersonation to a different player when the mimic tactically retreats and re-enters undercover mode.
    /// </summary>
    public void SetImpersonatedPlayer(PlayerControllerB newPlayer)
    {
        if (newPlayer == null) return;
        SetDemonicMode(false, syncToNetwork: false);
        StopSpeaking();
        ImpersonatedSteamId = newPlayer.playerSteamId;
        ImpersonatedPlayerName = newPlayer.playerUsername;
        _nextProactiveChatTime = Time.time + UnityEngine.Random.Range(15f, 30f);
        _reactiveSpeechCooldown = 0f;
        PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter] Impersonation shifted to '{ImpersonatedPlayerName}' ({ImpersonatedSteamId})");
    }

    /// <summary>
    /// Instantly cuts off any speech audio currently playing on the voice emitter.
    /// </summary>
    public void StopSpeaking()
    {
        if (_voiceAudioSource != null && _voiceAudioSource.isPlaying)
        {
            _voiceAudioSource.Stop();
            _voiceAudioSource.clip = null;
        }
        _isSpeaking = false;
    }

    /// <summary>
    /// Toggles the demonic voice distortion effects during attack/ambush mode.
    /// When enabled, heavily distorts the player's voice with pitch down, overdrive distortion,
    /// chorus modulation, and cavernous echo.
    /// When disabled, restores natural human voice parameters.
    /// </summary>
    public void SetDemonicMode(bool enabled, bool syncToNetwork = true)
    {
        _isDemonicMode = enabled && PhoneyPlugin.EnableDemonicAttackVoice.Value;
        ApplyDemonicEffects(_isDemonicMode);

        PhoneyPlugin.Logger.LogInfo(
            $"[VoiceEmitter] Demonic voice mode toggled: enabled={_isDemonicMode} (pitch={_voiceAudioSource?.pitch:F2}, distortion={_distortionFilter?.distortionLevel:F2}).");

        if (syncToNetwork && MaskedEnemy != null)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsServer || nm.IsHost))
            {
                PhoneyNetworkManager.Instance.BroadcastDemonicMode(MaskedEnemy, _isDemonicMode);
            }
        }
    }

    private void ApplyDemonicEffects(bool demonic)
    {
        if (_distortionFilter != null)
        {
            _distortionFilter.enabled = demonic;
            if (demonic)
                _distortionFilter.distortionLevel = Mathf.Clamp(PhoneyPlugin.DemonicDistortionLevel.Value, 0.1f, 0.95f);
        }

        if (_chorusFilter != null)
        {
            _chorusFilter.enabled = demonic;
        }

        if (_echoFilter != null)
        {
            _echoFilter.enabled = demonic;
        }

        if (_voiceAudioSource != null)
        {
            if (demonic)
            {
                _voiceAudioSource.pitch = Mathf.Clamp(PhoneyPlugin.DemonicVoicePitch.Value, 0.55f, 0.90f);
                _voiceAudioSource.volume = 1.25f;
                _voiceAudioSource.maxDistance = 35f;
            }
            else
            {
                _voiceAudioSource.pitch = 1.0f;
                _voiceAudioSource.volume = 1.0f;
                _voiceAudioSource.maxDistance = 28f;
            }
        }

        PhoneyPlugin.Logger.LogInfo(
            $"[VoiceEmitter] '{MaskedEnemy?.gameObject.name}' DemonicVoice={demonic} (Filters={(_distortionFilter != null ? "active" : "null")}, Pitch={_voiceAudioSource?.pitch:F2})");
    }

    // ─── Proactive wandering banter (host-driven) ────────────────────────────

    private void Update()
    {
        if (MaskedEnemy == null || MaskedEnemy.isEnemyDead || _isSpeaking) return;

        // Only the host/server drives proactive speech
        var nm = NetworkManager.Singleton;
        if (nm == null || (!nm.IsServer && !nm.IsHost)) return;

        if (Time.time < _nextProactiveChatTime) return;

        _nextProactiveChatTime = Time.time + UnityEngine.Random.Range(45f, 90f);

        if (!MaskedEnemy.inKillAnimation && MaskedEnemy.currentBehaviourStateIndex != 2)
        {
            float aggressMult = PhoneyPlugin.Aggressiveness.Value;
            if (UnityEngine.Random.value > aggressMult) return; // Respect aggressiveness config

            var proactiveClip = ClipVault.Instance.FindProactiveClip(ImpersonatedSteamId);
            if (proactiveClip != null)
            {
                PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter] Proactive banter: \"{proactiveClip.Transcript}\"");
                ClipVault.Instance.RecordClipPlayed(proactiveClip);
                _reactiveSpeechCooldown = Time.time + proactiveClip.DurationSeconds + 1.5f;
                TriggerClip(proactiveClip, UnityEngine.Random.Range(0.2f, 0.8f));
            }
        }
    }

    /// <summary>
    /// Attempts to say a natural hello/greeting when the mimic first spots a teammate.
    /// Returns true if a greeting voice clip was played, false otherwise.
    /// </summary>
    public bool TryPlayEncounterGreeting()
    {
        if (MaskedEnemy == null || MaskedEnemy.isEnemyDead || _isSpeaking) return false;
        if (Time.time < _reactiveSpeechCooldown) return false;

        var nm = NetworkManager.Singleton;
        if (nm == null || (!nm.IsServer && !nm.IsHost)) return false;

        var greetingClip = ClipVault.Instance.FindProactiveClip(ImpersonatedSteamId, SemanticIntent.Greeting);
        if (greetingClip != null)
        {
            PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter] First encounter greeting to player: \"{greetingClip.Transcript}\"");
            _reactiveSpeechCooldown = Time.time + greetingClip.DurationSeconds + 3.0f;
            _nextProactiveChatTime = Time.time + 40f;
            ClipVault.Instance.RecordClipPlayed(greetingClip);
            TriggerClip(greetingClip, UnityEngine.Random.Range(0.2f, 0.5f));
            return true;
        }
        return false;
    }

    // ─── Reactive response when a nearby player speaks ───────────────────────

    /// <summary>
    /// Called by AudioCaptureManager when a player's speech is transcribed.
    /// Only processes on the host — then syncs result to all clients.
    /// </summary>
    public void OnHeardPlayerSpeech(string transcript, PlayerControllerB? speakingPlayer)
    {
        speakingPlayer ??= StartOfRound.Instance?.localPlayerController ?? GameNetworkManager.Instance?.localPlayerController;
        if (speakingPlayer == null) return;

        PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter] OnHeardPlayerSpeech: '{speakingPlayer.playerUsername}' said \"{transcript}\"");

        if (MaskedEnemy == null || MaskedEnemy.isEnemyDead)
        {
            PhoneyPlugin.Logger.LogWarning("[VoiceEmitter]   Masked is null or dead — skipping.");
            return;
        }
        float dist = Vector3.Distance(transform.position, speakingPlayer.transform.position);
        if (dist > 22f)
        {
            PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter]   Player too far ({dist:F1}m > 22m) — out of range.");
            return;
        }

        // ── 1. Unified Speech Evaluation (Accusation Check + Response Selection) ──
        var decision = DialogueBrain.Instance.EvaluateSpeech(
            ImpersonatedSteamId,
            transcript,
            PhoneyPlugin.EnableUnfilteredBanter.Value);

        // If a nearby player calls out or exposes the mimic ("that guy's a mimic", "he's a mimic", "are you a mimic?"),
        // the mimic's cover is blown! It cuts off any friendly speech and immediately launches an ambush!
        if (PhoneyPlugin.EnableVoiceAccusationAggression.Value && decision.IsAccusation)
        {
            var ai = GetComponent<PhoneyDeceptiveAI>();
            if (ai != null)
            {
                PhoneyPlugin.Logger.LogInfo(
                    $"[VoiceEmitter] ⚠️ MIMIC COVER BLOWN! '{speakingPlayer.playerUsername}' called out: \"{transcript}\" (Matched: \"{decision.MatchedAccusation}\")! '{MaskedEnemy.gameObject.name}' triggering instant aggression on '{speakingPlayer.playerUsername}'!");

                // Cut off any active speech audio immediately
                if (_voiceAudioSource != null && _voiceAudioSource.isPlaying)
                {
                    _voiceAudioSource.Stop();
                    _voiceAudioSource.clip = null;
                }
                _isSpeaking = false;

                ai.TriggerInstantAmbush(speakingPlayer);
                return;
            }
        }

        if (_isSpeaking)
        {
            PhoneyPlugin.Logger.LogInfo("[VoiceEmitter]   Already speaking — skipping response.");
            return;
        }
        if (Time.time < _reactiveSpeechCooldown)
        {
            PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter]   On conversational cooldown for {_reactiveSpeechCooldown - Time.time:F1}s more — skipping.");
            return;
        }

        var nm = NetworkManager.Singleton;
        if (nm == null || (!nm.IsServer && !nm.IsHost))
        {
            PhoneyPlugin.Logger.LogInfo("[VoiceEmitter]   Not host — only host drives speech. Skipping.");
            return;
        }

        var clip = decision.ResponseClip;

        if (clip != null)
        {
            PhoneyPlugin.Logger.LogInfo($"[VoiceEmitter]   DialogueBrain chose clip: \"{clip.Transcript}\" (delay={decision.ResponseDelay:F2}s)");
            // Conversational rhythm: reply immediately with a natural 2.5s pause after speaking
            _reactiveSpeechCooldown = Time.time + clip.DurationSeconds + decision.ResponseDelay + 2.5f;
            _nextProactiveChatTime = Time.time + 35f;
            ClipVault.Instance.RecordClipPlayed(clip);
            TriggerClip(clip, decision.ResponseDelay);
        }
        else
        {
            PhoneyPlugin.Logger.LogInfo("[VoiceEmitter]   DialogueBrain returned NULL — acknowledging with silent gesture.");
            _reactiveSpeechCooldown = Time.time + 2.5f;

            var ai = GetComponent<PhoneyDeceptiveAI>();
            if (ai != null)
            {
                ai.StartFriendlyCrouch();
            }
        }
    }

    // ─── Trigger: host plays locally AND broadcasts to clients ───────────────

    private void TriggerClip(RecordedClip clip, float delay)
    {
        // Broadcast to all clients (and play locally on host)
        PhoneyNetworkManager.Instance.SyncAndPlayClip(MaskedEnemy!, this, clip, delay);
    }

    // ─── Actual playback coroutine ────────────────────────────────────────────

    /// <summary>
    /// Plays a recorded clip on the Masked enemy's AudioSource.
    /// <paramref name="fromNetwork"/> = true means this is a client-side replay;
    /// it will NOT re-broadcast to avoid an echo loop.
    /// </summary>
    public IEnumerator PlayClipRoutine(RecordedClip clip, float delay, bool fromNetwork)
    {
        _isSpeaking = true;

        if (delay > 0.05f)
            yield return new WaitForSeconds(delay);

        if (MaskedEnemy == null || MaskedEnemy.isEnemyDead)
        {
            _isSpeaking = false;
            yield break;
        }

        AudioClip? unityClip;
        AudioClip? reversedClip = null;

        if (_isDemonicMode)
        {
            // CRYPTID EFFECT: Reverse the audio samples so speech plays backwards!
            // Combined with pitch-down + distortion + echo, this sounds terrifyingly inhuman.
            reversedClip = CreateReversedClip(clip);
            unityClip = reversedClip ?? clip.GetOrCreateAudioClip();
        }
        else
        {
            unityClip = clip.GetOrCreateAudioClip();
        }

        if (_voiceAudioSource != null && unityClip != null)
        {
            ApplyDemonicEffects(_isDemonicMode);

            _voiceAudioSource.loop = false;
            _voiceAudioSource.clip = unityClip;

            if (_isDemonicMode)
            {
                float basePitch = PhoneyPlugin.DemonicVoicePitch.Value;
                _voiceAudioSource.pitch = UnityEngine.Random.Range(basePitch - 0.04f, basePitch + 0.04f);
                _voiceAudioSource.volume = 1.25f;
            }
            else
            {
                _voiceAudioSource.pitch = UnityEngine.Random.Range(0.97f, 1.03f);
                _voiceAudioSource.volume = 1.0f;
            }

            _voiceAudioSource.Play();

            PhoneyPlugin.Logger.LogInfo(
                $"[VoiceEmitter] [{(fromNetwork ? "CLIENT" : "HOST")}] Masked as '{ImpersonatedPlayerName}' {(_isDemonicMode ? "[DEMONIC-REVERSED]" : "[NORMAL]")}: \"{clip.Transcript}\" [{clip.DurationSeconds:F1}s]");

            // Wait for clip duration (pitch affects playback speed!)
            float actualDuration = clip.DurationSeconds / Mathf.Max(0.2f, _voiceAudioSource.pitch);
            yield return new WaitForSeconds(actualDuration);

            if (_voiceAudioSource != null)
            {
                _voiceAudioSource.Stop();
                _voiceAudioSource.clip = null;
            }

            // Clean up the temporary reversed clip to avoid memory leaks
            if (reversedClip != null)
            {
                Object.Destroy(reversedClip);
            }
        }

        _isSpeaking = false;
    }

    // ─── Reversed Audio Helper (Cryptid Backwards Speech) ────────────────────

    /// <summary>
    /// Creates a new AudioClip with the audio samples played in reverse.
    /// For multi-channel audio, reverses entire frames (not individual samples)
    /// to keep L/R channel pairing correct.
    /// </summary>
    private static AudioClip? CreateReversedClip(RecordedClip clip)
    {
        if (clip.AudioSamples == null || clip.AudioSamples.Length == 0) return null;

        try
        {
            float[] original = clip.AudioSamples;
            int channels = System.Math.Max(1, clip.Channels);
            int totalSamples = original.Length;
            int frameCount = totalSamples / channels;

            float[] reversed = new float[totalSamples];

            // Reverse frame-by-frame (each frame = channels samples)
            // so stereo L/R pairs stay correctly paired
            for (int f = 0; f < frameCount; f++)
            {
                int srcFrame = frameCount - 1 - f;
                for (int c = 0; c < channels; c++)
                {
                    reversed[f * channels + c] = original[srcFrame * channels + c];
                }
            }

            var reversedAudioClip = AudioClip.Create(
                $"Phoney_Reversed_{clip.ClipId[..6]}",
                frameCount,
                channels,
                clip.SampleRate,
                false);
            reversedAudioClip.SetData(reversed, 0);

            PhoneyPlugin.Logger.LogInfo(
                $"[VoiceEmitter] Created reversed audio clip ({frameCount} frames, {channels}ch, {clip.SampleRate}Hz) for cryptid effect.");

            return reversedAudioClip;
        }
        catch (System.Exception ex)
        {
            PhoneyPlugin.Logger.LogWarning($"[VoiceEmitter] Failed to create reversed clip: {ex.Message}. Falling back to forward playback.");
            return null;
        }
    }
}
