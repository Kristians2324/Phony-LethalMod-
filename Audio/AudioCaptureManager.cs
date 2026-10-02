using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dissonance;
using Dissonance.Audio.Capture;
using GameNetcodeStuff;
using NAudio.Wave;
using Phoney.AI;
using Phoney.Core;
using UnityEngine;

namespace Phoney.Audio;

public class AudioCaptureManager
{
    private static AudioCaptureManager? _instance;
    public static AudioCaptureManager Instance => _instance ??= new AudioCaptureManager();

    private readonly Dictionary<ulong, VoiceCaptureListener> _activeListeners = new();
    private readonly List<PhoneyVoiceEmitter> _activeEmitters = new();
    private DissonanceVoiceCaptureListener? _dissonanceListener;
    private DissonanceComms? _subscribedComms;

    public volatile float LastKnownMainThreadTime;
    public PlayerControllerB? CachedLocalPlayer;
    public ulong CachedLocalSteamId;
    public string CachedLocalPlayerName = "Player";

    public void UpdateMainThreadState()
    {
        LastKnownMainThreadTime = Time.time;
        var lp = StartOfRound.Instance?.localPlayerController ?? GameNetworkManager.Instance?.localPlayerController;
        if (lp != null)
        {
            CachedLocalPlayer = lp;
            CachedLocalSteamId = lp.playerSteamId;
            CachedLocalPlayerName = lp.playerUsername;
        }
    }

    public void RegisterEmitter(PhoneyVoiceEmitter emitter)
    {
        if (!_activeEmitters.Contains(emitter))
        {
            _activeEmitters.Add(emitter);
            PhoneyPlugin.Logger.LogInfo($"[AudioCapture] Registered emitter for '{emitter.ImpersonatedPlayerName}'. Total emitters: {_activeEmitters.Count}");
        }
    }

    public void UnregisterEmitter(PhoneyVoiceEmitter emitter)
    {
        _activeEmitters.Remove(emitter);
        PhoneyPlugin.Logger.LogInfo($"[AudioCapture] Unregistered emitter for '{emitter.ImpersonatedPlayerName}'. Remaining: {_activeEmitters.Count}");
    }

    /// <summary>
    /// Subscribes directly to Dissonance to capture the LOCAL player's microphone in real time.
    /// Works regardless of whether playing solo, LAN, or online.
    /// </summary>
    public void TrySubscribeToDissonance()
    {
        var comms = StartOfRound.Instance?.voiceChatModule;
        if (comms == null) return;
        if (_dissonanceListener != null && _subscribedComms == comms) return;

        try
        {
            _dissonanceListener ??= new DissonanceVoiceCaptureListener();
            comms.SubscribeToRecordedAudio(_dissonanceListener);
            _subscribedComms = comms;
            PhoneyPlugin.Logger.LogInfo("[AudioCapture] ✓ SUBSCRIBED to Dissonance local microphone capture!");
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogError($"[AudioCapture] Failed to subscribe to Dissonance: {ex}");
        }
    }

    /// <summary>
    /// Attaches a VoiceCaptureListener to remote players' voice chat AudioSources.
    /// </summary>
    public void BindPlayerVoice(PlayerControllerB player)
    {
        if (player == null || player.playerSteamId == 0) return;
        if (_activeListeners.ContainsKey(player.playerSteamId)) return;

        AudioSource? voiceSource = player.currentVoiceChatAudioSource;
        if (voiceSource == null) return;

        var listener = voiceSource.gameObject.GetComponent<VoiceCaptureListener>();
        if (listener == null)
            listener = voiceSource.gameObject.AddComponent<VoiceCaptureListener>();

        listener.Initialize(player.playerSteamId, player.playerUsername, player);
        _activeListeners[player.playerSteamId] = listener;

        PhoneyPlugin.Logger.LogInfo($"[AudioCapture] ✓ Remote voice listener BOUND to '{player.playerUsername}' (SteamID={player.playerSteamId})");
    }

    /// <summary>
    /// Called when Whisper finishes transcribing a player's speech.
    /// Routes the transcript to all nearby Masked voice emitters.
    /// </summary>
    public void NotifySpeechTranscribed(string transcript, PlayerControllerB? player)
    {
        player ??= CachedLocalPlayer ?? StartOfRound.Instance?.localPlayerController ?? GameNetworkManager.Instance?.localPlayerController;
        PhoneyPlugin.Logger.LogInfo($"[AudioCapture] NotifySpeechTranscribed: '{player?.playerUsername ?? "LocalPlayer"}' said: \"{transcript}\"");

        if (string.IsNullOrWhiteSpace(transcript)) return;

        // Prune dead emitters
        int before = _activeEmitters.Count;
        _activeEmitters.RemoveAll(e => e == null || e.MaskedEnemy == null || e.MaskedEnemy.isEnemyDead);
        int after = _activeEmitters.Count;
        if (before != after)
            PhoneyPlugin.Logger.LogInfo($"[AudioCapture] Pruned {before - after} dead emitters. {after} remain.");

        if (_activeEmitters.Count == 0)
        {
            PhoneyPlugin.Logger.LogWarning("[AudioCapture] No active Masked emitters to respond.");
            return;
        }

        foreach (var emitter in _activeEmitters)
        {
            emitter.OnHeardPlayerSpeech(transcript, player);
        }
    }

    public void Clear()
    {
        _activeListeners.Clear();
        _activeEmitters.Clear();
        _subscribedComms = null;
    }
}

/// <summary>
/// Direct Dissonance subscriber: captures the local player's raw microphone stream directly from Dissonance.
/// </summary>
public class DissonanceVoiceCaptureListener : IMicrophoneSubscriber
{
    private readonly List<float> _sampleBuffer = new(48000 * 6);
    private readonly object _bufferLock = new();
    private bool _isCapturing;
    private int _silentSampleCount;
    private const float AmplitudeThreshold = 0.003f; // Sensitive threshold for speech detection
    private int _chunksDispatched;

    public void ReceiveMicrophoneData(ArraySegment<float> buffer, WaveFormat format)
    {
        if (buffer.Array == null || buffer.Count == 0) return;

        int sampleRate = format.SampleRate;
        int channels = format.Channels;

        // Calculate RMS of this frame
        float sum = 0f;
        int end = buffer.Offset + buffer.Count;
        for (int i = buffer.Offset; i < end; i++)
        {
            float s = buffer.Array[i];
            sum += s * s;
        }
        float rms = Mathf.Sqrt(sum / buffer.Count);

        lock (_bufferLock)
        {
            if (rms > AmplitudeThreshold)
            {
                _isCapturing = true;
                _silentSampleCount = 0;
                for (int i = buffer.Offset; i < end; i++)
                    _sampleBuffer.Add(buffer.Array[i]);
            }
            else if (_isCapturing)
            {
                for (int i = buffer.Offset; i < end; i++)
                    _sampleBuffer.Add(buffer.Array[i]);

                _silentSampleCount += buffer.Count;

                int silenceLimitSamples = (sampleRate * channels) / 2; // 0.5s silence triggers dispatch
                int maxCapacitySamples = (sampleRate * channels) * 6;  // 6s max utterance

                if (_silentSampleCount >= silenceLimitSamples || _sampleBuffer.Count >= maxCapacitySamples)
                {
                    DispatchChunk(sampleRate, channels);
                }
            }
        }
    }

    public void Reset()
    {
        lock (_bufferLock)
        {
            _isCapturing = false;
            _silentSampleCount = 0;
            _sampleBuffer.Clear();
        }
    }

    private void DispatchChunk(int sampleRate, int channels)
    {
        _isCapturing = false;
        _silentSampleCount = 0;

        int minSamples = (int)(sampleRate * channels * 0.35f);
        if (_sampleBuffer.Count < minSamples)
        {
            _sampleBuffer.Clear();
            return;
        }

        float[] chunk = _sampleBuffer.ToArray();
        _sampleBuffer.Clear();

        _chunksDispatched++;
        int chunkNum = _chunksDispatched;

        ulong steamId = AudioCaptureManager.Instance.CachedLocalSteamId;
        string pName = AudioCaptureManager.Instance.CachedLocalPlayerName;
        float timestamp = AudioCaptureManager.Instance.LastKnownMainThreadTime;

        MainThreadDispatcher.Enqueue(() =>
        {
            PhoneyPlugin.Logger.LogInfo($"[DissonanceMic] Dispatching chunk #{chunkNum} ({chunk.Length} samples @ {sampleRate}Hz) for '{pName}' → Whisper.");
        });

        SpeechTranscriber.Instance.EnqueueAudioSegment(
            steamId,
            pName,
            chunk,
            sampleRate,
            channels,
            timestamp,
            clip =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    PhoneyPlugin.Logger.LogInfo($"[DissonanceMic] Whisper transcribed: \"{clip.Transcript}\"");
                    var player = AudioCaptureManager.Instance.CachedLocalPlayer
                                 ?? StartOfRound.Instance?.localPlayerController
                                 ?? GameNetworkManager.Instance?.localPlayerController;
                    if (!string.IsNullOrWhiteSpace(clip.Transcript) && clip.Transcript != "[Unprocessed voice]")
                    {
                        AudioCaptureManager.Instance.NotifySpeechTranscribed(clip.Transcript, player);
                    }
                });
            });
    }
}

/// <summary>
/// Attached to remote players' voice chat AudioSources as a backup listener.
/// </summary>
public class VoiceCaptureListener : MonoBehaviour
{
    public ulong PlayerSteamId { get; private set; }
    public string PlayerName { get; private set; } = "Crewmate";
    public PlayerControllerB? PlayerController { get; private set; }

    private readonly List<float> _sampleBuffer = new(48000 * 6);
    private readonly object _bufferLock = new();

    private bool _isCapturing;
    private int _silentSampleCount;
    private const float AmplitudeThreshold = 0.008f;
    private int _sampleRate = 48000;
    private float _lastKnownTime;

    public void Initialize(ulong steamId, string playerName, PlayerControllerB player)
    {
        PlayerSteamId = steamId;
        PlayerName = playerName;
        PlayerController = player;
        _sampleRate = AudioSettings.outputSampleRate;
    }

    private void Update()
    {
        _lastKnownTime = Time.time;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (PlayerSteamId == 0) return;

        float sum = 0f;
        for (int i = 0; i < data.Length; i++)
            sum += data[i] * data[i];
        float rms = UnityEngine.Mathf.Sqrt(sum / data.Length);

        lock (_bufferLock)
        {
            if (rms > AmplitudeThreshold)
            {
                _isCapturing = true;
                _silentSampleCount = 0;
                _sampleBuffer.AddRange(data);
            }
            else if (_isCapturing)
            {
                _sampleBuffer.AddRange(data);
                _silentSampleCount += data.Length;

                int silenceLimitSamples = (_sampleRate * channels) / 2;
                int maxCapacitySamples = (_sampleRate * channels) * 6;

                if (_silentSampleCount >= silenceLimitSamples || _sampleBuffer.Count >= maxCapacitySamples)
                {
                    DispatchChunk(channels);
                }
            }
        }
    }

    private void DispatchChunk(int channels)
    {
        _isCapturing = false;
        _silentSampleCount = 0;

        int minSamples = (int)(_sampleRate * channels * 0.4f);
        if (_sampleBuffer.Count < minSamples)
        {
            _sampleBuffer.Clear();
            return;
        }

        float[] chunk = _sampleBuffer.ToArray();
        _sampleBuffer.Clear();

        ulong steamId = PlayerSteamId;
        string pName = PlayerName;
        int rate = _sampleRate;
        float timestamp = _lastKnownTime;
        var pControl = PlayerController;

        SpeechTranscriber.Instance.EnqueueAudioSegment(
            steamId,
            pName,
            chunk,
            rate,
            channels,
            timestamp,
            clip =>
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (pControl != null && !string.IsNullOrWhiteSpace(clip.Transcript)
                        && clip.Transcript != "[Unprocessed voice]")
                    {
                        AudioCaptureManager.Instance.NotifySpeechTranscribed(clip.Transcript, pControl);
                    }
                });
            });
    }
}
