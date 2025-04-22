using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

/// <summary>
/// Manages synchronization between the Obsidian vault and the database.
/// This class handles file system events and ensures proper synchronization
/// between the file system and the database state.
/// </summary>
public class SyncManager : IDisposable
{
    private readonly ILogger<SyncManager> _logger = LoggerService.GetLogger<SyncManager>();
    private readonly FileSystemWatcher watcher;
    private readonly ObsidianDB DB;
    private readonly HashSet<string> lockedPaths = new();
    private string lockedPath = string.Empty;
    private bool _disposed;

    public bool Active = false;

    /// <summary>
    /// Initializes a new instance of the SyncManager class.
    /// </summary>
    /// <param name="db">The ObsidianDB instance to synchronize with.</param>
    /// <exception cref="ArgumentNullException">Thrown when db is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the vault path is invalid.</exception>
    /// <remarks>
    /// Sets up a FileSystemWatcher to monitor the Obsidian vault directory
    /// for changes to markdown files. The watcher is configured to track
    /// various file system events including changes, creations, deletions,
    /// and renames.
    /// </remarks>
    public SyncManager(ObsidianDB db)
    {
        DB = db ?? throw new ArgumentNullException(nameof(db));
        
        if (string.IsNullOrWhiteSpace(db.VaultPath))
        {
            throw new ArgumentException("Vault path cannot be null or empty", nameof(db));
        }

        if (!Directory.Exists(db.VaultPath))
        {
            throw new ArgumentException($"Vault path does not exist: {db.VaultPath}", nameof(db));
        }

        watcher = new FileSystemWatcher(db.VaultPath)
        {
            NotifyFilter = NotifyFilters.Attributes
                         | NotifyFilters.CreationTime
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size,
            Filter = "*.md",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnCreated;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        _logger.LogInformation("SyncManager initialized for vault: {VaultPath}", db.VaultPath);
        Active = true;
    }

    /// <summary>
    /// Performs periodic synchronization tasks.
    /// </summary>
    /// <remarks>
    /// This method is called periodically to handle any pending
    /// synchronization operations.
    /// </remarks>
    public void Tick()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Reloads a note from the file system into the database.
    /// </summary>
    /// <param name="path">The full path to the note file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the note file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note cannot be reloaded.</exception>
    /// <remarks>
    /// This method is thread-safe and uses locking to prevent
    /// concurrent modifications to the same note. It creates a
    /// temporary note to get the ID, then reloads the actual note
    /// from the database.
    /// </remarks>
    internal void ReloadNote(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Note file not found: {path}", path);
        }

        try
        {
            lock (lockedPath)
            {
                lockedPath = path;
                try
                {
                    Note temp = new(path);
                    Note? note = DB.GetFromId(temp.ID);
                    
                    if (note == null)
                    {
                        throw new InvalidOperationException($"Note with ID {temp.ID} not found in database");
                    }

                    note.Reload();
                    _logger.LogInformation("Successfully reloaded note: {Path}", path);
                }
                finally
                {
                    lockedPath = string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is not ArgumentNullException && ex is not FileNotFoundException)
        {
            _logger.LogError(ex, "Failed to reload note: {Path}", path);
            throw new InvalidOperationException($"Failed to reload note: {path}", ex);
        }
    }

    /// <summary>
    /// Commits a note's changes to the file system.
    /// </summary>
    /// <param name="id">The ID of the note to commit.</param>
    /// <exception cref="ArgumentNullException">Thrown when id is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note cannot be found or committed.</exception>
    /// <remarks>
    /// This method is thread-safe and uses locking to prevent
    /// concurrent modifications to the same note. It retrieves
    /// the note from the database and saves it to the file system.
    /// </remarks>
    internal void CommitNote(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentNullException(nameof(id));
        }

        try
        {
            Note? note = DB.GetFromId(id);
            if (note == null)
            {
                throw new InvalidOperationException($"Note with ID {id} not found in database");
            }

            lock (lockedPath)
            {
                lockedPath = note.Path;
                try
                {
                    note.Save();
                    _logger.LogInformation("Successfully committed note: {Id} at {Path}", id, note.Path);
                }
                finally
                {
                    lockedPath = string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is not ArgumentNullException)
        {
            _logger.LogError(ex, "Failed to commit note: {Id}", id);
            throw new InvalidOperationException($"Failed to commit note: {id}", ex);
        }
    }

    /// <summary>
    /// Handles file change events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the file change.</param>
    /// <remarks>
    /// This method is called when a file in the vault is modified.
    /// It checks if the file is locked and if the change type is
    /// appropriate before triggering a reload of the note.
    /// </remarks>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnChanged");
            return;
        }

        if (lockedPaths.Contains(e.FullPath))
        {
            _logger.LogDebug("Ignoring change to locked file: {Path}", e.FullPath);
            return;
        }

        try
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
            {
                _logger.LogDebug("Ignoring non-change event: {ChangeType} for {Path}", e.ChangeType, e.FullPath);
                return;
            }

            if (e.FullPath == lockedPath)
            {
                _logger.LogDebug("Ignoring change to currently locked path: {Path}", e.FullPath);
                return;
            }

            _logger.LogInformation("File changed: {Path}", e.FullPath);
            string fileContent = File.ReadAllText(e.FullPath);
            _logger.LogDebug("File contents: {Content}", fileContent);

            ReloadNote(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file change for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file creation events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the created file.</param>
    /// <remarks>
    /// This method is called when a new markdown file is created in the vault.
    /// Currently logs the creation event but does not perform additional actions.
    /// </remarks>
    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnCreated");
            return;
        }

        try
        {
            _logger.LogInformation("File created: {Path}", e.FullPath);
            // TODO: Implement note creation in database
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file creation for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file deletion events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the deleted file.</param>
    /// <remarks>
    /// This method is called when a markdown file is deleted from the vault.
    /// It checks if the deleted file is locked before processing the deletion.
    /// </remarks>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null FileSystemEventArgs in OnDeleted");
            return;
        }

        if (lockedPaths.Contains(e.FullPath))
        {
            _logger.LogDebug("Ignoring deletion of locked file: {Path}", e.FullPath);
            return;
        }

        try
        {
            _logger.LogInformation("File deleted: {Path}", e.FullPath);
            // TODO: Implement note deletion from database
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file deletion for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// Handles file rename events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the renamed file.</param>
    /// <remarks>
    /// This method is called when a markdown file is renamed in the vault.
    /// It checks if the renamed file is locked before processing the rename.
    /// </remarks>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if(!Active){return;}
        if (e == null)
        {
            _logger.LogWarning("Received null RenamedEventArgs in OnRenamed");
            return;
        }

        if (lockedPaths.Contains(e.OldFullPath))
        {
            _logger.LogDebug("Ignoring rename of locked file: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            return;
        }

        try
        {
            _logger.LogInformation("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            // TODO: Implement note rename in database
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file rename from {OldPath} to {NewPath}", e.OldFullPath, e.FullPath);
        }
    }

    /// <summary>
    /// Handles error events from the FileSystemWatcher.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The event data containing information about the error.</param>
    /// <remarks>
    /// This method logs any errors that occur in the FileSystemWatcher
    /// using the configured logger.
    /// </remarks>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "File system watcher error occurred");
    }

    /// <summary>
    /// Releases all resources used by the SyncManager instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the SyncManager instance and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            watcher?.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Finalizes the SyncManager instance.
    /// </summary>
    ~SyncManager()
    {
        Dispose(false);
    }
}