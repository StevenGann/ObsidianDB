using System;
using System.Linq;

namespace ObsidianDB;

public static class Utilities
{
    public static string RemoveBlock(string text, string openingTag, string closingTag)
    {
        string workingText = text;
        int offset = 0;
        while (true)
        {
            int openingPos = workingText.IndexOf(openingTag, offset);
            if (openingPos == -1) { return workingText; } //Opening tag is absent, we're done
            int closingPos = workingText.IndexOf(closingTag, openingPos + openingTag.Length);
            if (closingPos > openingPos)
            {
                int blockLength = closingPos - openingPos + openingTag.Length;
                //Console.WriteLine($"REMOVED:\n{workingText.Substring(openingPos, blockLength)}");
                workingText = workingText.Remove(openingPos, blockLength);
            }
            else
            {
                offset += openingTag.Length;
            }
        }
    }

    public static bool ContainsPlaintext(string text)
    {
        return text.Any(x => char.IsLetter(x));
    }

    public static void PrintException(Exception? ex)
    {
        if (ex != null)
        {
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine("Stacktrace:");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine();
            PrintException(ex.InnerException);
        }
    }
}