using System;
using System.Threading;
using ObsidianDB;
using ObsidianDB.Logging;
using Microsoft.Extensions.Logging;
using System.IO;

// Configure logging
LoggerService.ConfigureLogging(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddConsole();
});

// Get the path to the example vault
string exampleVaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "submodules", "Personal-Wiki");
exampleVaultPath = Path.GetFullPath(exampleVaultPath);

// Initialize and scan the database
ObsidianDB.ObsidianDB db = new(exampleVaultPath);
db.ScanNotes();

// Export the database to JSON for debugging
string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vault_debug.json");
db.ToJson(jsonPath);
Console.WriteLine($"Database exported to: {jsonPath}");

// Keep the program running to monitor changes
while(true)
{
    //db.Update();
    Thread.Sleep(1000);
}