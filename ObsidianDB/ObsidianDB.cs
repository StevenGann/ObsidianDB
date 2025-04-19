using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

public class ObsidianDB
{
    private readonly ILogger<ObsidianDB> _logger = LoggerService.GetLogger<ObsidianDB>();
    public string VaultPath { get; set; }

    public List<Note> Notes { get; set; } = new();

    internal SyncManager syncManager;
    internal CallbackManager callbackManager;

    private static List<ObsidianDB> obsidianDBs = new();

    public static ObsidianDB? GetDatabaseInstance(string notePath)
    {
        foreach(ObsidianDB db in obsidianDBs)
        {
            if(notePath.ToUpperInvariant().Contains( db.VaultPath.ToUpperInvariant()))
            {
                return db;
            }
        }
        return null;
    }

    public ObsidianDB(string path)
    {
        VaultPath = path;
        syncManager = new(this);
        callbackManager = new(this);
        obsidianDBs.Add(this);
    }

    public IEnumerable<Note> GetNotes()
    {
        int index = 0;
        while (index < Notes.Count)
        {
            yield return Notes[index];
            index++;
        }
    }

    public void ScanNotes()
    {
        string[] files = Directory.GetFiles(VaultPath, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Found {Count} files", files.Length);

        foreach (string file in files)
        {
            try
            {
                Note note = new(file);
                Notes.Add(note);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading note {File}: {Message}", file, ex.Message);
            }
        }
    }

    public Note? GetFromId(string id)
    {
        foreach (Note note in Notes)
        {
            if (note.ID == id) { return note; }
        }

        return null;
    }

    public void Update()
    {
        syncManager.Tick();
        callbackManager.Tick();
    }
}
