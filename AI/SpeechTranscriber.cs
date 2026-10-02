using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Phoney.Audio;
using Phoney.Vault;
using Whisper.net;

namespace Phoney.AI;

public class SpeechTranscriber : IDisposable
{
    private static SpeechTranscriber? _instance;
    public static SpeechTranscriber Instance => _instance ??= new SpeechTranscriber();

    private readonly ConcurrentQueue<PendingSegment> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _isInitialized;
    public bool IsInitialized => _isInitialized;

    private bool _isDownloading;

    private readonly string _modelFolder;
    private readonly string _modelPath;

    public struct PendingSegment
    {
        public ulong PlayerSteamId;
        public string PlayerName;
        public float[] RawSamples;
        public int SampleRate;
        public int Channels;
        public float Timestamp;
        public Action<RecordedClip>? OnTranscribed;
    }

    public SpeechTranscriber()
    {
        string assemblyFolder = Path.GetDirectoryName(typeof(SpeechTranscriber).Assembly.Location) ?? ".";
        _modelFolder = Path.Combine(assemblyFolder, "models");
        _modelPath = Path.Combine(_modelFolder, "ggml-tiny.bin");

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void InitializeAsync()
    {
        Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(_modelPath))
                {
                    await DownloadModelAsync();
                }

                if (File.Exists(_modelPath))
                {
                    PhoneyPlugin.Logger.LogInfo($"[Transcriber] Loading Whisper model from: {_modelPath}");
                    _factory = WhisperFactory.FromPath(_modelPath);
                    _processor = _factory.CreateBuilder()
                        .WithLanguage("en")
                        .WithThreads(2)
                        .Build();
                    _isInitialized = true;
                    PhoneyPlugin.Logger.LogInfo("[Transcriber] Whisper AI model initialized successfully!");
                }
            }
            catch (Exception ex)
            {
                PhoneyPlugin.Logger.LogError($"[Transcriber] Failed to initialize Whisper model: {ex}");
            }
        });
    }

    private async Task DownloadModelAsync()
    {
        if (_isDownloading) return;
        _isDownloading = true;

        try
        {
            Directory.CreateDirectory(_modelFolder);
            PhoneyPlugin.Logger.LogInfo("[Transcriber] Downloading lightweight Whisper tiny model (~75MB)...");

            string url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            string tempPath = _modelPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            if (File.Exists(_modelPath)) File.Delete(_modelPath);
            File.Move(tempPath, _modelPath);

            PhoneyPlugin.Logger.LogInfo("[Transcriber] Model download complete!");
        }
        catch (Exception ex)
        {
            PhoneyPlugin.Logger.LogError($"[Transcriber] Model download error: {ex.Message}");
        }
        finally
        {
            _isDownloading = false;
        }
    }

    public void EnqueueAudioSegment(
        ulong playerSteamId,
        string playerName,
        float[] samples,
        int sampleRate,
        int channels,
        float timestamp,
        Action<RecordedClip>? onTranscribed = null)
    {
        _queue.Enqueue(new PendingSegment
        {
            PlayerSteamId = playerSteamId,
            PlayerName = playerName,
            RawSamples = samples,
            SampleRate = sampleRate,
            Channels = channels,
            Timestamp = timestamp,
            OnTranscribed = onTranscribed
        });
    }

    private async Task ProcessQueueAsync()
    {
        PhoneyPlugin.Logger.LogInfo("[Transcriber] Background worker started — waiting for audio segments.");
        int segmentsProcessed = 0;

        while (!_cts.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var seg))
            {
                segmentsProcessed++;
                PhoneyPlugin.Logger.LogInfo($"[Transcriber] Processing segment #{segmentsProcessed} for '{seg.PlayerName}' — {seg.RawSamples.Length} samples @ {seg.SampleRate}Hz x{seg.Channels}ch");

                try
                {
                    var clip = new RecordedClip(
                        seg.PlayerSteamId,
                        seg.PlayerName,
                        seg.RawSamples,
                        seg.SampleRate,
                        seg.Channels,
                        seg.Timestamp);

                    if (_isInitialized && _processor != null)
                    {
                        PhoneyPlugin.Logger.LogInfo($"[Transcriber] Whisper processing segment #{segmentsProcessed}...");

                        float[] mono16k = WavUtility.ResampleTo16kMono(seg.RawSamples, seg.SampleRate, seg.Channels);

                        // Ensure at least 0.5s of audio (8000 samples at 16kHz) for native stability
                        if (mono16k.Length < 8000)
                        {
                            Array.Resize(ref mono16k, 8000);
                        }

                        PhoneyPlugin.Logger.LogInfo($"[Transcriber] Resampled to {mono16k.Length} samples @ 16kHz mono. Running Whisper inference...");

                        var sb = new StringBuilder();

                        await foreach (var segmentData in _processor.ProcessAsync(mono16k, _cts.Token))
                        {
                            PhoneyPlugin.Logger.LogInfo($"[Transcriber] Whisper segment text: \"{segmentData.Text}\"");
                            sb.Append(segmentData.Text).Append(' ');
                        }

                        string transcript = sb.ToString().Trim();

                        if (!IsValidTranscript(transcript))
                        {
                            PhoneyPlugin.Logger.LogInfo($"[Transcriber] Ignored non-speech / Whisper hallucination: \"{transcript}\"");
                            clip.DisposeClip();
                            continue;
                        }

                        clip.Transcript = transcript;
                        clip.Intent = IntentClassifier.Classify(transcript, out bool hasProfanity, out bool isQuestion);
                        clip.ContainsProfanity = hasProfanity;
                        clip.IsQuestion        = isQuestion;

                        PhoneyPlugin.Logger.LogInfo($"[Transcriber] ✓ TRANSCRIBED: \"{transcript}\" | Intent={clip.Intent} Profanity={hasProfanity} Question={isQuestion}");
                    }
                    else
                    {
                        PhoneyPlugin.Logger.LogWarning($"[Transcriber] Whisper NOT initialized yet (isInitialized={_isInitialized}) — ignoring audio segment.");
                        clip.DisposeClip();
                        continue;
                    }

                    ClipVault.Instance.AddClip(clip);
                    PhoneyPlugin.Logger.LogInfo($"[Transcriber] Clip stored in vault. Total clips: {ClipVault.Instance.TotalClipCount}");
                    seg.OnTranscribed?.Invoke(clip);
                }
                catch (Exception ex)
                {
                    PhoneyPlugin.Logger.LogError($"[Transcriber] ERROR processing segment #{segmentsProcessed}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            else
            {
                await Task.Delay(100, _cts.Token);
            }
        }

        PhoneyPlugin.Logger.LogInfo("[Transcriber] Background worker shutting down.");
    }

    private static bool IsValidTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        string t = transcript.Trim();
        if (t.Length < 2) return false;

        // Fully enclosed in brackets or parentheses: [Music], (laughs), [coughing], [BLANK_AUDIO]
        if ((t.StartsWith("[") && t.EndsWith("]")) || (t.StartsWith("(") && t.EndsWith(")")))
            return false;

        // Strip out any bracketed tokens and see if meaningful alphanumeric text remains
        string stripped = System.Text.RegularExpressions.Regex.Replace(t, @"\[.*?\]|\(.*?\)", "").Trim();
        if (string.IsNullOrWhiteSpace(stripped) || stripped.Length < 2)
            return false;

        if (!stripped.Any(char.IsLetterOrDigit))
            return false;

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _processor?.Dispose();
        _factory?.Dispose();
        _cts.Dispose();
    }
}
