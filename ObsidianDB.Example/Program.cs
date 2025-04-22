using System;
using System.Threading;
using ObsidianDB;
using ObsidianDB.Logging;
using Microsoft.Extensions.Logging;

// Configure logging
LoggerService.ConfigureLogging(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddConsole();
});

//new ObsidianDB.Note(@"C:\Users\sgann\Obsidian\Vault\Projects\Knowledgebase-AI\ObsidianDB.md");

//Note note = new ObsidianDB.Note(@"C:\Users\owner\OneDrive\Apps\remotely-save\Vault\Projects\Knowledgebase-AI\ObsidianDB.md");
//note.Frontmatter.Add("debug", ["testing"]);
//note.Body = "A quick test\n" + note.Body;
//note.Save();


//ObsidianDB.ObsidianDB db = new(@"C:\Users\sgann\Obsidian\Vault");
ObsidianDB.ObsidianDB db = new(@"C:\Users\owner\OneDrive\Apps\remotely-save\Vault");
db.ScanNotes();

while(true)
{
    db.Update();
    Thread.Sleep(1000);

    Note n = db.GetFromId("59d9b9f0-73bd-430e-b0c5-2725c66cfbfa");
    Console.WriteLine(n.Hash);
}