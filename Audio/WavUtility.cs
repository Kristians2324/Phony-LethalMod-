using System;
using System.IO;
using UnityEngine;

namespace Phoney.Audio;

public static class WavUtility
{
    private const int HeaderSize = 44;

    /// <summary>
    /// Resamples arbitrary PCM float audio to 16,000 Hz Mono (the exact format required by Whisper).
    /// </summary>
    public static float[] ResampleTo16kMono(float[] samples, int srcRate, int srcChannels)
    {
        if (samples == null || samples.Length == 0) return Array.Empty<float>();

        // 1. Convert to Mono by averaging channels
        int frameCount = samples.Length / Math.Max(1, srcChannels);
        float[] monoSamples = new float[frameCount];

        if (srcChannels > 1)
        {
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                for (int c = 0; c < srcChannels; c++)
                {
                    sum += samples[i * srcChannels + c];
                }
                monoSamples[i] = sum / srcChannels;
            }
        }
        else
        {
            Array.Copy(samples, monoSamples, frameCount);
        }

        if (srcRate == 16000)
        {
            return monoSamples;
        }

        // 2. Linear interpolation to 16,000 Hz
        int targetLength = (int)((long)frameCount * 16000 / srcRate);
        if (targetLength <= 0) return Array.Empty<float>();

        float[] resampled = new float[targetLength];
        double ratio = (double)(frameCount - 1) / Math.Max(1, targetLength - 1);

        for (int i = 0; i < targetLength; i++)
        {
            double srcIdx = i * ratio;
            int lowIdx = (int)srcIdx;
            int highIdx = Math.Min(lowIdx + 1, frameCount - 1);
            float frac = (float)(srcIdx - lowIdx);

            resampled[i] = Mathf.Lerp(monoSamples[lowIdx], monoSamples[highIdx], frac);
        }

        return resampled;
    }

    /// <summary>
    /// Converts raw PCM float samples (-1.0f to 1.0f) into a 16-bit PCM RIFF WAV byte array.
    /// </summary>
    public static byte[] ToWavBytes(float[] samples, int sampleRate, int channels)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        int sampleCount = samples.Length;
        int byteRate = sampleRate * channels * 2;
        short blockAlign = (short)(channels * 2);

        // RIFF header
        writer.Write("RIFF".ToCharArray());
        writer.Write(HeaderSize + (sampleCount * 2) - 8);
        writer.Write("WAVE".ToCharArray());

        // fmt chunk
        writer.Write("fmt ".ToCharArray());
        writer.Write(16); // Subchunk1Size for PCM
        writer.Write((short)1); // AudioFormat: 1 = PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)16); // BitsPerSample

        // data chunk
        writer.Write("data".ToCharArray());
        writer.Write(sampleCount * 2);

        // PCM 16-bit samples
        for (int i = 0; i < sampleCount; i++)
        {
            float s = Mathf.Clamp(samples[i], -1.0f, 1.0f);
            short sample16 = (short)(s * 32767f);
            writer.Write(sample16);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Creates an in-memory Unity AudioClip from float PCM samples.
    /// </summary>
    public static AudioClip CreateAudioClip(float[] samples, int sampleRate, int channels, string clipName = "PhoneyVoiceClip")
    {
        int lengthSamples = samples.Length / Math.Max(1, channels);
        AudioClip clip = AudioClip.Create(clipName, lengthSamples, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
