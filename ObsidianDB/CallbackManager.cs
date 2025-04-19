using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

/// <summary>
/// Manages callbacks for note updates in ObsidianDB.
/// This class provides a subscription-based system for notifying interested parties
/// when specific notes are modified or updated.
/// </summary>
public class CallbackManager : IDisposable
{
    private readonly ILogger<CallbackManager> _logger = LoggerService.GetLogger<CallbackManager>();
    private readonly object _lock = new object();
    private bool _disposed;
    
    /// <summary>
    /// Delegate type for note update callbacks.
    /// </summary>
    public delegate void Callback(Note note);

    /// <summary>
    /// Dictionary mapping note IDs to their subscribed callbacks.
    /// </summary>
    private readonly Dictionary<string, List<Callback>> _subscriptions = new();

    /// <summary>
    /// Queue of note IDs that have pending updates to be processed.
    /// </summary>
    private readonly List<string> _updates = new();

    /// <summary>
    /// Reference to the parent ObsidianDB instance.
    /// </summary>
    internal ObsidianDB DB;

    /// <summary>
    /// Initializes a new instance of the CallbackManager.
    /// </summary>
    /// <param name="db">The ObsidianDB instance this manager belongs to.</param>
    public CallbackManager(ObsidianDB db)
    {
        DB = db;
    }

    /// <summary>
    /// Subscribes a callback to be notified when a specific note is updated.
    /// </summary>
    /// <param name="id">The ID of the note to subscribe to.</param>
    /// <param name="callback">The callback function to be invoked when the note is updated.</param>
    public void Subscribe(string id, Callback callback)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentNullException(nameof(id));
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));

        lock (_lock)
        {
            if (_subscriptions.ContainsKey(id))
            {
                _subscriptions[id].Add(callback);
            }
            else
            {
                _subscriptions.Add(id, [callback]);
            }
        }
    }

    /// <summary>
    /// Unsubscribes a callback from a specific note.
    /// </summary>
    /// <param name="id">The ID of the note to unsubscribe from.</param>
    /// <param name="callback">The callback function to remove.</param>
    /// <returns>True if the callback was found and removed, false otherwise.</returns>
    public bool Unsubscribe(string id, Callback callback)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentNullException(nameof(id));
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));

        lock (_lock)
        {
            if (_subscriptions.TryGetValue(id, out var callbacks))
            {
                bool removed = callbacks.Remove(callback);
                if (callbacks.Count == 0)
                {
                    _subscriptions.Remove(id);
                }
                return removed;
            }
            return false;
        }
    }

    /// <summary>
    /// Processes all pending note updates by triggering their associated callbacks.
    /// This method is called periodically to handle batched updates.
    /// </summary>
    internal void Tick()
    {
        List<string> updatesToProcess;
        lock (_lock)
        {
            updatesToProcess = new List<string>(_updates);
            _updates.Clear();
        }

        foreach (string id in updatesToProcess)
        {
            List<Callback> callbacks;
            lock (_lock)
            {
                if (!_subscriptions.ContainsKey(id))
                    continue;
                callbacks = new List<Callback>(_subscriptions[id]);
            }

            Note? note = DB.GetFromId(id);
            if (note != null)
            {
                _logger.LogInformation("Triggering {Count} callback(s) for {Title}", callbacks.Count, note.Title);
                foreach (Callback callback in callbacks)
                {
                    try
                    {
                        callback(note);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing callback for note {Title}", note.Title);
                    }
                }
            }
            else
            {
                // Remove subscription if note no longer exists
                lock (_lock)
                {
                    _subscriptions.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Enqueues a note ID for update processing.
    /// This method is called when a note is modified to schedule callback execution.
    /// </summary>
    /// <param name="id">The ID of the note that was updated.</param>
    internal void EnqueueUpdate(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentNullException(nameof(id));

        lock (_lock)
        {
            if (!_subscriptions.ContainsKey(id)) { return; }
            if (_updates.Contains(id)) { return; }

            _updates.Add(id);
        }
    }

    /// <summary>
    /// Immediately triggers all callbacks associated with a specific note.
    /// This method bypasses the update queue and processes callbacks immediately.
    /// </summary>
    /// <param name="note">The note that triggered the callbacks.</param>
    public void TriggerCallbacks(Note note)
    {
        if (note == null)
            throw new ArgumentNullException(nameof(note));

        List<Callback> callbacks;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(note.ID, out var callbackList))
                return;
            callbacks = new List<Callback>(callbackList);
        }

        _logger.LogInformation("Triggering {Count} callback(s) for {Title}", callbacks.Count, note.Title);
        foreach (var callback in callbacks)
        {
            try
            {
                callback(note);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing callback for note {Title}", note.Title);
            }
        }
    }

    /// <summary>
    /// Releases all resources used by the CallbackManager instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the CallbackManager instance and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            lock (_lock)
            {
                _subscriptions.Clear();
                _updates.Clear();
            }
        }

        _disposed = true;
    }

    /// <summary>
    /// Finalizes the CallbackManager instance.
    /// </summary>
    ~CallbackManager()
    {
        Dispose(false);
    }
}