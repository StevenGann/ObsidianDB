using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;

namespace ObsidianDB;

/// <summary>
/// Represents a database instance for managing an Obsidian vault.
/// This class provides the core functionality for interacting with an Obsidian vault,
/// including note scanning, synchronization, and callback management.
/// </summary>
/// <remarks>
/// The ObsidianDB class serves as the main entry point for interacting with an Obsidian vault programmatically.
/// It maintains a collection of notes, manages file system synchronization, and provides a callback system
/// for monitoring changes to notes. Each instance is associated with a specific vault path and maintains
/// its own state independently.
/// </remarks>
public class ObsidianDB : IDisposable
{
    private readonly ILogger<ObsidianDB> _logger = LoggerService.GetLogger<ObsidianDB>();
    private readonly object _lock = new object();
    private bool _disposed;

    /// <summary>
    /// Gets or sets the path to the Obsidian vault directory.
    /// This path is used as the root for all note operations and file system monitoring.
    /// </summary>
    /// <value>The absolute path to the Obsidian vault directory.</value>
    public string VaultPath { get; set; }

    /// <summary>
    /// Gets or sets the collection of notes in the vault.
    /// This list is populated during scanning and updated through synchronization.
    /// </summary>
    /// <value>A list of Note objects representing the notes in the vault.</value>
    public List<Note> Notes { get; private set; } = new();

    /// <summary>
    /// Dictionary for fast note lookup by ID.
    /// </summary>
    private readonly Dictionary<string, Note> _notesById = new();

    /// <summary>
    /// Manages file system synchronization for the vault.
    /// This internal component handles monitoring file changes and keeping the database in sync.
    /// </summary>
    internal SyncManager syncManager;

    /// <summary>
    /// Manages callbacks for note updates.
    /// This internal component handles notification of changes to subscribed parties.
    /// </summary>
    internal CallbackManager callbackManager;

    private static readonly List<ObsidianDB> obsidianDBs = new();
    private static readonly object _staticLock = new object();

    /// <summary>
    /// Retrieves an existing ObsidianDB instance associated with the given note path.
    /// </summary>
    /// <param name="notePath">The path to a note file within a vault.</param>
    /// <returns>
    /// The ObsidianDB instance associated with the vault containing the note,
    /// or null if no matching instance is found.
    /// </returns>
    /// <remarks>
    /// This method searches through all active ObsidianDB instances to find one
    /// whose vault path contains the given note path. The comparison is case-insensitive.
    /// </remarks>
    public static ObsidianDB? GetDatabaseInstance(string notePath)
    {
        lock (_staticLock)
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
    }

    /// <summary>
    /// Initializes a new instance of the ObsidianDB class for the specified vault path.
    /// </summary>
    /// <param name="path">The path to the Obsidian vault directory.</param>
    /// <remarks>
    /// This constructor:
    /// - Sets up the vault path
    /// - Initializes the sync manager for file system monitoring
    /// - Initializes the callback manager for change notifications
    /// - Registers the instance in the global collection
    /// </remarks>
    public ObsidianDB(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path));

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Vault path does not exist: {path}");

        VaultPath = path;
        syncManager = new(this);
        callbackManager = new(this);

        lock (_staticLock)
        {
            obsidianDBs.Add(this);
        }
    }

    /// <summary>
    /// Gets an enumerable collection of all notes in the vault.
    /// </summary>
    /// <returns>An enumerable collection of Note objects.</returns>
    /// <remarks>
    /// This method provides a safe way to iterate over all notes in the vault.
    /// The implementation uses yield return to provide efficient enumeration.
    /// </remarks>
    public IEnumerable<Note> GetNotes()
    {
        lock (_lock)
        {
            int index = 0;
            while (index < Notes.Count)
            {
                yield return Notes[index];
                index++;
            }
        }
    }

    /// <summary>
    /// Scans the vault directory for markdown files and loads them into the database.
    /// </summary>
    /// <remarks>
    /// This method:
    /// - Recursively searches for all .md files in the vault directory
    /// - Creates Note objects for each file found
    /// - Logs the number of files found and any errors encountered
    /// - Handles exceptions gracefully, logging errors but continuing with other files
    /// </remarks>
    public void ScanNotes()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        string[] files = Directory.GetFiles(VaultPath, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Found {Count} files", files.Length);

        var newNotes = new List<Note>();
        var newNotesById = new Dictionary<string, Note>();

        foreach (string file in files)
        {
            try
            {
                Note note = new(file);
                newNotes.Add(note);
                newNotesById[note.ID] = note;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading note {File}: {Message}", file, ex.Message);
            }
        }

        lock (_lock)
        {
            Notes = newNotes;
            _notesById.Clear();
            foreach (var kvp in newNotesById)
            {
                _notesById[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Retrieves a note from the database by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the note to retrieve.</param>
    /// <returns>The Note object with the specified ID, or null if not found.</returns>
    /// <remarks>
    /// This method uses a dictionary for O(1) lookup performance.
    /// </remarks>
    public Note? GetFromId(string id)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        lock (_lock)
        {
            return _notesById.TryGetValue(id, out var note) ? note : null;
        }
    }

    /// <summary>
    /// Updates the database state by processing pending synchronization and callback events.
    /// </summary>
    /// <remarks>
    /// This method should be called periodically to ensure the database stays in sync
    /// with the file system and to process any pending callbacks. It:
    /// - Processes file system changes through the sync manager
    /// - Executes any pending callbacks through the callback manager
    /// </remarks>
    public void Update()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        syncManager.Tick();
        callbackManager.Tick();
    }

    /// <summary>
    /// Releases all resources used by the ObsidianDB instance.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the ObsidianDB instance and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            lock (_staticLock)
            {
                obsidianDBs.Remove(this);
            }

            syncManager?.Dispose();
            callbackManager?.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Finalizes the ObsidianDB instance.
    /// </summary>
    ~ObsidianDB()
    {
        Dispose(false);
    }
}
