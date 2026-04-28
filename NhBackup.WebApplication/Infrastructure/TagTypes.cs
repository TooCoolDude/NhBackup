namespace NhBackup.WebApplication.Infrastructure;

public static class TagTypes
{
    public const string Parody = "parody";
    public const string Character = "character";
    public const string Tag = "tag";
    public const string Artist = "artist";
    public const string Group = "group";
    public const string Language = "language";
    public const string Category = "category";

    public static readonly Dictionary<string, string> Labels = new()
    {
        { Parody,    "Parodies" },
        { Character, "Characters" },
        { Tag,       "Tags" },
        { Artist,    "Artists" },
        { Group,     "Groups" },
        { Language,  "Languages" },
        { Category,  "Categories" }
    };

    public static readonly Dictionary<string, int> Order = new()
    {
        { Parody,    1 },
        { Character, 2 },
        { Tag,       3 },
        { Artist,    4 },
        { Group,     5 },
        { Language,  6 },
        { Category,  7 }
    };

    public static string GetLabel(string type) =>
        Labels.TryGetValue(type.ToLower(), out var label) ? label : type;

    public static int GetOrder(string type) =>
        Order.TryGetValue(type.ToLower(), out var order) ? order : 99;
}
