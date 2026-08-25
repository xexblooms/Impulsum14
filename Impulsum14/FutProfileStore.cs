using System.Text.Json;

namespace Impulsum14;

internal sealed class FutClub
{
    public bool Established { get; set; } = false;   // false => new player, no club yet
    public long EstablishedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // unix seconds when the club was created
    public long TeamId { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Abbr { get; set; } = "";
    public int BadgeId { get; set; } = 0;
    public int StadiumId { get; set; } = 0;
    public int KitId { get; set; } = 0;

    public long ActiveStadiumId { get; set; } = 6200047;
    public long ActiveBallId { get; set; } = 8120092;
    public long ActiveHomeKitId { get; set; } = 6300070;
    public long ActiveAwayKitId { get; set; } = 6400073;
    public long ActiveBadgeId { get; set; } = 6000070;

    public FutClub()
    {
        try
        {
            var rnd = new Random();
            var badges = ClubItems.Catalog.Where(c => c.Type == "badge").ToArray();
            if (badges.Length > 0)
            {
                var b = badges[rnd.Next(badges.Length)];
                ActiveBadgeId = b.ResourceId;
                BadgeId = b.AssetId;
                TeamId = b.TeamId != 0 ? b.TeamId : b.AssetId;
            }
            var kits = ClubItems.Catalog.Where(c => c.Type == "kit").ToArray();
            var nonRareKits = kits.Where(k => k.Rare == 0).ToArray();
            if (nonRareKits.Length > 0) ActiveHomeKitId = nonRareKits[rnd.Next(nonRareKits.Length)].ResourceId;
            var rareKits = kits.Where(k => k.Rare == 1).ToArray();
            if (rareKits.Length > 0) ActiveAwayKitId = rareKits[rnd.Next(rareKits.Length)].ResourceId;
            var stadiums = ClubItems.Catalog.Where(c => c.Type == "stadium" && (c.Rating < 75 || c.Rare == 0)).ToArray();
            if (stadiums.Length > 0) ActiveStadiumId = stadiums[rnd.Next(stadiums.Length)].ResourceId;
        }
        catch { }
    }
}

internal sealed class FutSeason
{
    public int SeasonId { get; set; } = 1;
    public int Points { get; set; } = 0;
    public int GamesPlayed { get; set; } = 0;
    public int GamesWon { get; set; } = 0;
    public int GamesLost { get; set; } = 0;
    public int GamesDraw { get; set; } = 0;
    public int TitlesWon { get; set; } = 0;
    public int Promotions { get; set; } = 0;
    public int Relegations { get; set; } = 0;
    public int Coins { get; set; } = 0;
    public bool Completed { get; set; } = false;
}

internal sealed class SavedTournament
{
    public int Round { get; set; } = 1;             
    public int DataVersion { get; set; } = 1;
    public string TournamentData { get; set; } = "";
    public int ProgressDataVersion { get; set; } = 1;
    public string ProgressData { get; set; } = "";
    public bool Active { get; set; } = false;         
    public bool Won { get; set; } = false;         
}

internal sealed class MarketOfferState
{
    public long Bid { get; set; }
    public long OfferedItemId { get; set; }
    public long AcceptAtUnix { get; set; }
    public long SettledAt { get; set; }
}

internal sealed class FutProfile
{
    public long NucleusId { get; set; } = 1000;
    public string PersonaName { get; set; } = UserConfig.Username;
    public bool IsReturningUser { get; set; } = false;   // new player by default
    public long Coins { get; set; } = 0;
    public long FifaPoints { get; set; } = 0;
    public FutClub Club { get; set; } = new();
    public int OfflineDivision { get; set; } = 1;   
    public int OnlineDivision { get; set; } = 1;         
    public int TrophiesWon { get; set; } = 0;            // FUT offline tournament cups won (unlocks higher cups)
    public int Wins { get; set; } = 0;        
    public int Draws { get; set; } = 0;
    public int Losses { get; set; } = 0;
    public FutSeason Season { get; set; } = new();
    public string SeasonSaveBlob { get; set; } = "";     // client's encoded season save (captured, never advertised back)
    // In-progress offline tournaments, keyed by tournament id -> the client's saved bracket state.
    public Dictionary<int, SavedTournament> SavedTournaments { get; set; } = new();
    public Dictionary<int, int> PacksSinceSpecial { get; set; } = new();

    public long MarketHeldCoins { get; set; } = 0;
    public Dictionary<long, long> MarketMyBids { get; set; } = new();            // tradeId -> bid amount
    public List<long> MarketWatched { get; set; } = new();                         // transfer target tradeIds
    public Dictionary<long, MarketOfferState> MarketAcceptedOffers { get; set; } = new();
    public List<long> MarketRefundedBids { get; set; } = new();
    public Dictionary<long, long> MarketBoughtAt { get; set; } = new();           // market index g -> cycle k
}

internal static class FutProfileStore
{
    private static readonly object _lock = new();
    private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "Profile", "fut_profile.json");
    private static readonly FutProfile _profile = Load();

    public static FutProfile Get()
    {
        lock (_lock) return _profile;
    }

    public static void Mutate(Action<FutProfile> change)
    {
        lock (_lock)
        {
            change(_profile);
            Save();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _profile.IsReturningUser = false;
            _profile.Coins = 0;
            _profile.FifaPoints = 0;
            _profile.Club = new FutClub();
            _profile.OfflineDivision = 1; 
            _profile.OnlineDivision = 1;    
            _profile.TrophiesWon = 0;
            _profile.Wins = 0;
            _profile.Draws = 0;
            _profile.Losses = 0;
            _profile.Season = new FutSeason();
            _profile.SeasonSaveBlob = "";
            _profile.SavedTournaments = new();
            Save();
        }
    }

    private static FutProfile Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<FutProfile>(File.ReadAllText(_path)) ?? new FutProfile();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FutProfile] failed to load {_path}, using defaults: {ex.GetType().Name}: {ex.Message}");
        }
        return new FutProfile();
    }

    private static void Save()
    {
        try
        {
            Market.SnapshotInto(_profile);   // keep watchlist/bids/escrow across restarts
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FutProfile] failed to save {_path}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
