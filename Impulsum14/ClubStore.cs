using System.Text.Json;

namespace Impulsum14;

internal sealed class ClubData
{
    public List<ClubItem> Inventory { get; set; } = new();
    public List<CosmeticItem> Cosmetics { get; set; } = new();
    public List<ConsumableItem> Consumables { get; set; } = new();
    public List<Manager> Managers { get; set; } = new();
    public List<StaffCard> Staff { get; set; } = new();
    public List<Squad> Squads { get; set; } = new();
    public int ActiveSquadId { get; set; } = 0;
    public bool Seeded { get; set; } = false;
    public bool AllPlayersSeeded { get; set; } = false;
    public bool StaffSeeded { get; set; } = false;
    public int StaffVersion { get; set; } = 0;
    public int ConsumablesVersion { get; set; } = 0;
    public Dictionary<long, PlayerMod> PlayerMods { get; set; } = new();
    public Dictionary<long, Auction> Listings { get; set; } = new();
    public long TradeIdSeq { get; set; } = 1_000_000_000;
    public long MarketBuySeq { get; set; } = 850_000_000;
}

internal sealed class Auction
{
    public long ItemId { get; set; }
    public long TradeId { get; set; }
    public string Kind { get; set; } = "player";   // player | cosmetic | consumable | staff
    public int StartingBid { get; set; }
    public int BuyNowPrice { get; set; }
    public int CurrentBid { get; set; }
    public long ExpiresAtUnix { get; set; }
    public string State { get; set; } = "active";
    public long ListedAtUnix { get; set; }   
    public long BotBuyAtUnix { get; set; }  
    public long BotBidCeiling { get; set; }   
    public int Offers { get; set; }   
    public long SoldFor { get; set; }   
}

internal sealed class PlayerMod
{
    public int PlayStyle { get; set; } = -1;      
    public string Position { get; set; } = "";     
    public int Contract { get; set; } = -1;       
    public int Fitness { get; set; } = -1;  
    public int[] AttrBoost { get; set; } = new int[6]; 
    public int TrainingFlag { get; set; } = 0;    
    public string Injury { get; set; } = "";
    public int InjuryGames { get; set; } = 0;
    public int Suspension { get; set; } = 0;       
    public int YellowCards { get; set; } = 0;    
}

internal static class ClubStore
{
    private static readonly object _lock = new();
    private static readonly string _path = Path.Combine(AppContext.BaseDirectory, "Profile", "club_data.json");
    private static readonly ClubData _data = Load();

    static ClubStore()
    {
        MigrateItemIds();

        if (Environment.GetEnvironmentVariable("FUT_SEED_CLUB") == "1")
        {
            if (!_data.AllPlayersSeeded)
                SeedWholeDatabase();
            else if (!_data.Seeded)
                Seed();

            SeedSpecials();
            SeedCosmetics();
            SeedConsumables();
            SeedStaff();
            return;
        }

        if (_data.AllPlayersSeeded || !FutProfileStore.Get().Club.Established)
        {
            lock (_lock)
            {
                bool had = _data.Inventory.Count > 0 || _data.Squads.Count > 0 || _data.Cosmetics.Count > 0
                           || _data.Consumables.Count > 0 || _data.Staff.Count > 0;
                _data.Inventory.Clear();
                _data.Cosmetics.Clear();
                _data.Consumables.Clear();
                _data.Staff.Clear();
                _data.Squads.Clear();
                _data.ActiveSquadId = 0;
                _data.AllPlayersSeeded = false;
                _data.Seeded = false;
                _data.StaffSeeded = false;
                _data.StaffVersion = 0;
                _data.ConsumablesVersion = 0;
                if (had) Save();
            }
            Console.WriteLine("[Club] empty club (fresh account / cleared stale seed)");
            MigrateCollections();
            return;
        }

        MigrateCollections();
        MigrateStaff();
    }

    private static readonly HashSet<string> StarterDef = new() { "CB", "RB", "LB", "RWB", "LWB" };
    private static readonly HashSet<string> StarterMid = new() { "CM", "CDM", "CAM", "RM", "LM" };
    private static readonly HashSet<string> StarterAtt = new() { "ST", "CF", "RW", "LW", "RF", "LF" };

    private static readonly string[] Formation442 =
    {
        "GK", "RB", "CB", "CB", "LB", "RM", "CM", "CM", "LM", "ST", "ST",   // XI
        "GK", "CB", "LB", "CM", "RM", "ST", "LW",                           // bench
        "CB", "RB", "CM", "CAM", "ST",                                      // reserves
    };

    private const int XiSlots = 11;

    public static void SeedStarterSquad(Random rnd = null)
    {
        rnd ??= new Random();
        lock (_lock)
        {
            // Fresh club -> clean slate, then grant the squad.
            _data.Inventory.Clear();
            _data.Cosmetics.Clear();
            _data.Consumables.Clear();
            _data.Staff.Clear();
            _data.Squads.Clear();
            var club = FutProfileStore.Get().Club;
            long newBadge = club.ActiveBadgeId;
            long newHomeKit = club.ActiveHomeKitId;
            long newAwayKit = club.ActiveAwayKitId;
            long newStadium = club.ActiveStadiumId;
            long newBall = club.ActiveBallId;

            long nextCosmeticId = ClubItems.CosmeticItemIdBase + 500000;
            void Grant(long resId) {
                var it = ClubItems.Catalog.FirstOrDefault(c => c.ResourceId == resId);
                if (it.ResourceId == resId) _data.Cosmetics.Add(it with { ItemId = nextCosmeticId++ });
            }
            Grant(newBadge);
            Grant(newHomeKit);
            Grant(newAwayKit);
            Grant(newStadium);
            Grant(newBall);


            var tiers = new int[Formation442.Length];
            var xiDraw = Enumerable.Range(0, XiSlots).OrderBy(_ => rnd.Next()).ToArray();
            tiers[xiDraw[0]] = 2; tiers[xiDraw[1]] = 2;
            var free = Enumerable.Range(0, tiers.Length).Where(i => tiers[i] == 0).ToArray();
            tiers[free[rnd.Next(free.Length)]] = 2;

            int silverSlot = -1;
            if (rnd.NextDouble() < 0.6)
            {
                var bronzeSlots = Enumerable.Range(0, tiers.Length).Where(i => tiers[i] == 0).ToArray();
                silverSlot = bronzeSlots[rnd.Next(bronzeSlots.Length)];
                tiers[silverSlot] = 1;
            }

            int rareSlot = -1;
            if (rnd.NextDouble() < 0.20)
            {
                if (silverSlot >= 0 && rnd.NextDouble() < 0.4)
                {
                    rareSlot = silverSlot;
                }
                else
                {
                    var bronze = Enumerable.Range(0, tiers.Length).Where(i => tiers[i] == 0).ToArray();
                    if (bronze.Length > 0) rareSlot = bronze[rnd.Next(bronze.Length)];
                }
            }

            var used = new HashSet<int>();
            var chosen = new List<RealPlayer>();
            for (int i = 0; i < Formation442.Length; i++)
                chosen.Add(PickStarterPlayer(Formation442[i], tiers[i], i == rareSlot, used, rnd));

            for (int i = 0; i < chosen.Count; i++)
                _data.Inventory.Add(new ClubItem(ItemIds.For(chosen[i]), chosen[i], 7));

            string clubName = FutProfileStore.Get().Club.Name;
            
            var bzSvManagers = Managers.All.Where(m => m.Rating <= 74).ToArray();
            Manager pickedManager = bzSvManagers.Length > 0 ? bzSvManagers[rnd.Next(bzSvManagers.Length)] : default;
            long managerItemId = 0;
            if (pickedManager.ResourceId != 0)
            {
                _data.Managers.Add(pickedManager);
                managerItemId = Impulsum14.WebServer.ManagerItemIdBase + Array.IndexOf(Managers.All, pickedManager);
            }

            var squad = new Squad { Id = 0, Name = string.IsNullOrWhiteSpace(clubName) ? "Squad 1" : clubName, Formation = "f442", ManagerId = managerItemId };
            for (int i = 0; i < chosen.Count; i++) squad.Slots[i] = ItemIds.For(chosen[i]);
            _data.Squads.Add(squad);
            _data.ActiveSquadId = 0;
            Save();
            string colours = string.Join("", tiers.Select(t => t == 2 ? "G" : t == 1 ? "S" : "b"));
            Console.WriteLine($"[Club] starter squad granted: {chosen.Count} players " +
                              $"(XI|bench|reserves {colours[..XiSlots]}|{colours[XiSlots..18]}|{colours[18..]})" +
                              (silverSlot < 0 ? ", no silver" : "") +
                              (rareSlot >= 0 ? $", one rare in slot {rareSlot}" : ", no rares"));
        }
    }

    private static RealPlayer PickStarterPlayer(string pos, int tier, bool wantRare, HashSet<int> used, Random rnd)
    {
        Func<RealPlayer, bool> inGroup =
            pos == "GK"              ? p => p.Position == "GK" :
            StarterDef.Contains(pos) ? p => StarterDef.Contains(p.Position) :
            StarterMid.Contains(pos) ? p => StarterMid.Contains(p.Position) :
                                       p => StarterAtt.Contains(p.Position);
        Func<RealPlayer, bool> inTier = tier switch
        {
            2 => p => p.Rating >= 75 && p.Rating <= 76 && p.Rare == 0,
            1 => p => p.Rating >= 65 && p.Rating <= 74,
            _ => p => p.Rating >= 50 && p.Rating <= 64,
        };

        var pool = RealPlayers.All.Where(p => !used.Contains(p.Id) && p.Position == pos && inTier(p)).ToArray();
        if (pool.Length == 0)
            pool = RealPlayers.All.Where(p => !used.Contains(p.Id) && inGroup(p) && inTier(p)).ToArray();

        if (tier != 2 && pool.Length > 0)
        {
            var pref = pool.Where(p => (p.Rare != 0) == wantRare).ToArray();
            if (pref.Length > 0) pool = pref;
        }
        if (pool.Length == 0)
            pool = RealPlayers.All.Where(p => !used.Contains(p.Id) && inGroup(p)).ToArray();
        if (pool.Length == 0)
            pool = RealPlayers.All.Where(p => !used.Contains(p.Id)).ToArray();

        var pick = pool[rnd.Next(pool.Length)];
        used.Add(pick.Id);
        return pick;
    }

    private static void MigrateCollections()
    {
        lock (_lock)
        {
            int dropped = _data.Cosmetics.RemoveAll(
                c => c.Category == 0 || (c.Type == "ball" && c.ResourceId < ClubItems.BallIdFloor));
            if (dropped > 0)
            {
                Console.WriteLine($"[Club] dropped {dropped} stale club items");
                Save();
            }
            int promoted = 0;
            for (int i = 0; i < _data.Inventory.Count; i++)
                if (_data.Inventory[i].Pile == 0)
                {
                    _data.Inventory[i] = new ClubItem(_data.Inventory[i].ItemId, _data.Inventory[i].Player, 6);
                    promoted++;
                }
            if (promoted > 0)
            {
                Console.WriteLine($"[Club] promoted {promoted} unassigned items into the club");
                Save();
            }
        }
    }

    private const int CurrentConsumablesVersion = 1;

    private static void SeedConsumables()
    {
        lock (_lock)
        {
            var catalog = ConsumableItems.Catalog;
            _data.Consumables.Clear();
            _data.Consumables.AddRange(catalog);
            _data.ConsumablesVersion = CurrentConsumablesVersion;
            if (catalog.Length > 0)
                Console.WriteLine($"[Club] consumables: {catalog.Length}");
            Save();
        }
    }

    private static void MigrateConsumables()
    {
        lock (_lock)
        {
            if (_data.ConsumablesVersion >= CurrentConsumablesVersion || ConsumableItems.Catalog.Length == 0)
                return;
            int had = _data.Consumables.Count;
            _data.Consumables.Clear();
            _data.Consumables.AddRange(ConsumableItems.Catalog);
            _data.ConsumablesVersion = CurrentConsumablesVersion;
            Save();
            Console.WriteLine($"[Club] consumables re-seeded to full catalog: {had} -> {_data.Consumables.Count}");
        }
    }

    private static void SeedCosmetics()
    {
        lock (_lock)
        {
            var catalog = ClubItems.Catalog;
            _data.Cosmetics.Clear();
            _data.Cosmetics.AddRange(catalog);
            if (catalog.Length > 0)
                Console.WriteLine($"[Club] club items: {catalog.Length}");
            Save();
        }
    }

    private const int CurrentStaffVersion = 8;
    private const int StarterStaffPerKind = 5;

    private static void SeedStaff()
    {
        lock (_lock)
        {
            var catalog = Staff.All;
            _data.Staff.Clear();
            _data.Staff.AddRange(catalog);
            _data.StaffSeeded = true;
            _data.StaffVersion = CurrentStaffVersion;
            if (catalog.Length > 0)
                Console.WriteLine($"[Club] staff (full debug catalog): {catalog.Length}");
            Save();
        }
    }

    private static void GrantStarterStaff(Random rnd)
    {
        foreach (var kind in Staff.All.Select(s => s.ItemType).Distinct())
            _data.Staff.AddRange(Staff.All.Where(s => s.ItemType == kind)
                                          .OrderBy(_ => rnd.Next())
                                          .Take(StarterStaffPerKind));
        _data.StaffSeeded = true;
        _data.StaffVersion = CurrentStaffVersion;
    }

    private static void MigrateStaff()
    {
        lock (_lock)
        {
            if (_data.StaffVersion >= CurrentStaffVersion || Staff.All.Length == 0) return;
            int had = _data.Staff.Count;
            _data.Staff.Clear();
            _data.Staff.AddRange(Staff.All);
            _data.StaffSeeded = true;
            _data.StaffVersion = CurrentStaffVersion;
            Save();
            Console.WriteLine($"[Club] staff re-seeded from corrected catalog: {had} -> {_data.Staff.Count}");
        }
    }

    private static void SeedSpecials()
    {
        lock (_lock)
        {
            var specials = SpecialCards.All;
            int before = _data.Inventory.RemoveAll(c => c.Player.IsSpecial);
            for (int i = 0; i < specials.Length; i++)
                _data.Inventory.Add(new ClubItem(ItemIds.For(specials[i]), specials[i], 6));   // 6 = club

            var live = new HashSet<long>(_data.Inventory.Select(c => c.ItemId));
            foreach (var squad in _data.Squads)
                foreach (int idx in squad.Slots.Where(s => !live.Contains(s.Value)).Select(s => s.Key).ToList())
                    squad.Slots.Remove(idx);

            if (before > 0 || specials.Length > 0)
                Console.WriteLine($"[Club] special cards: removed {before}, added {specials.Length}");
            Save();
        }
    }

    private static void SeedWholeDatabase()
    {
        lock (_lock)
        {
            _data.Inventory.Clear();
            _data.Squads.Clear();

            var all = RealPlayers.All;
            var defPos = new HashSet<string> { "CB", "RB", "LB", "RWB", "LWB" };
            var midPos = new HashSet<string> { "CM", "CDM", "CAM", "RM", "LM" };
            var attPos = new HashSet<string> { "ST", "CF", "RW", "LW", "RF", "LF" };
            var xi = new List<RealPlayer>();
            var inXi = new HashSet<int>();
            void Pick(Func<RealPlayer, bool> where, int n)
            {
                foreach (var p in all.Where(p => !inXi.Contains(p.Id) && where(p))
                                     .OrderByDescending(p => p.Rating).Take(n))
                { xi.Add(p); inXi.Add(p.Id); }
            }
            Pick(p => p.Position == "GK", 1);
            Pick(p => defPos.Contains(p.Position), 4);
            Pick(p => midPos.Contains(p.Position), 4);
            Pick(p => attPos.Contains(p.Position), 2);
            Pick(_ => true, 11 - xi.Count);   // top up if any category came short

            foreach (var p in all)
                _data.Inventory.Add(new ClubItem(ItemIds.For(p), p, inXi.Contains(p.Id) ? 7 : 6));   // 7 = in squad, 6 = club

            if (xi.Count > 0)
            {
                var squad = new Squad { Id = 0, Name = "FUT14 FC", Formation = "f442" };
                for (int i = 0; i < xi.Count; i++) squad.Slots[i] = ItemIds.For(xi[i]);   // slot 0 = GK, then def/mid/att
                _data.Squads.Add(squad);
            }
            _data.ActiveSquadId = 0;
            _data.Seeded = true;
            _data.AllPlayersSeeded = true;
            Save();
            Console.WriteLine($"[Club] seeded {_data.Inventory.Count} players; starting XI = {xi.Count}");
        }
    }

    public static void Wipe()
    {
        lock (_lock)
        {
            _data.Inventory.Clear();
            _data.Cosmetics.Clear();
            _data.Consumables.Clear();
            _data.Managers.Clear();
            _data.Staff.Clear();
            _data.Squads.Clear();
            _data.ActiveSquadId = 0;
            _data.Seeded = false;
            _data.AllPlayersSeeded = false;
            _data.StaffSeeded = false;
            _data.StaffVersion = 0;
            _data.ConsumablesVersion = 0;
            _data.PlayerMods.Clear();
            _data.Listings.Clear();
            _data.TradeIdSeq = 1_000_000_000;
            _data.MarketBuySeq = 850_000_000;
            Save();
        }
        Console.WriteLine("[Club] wiped (club deleted)");
    }

    public static ClubData Get()
    {
        lock (_lock) return _data;
    }

    public static void Mutate(Action<ClubData> change)
    {
        lock (_lock)
        {
            change(_data);
            Save();
        }
    }

    private static void Seed()
    {
        lock (_lock)
        {
            _data.Seeded = true;
            Save();
        }
    }

    public static long NextPlayerItemId(ClubData data)
    {
        long highest = data.Inventory.Where(c => ItemIds.IsPackItem(c.ItemId))
                                     .Select(c => c.ItemId)
                                     .DefaultIfEmpty(ItemIds.PackItemBase - 1)
                                     .Max();
        long id = Math.Max(data.MarketBuySeq, highest + 1);
        data.MarketBuySeq = id + 1;
        return id;
    }

    private static void MigrateItemIds()
    {
        lock (_lock)
        {
            var remap = new Dictionary<long, long>();
            var occupant = new Dictionary<long, int>();   // canonical id -> index into kept
            var kept = new List<ClubItem>();
            int dupCopies = 0, collisionsSplit = 0;

            long idFloor = Math.Max(_data.MarketBuySeq,
                _data.Inventory.Where(c => ItemIds.IsPackItem(c.ItemId))
                               .Select(c => c.ItemId)
                               .DefaultIfEmpty(ItemIds.PackItemBase - 1)
                               .Max() + 1);
            long nextFreeId = idFloor;

            foreach (var item in _data.Inventory)
            {
                long canonical = ItemIds.IsPackItem(item.ItemId) ? item.ItemId : ItemIds.For(item.Player);
                if (item.ItemId != canonical) remap[item.ItemId] = canonical;

                if (occupant.TryGetValue(canonical, out int keptIdx))
                {
                    if (kept[keptIdx].Player.CardId == item.Player.CardId)
                    {
                        if (item.Pile > kept[keptIdx].Pile)
                            kept[keptIdx] = new ClubItem(canonical, item.Player, item.Pile);
                        dupCopies++;
                        continue;
                    }
                    long freshId = nextFreeId++;
                    occupant[freshId] = kept.Count;
                    kept.Add(new ClubItem(freshId, item.Player, item.Pile));
                    collisionsSplit++;
                    continue;
                }
                occupant[canonical] = kept.Count;
                kept.Add(new ClubItem(canonical, item.Player, item.Pile));
            }

            bool changed = remap.Count > 0 || collisionsSplit > 0 || dupCopies > 0
                           || kept.Count != _data.Inventory.Count;
            foreach (var squad in _data.Squads)
                foreach (int idx in squad.Slots.Keys.ToList())
                {
                    long id = squad.Slots[idx];
                    if (remap.TryGetValue(id, out long mapped))
                    {
                        squad.Slots[idx] = mapped;
                        changed = true;
                    }
                    else if (id > 0 && id < ItemIds.PlayerBase
                             && ItemIds.TryResolve(ItemIds.PlayerBase + id, out _))
                    {
                        squad.Slots[idx] = ItemIds.PlayerBase + id;
                        changed = true;
                    }
                }

            long target = Math.Max(idFloor, nextFreeId);
            if (target > _data.MarketBuySeq) { _data.MarketBuySeq = target; changed = true; }

            if (!changed) return;
            _data.Inventory.Clear();
            _data.Inventory.AddRange(kept);

            string backup = Path.Combine(AppContext.BaseDirectory, "Profile", "club_data.pre-stableid.json");
            try
            {
                if (File.Exists(_path) && !File.Exists(backup)) File.Copy(_path, backup);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Club] could not back up {_path}: {ex.GetType().Name}: {ex.Message}");
            }

            Save();
            Console.WriteLine($"[Club] player item IDs migrated: {remap.Count} remapped" +
                              (collisionsSplit > 0 ? $", {collisionsSplit} ID collision(s) split (both players kept)" : "") +
                              (dupCopies > 0 ? $", {dupCopies} true duplicate copy/copies collapsed" : ""));
        }
    }

    private static ClubData Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ClubData>(File.ReadAllText(_path)) ?? new ClubData();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Club] failed to load {_path}, using defaults: {ex.GetType().Name}: {ex.Message}");
        }
        return new ClubData();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Club] failed to save {_path}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
