using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

public class CallbackManager
{
    private readonly ILogger<CallbackManager> _logger = LoggerService.GetLogger<CallbackManager>();
    public delegate void Callback(Note note);

    private Dictionary<string, List<Callback>> subscriptions = new();
    public List<string> updates = new();

    internal ObsidianDB DB;

    public CallbackManager(ObsidianDB db)
    {
        DB = db;
    }

    public void Subscribe(string id, Callback callback)
    {
        if (subscriptions.ContainsKey(id))
        {
            subscriptions[id].Add(callback);
        }
        else
        {
            subscriptions.Add(id, [callback]);
        }
    }

    internal void Tick()
    {
        foreach (string id in updates)
        {
            if (subscriptions.ContainsKey(id))
            {

                Note? note = DB.GetFromId(id);
                if (note != null)
                {
                    _logger.LogInformation("Triggering {Count} callback(s) for {Title}", subscriptions[id].Count, note.Title);
                    foreach (Callback callback in subscriptions[id])
                    {
                        callback(note);
                    }
                }
                else
                {
                    subscriptions.Remove(id);
                }

            }
        }
    }

    internal void EnqueueUpdate(string id)
    {
        if (!subscriptions.ContainsKey(id)) { return; }
        if (updates.Contains(id)) { return; }

        updates.Add(id);
    }

    public void TriggerCallbacks(Note note)
    {
        if (subscriptions.TryGetValue(note.ID, out var callbacks))
        {
            _logger.LogInformation("Triggering {Count} callback(s) for {Title}", callbacks.Count, note.Title);
            foreach (var callback in callbacks)
            {
                callback(note);
            }
        }
    }
}