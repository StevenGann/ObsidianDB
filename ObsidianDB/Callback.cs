namespace ObsidianDB;

public static class CallbackManager
{
    public delegate void Callback(Note note);

    private static Dictionary<string, List<Callback>> subscriptions = new();
    public static List<string> updates = new();

    public static void Subscribe(string id, Callback callback)
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

    internal static void Tick(ObsidianDB db)
    {
        foreach (string id in updates)
        {
            if (subscriptions.ContainsKey(id))
            {

                Note? note = db.GetFromId(id);
                if (note != null)
                {
                    Console.WriteLine($"Triggering {subscriptions[id].Count} callback(s) for {note.Title}");
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

    internal static void EnqueueUpdate(string id)
    {
        if (!subscriptions.ContainsKey(id)) { return; }
        if (updates.Contains(id)) { return; }

        updates.Add(id);
    }
}