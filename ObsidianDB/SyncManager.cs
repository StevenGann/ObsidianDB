using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

public class SyncManager
{
    private readonly ILogger<SyncManager> _logger = LoggerService.GetLogger<SyncManager>();
    private readonly FileSystemWatcher watcher;
    private readonly string vaultPath;
    private readonly HashSet<string> lockedPaths = new();
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

    internal void CommitNote(string id)
    {
        Note note = DB.GetFromId(id) ;
        lock (lockedPath)
        {            
            lockedPath = note.Path;
            Thread.Sleep(1);
            note.Save();
        }
        Thread.Sleep(1);
        lock (lockedPath) { lockedPath = ""; Thread.Sleep(1); }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (lockedPaths.Contains(e.FullPath)) return;

        _logger.LogInformation("Changed: {Path}", e.FullPath);
        _logger.LogDebug("Locked paths: {Paths}", string.Join(", ", lockedPaths));
        
        string value = System.IO.File.ReadAllText(e.FullPath);
        _logger.LogDebug("File contents: {Content}", value);

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

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (lockedPaths.Contains(e.FullPath)) return;

        _logger.LogInformation("Deleted: {Path}", e.FullPath);
        Console.WriteLine($"Deleted: {e.FullPath}");
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (lockedPaths.Contains(e.OldFullPath)) return;

        _logger.LogInformation("Renamed:\n    Old: {OldPath}\n    New: {NewPath}", e.OldFullPath, e.FullPath);
        Console.WriteLine($"Renamed:");
        Console.WriteLine($"    Old: {e.OldFullPath}");
        Console.WriteLine($"    New: {e.FullPath}");
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "File system watcher error occurred");
    }
}