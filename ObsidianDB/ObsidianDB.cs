using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    internal HyperVectorDB.HyperVectorDB? _vectorDB;
    private readonly object _lock = new object();
    private bool _disposed;

    /// <summary>
    /// Gets or sets the path to the Obsidian vault directory.
    /// This path is used as the root for all note operations and file system monitoring.
    /// </summary>
    /// <value>The absolute path to the Obsidian vault directory.</value>
    public string VaultPath { get; private set; }

    /// <summary>
    /// Gets the name of the vault, which is the last directory name in the VaultPath.
    /// </summary>
    public string Name => Path.GetFileName(VaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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

    // State variable for markdown preprocessing
    private static bool skippingBlock = false;

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
            foreach (ObsidianDB db in obsidianDBs)
            {
                if (notePath.ToUpperInvariant().Contains(db.VaultPath.ToUpperInvariant()))
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

        // TODO: Make the embedder configurable or self-contained.
        _vectorDB = new HyperVectorDB.HyperVectorDB(new HyperVectorDB.Embedder.LmStudio(), Name, 32);
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
    public void ScanNotes(bool vectorize = false)
    {
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Scanning vault {Vault}", Name);
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        string[] files = Directory.GetFiles(VaultPath, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Found {Count} files", files.Length);

        var newNotes = new List<Note>();
        var newNotesById = new Dictionary<string, Note>();

        // Measure file scanning time
        var scanStopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (string file in files)
        {
            try
            {
                syncManager.Active = false;
                Note note = new(file);
                syncManager.Active = true;
                newNotes.Add(note);
                newNotesById[note.ID] = note;
                _logger.LogInformation("Scanned note {Note}", note.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading note {File}: {Message}", file, ex.Message);
            }
        }
        scanStopwatch.Stop();
        _logger.LogInformation("File scanning completed in {ElapsedSeconds:F2}s", scanStopwatch.Elapsed.TotalSeconds);

        // Measure caching time
        var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            Notes = newNotes;
            _notesById.Clear();
            foreach (var kvp in newNotesById)
            {
                _notesById[kvp.Key] = kvp.Value;
            }
        }
        cacheStopwatch.Stop();
        _logger.LogInformation("Index caching completed in {ElapsedSeconds:F2}s", cacheStopwatch.Elapsed.TotalSeconds);

        if (vectorize)
        {
            _logger.LogInformation("Vectorizing vault {Vault}", Name);
            double fileCount = 0;
            double vectorizeTotalSeconds = 0;
            foreach (string file in files)
            {
                try
                {
                    var vectorizeStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _logger.LogInformation("Vectorizing {File}", file);
                    _vectorDB!.IndexDocumentFile(file, CustomPreprocessor, CustomPostprocessor);
                    vectorizeStopwatch.Stop();

                    double progress = (fileCount + 1) / files.Length;
                    vectorizeTotalSeconds += vectorizeStopwatch.Elapsed.TotalSeconds;
                    TimeSpan remainingTime = TimeSpan.FromSeconds((files.Length - (fileCount + 1)) * (vectorizeTotalSeconds / (fileCount + 1)));

                    _logger.LogInformation("Vectorized {File} in {ElapsedSeconds:F2}s", file, vectorizeStopwatch.Elapsed.TotalSeconds);

                    _logger.LogInformation("{FileCount}/{FileTotal} indexed, ETA {Hours:00}:{Minutes:00}:{Seconds:00}",
                        (int)fileCount + 1, files.Length,
                        remainingTime.Hours, remainingTime.Minutes, remainingTime.Seconds);

                    if (((int)fileCount) % 5 == 0)
                    {
                        _vectorDB!.Save();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error vectorizing note {File}: {Message}", file, ex.Message);
                }
                fileCount++;
            }

            try
            {
                _vectorDB!.Save();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving vector database: {Message}", ex.Message);
            }
        }

        totalStopwatch.Stop();
        _logger.LogInformation("Total vault scan completed in {ElapsedMinutes:F2}m", totalStopwatch.Elapsed.TotalMinutes);
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
    /// Retrieves a note from the database by its full file path.
    /// </summary>
    /// <param name="path">The full path to the note file.</param>
    /// <returns>The Note object with the specified path, or null if not found.</returns>
    public Note? GetFromPath(string path)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path));

        lock (_lock)
        {
            return Notes.FirstOrDefault(note => note.Path.Equals(path, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Retrieves a note from the database by its title.
    /// </summary>
    /// <param name="title">The title of the note to find.</param>
    /// <returns>The Note object with the specified title, or null if not found.</returns>
    public Note? GetFromTitle(string title)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentNullException(nameof(title));

        lock (_lock)
        {
            return Notes.FirstOrDefault(note => note.Title?.Equals(title, StringComparison.OrdinalIgnoreCase) == true);
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

        try
        {
            syncManager.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating filesystem sync: {Message}", ex.Message);
        }

        try
        {
            callbackManager.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing callbacks: {Message}", ex.Message);
        }

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

    /// <summary>
    /// Custom preprocessor for document indexing that handles markdown-specific formatting
    /// Filters out:
    /// - Empty lines
    /// - YAML frontmatter
    /// - Code blocks
    /// - Metadata lines
    /// - Annotation lines
    /// - Index links
    /// </summary>
    private static string? CustomPreprocessor(string line, string? path, int? lineNumber)
    {

        if (path != null && path.ToUpperInvariant().EndsWith(".MD"))
        {
            if (lineNumber == 0)
            {
                skippingBlock = false;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }
            // Skip YAML frontmatter
            else if (line.Contains("---"))
            {
                skippingBlock = !skippingBlock;
                return null;
            }
            // Handle code blocks
            else if (line.Contains("```"))
            {
                skippingBlock = !skippingBlock;
                return null;
            }
            // Skip metadata and special markdown lines
            else if (line.EndsWith("aliases: ") ||
                    line.Contains("date created:") ||
                    line.Contains("date modified:") ||
                    (line.EndsWith(":") && !line.StartsWith("#")) ||
                    line.StartsWith('#') && !line.Contains("# "))
            {
                return null;
            }

            // Skip annotation lines
            else if (line.Contains("%%")) { return null; }

            // Skip index links
            else if (line.Trim().StartsWith("[[") && line.Trim().EndsWith("]]")) { return null; }

            // Skip content within blocks
            if (skippingBlock) { return null; }
        }

        return line.Trim();
    }

    /// <summary>
    /// Custom postprocessor that adds file path and line number information to each line
    /// </summary>
    private static string? CustomPostprocessor(string line, string? path, int? lineNumber)
    {
        if (path == null) { return line; }
        ObsidianDB db = ObsidianDB.GetDatabaseInstance(path);
        Note note = db.GetFromPath(path);
        return $"{note.ID}|{lineNumber}";
    }

    /// <summary>
    /// Creates a new note in the vault with the specified content and metadata.
    /// </summary>
    /// <param name="relativePath">The path of the note relative to the vault directory.</param>
    /// <param name="body">The main content of the note.</param>
    /// <param name="tags">Optional list of tags to add to the note.</param>
    /// <param name="frontmatter">Optional dictionary of additional frontmatter key-value pairs.</param>
    /// <returns>The newly created Note object.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is invalid or outside the vault directory.</exception>
    /// <exception cref="IOException">Thrown when there's an error creating the file.</exception>
    /// <remarks>
    /// This method:
    /// - Creates the necessary directories
    /// - Creates the note file with YAML frontmatter
    /// - Adds required metadata (GUID, hash)
    /// - Adds the note to the database's collection
    /// - Notifies subscribers of the new note
    /// </remarks>
    public Note AddNote(string relativePath, string body, List<string>? tags = null, Dictionary<string, List<string>?>? frontmatter = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ObsidianDB));

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be empty", nameof(relativePath));

        // Normalize the path to use system directory separators
        string normalizedPath = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
        string fullPath = System.IO.Path.Combine(VaultPath, normalizedPath);

        // Ensure the path is within the vault
        if (!fullPath.StartsWith(VaultPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Note path must be within the vault directory", nameof(relativePath));

        // Create the directory if it doesn't exist
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Prepare the frontmatter
        var noteFrontmatter = new Dictionary<string, List<string>?>();

        // Add provided frontmatter
        if (frontmatter != null)
        {
            foreach (var kvp in frontmatter)
            {
                noteFrontmatter[kvp.Key] = kvp.Value;
            }
        }

        // Add tags if provided
        if (tags != null && tags.Count > 0)
        {
            noteFrontmatter["tags"] = tags;
        }

        // Add creation and modification dates
        string timestamp = DateTime.Now.ToString("dddd, MMMM d yyyy, h:M:ss tt");
        noteFrontmatter["date created"] = [timestamp];
        noteFrontmatter["date modified"] = [timestamp];

        // Build the initial file content
        var content = new List<string>
        {
            "---"
        };

        // Add frontmatter
        foreach (var kvp in noteFrontmatter)
        {
            if (kvp.Value == null || kvp.Value.Count == 0)
            {
                content.Add($"{kvp.Key}:");
            }
            else if (kvp.Value.Count == 1)
            {
                content.Add($"{kvp.Key}: {kvp.Value[0]}");
            }
            else
            {
                content.Add($"{kvp.Key}:");
                foreach (var value in kvp.Value)
                {
                    content.Add($"  - {value}");
                }
            }
        }

        content.Add("---");
        content.Add(body);

        // Write the initial file
        File.WriteAllLines(fullPath, content);

        // Create and initialize the note
        Note note = new(fullPath);

        lock (_lock)
        {
            Notes.Add(note);
            _notesById[note.ID] = note;
        }

        // Notify subscribers
        callbackManager.EnqueueUpdate(note.ID);

        _logger.LogInformation("Created new note: {Path}", fullPath);
        return note;
    }

    /// <summary>
    /// Serializes the entire ObsidianDB instance and its notes to a JSON file.
    /// This method is useful for debugging and inspecting the state of the database.
    /// </summary>
    /// <param name="path">The path where the JSON file should be saved.</param>
    /// <remarks>
    /// The JSON output includes:
    /// - Basic database information (vault path, name)
    /// - All notes with their metadata and content
    /// - Internal and external links
    /// - Tags and frontmatter
    /// The output can be large for vaults with many notes.
    /// </remarks>
    public void ToJson(string path)
    {
        try
        {
            _logger.LogInformation("Serializing database to JSON: {Path}", path);

            // Create a DTO that includes only the data we want to serialize
            var dbDto = new
            {
                VaultPath,
                Name,
                Notes = Notes.Select(note => new
                {
                    note.Title,
                    note.Path,
                    note.RelativePath,
                    note.ID,
                    note.Hash,
                    note.Frontmatter,
                    note.Tags,
                    InternalLinks = note.InternalLinks.Select(link => new
                    {
                        link.Title,
                        link.DisplayText,
                        link.NoteId
                    }).ToList(),
                    ExternalLinks = note.ExternalLinks.Select(link => new
                    {
                        link.DisplayText,
                        link.Url
                    }).ToList(),
                    note.Body,
                    note.PlaintextBody,
                    Backlinks = note.Backlinks.Select(backlink => new
                    {
                        backlink.Title,
                        backlink.DisplayText,
                        backlink.SourceNoteId
                    }).ToList()
                }).ToList()
            };

            // Configure JSON serialization options
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            // Create directory if it doesn't exist
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write to a temporary file first
            string tempPath = path + ".tmp";
            string json = JsonSerializer.Serialize(dbDto, options);
            File.WriteAllText(tempPath, json);

            // Move the temporary file to the final location
            File.Move(tempPath, path, true);

            _logger.LogInformation("Database successfully serialized to JSON: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serializing database to JSON: {Message}", ex.Message);
            throw;
        }
    }
}
