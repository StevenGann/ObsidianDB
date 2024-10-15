using System;
using System.IO;
using System.Threading;

namespace ObsidianDB;

public class SyncManager
{
    private FileSystemWatcher? watcher;
    private ObsidianDB? DB;

    string lockedPath = "";
    public SyncManager(ObsidianDB db)
    {
        DB = db;
        watcher = new FileSystemWatcher(db.VaultPath);

        watcher.NotifyFilter = NotifyFilters.Attributes
                                 | NotifyFilters.CreationTime
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.FileName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size;

        watcher.Changed += OnChanged;
        watcher.Created += OnCreated;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        watcher.Filter = "*.md";
        watcher.IncludeSubdirectories = true;
        watcher.EnableRaisingEvents = true;
    }

    public void Tick()
    {

    }

    internal void ReloadNote(string path)
    {
        lock (lockedPath)
        {
            lockedPath = path;
            Thread.Sleep(1);
            Note temp = new(path);
            Thread.Sleep(1);
            Note note = DB!.GetFromId(temp.ID)!;
            note.Reload();
        }
        Thread.Sleep(1);
        lock (lockedPath) { lockedPath = ""; Thread.Sleep(1); }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed)
        {
            return;
        }
        if (e.FullPath == lockedPath) { return; }
        Console.WriteLine($"Changed: {e.FullPath}");
        Console.WriteLine(lockedPath);
        ReloadNote(e.FullPath);
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        string value = $"Created: {e.FullPath}";
        Console.WriteLine(value);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Console.WriteLine($"Deleted: {e.FullPath}");

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Console.WriteLine($"Renamed:");
        Console.WriteLine($"    Old: {e.OldFullPath}");
        Console.WriteLine($"    New: {e.FullPath}");
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        Utilities.PrintException(e.GetException());
}