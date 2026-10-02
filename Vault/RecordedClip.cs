using System;
using Phoney.AI;
using Phoney.Audio;
using UnityEngine;

namespace Phoney.Vault;

public class RecordedClip
{
    public string ClipId { get; }
    public ulong PlayerSteamId { get; }
    public string PlayerName { get; }
    public string Transcript { get; set; }
    public SemanticIntent Intent { get; set; }
    public bool ContainsProfanity { get; set; }
    public bool IsQuestion { get; set; }
    public float DurationSeconds { get; }
    public float RecordedTimestamp { get; }
    public float[] AudioSamples { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    private AudioClip? _cachedClip;

    public RecordedClip(
        ulong playerSteamId,
        string playerName,
        float[] audioSamples,
        int sampleRate,
        int channels,
        float recordedTimestamp)
    {
        ClipId = Guid.NewGuid().ToString("N");
        PlayerSteamId = playerSteamId;
        PlayerName = playerName;
        AudioSamples = audioSamples;
        SampleRate = sampleRate;
        Channels = channels;
        RecordedTimestamp = recordedTimestamp;
        DurationSeconds = (float)audioSamples.Length / (sampleRate * Math.Max(1, channels));
        Transcript = string.Empty;
        Intent = SemanticIntent.Unknown;
    }

    /// <summary>
    /// Gets or creates the Unity AudioClip for playback on the Masked AudioSource.
    /// </summary>
    public AudioClip GetOrCreateAudioClip()
    {
        if (_cachedClip == null)
        {
            _cachedClip = WavUtility.CreateAudioClip(AudioSamples, SampleRate, Channels, $"Phoney_{PlayerName}_{ClipId[..6]}");
        }
        return _cachedClip;
    }

    public void DisposeClip()
    {
        if (_cachedClip != null)
        {
            UnityEngine.Object.Destroy(_cachedClip);
            _cachedClip = null;
        }
    }
}
