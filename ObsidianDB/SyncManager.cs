using System;
using System.IO;

namespace ObsidianDB;

public class SyncManager
{
    private FileSystemWatcher? watcher;
    private ObsidianDB? DB;
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

    internal void ResyncNote(string path)
    {
        throw new NotImplementedException();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType != WatcherChangeTypes.Changed)
        {
            ResyncNote(e.FullPath);
            return;
        }
        Console.WriteLine($"Changed: {e.FullPath}");

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