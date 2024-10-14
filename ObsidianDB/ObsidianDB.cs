using System.Runtime.CompilerServices;
using System.Security.Principal;

namespace ObsidianDB;

public class ObsidianDB
{
    public string VaultPath { get; set; }
    public string MetaDataPath { get; set; }

    public List<Note> Notes{ get; set; } = new();

    public IEnumerable<Note> GetNotes()
    {
        int index = 0;
        while(index < Notes.Count)
        {
            yield return Notes[index];
            index++;
        }
    }

    public void ScanNotes()
    {
        string[] files = Directory.GetFiles(VaultPath, "*.md", SearchOption.AllDirectories);
        Console.WriteLine($"Found {files.Length} files");

        foreach (string file in files)
        {
            Note note = new(file);
            Notes.Add(note);
        }
    }

    public Note? GetFromId(string id)
    {
        foreach (Note note in Notes)
        {
            if (note.ID == id){return note;}
        }

        return null;
    }

    public void Update()
    {
        CallbackManager.Tick(this);
    }
}
