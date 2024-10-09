using System.Security.Cryptography;
using System.Text;
using System.Xml.XPath;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Renderers;
using MessagePack.ImmutableCollection;

namespace ObsidianDB;

public class Note
{
    public string Title { get; set; }
    public string Path { get; set; }
    public string Filename { get; set; }
    public string ID { get; set; }
    public string Hash { get; set; }
    public Dictionary<string, List<string>?> Frontmatter = new();
    public List<string> Tags = new();

    public Note(string path)
    {
        string[] lines = System.IO.File.ReadAllLines(path);

        Title = ExtractTitle(lines);
        Path = path;
        Filename = System.IO.Path.GetFileName(path);
        Frontmatter = ExtractFrontMatter(lines);
        if (Frontmatter.ContainsKey("guid")) { ID = Frontmatter["guid"].FirstOrDefault(); }
        else { ID = InsertGUID(path); }
        if (Frontmatter.ContainsKey("hash")) { Hash = Frontmatter["hash"].FirstOrDefault(); }
        else { Hash = InsertHash(path); }

        Tags = ExtractTags(lines);
    }

    private string? InsertHash(string path)
    {
        string[] lines = System.IO.File.ReadAllLines(path);
        string hash = GenerateHash(lines);

        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }
        lines[index] += $"\nhash: {hash}";
        System.IO.File.WriteAllLines(path, lines);

        Frontmatter.Add("hash", [hash]);
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

    private List<string> ExtractTags(string[] lines)
    {
        throw new NotImplementedException();
    }

    private string InsertGUID(string path)
    {
        string[] lines = System.IO.File.ReadAllLines(path);
        string id = Guid.NewGuid().ToString();

        int index = 0;
        while (index < lines.Length && !lines[index].StartsWith("---")) // Looking for start of YAML block
        {
            index++;
        }
        lines[index] += $"\nguid: {id}";
        System.IO.File.WriteAllLines(path, lines);

        Frontmatter.Add("guid", [id]);
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
                    result.Add(key, [value]);
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

    private string ExtractTitle(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("# "))
            {
                string title = line.Replace("#", "").Trim();
                return title;
            }
        }

        return "";
    }

    public string GetPlaintext()
    {
        return Markdown.ToPlainText(GetBody());
    }

    public string GetBody()
    {
        string markdown = System.IO.File.ReadAllText(Path);

        return Utilities.RemoveBlock(markdown, "---", "---");
    }
}