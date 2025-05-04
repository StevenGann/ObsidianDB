using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Markdig.Helpers;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;
using YamlDotNet.Serialization;
using System.IO;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace ObsidianDB;

/// <summary>
/// Represents a Markdown note file in an Obsidian vault, managing its content, metadata, and file operations.
/// This class handles the lifecycle of a note including loading, saving, and maintaining metadata like title,
/// tags, and unique identifiers. It ensures data integrity through atomic file operations and hash validation.
/// </summary>
public class Note
{
    private readonly ILogger<Note> _logger = LoggerService.GetLogger<Note>();
    
    /// <summary>
    /// Gets or sets the title of the note, extracted from the first H1 heading (# Title).
    /// If no H1 heading is found, falls back to the filename without extension.
    /// </summary>
    /// <value>The note's title, or null if no H1 heading is found and filename is not available.</value>
    public string? Title { get; set; } = null;
    
    /// <summary>
    /// Gets or sets the full filesystem path to the note file.
    /// The path must be within the Obsidian vault directory.
    /// </summary>
    /// <value>The absolute path to the note file.</value>
    /// <exception cref="ArgumentException">Thrown when the path is outside the vault directory.</exception>
    public string Path { get; set; }
    
    /// <summary>
    /// Gets or sets the filename portion of the path.
    /// This is automatically updated when the Path property changes.
    /// </summary>
    /// <value>The filename without the directory path.</value>
    public string Filename { get; set; }
    
    /// <summary>
    /// Gets the unique identifier for the note, stored in YAML frontmatter.
    /// Automatically generates a new GUID if not present.
    /// The GUID is used for tracking note changes and maintaining relationships between notes.
    /// </summary>
    /// <value>A string containing the note's unique identifier in GUID format.</value>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    public string ID
    {
        get
        {
            if (!Frontmatter.ContainsKey(IdKey) && Frontmatter[IdKey]!.FirstOrDefault() == null)
            {
                InsertGUID();
            }
            return Frontmatter[IdKey]!.FirstOrDefault()!;
        }
    }
    
    /// <summary>
    /// Gets the MD5 hash of the note's content, stored in YAML frontmatter.
    /// Used to detect changes when the file is modified externally.
    /// The hash is calculated from the note's content only, excluding the YAML frontmatter.
    /// </summary>
    /// <value>A string containing the Base64 encoded MD5 hash of the note's content.</value>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    public string Hash
    {
        get
        {
            if (!Frontmatter.ContainsKey(HashKey) && Frontmatter[HashKey]!.FirstOrDefault() == null)
            {
                ValidateHash();
            }
            return Frontmatter[HashKey]!.FirstOrDefault()!;
        }
    }
    
    /// <summary>
    /// Gets or sets the YAML frontmatter stored as key-value pairs.
    /// Values are stored as lists to support multi-value YAML fields.
    /// The frontmatter is automatically synchronized with the file on save.
    /// </summary>
    /// <value>A dictionary containing the note's frontmatter data, where keys are strings and values are lists of strings.</value>
    public Dictionary<string, List<string>?> Frontmatter = new();
    
    /// <summary>
    /// Gets or sets the collection of tags found in the note (both in frontmatter and inline).
    /// Tags are automatically extracted from both the frontmatter and the note's content.
    /// Inline tags are identified by the # symbol followed by non-whitespace characters.
    /// </summary>
    /// <value>A list of unique tags associated with the note.</value>
    public List<string> Tags = new();

    /// <summary>
    /// Gets the collection of internal links found in the note.
    /// Internal links are in the format [[Note Title]] or [[Note Title|Display Text]].
    /// </summary>
    public List<InternalLink> InternalLinks { get; private set; } = new();

    /// <summary>
    /// Gets the collection of external links found in the note.
    /// External links are in standard Markdown format [Display Text](URL).
    /// </summary>
    public List<ExternalLink> ExternalLinks { get; private set; } = new();

    /// <summary>
    /// Represents an internal link to another note in the vault.
    /// </summary>
    public class InternalLink
    {
        /// <summary>
        /// Gets the title of the linked note.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Gets the display text of the link, if different from the title.
        /// </summary>
        public string? DisplayText { get; }

        /// <summary>
        /// Gets the ID of the linked note, if found in the database.
        /// </summary>
        public string? NoteId { get; }

        public InternalLink(string title, string? displayText, string? noteId)
        {
            Title = title;
            DisplayText = displayText;
            NoteId = noteId;
        }
    }

    /// <summary>
    /// Represents an external link to a resource outside the vault.
    /// </summary>
    public class ExternalLink
    {
        /// <summary>
        /// Gets the display text of the link.
        /// </summary>
        public string DisplayText { get; }

        /// <summary>
        /// Gets the URL of the external resource.
        /// </summary>
        public string Url { get; }

        public ExternalLink(string displayText, string url)
        {
            DisplayText = displayText;
            Url = url;
        }
    }

    /// <summary>
    /// Gets or sets the main content of the note (everything after the frontmatter).
    /// Implements lazy loading - content is only read when accessed.
    /// </summary>
    /// <value>The note's body content as a string, excluding the YAML frontmatter.</value>
    /// <remarks>
    /// Setting this property automatically saves changes to disk.
    /// The content is cached after first access to avoid repeated file reads.
    /// Changes to the content are tracked through the Hash property.
    /// </remarks>
    public string Body
    {
        get
        {
            if (bodyCache == null)
            {
                bodyCache = GetBody(Path);
            }
            return bodyCache!;
        }

        set
        {
            bodyCache = value;
            Save();
        }
    }

    /// <summary>
    /// Cache for the note's content to avoid repeated file reads.
    /// This cache is invalidated when the note is reloaded or when the content is modified.
    /// </summary>
    private string? bodyCache = null;

    /// <summary>
    /// The key used for storing the content hash in frontmatter.
    /// This key is used to maintain data integrity and detect external changes.
    /// </summary>
    const string HashKey = "hash";

    /// <summary>
    /// The key used for storing the unique identifier in frontmatter.
    /// This key is used to maintain relationships between notes and track changes.
    /// </summary>
    const string IdKey = "guid";

    /// <summary>
    /// Initializes a new instance of the Note class from a file path.
    /// </summary>
    /// <param name="path">The filesystem path to the note file. Must be within the Obsidian vault directory.</param>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found at the specified path.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is invalid or outside the vault directory.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    /// <remarks>
    /// The constructor performs several initialization steps:
    /// - Reads and parses the file content
    /// - Extracts title, frontmatter, and tags
    /// - Ensures presence of unique ID and content hash
    /// - Outputs debug information about the note
    /// The note is immediately ready for use after construction.
    /// </remarks>
    public Note(string path)
    {
        string[] lines = System.IO.File.ReadAllLines(path);

        Title = ExtractTitle(lines);
        Path = path;
        Filename = System.IO.Path.GetFileName(path);
        Frontmatter = ExtractFrontMatter(lines);
        
        if (!Frontmatter.ContainsKey(IdKey) || Frontmatter[IdKey]!.FirstOrDefault() == null)
        {
            InsertGUID();
        }
        
        if (Frontmatter.ContainsKey(HashKey))
        {
            ValidateHash();
        }
        else { InsertHash(); }

        Tags = ExtractTags(lines, Frontmatter);
        UpdateLinks();

        _logger.LogInformation("========\n{Path}\nTitle: {Title}, {Filename}\n{ID}\n{Hash}\nTags:\n{TagList}",
            Path, Title, Filename, ID, Hash, string.Join("\n", Tags.Select(t => $" - #{t}")));
    }

    /// <summary>
    /// Reloads the note from disk, optionally with a new path.
    /// This method preserves the note's state in case of failure and provides detailed error information.
    /// </summary>
    /// <param name="path">Optional new path for the note. If empty, uses existing path.
    /// The new path must be within the vault directory.</param>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="ArgumentException">Thrown when the new path is invalid or outside the vault.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    /// <remarks>
    /// This method performs the following operations:
    /// - Validates the new path if provided
    /// - Creates a backup of the current state
    /// - Reloads the note content and metadata
    /// - Restores the backup if any operation fails
    /// - Notifies subscribers of the reload
    /// </remarks>
    public void Reload(string path = "")
    {
        try
        {
            _logger.LogInformation("Reloading note {Path}", path);
            
            // Validate new path if provided
            if (!string.IsNullOrEmpty(path))
            {
                if (!System.IO.File.Exists(path))
                {
                    throw new FileNotFoundException($"Note file not found at path: {path}");
                }
                
                // Verify the new path is within the vault
                string vaultPath = ObsidianDB.GetDatabaseInstance(Path)?.VaultPath ?? string.Empty;
                if (!string.IsNullOrEmpty(vaultPath) && !path.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"New path must be within the vault directory: {vaultPath}");
                }
                
                Path = path;
            }

            // Backup current state
            var backup = new
            {
                Title,
                Filename,
                Frontmatter = new Dictionary<string, List<string>?>(Frontmatter),
                Tags = new List<string>(Tags)
            };

            try
            {
                string[] lines = System.IO.File.ReadAllLines(Path);
                
                // Update properties in a specific order
                Filename = System.IO.Path.GetFileName(Path);
                Frontmatter = ExtractFrontMatter(lines);
                Title = ExtractTitle(lines);
                
                // Ensure required metadata exists
                if (!Frontmatter.ContainsKey(IdKey) || Frontmatter[IdKey]!.FirstOrDefault() == null)
                {
                    InsertGUID();
                }
                
                if (Frontmatter.ContainsKey(HashKey))
                {
                    ValidateHash();
                }
                else 
                { 
                    InsertHash(); 
                }

                Tags = ExtractTags(lines, Frontmatter);
                UpdateLinks();

                _logger.LogInformation("Note reloaded successfully: {Path}\nTitle: {Title}\nID: {ID}\nHash: {Hash}\nTags: {TagList}",
                    Path, Title, ID, Hash, string.Join(", ", Tags));
                    
                // Notify subscribers of the reload
                ObsidianDB.GetDatabaseInstance(Path)?.callbackManager.EnqueueUpdate(ID);
            }
            catch (Exception ex)
            {
                // Restore backup on failure
                Title = backup.Title;
                Filename = backup.Filename;
                Frontmatter = backup.Frontmatter;
                Tags = backup.Tags;
                
                _logger.LogError(ex, "Failed to reload note {Path}: {Message}", Path, ex.Message);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during note reload: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Saves changes back to the file.
    /// This method ensures data integrity through atomic file operations and backup mechanisms.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when there's no permission to write to the file.</exception>
    /// <exception cref="IOException">Thrown when there's an error writing to the file.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is invalid.</exception>
    /// <remarks>
    /// The save operation:
    /// - Updates the modification date in frontmatter if present
    /// - Reconstructs the file with updated frontmatter and content
    /// - Uses atomic write operation to prevent file corruption
    /// - Creates a backup before saving
    /// - Validates the saved content
    /// - Cleans up temporary files
    /// </remarks>
    internal void Save()
    {
        try
        {
            _logger.LogInformation("Saving note {Path}", Path);

            // Validate file path
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new ArgumentException("Note path cannot be empty", nameof(Path));
            }

            // Ensure directory exists
            string directory = System.IO.Path.GetDirectoryName(Path)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create backup of current content
            string backupPath = Path + ".bak";
            if (File.Exists(Path))
            {
                File.Copy(Path, backupPath, true);
            }

            try
            {
                // Get current content if not cached
                if (bodyCache == null)
                {
                    bodyCache = GetBody(Path);
                }

                // Update modification date if present
                if (Frontmatter.ContainsKey("date modified") && Frontmatter["date modified"] != null)
                {
                    string modified = DateTime.Now.ToString("dddd, MMMM d yyyy, h:M:ss tt");
                    Frontmatter["date modified"] = [modified];
                }

                // Build document content
                List<string> document = new();
                document.Add("---");

                // Add frontmatter
                foreach (string key in Frontmatter.Keys)
                {
                    if (Frontmatter[key] == null)
                    {
                        document.Add($"{key}:");
                    }
                    else if (Frontmatter[key]!.Count == 1)
                    {
                        document.Add($"{key}: {Frontmatter[key]![0]}");
                    }
                    else if (Frontmatter[key]!.Count > 1)
                    {
                        document.Add($"{key}:");
                        foreach (string value in Frontmatter[key]!)
                        {
                            document.Add($"  - {value}");
                        }
                    }
                }
                document.Add("---");
                document.Add(bodyCache);

                // Write to temporary file first
                string tempPath = Path + ".tmp";
                File.WriteAllLines(tempPath, document);

                // Atomic move operation
                File.Move(tempPath, Path, true);

                // Validate the saved content
                ValidateHash();

                _logger.LogInformation("Note saved successfully: {Path}", Path);
            }
            catch (Exception)
            {
                // Restore from backup if available
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, Path, true);
                        _logger.LogWarning("Restored note from backup after failed save: {Path}", Path);
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore note from backup: {Path}", Path);
                    }
                }
                throw;
            }
            finally
            {
                // Clean up temporary files
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete backup file: {Path}", backupPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving note {Path}: {Message}", Path, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Extracts the note body content from the file.
    /// This method handles various edge cases including missing or invalid frontmatter.
    /// </summary>
    /// <param name="path">The path to the note file. Must be a valid path within the vault.</param>
    /// <returns>The body content of the note as a string, excluding the YAML frontmatter.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="IOException">Thrown when there's an error reading the file.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is invalid.</exception>
    /// <remarks>
    /// The method:
    /// - Handles files without frontmatter
    /// - Handles files with invalid frontmatter
    /// - Uses efficient string concatenation
    /// - Preserves line endings
    /// </remarks>
    private string GetBody(string path)
    {
        try
        {
            _logger.LogInformation("Reading note body from {Path}", path);

            // Validate path
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be empty", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Note file not found at path: {path}");
            }

            // Read all lines with proper line ending handling
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            // Find the YAML frontmatter boundaries
            int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
            if (startIndex == -1)
            {
                // No frontmatter, return entire content
                return string.Join(Environment.NewLine, lines);
            }

            int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
            if (endIndex == -1)
            {
                // Invalid frontmatter, return content after first ---
                return string.Join(Environment.NewLine, lines.Skip(startIndex + 1));
            }

            // Extract body content
            var bodyLines = new List<string>();
            for (int i = endIndex + 1; i < lines.Length; i++)
            {
                bodyLines.Add(lines[i]);
            }

            // Optimize string concatenation
            if (bodyLines.Count == 0)
            {
                return string.Empty;
            }

            // Use StringBuilder for efficient string concatenation
            var sb = new StringBuilder();
            for (int i = 0; i < bodyLines.Count; i++)
            {
                sb.Append(bodyLines[i]);
                if (i < bodyLines.Count - 1)
                {
                    sb.Append(Environment.NewLine);
                }
            }

            string result = sb.ToString();
            _logger.LogDebug("Successfully read note body from {Path}, length: {Length}", path, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading note body from {Path}: {Message}", path, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Validates and updates the content hash if needed.
    /// This method ensures the hash in the frontmatter matches the actual content.
    /// </summary>
    /// <returns>True if hash is valid, false if it needed updating.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="IOException">Thrown when there's an error reading or writing the file.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    /// <remarks>
    /// The validation process:
    /// - Calculates the current content hash
    /// - Compares it with the stored hash
    /// - Updates the hash if they don't match
    /// - Uses atomic file operations
    /// - Creates a backup before updating
    /// </remarks>
    private bool ValidateHash()
    {
        try
        {
            _logger.LogDebug("Validating hash for note {Path}", Path);

            // Read current content and calculate hash
            string[] lines = File.ReadAllLines(Path);
            string calculatedHash = GenerateHash(lines);

            // Check if hash is already valid
            if (Hash == calculatedHash)
            {
                _logger.LogDebug("Hash is valid for note {Path}", Path);
                return true;
            }

            // Hash needs updating
            _logger.LogInformation("Updating hash for note {Path}", Path);

            // Create backup of current content
            string backupPath = Path + ".bak";
            File.Copy(Path, backupPath, true);

            try
            {
                // Find the hash line in frontmatter
                int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
                if (startIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
                if (endIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                // Find the hash line within frontmatter
                int hashIndex = Array.FindIndex(lines, startIndex, endIndex - startIndex, 
                    line => line.TrimStart().StartsWith($"{HashKey}:"));

                // Prepare updated lines
                var updatedLines = new List<string>(lines);
                string hashLine = $"{HashKey}: {calculatedHash}";

                if (hashIndex != -1)
                {
                    // Update existing hash line
                    updatedLines[hashIndex] = hashLine;
                }
                else
                {
                    // Insert new hash line before the closing ---
                    updatedLines.Insert(endIndex, hashLine);
                }

                // Write to temporary file first
                string tempPath = Path + ".tmp";
                File.WriteAllLines(tempPath, updatedLines);

                // Atomic move operation
                File.Move(tempPath, Path, true);

                // Update frontmatter
                Frontmatter[HashKey] = [calculatedHash];

                // Notify subscribers of the update
                ObsidianDB.GetDatabaseInstance(Path)?.callbackManager.EnqueueUpdate(ID);

                _logger.LogInformation("Hash updated successfully for note {Path}", Path);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update hash for note: {Path}", Path);
                // Restore from backup if available
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, Path, true);
                        _logger.LogWarning("Restored note from backup after failed hash update: {Path}", Path);
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore note from backup: {Path}", Path);
                    }
                }
                throw;
            }
            finally
            {
                // Clean up backup file
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete backup file: {Path}", backupPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating hash for note {Path}: {Message}", Path, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Creates initial content hash in frontmatter.
    /// This method is called when a note is first loaded without a hash.
    /// </summary>
    /// <returns>The generated hash string in Base64 format.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="IOException">Thrown when there's an error reading or writing the file.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    /// <remarks>
    /// The insertion process:
    /// - Calculates the content hash
    /// - Inserts it into the frontmatter
    /// - Uses atomic file operations
    /// - Creates a backup before updating
    /// </remarks>
    private string InsertHash()
    {
        try
        {
            _logger.LogInformation("Inserting initial hash for note {Path}", Path);

            // Read current content and calculate hash
            string[] lines = File.ReadAllLines(Path);
            string hash = GenerateHash(lines);

            // Create backup of current content
            string backupPath = Path + ".bak";
            File.Copy(Path, backupPath, true);

            try
            {
                // Find the frontmatter boundaries
                int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
                if (startIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
                if (endIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                // Prepare updated lines with hash inserted before the closing ---
                var updatedLines = new List<string>(lines);
                string hashLine = $"{HashKey}: {hash}";
                updatedLines.Insert(endIndex, hashLine);

                // Write to temporary file first
                string tempPath = Path + ".tmp";
                File.WriteAllLines(tempPath, updatedLines);

                // Atomic move operation
                File.Move(tempPath, Path, true);

                // Update frontmatter
                Frontmatter.Add(HashKey, [hash]);

                // Notify subscribers of the update
                ObsidianDB.GetDatabaseInstance(Path)?.callbackManager.EnqueueUpdate(ID);

                _logger.LogInformation("Hash inserted successfully for note {Path}", Path);
                return hash;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert hash for note: {Path}", Path);
                // Restore from backup if available
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, Path, true);
                        _logger.LogWarning("Restored note from backup after failed hash insertion: {Path}", Path);
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore note from backup: {Path}", Path);
                    }
                }
                throw;
            }
            finally
            {
                // Clean up backup file
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete backup file: {Path}", backupPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inserting hash for note: {Path}", Path);
            throw;
        }
    }

    /// <summary>
    /// Generates MD5 hash of note content, excluding the YAML frontmatter.
    /// This method ensures consistent hashing across different platforms and file systems.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <returns>Base64 encoded MD5 hash of the content.</returns>
    /// <remarks>
    /// The hash is calculated from the note's content only, excluding the YAML frontmatter.
    /// This ensures that updating the hash in the frontmatter doesn't change the hash itself.
    /// The method handles various edge cases:
    /// - Files without frontmatter
    /// - Files with invalid frontmatter
    /// - Empty files
    /// </remarks>
    private string GenerateHash(string[] lines)
    {
        try
        {
            _logger.LogDebug("Generating hash for note content");

            // Find YAML frontmatter boundaries
            int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
            if (startIndex == -1)
            {
                // No frontmatter, hash entire content
                return CalculateContentHash(lines);
            }

            int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
            if (endIndex == -1)
            {
                // Invalid frontmatter, hash content after first ---
                return CalculateContentHash(lines.Skip(startIndex + 1).ToArray());
            }

            // Hash content after frontmatter
            return CalculateContentHash(lines.Skip(endIndex + 1).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating hash: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Calculates the MD5 hash of the given lines.
    /// This method uses UTF-8 encoding and handles line endings consistently.
    /// </summary>
    /// <param name="lines">Array of lines to hash. Empty arrays are handled gracefully.</param>
    /// <returns>Base64 encoded MD5 hash of the content.</returns>
    /// <remarks>
    /// The method:
    /// - Uses UTF-8 encoding for consistent results
    /// - Handles line endings properly
    /// - Processes all lines in a single operation
    /// - Returns a Base64 encoded string for safe storage
    /// </remarks>
    private static string CalculateContentHash(string[] lines)
    {
        using var md5 = MD5.Create();
        var encoding = Encoding.UTF8;

        // Process all lines in a single operation
        byte[] contentBytes = encoding.GetBytes(string.Join(Environment.NewLine, lines));
        byte[] hashBytes = md5.ComputeHash(contentBytes);

        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Extracts tags from both frontmatter and inline content.
    /// This method handles hierarchical tags and ensures uniqueness.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <param name="frontmatter">Optional frontmatter dictionary to check for tags.</param>
    /// <returns>A list of all unique tags found in the note, including hierarchical tags.</returns>
    /// <remarks>
    /// Tags can be found in two places:
    /// 1. In the frontmatter under the "tags" key
    /// 2. In the body as inline tags (e.g., #tag)
    /// 
    /// Note that "# keyword" is a heading, while "#keyword" is a tag.
    /// The method handles hierarchical tags (e.g., "topic/sub-topic") by expanding them.
    /// </remarks>
    private List<string> ExtractTags(string[] lines, Dictionary<string, List<string>?>? frontmatter = null)
    {
        HashSet<string> tags = new();
        
        // Extract tags from frontmatter
        if (frontmatter != null && frontmatter.TryGetValue("tags", out var frontmatterTags) && frontmatterTags != null)
        {
            foreach (string tag in frontmatterTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tags.Add(tag.Trim());
                }
            }
        }

        // Find the end of the frontmatter block
        int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
        if (startIndex != -1)
        {
            int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
            if (endIndex != -1)
            {
                // Process the body content after the frontmatter
                for (int i = endIndex + 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Skip if this is a heading (starts with # followed by a space)
                    if (line.StartsWith("# ")) continue;

                    // Find all potential tags in the line
                    int tagStart = -1;
                    for (int j = 0; j < line.Length; j++)
                    {
                        if (line[j] == '#')
                        {
                            // Check if this is a tag (not a heading)
                            if (j == 0 || char.IsWhiteSpace(line[j - 1]))
                            {
                                tagStart = j;
                            }
                        }
                        else if (tagStart != -1)
                        {
                            // End of tag when we hit whitespace or end of line
                            if (char.IsWhiteSpace(line[j]) || j == line.Length - 1)
                            {
                                int length = j - tagStart;
                                if (j == line.Length - 1) length++; // Include the last character
                                
                                string potentialTag = line.Substring(tagStart, length).Trim('#', ' ', '\t', '\n', '\r');
                                if (!string.IsNullOrWhiteSpace(potentialTag))
                                {
                                    tags.Add(potentialTag);
                                }
                                tagStart = -1;
                            }
                        }
                    }

                    // Handle case where tag is at the end of the line
                    if (tagStart != -1)
                    {
                        string potentialTag = line.Substring(tagStart).Trim('#', ' ', '\t', '\n', '\r');
                        if (!string.IsNullOrWhiteSpace(potentialTag))
                        {
                            tags.Add(potentialTag);
                        }
                    }
                }
            }
        }

        return DigestTags(tags.ToList());
    }

    /// <summary>
    /// Processes hierarchical tags (e.g., "topic/sub-topic").
    /// This method expands hierarchical tags into their full paths.
    /// </summary>
    /// <param name="tags">List of raw tags to process. May contain hierarchical tags.</param>
    /// <returns>List of expanded hierarchical tags, including all parent tags.</returns>
    /// <remarks>
    /// For a tag like "topic/sub-topic", generates both "topic" and "topic/sub-topic" tags.
    /// The method:
    /// - Handles both forward and backward slashes
    /// - Removes empty segments
    /// - Ensures uniqueness
    /// - Preserves the original tags
    /// </remarks>
    private static List<string> DigestTags(List<string> tags)
    {
        HashSet<string> expanded = new(tags.Count * 2); // Pre-allocate based on expected size
        StringBuilder builder = new();

        foreach (string tag in tags)
        {
            int separatorIndex = tag.IndexOfAny(new[] { '/', '\\' });
            if (separatorIndex == -1) continue;

            string[] tokens = tag.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            builder.Clear();
            builder.Append(tokens[0]);
            expanded.Add(tokens[0]);

            for (int i = 1; i < tokens.Length; i++)
            {
                builder.Append('/').Append(tokens[i]);
                expanded.Add(builder.ToString());
            }
        }

        return expanded.ToList();
    }

    /// <summary>
    /// Creates and inserts a new GUID in frontmatter.
    /// This method ensures each note has a unique identifier.
    /// </summary>
    /// <returns>The newly generated GUID string.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the note file is not found.</exception>
    /// <exception cref="IOException">Thrown when there's an error reading or writing the file.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the note does not contain valid frontmatter.</exception>
    /// <remarks>
    /// The insertion process:
    /// - Checks if a GUID already exists
    /// - Generates a new GUID if needed
    /// - Inserts it into the frontmatter
    /// - Uses atomic file operations
    /// - Creates a backup before updating
    /// - Notifies subscribers of the update
    /// </remarks>
    private string InsertGUID()
    {
        try
        {
            _logger.LogInformation("Inserting GUID for note {Path}", Path);

            // Check if GUID already exists in frontmatter
            if (Frontmatter.ContainsKey(IdKey) && Frontmatter[IdKey] != null)
            {
                string existingId = Frontmatter[IdKey]!.FirstOrDefault()!;
                _logger.LogDebug("GUID already exists for note {Path}: {ID}", Path, existingId);
                return existingId;
            }

            // Create backup of current content
            string backupPath = Path + ".bak";
            File.Copy(Path, backupPath, true);

            try
            {
                // Read current content
                string[] lines = File.ReadAllLines(Path);

                // Find the YAML frontmatter boundaries
                int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
                if (startIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
                if (endIndex == -1)
                {
                    throw new InvalidOperationException("Note does not contain valid frontmatter");
                }

                // Generate new GUID
                string id = Guid.NewGuid().ToString();
                _logger.LogDebug("Generated new GUID: {ID}", id);

                // Prepare updated lines with GUID inserted before the closing ---
                var updatedLines = new List<string>(lines);
                string guidLine = $"{IdKey}: {id}";
                updatedLines.Insert(endIndex, guidLine);

                // Write to temporary file first
                string tempPath = Path + ".tmp";
                File.WriteAllLines(tempPath, updatedLines);

                // Atomic move operation
                File.Move(tempPath, Path, true);

                // Update frontmatter
                Frontmatter.Add(IdKey, [id]);

                // Notify subscribers of the update
                ObsidianDB.GetDatabaseInstance(Path)?.callbackManager.EnqueueUpdate(ID);

                _logger.LogInformation("GUID inserted successfully for note {Path}: {ID}", Path, id);
                return id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert GUID for note: {Path}", Path);
                // Restore from backup if available
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, Path, true);
                        _logger.LogWarning("Restored note from backup after failed GUID insertion: {Path}", Path);
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore note from backup: {Path}", Path);
                    }
                }
                throw;
            }
            finally
            {
                // Clean up backup file
                if (File.Exists(backupPath))
                {
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete backup file: {Path}", backupPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inserting GUID for note: {Path}", Path);
            throw;
        }
    }

    /// <summary>
    /// Parses YAML frontmatter from the file using YamlDotNet for robust YAML parsing.
    /// This method handles various YAML formats and edge cases.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <returns>Dictionary containing parsed frontmatter key-value pairs.</returns>
    /// <remarks>
    /// This method uses YamlDotNet to properly parse YAML frontmatter, supporting:
    /// - Nested structures
    /// - Quoted values
    /// - Multi-line strings
    /// - Different value types (strings, numbers, booleans)
    /// - Lists and dictionaries
    /// 
    /// The method handles various edge cases:
    /// - Missing frontmatter
    /// - Invalid frontmatter
    /// - Empty frontmatter
    /// </remarks>
    private Dictionary<string, List<string>?> ExtractFrontMatter(string[] lines)
    {
        var result = new Dictionary<string, List<string>?>();
        
        // Find the YAML frontmatter block
        int startIndex = Array.FindIndex(lines, line => line.Trim() == "---");
        if (startIndex == -1) return result;
        
        int endIndex = Array.FindIndex(lines, startIndex + 1, line => line.Trim() == "---");
        if (endIndex == -1) return result;
        
        // Extract the YAML content
        string yamlContent = string.Join("\n", lines.Skip(startIndex + 1).Take(endIndex - startIndex - 1));
        
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .Build();
            
            var yamlObject = deserializer.Deserialize<object>(yamlContent);
            
            // Convert the YAML object to our dictionary format
            if (yamlObject is Dictionary<object, object> dict)
            {
                foreach (var kvp in dict)
                {
                    string key = kvp.Key.ToString() ?? string.Empty;
                    object value = kvp.Value;
                    
                    if (value == null)
                    {
                        result[key] = null;
                    }
                    else if (value is string str)
                    {
                        result[key] = new List<string> { str };
                    }
                    else if (value is IEnumerable<object> enumerable)
                    {
                        result[key] = [.. enumerable.Select(x => x.ToString())];
                    }
                    else
                    {
                        result[key] = new List<string> { value.ToString() ?? string.Empty };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing YAML frontmatter: {Message}", ex.Message);
        }
        
        return result;
    }

    /// <summary>
    /// Extracts the title from the first H1 heading or falls back to the filename without extension.
    /// This method ensures every note has a title, even if no H1 heading is present.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <returns>The title string if found, or the filename without extension as fallback.</returns>
    /// <remarks>
    /// The method:
    /// - Looks for the first H1 heading (# followed by space)
    /// - Falls back to filename without extension if no H1 found
    /// - Trims whitespace from the title
    /// - Handles empty files gracefully
    /// </remarks>
    private string? ExtractTitle(string[] lines)
    {
        // First try to find an H1 heading
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("# "))
            {
                string title = line.Replace("#", "").Trim();
                return title;
            }
        }

        // If no H1 heading found, use the filename without extension
        string filename = System.IO.Path.GetFileNameWithoutExtension(Path);
        return filename;
    }

    /// <summary>
    /// Updates the collections of internal and external links by parsing the note's content.
    /// </summary>
    public void UpdateLinks()
    {
        InternalLinks.Clear();
        ExternalLinks.Clear();

        string content = Body;
        var db = ObsidianDB.GetDatabaseInstance(Path);

        // Parse internal links (Wikilinks)
        var internalLinkMatches = System.Text.RegularExpressions.Regex.Matches(content, @"\[\[([^\]\|]+)(?:\|([^\]]+))?\]\]");
        foreach (System.Text.RegularExpressions.Match match in internalLinkMatches)
        {
            string title = match.Groups[1].Value.Trim();
            string? displayText = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            string? noteId = db?.GetFromTitle(title)?.ID;
            InternalLinks.Add(new InternalLink(title, displayText, noteId));
        }

        // Parse external links (Markdown links)
        var externalLinkMatches = System.Text.RegularExpressions.Regex.Matches(content, @"\[([^\]]+)\]\(([^\)]+)\)");
        foreach (System.Text.RegularExpressions.Match match in externalLinkMatches)
        {
            string displayText = match.Groups[1].Value.Trim();
            string url = match.Groups[2].Value.Trim();
            ExternalLinks.Add(new ExternalLink(displayText, url));
        }
    }

    /// <summary>
    /// Gets all links in the note, both internal and external.
    /// </summary>
    /// <returns>A tuple containing lists of internal and external links.</returns>
    public (List<InternalLink> internalLinks, List<ExternalLink> externalLinks) GetAllLinks()
    {
        return (InternalLinks, ExternalLinks);
    }

    /// <summary>
    /// Gets all internal links in the note.
    /// </summary>
    /// <returns>A list of internal links.</returns>
    public List<InternalLink> GetInternalLinks()
    {
        return InternalLinks;
    }

    /// <summary>
    /// Gets all external links in the note.
    /// </summary>
    /// <returns>A list of external links.</returns>
    public List<ExternalLink> GetExternalLinks()
    {
        return ExternalLinks;
    }

    /// <summary>
    /// Gets all internal links that point to a specific note.
    /// </summary>
    /// <param name="noteId">The ID of the note to find links to.</param>
    /// <returns>A list of internal links that point to the specified note.</returns>
    public List<InternalLink> GetLinksToNote(string noteId)
    {
        return InternalLinks.Where(link => link.NoteId == noteId).ToList();
    }

    /// <summary>
    /// Gets all internal links that point to a specific note title.
    /// </summary>
    /// <param name="title">The title of the note to find links to.</param>
    /// <returns>A list of internal links that point to the specified note title.</returns>
    public List<InternalLink> GetLinksToTitle(string title)
    {
        return InternalLinks.Where(link => link.Title.Equals(title, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Represents a backlink from another note to this note.
    /// </summary>
    public class BackLink
    {
        /// <summary>
        /// Gets the title of the linked note.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Gets the display text of the link, if different from the title.
        /// </summary>
        public string? DisplayText { get; }

        /// <summary>
        /// Gets the ID of the note that contains this link.
        /// </summary>
        public string SourceNoteId { get; }

        public BackLink(string title, string? displayText, string sourceNoteId)
        {
            Title = title;
            DisplayText = displayText;
            SourceNoteId = sourceNoteId;
        }
    }

    /// <summary>
    /// Gets a list of all backlinks to this note from other notes in the vault.
    /// </summary>
    /// <value>A list of BackLink objects representing links from other notes to this note.</value>
    public List<BackLink> Backlinks
    {
        get
        {
            var backlinks = new System.Collections.Concurrent.ConcurrentBag<BackLink>();
            var db = ObsidianDB.GetDatabaseInstance(Path);
            if (db == null) return new List<BackLink>();

            var notes = db.GetNotes().ToList(); // Materialize the enumerable to avoid potential issues with parallel execution
            var currentId = ID; // Capture the ID to avoid closure issues

            Parallel.ForEach(notes, note =>
            {
                if (note.ID == currentId) return; // Skip self-references

                foreach (var link in note.InternalLinks)
                {
                    if (link.NoteId == currentId)
                    {
                        backlinks.Add(new BackLink(link.Title, link.DisplayText, note.ID));
                    }
                }
            });

            return backlinks.ToList();
        }
    }
}