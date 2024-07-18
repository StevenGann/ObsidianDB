namespace ObsidianDB;

public class Tag
{
   

    public string Name { get; set; }
    public string ID { get; set; } = Guid.NewGuid().ToString();

     public Tag(string name)
    {
        Name = name.Trim().Trim('#', ',', '.', '!', '?');
    }
}