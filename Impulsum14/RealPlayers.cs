namespace Impulsum14;

internal readonly record struct RealPlayer(
    int Id, string Name, int TeamId, int NationId, string Position, int Rating, int Potential,
    int Pace, int Shooting, int Passing, int Dribbling, int Defending, int Physical, int Rare,
    int Strength = 0, int BallControl = 0, int ShotPower = 0, int SkillMoves = 0, int FkAccuracy = 0)
{
    public int ResourceId { get; init; }

    public string Set { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int CardId => ResourceId != 0 ? ResourceId : Id;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSpecial => ResourceId != 0;
}

internal readonly record struct ClubItem(long ItemId, RealPlayer Player, int Pile);

internal sealed class Squad
{
    public int Id { get; set; }
    public string Name { get; set; } = "FUT14 FC";
    public string Formation { get; set; } = "f442";
    public int Chemistry { get; set; }
    public int StarRating { get; set; }
    public long ManagerId { get; set; } = 0;
    public System.Collections.Generic.Dictionary<int, long> Slots { get; set; } = new();
    public System.Collections.Generic.Dictionary<int, int> KitNumbers { get; set; } = new();
}

internal static class RealPlayers
{
    internal static readonly RealPlayer[] All = Load();

    private static RealPlayer[] Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "FUTDB", "players.tsv");
        var list = new List<RealPlayer>();
        try
        {
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                if (c.Length < 13) continue;
                int Col(int i) => c.Length > i && c[i].Length > 0 && int.TryParse(c[i], out int v) ? v : 0;
                list.Add(new RealPlayer(
                    int.Parse(c[0]), "", int.Parse(c[1]), int.Parse(c[2]), c[3],
                    int.Parse(c[4]), int.Parse(c[5]), int.Parse(c[6]), int.Parse(c[7]),
                    int.Parse(c[8]), int.Parse(c[9]), int.Parse(c[10]), int.Parse(c[11]),
                    int.Parse(c[12]),
                    Col(13), Col(14), Col(15), Col(16), Col(17)));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RealPlayers] FAILED to load {path}: {ex.GetType().Name}: {ex.Message}");
        }
        return list.ToArray();
    }
}
