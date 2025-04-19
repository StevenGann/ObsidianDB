using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Markdig.Helpers;
using Microsoft.Extensions.Logging;
using ObsidianDB.Logging;
using YamlDotNet.Serialization;

namespace ObsidianDB;

/// <summary>
/// Represents a Markdown note file in an Obsidian vault, managing its content, metadata, and file operations.
/// </summary>
public class Note
{
    private readonly ILogger<Note> _logger = LoggerService.GetLogger<Note>();
    
    /// <summary>
    /// Gets or sets the title of the note, extracted from the first H1 heading (# Title).
    /// </summary>
    /// <value>The note's title, or null if no H1 heading is found.</value>
    public string? Title { get; set; } = null;
    
    /// <summary>
    /// Gets or sets the full filesystem path to the note file.
    /// </summary>
    /// <value>The absolute path to the note file.</value>
    public string Path { get; set; }
    
    /// <summary>
    /// Gets or sets the filename portion of the path.
    /// </summary>
    /// <value>The filename without the directory path.</value>
    public string Filename { get; set; }
    
    /// <summary>
    /// Gets the unique identifier for the note, stored in YAML frontmatter.
    /// Automatically generates a new GUID if not present.
    /// </summary>
    /// <value>A string containing the note's unique identifier.</value>
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
    /// </summary>
    /// <value>A string containing the MD5 hash of the note's content.</value>
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
    /// </summary>
    /// <value>A dictionary containing the note's frontmatter data.</value>
    public Dictionary<string, List<string>?> Frontmatter = new();
    
    /// <summary>
    /// Gets or sets the collection of tags found in the note (both in frontmatter and inline).
    /// </summary>
    /// <value>A list of tags associated with the note.</value>
    public List<string> Tags = new();

    /// <summary>
    /// Gets or sets the main content of the note (everything after the frontmatter).
    /// Implements lazy loading - content is only read when accessed.
    /// </summary>
    /// <value>The note's body content as a string.</value>
    /// <remarks>
    /// Setting this property automatically saves changes to disk.
    /// The content is cached after first access to avoid repeated file reads.
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
    /// </summary>
    private string? bodyCache = null;

    /// <summary>
    /// The key used for storing the content hash in frontmatter.
    /// </summary>
    const string HashKey = "hash";

    /// <summary>
    /// The key used for storing the unique identifier in frontmatter.
    /// </summary>
    const string IdKey = "guid";

    /// <summary>
    /// Initializes a new instance of the Note class from a file path.
    /// </summary>
    /// <param name="path">The filesystem path to the note file.</param>
    /// <remarks>
    /// The constructor performs several initialization steps:
    /// - Reads and parses the file content
    /// - Extracts title, frontmatter, and tags
    /// - Ensures presence of unique ID and content hash
    /// - Outputs debug information about the note
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

        _logger.LogInformation("========\n{Path}\nTitle: {Title}, {Filename}\n{ID}\n{Hash}\nTags:\n{TagList}",
            Path, Title, Filename, ID, Hash, string.Join("\n", Tags.Select(t => $" - #{t}")));
    }

    /// <summary>
    /// Reloads the note from disk, optionally with a new path.
    /// </summary>
    /// <param name="path">Optional new path for the note. If empty, uses existing path.</param>
    public void Reload(string path = "")
    {
        if(path != "")
        {
            Path = path;
        }
        string[] lines = System.IO.File.ReadAllLines(Path);

        Title = ExtractTitle(lines);
        Filename = System.IO.Path.GetFileName(Path);
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

        _logger.LogInformation("========\n{Path}\nTitle: {Title}, {Filename}\n{ID}\n{Hash}\nTags:\n{TagList}",
            Path, Title, Filename, ID, Hash, string.Join("\n", Tags.Select(t => $" - #{t}")));
    }

    /// <summary>
    /// Saves changes back to the file.
    /// </summary>
    /// <remarks>
    /// Updates the modification date in frontmatter if present.
    /// Reconstructs the file with updated frontmatter and content.
    /// </remarks>
    internal void Save()
    {
        if (bodyCache == null) { bodyCache = GetBody(Path); }
        List<string> document = new();

        if (Frontmatter.ContainsKey("date modified") && Frontmatter["date modified"] != null)
        {
            string modified = DateTime.Now.ToString("dddd, MMMM d yyyy, h:M:ss tt");
            Frontmatter["date modified"] = [modified];
        }

        // Assemble frontmatter
        document.Add("---");
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

        System.IO.File.WriteAllLines(Path, document.ToArray());
        ValidateHash();
    }

    /// <summary>
    /// Extracts the note body content from the file.
    /// </summary>
    /// <param name="path">The path to the note file.</param>
    /// <returns>The body content of the note as a string.</returns>
    private string GetBody(string path)
    {
        string[] lines = System.IO.File.ReadAllLines(path);
        List<string> bodyLines = new();
        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }
        index += 1;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Until end of YAML block
        {
            index++;
        }
        index++;

        while (index < lines.Length)
        {
            bodyLines.Add(lines[index]);
            index++;
        }

        string result = "";
        foreach (string line in bodyLines)
        {
            result += $"{line}\n";
        }
        return result;
    }

    /// <summary>
    /// Validates and updates the content hash if needed.
    /// </summary>
    /// <returns>True if hash is valid, false if it needed updating.</returns>
    private bool ValidateHash()
    {
        string[] lines = System.IO.File.ReadAllLines(Path);
        string calculatedHash = GenerateHash(lines);

        if (Hash == calculatedHash) { return true; }

        // Update Hash
        _logger.LogInformation("Updating hash to {Hash}", calculatedHash);
        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith($"{HashKey}:")) // Looking for hash YAML tag
        {
            index++;
        }
        lines[index] = $"{HashKey}: {calculatedHash}";
        System.IO.File.WriteAllLines(Path, lines);
        Frontmatter[HashKey] = [calculatedHash];

        ObsidianDB.GetDatabaseInstance(Path)!.callbackManager.EnqueueUpdate(ID);

        return false;
    }

    /// <summary>
    /// Creates initial content hash in frontmatter.
    /// </summary>
    /// <returns>The generated hash string, or null if operation fails.</returns>
    private string? InsertHash()
    {
        string[] lines = System.IO.File.ReadAllLines(Path);
        string hash = GenerateHash(lines);

        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }
        lines[index] += $"\n{HashKey}: {hash}";
        System.IO.File.WriteAllLines(Path, lines);

        Frontmatter.Add(HashKey, [hash]);
        return hash;
    }

    /// <summary>
    /// Generates MD5 hash of note content.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <returns>Base64 encoded MD5 hash of the content.</returns>
    private string GenerateHash(string[] lines)
    {
        string result = "error";
        using (var md5 = MD5.Create())
        {
            int index = 0;
            while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
            {
                var inputBuffer = Encoding.UTF8.GetBytes(lines[index]);
                md5.TransformBlock(inputBuffer, 0, inputBuffer.Length, inputBuffer, 0);
                index++;
            }
            index += 1;
            while (index < lines.Length && !lines[index].StartsWith("---")) // Until end of YAML block
            {
                index++;
            }
            index++;

            while (index < lines.Length)
            {
                var inputBuffer = Encoding.UTF8.GetBytes(lines[index]);
                md5.TransformBlock(inputBuffer, 0, inputBuffer.Length, inputBuffer, 0);
                index++;
            }

            md5.TransformFinalBlock([], 0, 0);
            result = Convert.ToBase64String(md5.Hash!);
        }
        return result;
    }

    /// <summary>
    /// Extracts tags from both frontmatter and inline content.
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <param name="frontmatter">Optional frontmatter dictionary to check for tags.</param>
    /// <returns>A list of all unique tags found in the note.</returns>
    /// <remarks>
    /// Tags can be found in two places:
    /// 1. In the frontmatter under the "tags" key
    /// 2. In the body as inline tags (e.g., #tag)
    /// 
    /// Note that "# keyword" is a heading, while "#keyword" is a tag.
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
    /// </summary>
    /// <param name="tags">List of raw tags to process.</param>
    /// <returns>List of expanded hierarchical tags.</returns>
    /// <remarks>
    /// For a tag like "topic/sub-topic", generates both "topic" and "topic/sub-topic" tags.
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
    /// </summary>
    /// <returns>The newly generated GUID string.</returns>
    private string InsertGUID()
    {
        if (Frontmatter.ContainsKey(IdKey) && Frontmatter[IdKey] != null)
        {
            return Frontmatter[IdKey]!.FirstOrDefault()!;
        }
        string[] lines = System.IO.File.ReadAllLines(Path);
        string id = Guid.NewGuid().ToString();

        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }
        lines[index] += $"\n{IdKey}: {id}";
        System.IO.File.WriteAllLines(Path, lines);

        Frontmatter.Add(IdKey, [id]);
        return id;
    }

    /// <summary>
    /// Parses YAML frontmatter from the file using YamlDotNet for robust YAML parsing.
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
    /// </summary>
    /// <param name="lines">Array of lines from the note file.</param>
    /// <returns>The title string if found, or the filename without extension as fallback.</returns>
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
}