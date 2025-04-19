# ObsidianDB

A powerful C# library for programmatically managing Obsidian vaults. ObsidianDB provides a robust API for interacting with Obsidian notes, handling frontmatter, tags, and file operations with safety and efficiency.

## Features

- **Note Management**
  - Read and write Obsidian notes
  - Handle YAML frontmatter
  - Extract and manage tags
  - Automatic title extraction from H1 headings
  - File path management

- **Safety Features**
  - Atomic file operations
  - Automatic backups
  - Content validation
  - Error handling and recovery
  - File integrity checks

- **Metadata Handling**
  - Automatic GUID generation
  - Content hashing
  - Modification date tracking
  - Frontmatter parsing and serialization

- **Performance**
  - Lazy loading of content
  - Efficient file operations
  - Caching mechanisms
  - Optimized tag processing

## Installation

```bash
dotnet add package ObsidianDB
```

## Quick Start

```csharp
using ObsidianDB;

// Initialize a note
var note = new Note("path/to/your/note.md");

// Access note properties
Console.WriteLine($"Title: {note.Title}");
Console.WriteLine($"Tags: {string.Join(", ", note.Tags)}");

// Modify and save
note.Body = "New content";
note.Save();
```

## API Reference

### Note Class

The `Note` class is the core of ObsidianDB, providing methods to interact with Obsidian notes:

- `Title`: Gets or sets the note's title
- `Body`: Gets or sets the note's content
- `Tags`: Gets the note's tags
- `Frontmatter`: Gets the note's YAML frontmatter
- `Save()`: Saves changes to the file
- `Reload()`: Reloads the note from disk

### Safety Features

ObsidianDB includes several safety features to protect your data:

- Atomic writes prevent file corruption
- Automatic backups before modifications
- Content validation through hashing
- Error recovery mechanisms
- File integrity checks

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Inspired by the [Obsidian](https://obsidian.md) note-taking application
- Built with .NET Core
- Uses [YamlDotNet](https://github.com/aaubry/YamlDotNet) for YAML parsing

## Support

If you find a bug or have a feature request, please open an issue on GitHub. 