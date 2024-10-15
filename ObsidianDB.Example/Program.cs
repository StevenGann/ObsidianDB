

//new ObsidianDB.Note(@"C:\Users\sgann\Obsidian\Vault\Projects\Knowledgebase-AI\ObsidianDB.md");

using System.Threading;
using ObsidianDB;

//Note note = new ObsidianDB.Note(@"C:\Users\owner\OneDrive\Apps\remotely-save\Vault\Projects\Knowledgebase-AI\ObsidianDB.md");
//note.Frontmatter.Add("debug", ["testing"]);
//note.Body = "A quick test\n" + note.Body;
//note.Save();


ObsidianDB.ObsidianDB db = new(@"C:\Users\sgann\Obsidian\Vault");
//ObsidianDB.ObsidianDB db = new(@"C:\Users\owner\OneDrive\Apps\remotely-save\Vault");
db.ScanNotes();

while(true)
{
    db.Update();
    Thread.Sleep(1000);
}