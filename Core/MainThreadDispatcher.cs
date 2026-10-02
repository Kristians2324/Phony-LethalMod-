using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Phoney.Core;

/// <summary>
/// Singleton MonoBehaviour that safely marshals actions from background threads
/// (audio thread, Task pool) back to the Unity main thread by draining a queue
/// during Update().
/// </summary>
public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher? _instance;
    private static readonly ConcurrentQueue<Action> _queue = new();

    public static void Initialize()
    {
        if (_instance != null) return;
        var go = new GameObject("[Phoney] MainThreadDispatcher");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<MainThreadDispatcher>();
    }

    /// <summary>
    /// Enqueue an action to be executed on the Unity main thread during the next Update().
    /// Safe to call from any thread.
    /// </summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;
        _queue.Enqueue(action);
    }

    public static void DrainQueue()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PhoneyPlugin.Logger.LogError($"[MainThreadDispatcher] Exception in queued action: {ex}");
            }
        }
    }

    private void Update()
    {
        DrainQueue();
    }

    private void OnDestroy()
    {
        _instance = null;
    }
}
