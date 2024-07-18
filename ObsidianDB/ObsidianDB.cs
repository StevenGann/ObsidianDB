using System.Security.Principal;

namespace ObsidianDB;

public class ObsidianDB
{
    public string VaultPath { get; set; }
    public string MetaDataPath { get; set; }

    public List<Tag> Tags{ get; set; } = new List<Tag>();

    public void ScanNotes()
    {
        string[] files = Directory.GetFiles(VaultPath, "*.md", SearchOption.AllDirectories);
        Console.WriteLine($"Found {files.Length} files");

        foreach (string file in files)
        {
            Note note = new(file);
            //Console.WriteLine($"{note.Title} - {note.Hash}");
            ScanTags(note);
        }
    }

    public List<string> ScanTags(Note note)
    {
        bool inYamlBlock = false;
        bool inCodeBlock = false;
        string[] lines = System.IO.File.ReadAllLines(note.Path);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!inCodeBlock && lines[i].Contains("---")) { inYamlBlock = !inYamlBlock; }
            if (!inYamlBlock && lines[i].Contains("```")) { inCodeBlock = !inCodeBlock; }

            if (!inCodeBlock && !inYamlBlock)
            {
                string[] tokens = lines[i].Split(' ');

                foreach (string token in tokens)
                {
                    if (token.Trim().StartsWith("#") && !token.Trim().EndsWith("#"))
                    {
                        if(Tags.Find(i => i.Name == token.Trim('#', ',', '.', '!', '?')) == null)
                        {
                            Console.WriteLine(token);
                            Tags.Add(new(token));
                        }
                    }
                }
            }
        }
        return new List<string>();
    }
}
