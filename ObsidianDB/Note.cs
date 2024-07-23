using System.Security.Cryptography;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using MessagePack.ImmutableCollection;

namespace ObsidianDB;

public class Note
{
    public string Title { get; set; }
    public string Path { get; set; }

    public string Hash { get; set; }

    public string ID { get; set; } = Guid.NewGuid().ToString();

    public List<string> Tags { get; set; } = new List<string>();

    public Note(string path)
    {
        Path = path;
        Title = System.IO.Path.GetFileName(path).Replace(".md", "");
        string[] lines = System.IO.File.ReadAllLines(Path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("# "))
            {
                Title = lines[i].Trim('#').Trim();
                break;
            }
        }

        using (var md5 = MD5.Create())
        {
            using (var stream = File.OpenRead(path))
            {
                Hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
            }
        }
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