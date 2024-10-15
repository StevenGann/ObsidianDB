using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Markdig.Helpers;

namespace ObsidianDB;

public class Note
{
    public string? Title { get; set; } = null;
    public string Path { get; set; }
    public string Filename { get; set; }
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
    public Dictionary<string, List<string>?> Frontmatter = new();
    public List<string> Tags = new();

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

    private string? bodyCache = null;

    const string HashKey = "hash";
    const string IdKey = "guid";

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

        Console.WriteLine("========");
        Console.WriteLine($"{Path}");
        Console.WriteLine($"Title: {Title}, {Filename}");
        Console.WriteLine($"{ID}");
        Console.WriteLine($"{Hash}");
        Console.WriteLine($"Tags:");
        foreach (string tag in Tags)
        {
            Console.WriteLine($" - #{tag}");
        }
    }

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

        Console.WriteLine("========");
        Console.WriteLine($"{Path}");
        Console.WriteLine($"Title: {Title}, {Filename}");
        Console.WriteLine($"{ID}");
        Console.WriteLine($"{Hash}");
        Console.WriteLine($"Tags:");
        foreach (string tag in Tags)
        {
            Console.WriteLine($" - #{tag}");
        }
    }

    public void Save()
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

    private bool ValidateHash()
    {
        string[] lines = System.IO.File.ReadAllLines(Path);
        string calculatedHash = GenerateHash(lines);

        if (Hash == calculatedHash) { return true; }

        // Update Hash
        Console.WriteLine("Updating hash to " + calculatedHash);
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

    private List<string> ExtractTags(string[] lines, Dictionary<string, List<string>?>? frontmatter = null)
    {
        List<string> tags = new();

        if (frontmatter != null && frontmatter.ContainsKey("tags") && frontmatter["tags"] != null)
        {
            foreach (string tag in frontmatter["tags"]!)
            {
                tags.Add(tag);
            }
        }

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

        while (index < lines.Length) // Until end of file
        {
            string[] tokens = lines[index].Split(' ');
            foreach (string token in tokens)
            {
                if (token.Trim().StartsWith("#") && !(token.Trim().StartsWith("##") || token.Trim().EndsWith("#")))
                {
                    string tag = token.Trim(' ', ',', '.', ';', '\t', '\n', '#');
                    //Console.WriteLine(tag);
                    tags.Add(tag);
                }
            }
            index++;
        }

        tags = DigestTags(tags);

        return tags;
    }

    private static List<string> DigestTags(List<string> tags)
    {
        List<string> expanded = new();

        foreach (string tag in tags)
        {
            if (tag.Contains('/') || tag.Contains('\\'))
            {
                var tokens = tag.Split('/', '\\');
                string aggregate = tokens[0];
                expanded.Add(aggregate);
                for (int i = 1; i < tokens.Length; i++)
                {
                    aggregate += $"/{tokens[i]}";
                    expanded.Add(aggregate);
                }
            }
        }

        return expanded.Distinct().ToList();
    }

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

    private Dictionary<string, List<string>?> ExtractFrontMatter(string[] lines)
    {
        Dictionary<string, List<string>?> result = new();
        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }

        index += 1;

        bool inSubBlock = false;
        List<string>? children = null;
        string key = "";
        while (index < lines.Length && !lines[index].StartsWith("---")) // Until end of YAML block
        {
            if (lines[index].Contains(':') && !lines[index].First().IsWhitespace()) // YAML tag
            {
                inSubBlock = false;
                if (children != null)
                {
                    result.Add(key, children);
                }

                key = lines[index].Split(':')[0];

                if (!lines[index].Trim().EndsWith(':'))//Single line YAML key-value
                {
                    string value = lines[index].Substring(key.Length + 1).Trim();
                    if (!result.ContainsKey(key))
                    {
                        result.Add(key, [value]);
                    }
                    children = null;
                }
                else
                {
                    inSubBlock = true;
                    children = new();
                }
            }
            else if (inSubBlock)
            {
                children!.Add(lines[index].Replace("- ", "").Trim());
            }

            index++;
        }

        if (children != null)
        {
            result.Add(key, children);
        }

        return result;
    }

    private string? ExtractTitle(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("# "))
            {
                string title = line.Replace("#", "").Trim();
                return title;
            }
        }

        return null;
    }

    
}