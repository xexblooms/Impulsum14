using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Impulsum14;

internal sealed class WebServer
{
    private readonly ILogger _log;
    private readonly int _port;
    private readonly string _contentRoot;
    private TcpListener _listener = null!;

    private string _lastPackItemList = "";
    private string _lastPurchaseResponseBody = "";

    private readonly object _pendingLock = new();
    private readonly List<(long Id, string Json)> _pendingPackItems = new();
    private readonly List<(long NewId, long OwnedId)> _pendingDuplicates = new();


    public WebServer(int port, ILogger log)
    {
        _port = port;
        _log = log;
        _contentRoot = FindContentRoot();
        _log.LogInformation("OSDK web content root: {0}", _contentRoot);
    }

    private static string FindContentRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var web = Path.Combine(dir, "web");
            if (Directory.Exists(web))
                return web;
            dir = Directory.GetParent(dir)?.FullName ?? dir;
        }
        return Path.Combine(AppContext.BaseDirectory, "web");
    }

    public void StartBotMarketLoop()
    {
        var th = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    SettleWonBids(now);
                    SettleBotBuys(now);
                    SettleExpiredListings(now);
                    SettleAcceptedOffers(now);
                    ReconcileBids(now);
                    Market.TryAdvanceFeed(now);
                    Market.RefreshLiveTotal(now);
                }
                catch (Exception ex)
                {
                    _log.LogError("bot market tick failed: {0}", ex.Message);
                }
                Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "BotMarketSim" };
        th.Start();
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        try { _listener.Start(); }
        catch (SocketException ex) { _log.LogError("WebServer failed to bind :{Port} ({Error})", _port, ex.Message); return; }

        _log.LogInformation("OSDK web listener up on http://127.0.0.1:{0}/", _port);

        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { break; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = new BufferedStream(client.GetStream(), 16384);
            try
            {
                while (true)
                {
                    WebReq req;
                    try { req = await ReadRequestAsync(stream); }
                    catch { break; }
                    if (req is null) break;   // connection closed

                    bool keepAlive = !string.Equals(req.Headers["Connection"], "close", StringComparison.OrdinalIgnoreCase);
                    byte[] response;
                    try { response = BuildResponse(req, keepAlive); }
                    catch (Exception ex)
                    {
                        _log.LogWarning("WebServer handler error: {0}", ex.Message);
                        response = BuildBytes("500 Internal Server Error", "text/plain", Array.Empty<byte>(), null, false);
                        keepAlive = false;
                    }

                    await stream.WriteAsync(response);
                    await stream.FlushAsync();
                    if (!keepAlive) break;
                }
            }
            catch (Exception ex) { _log.LogWarning("WebServer connection error: {0}", ex.Message); }
        }
    }

    private static async Task<WebReq> ReadRequestAsync(Stream stream)
    {
        var header = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1));
            if (n == 0) return null;
            header.Add(one[0]);
            int c = header.Count;
            if (c >= 4 && header[c - 1] == 10 && header[c - 2] == 13 && header[c - 3] == 10 && header[c - 4] == 13) break;
            if (c > 65536) return null;
        }

        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            int idx = lines[i].IndexOf(':');
            if (idx > 0) headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        string body = "";
        if (int.TryParse(headers["Content-Length"], out int len) && len > 0)
        {
            var buf = new byte[len];
            int got = 0;
            while (got < len)
            {
                int n = await stream.ReadAsync(buf.AsMemory(got, len - got));
                if (n == 0) break;
                got += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, got);
        }

        return new WebReq(parts[0], parts[1], headers, body);
    }

    private byte[] BuildResponse(WebReq req, bool keepAlive)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[WEB] {req.HttpMethod} {req.RawUrl}");
        foreach (string h in req.Headers)
            sb.AppendLine($"        {h}: {req.Headers[h]}");
        if (req.Body.Length > 0)
            sb.AppendLine($"      body({req.Body.Length}): {Trim(req.Body, 2048)}");
        _log.LogInformation(sb.ToString().TrimEnd());

        string lp = (req.Url?.AbsolutePath ?? "").ToLowerInvariant();
        var extra = new NameValueCollection();

        int i2014 = lp.IndexOf("/2014/", StringComparison.Ordinal);
        if (i2014 >= 0)   // serve any real live-content file we have (roster .bin, metadata/fixtures .json, gotw assets)
        {
            string rel = (req.Url?.AbsolutePath ?? "").Substring(i2014 + 6).TrimStart('/');
            string root = Path.GetFullPath(_contentRoot);
            string file = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            {
                var fbytes = File.ReadAllBytes(file);
                string ct = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".json" => "application/json; charset=utf-8",
                    ".xml"  => "text/xml; charset=utf-8",
                    ".png"  => "image/png",
                    _       => "application/octet-stream",
                };
                _log.LogInformation("      -> static {0} ({1} bytes)", rel, fbytes.Length);
                return BuildBytes("200 OK", ct, fbytes, extra, keepAlive);
            }
        }

        const string dmPrefix = "/fut/dynamicmessages/";
        int idm = lp.IndexOf(dmPrefix, StringComparison.Ordinal);
        if (idm >= 0)
        {
            string rel = (req.Url?.AbsolutePath ?? "").Substring(idm + dmPrefix.Length).TrimStart('/');
            string root = Path.GetFullPath(Path.Combine(_contentRoot, "dynamicmessages"));
            string file = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
            {
                var fbytes = File.ReadAllBytes(file);
                string ct = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".json" => "application/json; charset=utf-8",
                    ".xml"  => "text/xml; charset=utf-8",
                    ".png"  => "image/png",
                    _       => "application/octet-stream",
                };
                _log.LogInformation("      -> dynamicmessages {0} ({1} bytes)", rel, fbytes.Length);
                return BuildBytes("200 OK", ct, fbytes, extra, keepAlive);
            }
            _log.LogWarning("      -> dynamicmessages MISS (no mirror for {0})", rel);

            if (rel.StartsWith("fut/items/pc/", StringComparison.OrdinalIgnoreCase)
                && rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(System.IO.Path.GetFileNameWithoutExtension(rel), out int trophyId)
                && trophyId >= 8200000)
            {
                if (trophyId >= 8202000)
                {
                    string sjson = Seasons.TrophyJson(trophyId - 8202000);
                    _log.LogInformation("      -> season trophy item json {0} entry={1}", rel, trophyId - 8202000);
                    return BuildBytes("200 OK", "application/json; charset=utf-8",
                                      System.Text.Encoding.UTF8.GetBytes(sjson), extra, keepAlive);
                }
                int tourneyId = trophyId - 8200000;   // trophyResourceId 8200000+S -> tournamentId S
                Tournaments.ActiveTournamentId = tourneyId;
                string tjson = Tournaments.TrophyJson(tourneyId);
                _log.LogInformation("      -> trophy item json {0} tournamentId={1} (TOURNY_LOC key)",
                    rel, tourneyId);
                return BuildBytes("200 OK", "application/json; charset=utf-8",
                                  System.Text.Encoding.UTF8.GetBytes(tjson), extra, keepAlive);
            }

            if (rel.StartsWith("fut/items/", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("      -> 404 (CDN-style miss for {0})", rel);
                return BuildBytes("404 Not Found", "text/plain; charset=utf-8",
                                  System.Text.Encoding.UTF8.GetBytes("Not Found"), extra, keepAlive);
            }
        }

        var (contentType, payloadStr) = Route(req);

        if (lp.Contains("/rs4") || lp.StartsWith("/fut") || lp.Contains("accountinfo") || lp.Contains("/ut/"))
        {
            long nucleusId = ParseLong(req.Headers["Easw-Session-Data-Nucleus-Id"], BlazePersonaId);
            extra["sid"] = SessionId;
            extra["EASW-Session"] = SessionId;
            extra["EASW-Token"] = SessionId;
            extra["EASW-Userid"] = nucleusId.ToString();
            extra["X-UT-SID"] = SessionId;
            extra["X-POW-SID"] = SessionId;
        }

        if (lp.Contains("/pow/"))
        {
            extra["Access-Control-Allow-Origin"] = "*";
            extra["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
            extra["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-HTTP-Method-Override";
            extra["X-Pow-Sid"] = PowSid;
        }

        var payload = Encoding.UTF8.GetBytes(payloadStr);

        if (lp.Contains("/pow/") && payload.Length > 0 &&
            (req.Headers["Accept-Encoding"] ?? "").Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(payload, 0, payload.Length);
            extra["X-Unzippedlength"] = payload.Length.ToString();
            extra["Content-Encoding"] = "gzip";
            payload = ms.ToArray();
        }

        string status = "200 OK";
        if ((lp.EndsWith("/user") || lp.EndsWith("/userdata")) && !FutProfileStore.Get().Club.Established && !lp.Contains("/delete/game/"))
            status = "465 Tutorial";

        if (payloadStr.Length > 0)
            _log.LogInformation("      -> [{0}] {1} resp({2}): {3}", status, contentType, payload.Length, Trim(payloadStr, 2048));

        return BuildBytes(status, contentType, payload, extra, keepAlive);
    }

    private static byte[] BuildBytes(string status, string contentType, byte[] body, NameValueCollection extra, bool keepAlive)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r")).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (extra != null)
            foreach (string k in extra.Keys)
                if (k != null) sb.Append(k).Append(": ").Append(extra[k]).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var result = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(body, 0, result, head.Length, body.Length);
        return result;
    }

    // Lightweight stand-in for HttpListenerRequest so the routing code below is unchanged.
    private sealed class WebReq
    {
        public string HttpMethod { get; }
        public string RawUrl { get; }
        public Uri Url { get; }
        public NameValueCollection Headers { get; }
        public NameValueCollection QueryString { get; }
        public string Body { get; }

        public WebReq(string method, string rawUrl, NameValueCollection headers, string body)
        {
            HttpMethod = method;
            RawUrl = rawUrl;
            Headers = headers;
            Body = body ?? "";
            Url = new Uri("http://localhost" + (rawUrl.StartsWith('/') ? rawUrl : "/" + rawUrl), UriKind.Absolute);
            QueryString = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            foreach (var part in Url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq >= 0) QueryString[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
                else QueryString[Uri.UnescapeDataString(part)] = "";
            }
        }
    }

    private (string, string) Route(WebReq req)
    {
        string path = (req.Url?.AbsolutePath ?? "").ToLowerInvariant();
        bool wantsJson = (req.Headers["Accept"] ?? "").Contains("json", StringComparison.OrdinalIgnoreCase);

        // FUT/EASFC accountinfo. This is the EASFC backend ("rs4") handshake — the persona
        // here MUST match the one we authenticated over Blaze (AuthenticationComponent:
        // personaId=1000, name="FUT14"), or EASFC can't associate the session and shows
        // "unable to connect". The client sends its id in Easw-Session-Data-Nucleus-Id.
        // Field names confirmed in fifa14.exe: userAccountInfo/personas/personaId/
        // personaName/userClubList. Empty userClubList = new FUT user (no club yet).
        if (path.Contains("/pow/"))
            return ("application/json; charset=utf-8", PowBody(path, req));

        if (path.Contains("purchasegroup"))
            return ("application/json; charset=utf-8", StorePurchaseGroupBody());

        if (path.Contains("store/transaction"))
            return ("application/json; charset=utf-8", NoTransactionBody());

        if (path.Contains("/match/reset"))
        {
            long balMR = FutProfileStore.Get().Coins;
            return ("application/json; charset=utf-8",
                    "{\"allCoins\":" + balMR + ",\"credits\":" + balMR + ",\"coins\":" + balMR +
                    ",\"currencies\":" + CurrenciesJson(balMR) + "}");
        }
        if (path.Contains("/match/end") || path.Contains("destroymatch"))
        {
            string endReason = BodyRx(req.Body, "\"endReason\"\\s*:\\s*\"([^\"]*)\"");
            bool isWin  = endReason.Equals("WIN",  StringComparison.OrdinalIgnoreCase);
            bool isDraw = endReason.Equals("DRAW", StringComparison.OrdinalIgnoreCase)
                       || endReason.Equals("TIE",  StringComparison.OrdinalIgnoreCase);
            bool isDnf = endReason.Equals("DNF", StringComparison.OrdinalIgnoreCase);
            int myGoals = 0;
            var msm = System.Text.RegularExpressions.Regex.Match(req.Body, "\"myMatchStats\"\\s*:\\s*\\{([^}]*)\\}");
            if (msm.Success && int.TryParse(BodyRx(msm.Groups[1].Value, "\"goals\"\\s*:\\s*(\\d+)"), out int g)) myGoals = g;

            int matchCoins = (isWin ? 500 : isDraw ? 300 : 200) + myGoals * 20;

            bool credited = req.HttpMethod is "PUT" or "POST";
            int tournamentCoins = 0;
            int? awardedCup = null;
            long balME = FutProfileStore.Get().Coins;
            if (credited)
            {
                if (!isDnf) ApplyMatchConsequences(req.Body);
                if (Tournaments.CurrentMatchTournamentId is int tid)
                {
                    var (prize, wonFinal) = Tournaments.SettleTournamentMatch(tid, endReason);
                    tournamentCoins = prize;
                    if (wonFinal) awardedCup = tid;
                    Tournaments.CurrentMatchTournamentId = null;   // guard against a double-settle
                }
                int total = matchCoins + tournamentCoins;
                bool trophy = tournamentCoins > 0;
                FutProfileStore.Mutate(p =>
                {
                    p.Coins += total;
                    if (trophy) p.TrophiesWon++;
                    if (!isDnf) { if (isWin) p.Wins++; else if (isDraw) p.Draws++; else p.Losses++; }   // hub W-D-L record
                    balME = p.Coins;
                });
            }

            if (awardedCup is int wonId)
                _log.LogInformation("[FUT] tournament {0} WON -> +{1} match +{2} cup coins; trophies={3}; balance {4}",
                    wonId, matchCoins, tournamentCoins, FutProfileStore.Get().TrophiesWon, balME);
            else
                _log.LogInformation("[FUT] match/end ({0}, {1} goals): +{2} coins -> {3}",
                    string.IsNullOrEmpty(endReason) ? "?" : endReason, myGoals, credited ? matchCoins : 0, balME);
            return ("application/json; charset=utf-8", MatchEndBody(balME, matchCoins, tournamentCoins));
        }

        if (path.EndsWith("/match/ready") ||
            (path.EndsWith("/match") && req.HttpMethod == "PUT"))
        {
            long tsMR = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var readySquad = ResolveMatchSquad(req.Body);
            return ("application/json; charset=utf-8",
                    "{\"startDateTime\":" + tsMR + ",\"squad\":" + BuildFullSquadJson(readySquad) + "}");
        }

        if (path.EndsWith("/match"))
        {
            if (req.HttpMethod == "POST")
            {
                string t = BodyRx(req.Body, "\"tournamentId\"\\s*:\\s*(\\d+)");
                Tournaments.CurrentMatchTournamentId = (int.TryParse(t, out int ti) && ti > 0) ? ti : (int?)null;
            }
            long tsCM = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int matchId = System.Threading.Interlocked.Increment(ref _matchIdSeq);
            var matchSquad = ResolveMatchSquad(req.Body);
            return ("application/json; charset=utf-8",
                    "{\"id\":" + matchId + ",\"matchId\":" + matchId +
                    ",\"matchLengthMin\":6,\"matchlength\":6,\"matchDifficulty\":3" +
                    ",\"startDateTime\":" + tsCM +
                    ",\"squad\":" + BuildFullSquadJson(matchSquad) + "}");
        }

        if (path.Contains("tournament") || path.EndsWith("/teams"))
        {
            if (path.EndsWith("/teams") || path.Contains("tournamentteams"))
            {
                int gid = int.TryParse(req.QueryString["groupId"], out int g) ? g : 0;
                return ("application/json; charset=utf-8", Tournaments.TeamsJson(gid));
            }

            if (path.Contains("tournament/user"))
            {
                string tail = path[(path.LastIndexOf('/') + 1)..];
                bool haveId = int.TryParse(tail, out int tid) ||
                              (req.HttpMethod is "POST" or "PUT" &&
                               int.TryParse(BodyRx(req.Body, "\"tournamentId\"\\s*:\\s*(\\d+)"), out tid));
                if (haveId)
                {
                    Tournaments.ActiveTournamentId = tid;
                    if (req.HttpMethod is "POST" or "PUT")           // client saving its own bracket
                    {
                        int round = int.TryParse(BodyRx(req.Body, "\"round\"\\s*:\\s*(\\d+)"), out int rd) ? rd : 1;
                        int dv = int.TryParse(BodyRx(req.Body, "\"dataVersion\"\\s*:\\s*(\\d+)"), out int d) ? d : 1;
                        int pdv = int.TryParse(BodyRx(req.Body, "\"progressDataVersion\"\\s*:\\s*(\\d+)"), out int pv) ? pv : 1;
                        string echo = Tournaments.SaveProgress(tid, round, dv,
                            Tournaments.CaptureString(req.Body, "tournamentData"), pdv,
                            Tournaments.CaptureString(req.Body, "progressData"));
                        return ("application/json; charset=utf-8", echo);
                    }
                    return ("application/json; charset=utf-8", Tournaments.UserTournamentJson(tid));
                }
                if (req.HttpMethod is "POST" or "PUT")
                    return ("application/json; charset=utf-8", "{}");
                return ("application/json; charset=utf-8", Tournaments.UserListJson());
            }

            if (path.Contains("/schedule"))
                return ("application/json; charset=utf-8", "{\"schedule\":[]}");

            if (req.HttpMethod is "POST" or "PUT")
                return ("application/json; charset=utf-8", "{\"tournament\":[]}");

            if (path.Contains("/delete"))
                return ("application/json; charset=utf-8", "{}");

            return ("application/json; charset=utf-8", Tournaments.CatalogJson());
        }

        if (path.Contains("/season") || path.Contains("/division/"))
        {
            if (path.Contains("/squad/unlock"))
                return ("application/json; charset=utf-8", "{}");

            if (path.Contains("reset"))
            {
                int nd = Seasons.ParseResetDivision(path);
                if (nd >= 0) FutProfileStore.Mutate(p => p.OfflineDivision = nd);
                return ("application/json; charset=utf-8",
                        Seasons.ResetJson(nd >= 0 ? nd : FutProfileStore.Get().OfflineDivision));
            }

            if (path.Contains("season/history"))
                return ("application/json; charset=utf-8", Seasons.HistoryJson());

            if (path.EndsWith("/user") || path.Contains("season/user"))
            {
                if (req.HttpMethod is "PUT" or "POST")
                    FutProfileStore.Mutate(p => Seasons.CaptureSave(p, req.Body));
                return ("application/json; charset=utf-8", Seasons.UserJson(FutProfileStore.Get()));
            }

            // The division ladder catalog (season/list, and any other season GET).
            return ("application/json; charset=utf-8", Seasons.ListJson());
        }

        if (path.EndsWith("/accountinfo"))
        {
            long nucleusId = ParseLong(req.Headers["Easw-Session-Data-Nucleus-Id"], BlazePersonaId);
            return ("application/json; charset=utf-8",
                    "{\"userAccountInfo\":" + UserAccountInfoJson(nucleusId) + "}");
        }

        if (path.EndsWith("/auth") && (path.Contains("rs4") || path.Contains("/ut")))
            return ("application/json; charset=utf-8", "{\"sid\":\"" + SessionId + "\"}");

        if (path.EndsWith("dimerouting.xml") || path.EndsWith("cfgrouting.xml"))
            return ServeFile("dimerouting.xml", "text/xml; charset=utf-8");

        if (path.EndsWith("futboot.xml"))
            return ServeFile("futBoot.xml", "text/xml; charset=utf-8");

        if (path.EndsWith("/rosterupdate") || path.Contains("rosterupdate.xml"))
            return ServeFile("rosterupdate.xml", "text/xml; charset=utf-8");

        if (path.Contains("dimecfg.xml"))
            return ServeFile("dimecfg.xml", "text/xml; charset=utf-8");

        if (path.Contains("storecfg.xml"))
            return ServeFile("storecfg.xml", "text/xml; charset=utf-8");

        if (path.Contains("storedesc"))
            return ServeFile("storedesc.xml", "text/xml; charset=utf-8");

        if (path.Contains("sponsoredevents") || path.Contains("events_list.xml"))
            return ServeFile("events_list.xml", "text/xml; charset=utf-8");

        if (path.Contains("audiodnplist.csv"))
            return ServeFile("audioDNPList.csv", "text/csv; charset=utf-8");

        if (path.EndsWith("/marketfeed"))
        {
            return ("application/json; charset=utf-8", Market.FeedJson());
        }

        if (path.Contains("/trusteddevice"))
            return ("application/json; charset=utf-8",
                    "{\"changed\":false,\"exists\":true,\"locked\":false,\"trusted\":true}");

        if (path.EndsWith("/settings"))
            return ("application/json; charset=utf-8",
                    "{\"configs\":[" +
                    "{\"value\":1,\"type\":\"tokenRedemptionEnabled\"}," +
                    "{\"value\":1,\"type\":\"fifaPointsCancelTransactionFix\"}," +
                    "{\"value\":5,\"type\":\"clubCreateThreshold\"}," +
                    "{\"value\":90,\"type\":\"getOperationTimeoutSec\"}," +
                    "{\"value\":100,\"type\":\"maximumTradePileSize\"}]}");

        if (path.EndsWith("/hub"))
        {
            long hubNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var profHub = FutProfileStore.Get();
            long coinsHub = profHub.Coins - Market.HeldCoins;   // available = balance minus escrowed bids
            string currenciesHub = CurrenciesJson(coinsHub);
            var wl = Market.WatchlistCounts(hubNow);
            long liveListings = Market.LiveTotal(hubNow);
            var hubClub = ClubStore.Get();
            int clubPlayers = hubClub.Inventory.Count;
            int tradePileCount = hubClub.Inventory.Count(c => c.Pile == 3)
                + hubClub.TransferList.Count
                + hubClub.Listings.Values.Count(au => au.Kind != "player");
            int tradeSelling = hubClub.Listings.Values.Count(au => au.State == "active");
            int tradeSold = hubClub.Listings.Values.Count(au => au.State == "sold");
            int tradeNotification = tradeSold + wl.Outbid;
            string totwHub = ",\"squad\":" + Totw.HubSquadJson();
            return ("application/json; charset=utf-8",
                    "{\"credits\":" + coinsHub + ",\"currencies\":" + currenciesHub +
                    ",\"divisionOnline\":" + profHub.OnlineDivision +
                    ",\"divisionOffline\":" + profHub.OfflineDivision +
                    ",\"userInfo\":{\"personaId\":" + BlazePersonaId + ",\"clubName\":\"" + Esc(profHub.Club.Name) +
                    "\",\"clubAbbr\":\"" + Esc(profHub.Club.Abbr) + "\",\"assetId\":" + profHub.Club.BadgeId + ",\"badgeId\":" + profHub.Club.BadgeId +
                    ",\"won\":" + profHub.Wins + ",\"draw\":" + profHub.Draws + ",\"loss\":" + profHub.Losses +
                    ",\"established\":\"" + profHub.Club.EstablishedAt + "\",\"credits\":" + coinsHub + ",\"currencies\":" + currenciesHub +
                    ",\"unassignedPileSize\":0,\"unopenedPacks\":{\"preOrderPacks\":0,\"recoveredPacks\":0}}" +
                    totwHub +
                    ",\"clubPlayers\":" + clubPlayers +
                    ",\"auctionCount\":" + liveListings + ",\"tradePile\":{\"selling\":" + tradeSelling +
                    ",\"sold\":" + tradeSold + ",\"count\":" + tradePileCount + ",\"notification\":" + tradeNotification + "}" +
                    ",\"watchlist\":{\"winning\":" + wl.Winning + ",\"count\":" + wl.Count + ",\"outbid\":" + wl.Outbid +
                    ",\"notification\":" + wl.Outbid + "}}");
        }

        if (path.EndsWith("/clubuser"))
        {
            var pcUser = FutProfileStore.Get().Club;
            var prUser = new StringBuilder();
            prUser.Append("{\"personaId\":" + FutSquadPersonaId + ",\"clubName\":\"" + Esc(pcUser.Name) +
                "\",\"clubAbbr\":\"" + Esc(pcUser.Abbr) + "\",\"teamId\":" + pcUser.TeamId);
            prUser.Append(",\"badge\":" + ClubVisualNode("badge", pcUser.ActiveBadgeId));
            prUser.Append(",\"homekit\":" + ClubVisualNode("kit", pcUser.ActiveHomeKitId));
            prUser.Append(",\"awaykit\":" + ClubVisualNode("kit", pcUser.ActiveAwayKitId));
            prUser.Append('}');
            return ("application/json; charset=utf-8",
                    "{\"user\":[" + prUser + "," +
                    "{\"personaId\":" + Totw.ClubPersona + ",\"persona\":\"TOTW\",\"public\":true}]}");
        }

        if (path.Contains("/user/list"))
        {
            string q = req.Url?.Query ?? "";
            if (q.Contains(Totw.ClubPersona.ToString()))
            {
                _log.LogInformation("[TOTW] user/list resolved TOTW club (persona {0})", Totw.ClubPersona);
                return ("application/json; charset=utf-8", Totw.ClubInfoJson());
            }
            return ("application/json; charset=utf-8", "{}");
        }

        if (path.EndsWith("/pilesize"))
        {
            var data = ClubStore.Get();
            int clubPlayers = data.Inventory.Count;
            int activeSquad = data.Inventory.Count(c => c.Pile == 7);
            int tradePile   = data.Inventory.Count(c => c.Pile == 3);
            int consumables = AvailableConsumables().Count;   // catalog + owned, matches /club/consumables/
            _ = tradePile;
            string entries =
                "[{\"key\":1,\"value\":0},{\"key\":2,\"value\":100},{\"key\":3,\"value\":100}," +
                "{\"key\":4,\"value\":100},{\"key\":6,\"value\":" + clubPlayers +
                "},{\"key\":7,\"value\":" + activeSquad + "}]";
            string clientData =
                "[{\"pile\":1,\"count\":0,\"maxCount\":100},{\"pile\":2,\"count\":0,\"maxCount\":100}," +
                "{\"pile\":6,\"count\":" + clubPlayers + ",\"maxCount\":2000}," +
                "{\"pile\":7,\"count\":" + activeSquad + ",\"maxCount\":100}]";
            return ("application/json; charset=utf-8",
                "{\"entries\":" + entries + ",\"pileSizeClientData\":" + clientData +
                ",\"clubSize\":" + clubPlayers + ",\"consumableCount\":" + consumables + "}");
        }

        if (path.EndsWith("/clientdata/totw") && req.HttpMethod == "GET")
            return ("application/json; charset=utf-8", Totw.ChallengeEntriesJson());

        if (path.EndsWith("/totw") && !path.Contains("/clientdata/"))
            return ("application/json; charset=utf-8",
                    req.HttpMethod == "GET" ? Totw.SquadChallengeJson() : "{}");

        if (path.Contains("/clientdata/"))
        {
            string key = path[(path.LastIndexOf("/clientdata/", StringComparison.Ordinal) + "/clientdata/".Length)..];
            if (req.HttpMethod == "PUT" || req.HttpMethod == "POST")
            {
                ClientDataStore.Set(key, req.Body);
                return ("application/json; charset=utf-8", "{}");
            }
            return ("application/json; charset=utf-8", ClientDataStore.Get(key));
        }

        if (path.Contains("/user/credits"))
        {
            long coinsCredits = FutProfileStore.Get().Coins;
            return ("application/json; charset=utf-8",
                    "{\"credits\":" + coinsCredits + ",\"bidTokens\":{},\"currencies\":" + CurrenciesJson(coinsCredits) +
                    ",\"unopenedPacks\":{\"preOrderPacks\":0,\"recoveredPacks\":0},\"futCashBalance\":0}");
        }

        if (path.EndsWith("/auctionhouse") && req.HttpMethod == "POST")
        {
            long auctionItemId = 0;
            int startingBid = 0, buyNowPrice = 0, duration = 3600;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("itemData", out var itd)
                    && itd.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    auctionItemId = idEl.GetInt64();
                if (root.TryGetProperty("startingBid", out var sbEl) && sbEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    startingBid = sbEl.GetInt32();
                if (root.TryGetProperty("buyNowPrice", out var bnEl) && bnEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    buyNowPrice = bnEl.GetInt32();
                if (root.TryGetProperty("duration", out var duEl) && duEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    duration = duEl.GetInt32();
            }
            catch (Exception ex) { _log.LogWarning("[FUT] auctionhouse body parse failed: {0}", ex.Message); }

            if (auctionItemId == 0)
                return ("application/json; charset=utf-8", "{}");

            long newTradeId = 0;
            bool owned = false;
            long ahNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ClubStore.Mutate(data =>
            {
                long floor;
                string kind;
                int idx = data.Inventory.FindIndex(c => c.ItemId == auctionItemId);
                if (idx >= 0)
                {
                    owned = true;
                    kind = "player";
                    if (data.Inventory[idx].Pile != 3)         // listing implies it's on the transfer list
                        data.Inventory[idx] = new ClubItem(auctionItemId, data.Inventory[idx].Player, 3);
                    floor = Market.CheapestLiveBuyNow(data.Inventory[idx].Player, ahNow);
                    if (floor <= 0) floor = Market.MarketValue(data.Inventory[idx].Player);
                }
                else
                {
                    var cos = data.Cosmetics.FirstOrDefault(c => c.ItemId == auctionItemId);
                    if (cos.ItemId == auctionItemId)
                    {
                        owned = true; kind = "cosmetic";
                        floor = Market.CosmeticFloor(cos.AssetId, cos.ResourceId, ahNow);
                    }
                    else
                    {
                        var con = data.Consumables.FirstOrDefault(c => c.ItemId == auctionItemId);
                        if (con.ItemId == auctionItemId)
                        {
                            owned = true; kind = "consumable";
                            floor = Market.ConsumableFloor(con.ResourceId, ahNow);
                        }
                        else
                        {
                            if (ManagerByGlobalId(auctionItemId) is { } mgr && data.Managers.Contains(mgr))
                            {
                                owned = true; kind = "staff";
                                floor = Market.StaffFloor(mgr.ResourceId, ahNow);
                            }
                            else
                            {
                                if (StaffByGlobalId(auctionItemId) is { } stf && data.Staff.Contains(stf))
                                {
                                    owned = true; kind = "staff";
                                    floor = Market.StaffFloor(stf.ResourceId, ahNow);
                                }
                                else return;   // can only list something you own
                            }
                        }
                    }
                }

                newTradeId = data.Listings.TryGetValue(auctionItemId, out var existing)
                    ? existing.TradeId : data.TradeIdSeq++;
                int effPrice = buyNowPrice > 0 ? buyNowPrice : startingBid;
                long sellDelay = Market.UserSaleDelay(effPrice, floor, new Random());
                data.TransferList.Remove(auctionItemId);   // priced/listed -> no longer just "on the transfer list"
                data.Listings[auctionItemId] = new Auction
                {
                    ItemId = auctionItemId,
                    TradeId = newTradeId,
                    Kind = kind,
                    StartingBid = startingBid,
                    BuyNowPrice = buyNowPrice,
                    CurrentBid = 0,
                    ExpiresAtUnix = ahNow + duration,
                    State = "active",
                    ListedAtUnix = ahNow,
                    BotBuyAtUnix = buyNowPrice > 0 && sellDelay > 0 ? ahNow + sellDelay : 0,
                    BotBidCeiling = buyNowPrice <= 0 ? Math.Max(0, floor) : 0,   // pure auction: bots bid up to market price
                    SoldFor = 0,
                };
            });

            if (!owned)
                return ("application/json; charset=utf-8", "{}");
            _log.LogInformation("[FUT] listed item {0}: start {1}, buyNow {2}, {3}s -> tradeId {4}",
                auctionItemId, startingBid, buyNowPrice, duration, newTradeId);
            return ("application/json; charset=utf-8", "{\"id\":" + newTradeId + "}");
        }

        var mBuy = System.Text.RegularExpressions.Regex.Match(path, @"/trade/(\d+)(?:/(?:bid|offer))?$");
        if (mBuy.Success && (req.HttpMethod == "PUT" || req.HttpMethod == "POST"))
        {
            long buyTradeId = long.Parse(mBuy.Groups[1].Value);
            int amount = 0;
            long offeredId = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                var root = doc.RootElement;
                foreach (var key in new[] { "bid", "buyNowPrice", "amount" })
                    if (root.TryGetProperty(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number)
                    { amount = el.GetInt32(); break; }
                if (root.TryGetProperty("itemData", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray())
                        if (it.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        { offeredId = idEl.GetInt64(); break; }
            }
            catch (Exception ex) { _log.LogWarning("[FUT] bid body parse failed: {0}", ex.Message); }
            return MarketBuy(buyTradeId, amount, offeredId);
        }

        if (path.Contains("/trade/status") && req.HttpMethod == "GET")
        {
            var wantIds = new HashSet<long>();
            foreach (string part in (req.QueryString["tradeIds"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part.Trim(), out long tid)) wantIds.Add(tid);
            if (wantIds.Count == 0)
            {
                foreach (var kv in Market.MyBids) wantIds.Add(kv.Key);
                foreach (var kv in Market.AcceptedOffers) wantIds.Add(kv.Key);
                foreach (var kv in Market.Watched) wantIds.Add(kv.Key);
            }

            var data = ClubStore.Get();
            var tsRnd = new Random();
            long tsNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var byTrade = data.Listings.Values.ToDictionary(a => a.TradeId);
            var tsSb = new StringBuilder("[");
            int tsWritten = 0;
            foreach (long want in wantIds)
            {
                string entry = null;
                if (byTrade.TryGetValue(want, out var au))
                {
                    entry = TradePileEntryJson(au.ItemId, au, tsNow, tsRnd);
                }
                else if (want >= Market.TradeIdBase)   // a listing from the simulated market
                {
                    entry = Market.EntryByTradeId(want, tsNow);
                }
                if (entry == null) continue;
                if (tsWritten++ > 0) tsSb.Append(',');
                tsSb.Append(entry);
            }
            tsSb.Append(']');
            long tsCoins = FutProfileStore.Get().Coins - Market.HeldCoins;
            return ("application/json; charset=utf-8",
                    "{\"auctionInfo\":" + tsSb + ",\"duplicateItemIdList\":null,\"errorState\":null," +
                    "\"credits\":" + tsCoins + ",\"totalCredits\":" + tsCoins + ",\"coins\":" + tsCoins +
                    ",\"currencies\":" + CurrenciesJson(tsCoins) + ",\"bidTokens\":{}}");
        }

        if (path.Contains("/watchlist") && req.HttpMethod is "PUT" or "DELETE")
        {
            long tid = long.TryParse(req.QueryString["tradeId"], out long tq) ? tq : 0;
            if (tid == 0)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                    if (doc.RootElement.TryGetProperty("auctionInfo", out var ai)
                        && ai.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var el in ai.EnumerateArray())
                        {
                            if (el.TryGetProperty("tradeId", out var tEl)
                                && tEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            { tid = tEl.GetInt64(); break; }
                            if (el.TryGetProperty("id", out var idEl)
                                && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            { tid = idEl.GetInt64(); break; }
                        }
                }
                catch (Exception ex) { _log.LogWarning("[FUT] watchList body parse failed: {0}", ex.Message); }
            }
            if (tid >= Market.TradeIdBase)   // sim-market listings only; own listings are watched via the trade pile
            {
                if (req.HttpMethod == "PUT")
                {
                    Market.Watched.TryAdd(tid, 0);
                    _log.LogInformation("[Market] WATCH trade {0} added to transfer targets", tid);
                }
                else
                {
                    Market.Watched.TryRemove(tid, out _);
                    _log.LogInformation("[Market] WATCH trade {0} removed from transfer targets", tid);
                }
                FutProfileStore.Mutate(_ => { });   // persist watchlist across restarts
            }
            return ("application/json; charset=utf-8", "{}");
        }

        if (path.Contains("/watchlist") && req.HttpMethod == "GET")
        {
            int wlOffset = int.TryParse(req.QueryString["offset"], out int wof) ? wof : 0;
            int wlCount = int.TryParse(req.QueryString["count"], out int wcnt) ? wcnt : 50;
            long wlNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var watched = new List<long>();
            foreach (var kv in Market.MyBids) watched.Add(kv.Key);
            foreach (var kv in Market.AcceptedOffers)
                if (!watched.Contains(kv.Key)) watched.Add(kv.Key);
            foreach (var kv in Market.Watched)
                if (!watched.Contains(kv.Key)) watched.Add(kv.Key);

            var wlSb = new StringBuilder("[");
            int wlWritten = 0;
            foreach (long tid in watched.Skip(wlOffset).Take(wlCount))
            {
                string entry = Market.EntryByTradeId(tid, wlNow);
                if (entry == null) continue;
                if (wlWritten++ > 0) wlSb.Append(',');
                wlSb.Append(entry);
            }
            wlSb.Append(']');
            long wlCoins = FutProfileStore.Get().Coins - Market.HeldCoins;
            return ("application/json; charset=utf-8",
                    "{\"auctionInfo\":" + wlSb + ",\"total\":" + watched.Count +
                    ",\"duplicateItemIdList\":[],\"errorState\":null,\"credits\":" + wlCoins +
                    ",\"totalCredits\":" + wlCoins + ",\"coins\":" + wlCoins +
                    ",\"currencies\":" + CurrenciesJson(wlCoins) + ",\"bidTokens\":{}}");
        }

        if (path.EndsWith("/tradepile"))
        {
            long coinsTrade = FutProfileStore.Get().Coins - Market.HeldCoins;
            var tpData = ClubStore.Get();
            var tpRnd = new Random();
            long tpNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tpSb = new StringBuilder("[");
            int tpWritten = 0;
            foreach (var it in tpData.Inventory.Where(c => c.Pile == 3))
            {
                tpData.Listings.TryGetValue(it.ItemId, out var auP);
                string entry = TradePileEntryJson(it.ItemId, auP, tpNow, tpRnd);
                if (entry == null) continue;
                if (tpWritten++ > 0) tpSb.Append(',');
                tpSb.Append(entry);
            }
            var npIds = new HashSet<long>(tpData.TransferList);
            foreach (var au in tpData.Listings.Values)
                if (au.Kind != "player") npIds.Add(au.ItemId);
            foreach (long npId in npIds)
            {
                tpData.Listings.TryGetValue(npId, out var auN);
                string entry = TradePileEntryJson(npId, auN, tpNow, tpRnd);
                if (entry == null) continue;
                if (tpWritten++ > 0) tpSb.Append(',');
                tpSb.Append(entry);
            }
            tpSb.Append(']');
            return ("application/json; charset=utf-8",
                    "{\"errorState\":null,\"credits\":" + coinsTrade + ",\"auctionInfo\":" + tpSb +
                    ",\"currencies\":" + CurrenciesJson(coinsTrade) +
                    ",\"duplicateItemIdList\":[],\"bidTokens\":null,\"maxAuctionsAllowed\":30," +
                    "\"maximumTradePileSize\":100,\"total\":" + tpWritten + "}");
        }

        {
            var mDelTrade = System.Text.RegularExpressions.Regex.Match(path, @"/delete/game/.+/trade/(\d+)$");
            if (mDelTrade.Success)
            {
                long delTradeId = long.Parse(mDelTrade.Groups[1].Value);
                ClubStore.Mutate(data =>
                {
                    foreach (var kv in data.Listings.Where(kv => kv.Value.TradeId == delTradeId).ToList())
                    {
                        bool sold = kv.Value.State == "sold";
                        data.Listings.Remove(kv.Key);
                        data.TransferList.Remove(kv.Key);
                        switch (kv.Value.Kind)
                        {
                            case "player":
                            {
                                int delIdx = data.Inventory.FindIndex(c => c.ItemId == kv.Key);
                                if (delIdx < 0) break;
                                if (sold)
                                    data.Inventory.RemoveAt(delIdx);   // bot bought it -> card leaves the club
                                else
                                    data.Inventory[delIdx] = new ClubItem(kv.Key, data.Inventory[delIdx].Player, 6);
                                break;
                            }
                            case "cosmetic":
                                if (sold) data.Cosmetics.RemoveAll(c => c.ItemId == kv.Key);
                                break;
                            case "consumable":
                                if (sold) data.Consumables.RemoveAll(c => c.ItemId == kv.Key);
                                break;
                            case "staff":
                            {
                                if (ManagerByGlobalId(kv.Key) is { } dmgr)
                                {
                                    int clubIdx = data.Managers.IndexOf(dmgr);
                                    if (clubIdx >= 0 && sold) data.Managers.RemoveAt(clubIdx);
                                }
                                else if (StaffByGlobalId(kv.Key) is { } dstf)
                                {
                                    int clubIdx = data.Staff.IndexOf(dstf);
                                    if (clubIdx >= 0 && sold) data.Staff.RemoveAt(clubIdx);
                                }
                                break;
                            }
                        }
                    }
                });
                _log.LogInformation("[FUT] cleared trade {0} from the transfer list", delTradeId);
                return ("application/json; charset=utf-8", "{}");
            }
        }

        if (path.EndsWith("/club/stats/consumables"))
            return ("application/json; charset=utf-8", ConsumableStatsJson());

        if (path.EndsWith("/club/stats/year"))
            return ("application/json; charset=utf-8", ClubStatBlockJson(2, 2014));

        if (path.Contains("/club/consumables"))
        {
            int cCount = int.TryParse(req.QueryString["count"], out int ccl) ? ccl : 500;
            int cOff = int.TryParse(req.QueryString["start"], out int coff) ? coff : 0;
            string tab = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
            var filter = ConsumableTabFilter(tab);
            var src = filter == null ? AvailableConsumables() : AvailableConsumables().Where(filter).ToList();
            var cons = src.Skip(cOff).Take(cCount).ToArray();
            long dnow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dsb = new StringBuilder("[");
            for (int i = 0; i < cons.Length; i++)
            {
                if (i > 0) dsb.Append(',');
                dsb.Append(ConsumableItems.BuildJson(cons[i], dnow));
            }
            dsb.Append(']');
            return ("application/json; charset=utf-8", "{\"itemData\":" + dsb + "}");
        }

        if (path.EndsWith("/club"))
        {
            if (path.EndsWith("/user/club") && req.Body.Contains("clubName"))
            {
                string oldName = FutProfileStore.Get().Club.Name;
                FutProfileStore.Mutate(p =>
                {
                    p.Club.Established = true;
                    var nm = System.Text.RegularExpressions.Regex.Match(req.Body, "\"clubName\"\\s*:\\s*\"([^\"]*)\"");
                    if (nm.Success && nm.Groups[1].Value.Length > 0) p.Club.Name = nm.Groups[1].Value;
                    var ab = System.Text.RegularExpressions.Regex.Match(req.Body, "\"clubAbbr\"\\s*:\\s*\"([^\"]*)\"");
                    if (ab.Success && ab.Groups[1].Value.Length > 0) p.Club.Abbr = ab.Groups[1].Value;
                });
                string newName = FutProfileStore.Get().Club.Name;
                ClubStore.Mutate(d =>
                {
                    foreach (var sq in d.Squads)
                        if (string.IsNullOrWhiteSpace(sq.Name) || sq.Name == oldName)
                            sq.Name = newName;
                });
                _log.LogInformation("[FUT] club renamed to '{0}'", newName);
            }

            int countLimit = int.TryParse(req.QueryString["count"], out int cl) ? cl : 50;
            int offset = int.TryParse(req.QueryString["start"], out int off) ? off : 0;

            string typeFilter = (req.QueryString["type"] ?? "players").ToLowerInvariant();
            if (typeFilter is "equippables" or "badge" or "kit" or "ball" or "stadium")
            {
                string cosmeticsLevel = (req.QueryString["level"] ?? "").ToLowerInvariant();
                int cosmeticsLeague = int.TryParse(req.QueryString["league"], out int clg) ? clg : -1;
                int cosmeticsTeam = int.TryParse(req.QueryString["team"], out int ctm) ? ctm : -1;
                var cosData = ClubStore.Get();
                var cosmeticsPage = cosData.Cosmetics
                    .Where(c => !cosData.Listings.ContainsKey(c.ItemId) && !cosData.TransferList.Contains(c.ItemId))
                    .Where(c => typeFilter == "equippables" || c.Type == typeFilter)
                    .Where(c => cosmeticsLevel switch
                    {
                        "bronze" => c.Rating < 65,
                        "silver" => c.Rating is >= 65 and < 75,
                        "gold" => c.Rating >= 75,
                        _ => true,
                    })
                    .Where(c => cosmeticsLeague == -1 || TeamLeagues.LeagueOf(c.TeamId) == cosmeticsLeague)
                    .Where(c => cosmeticsTeam == -1 || c.TeamId == cosmeticsTeam)
                    .Skip(offset).Take(countLimit).ToArray();
                var cosClub = FutProfileStore.Get().Club;
                long cnow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var csb = new StringBuilder("[");
                for (int i = 0; i < cosmeticsPage.Length; i++)
                {
                    if (i > 0) csb.Append(',');
                    var c = cosmeticsPage[i];
                    if (c.Type == "ball")
                        Console.WriteLine($"[FUT] BALL BROWSE itemId={c.ItemId} resourceId={c.ResourceId} assetId={c.AssetId}");
                    string cState = c.Type switch
                    {
                        "ball"    => c.ResourceId == cosClub.ActiveBallId ? "activeBall" : "free",
                        "stadium" => c.ResourceId == cosClub.ActiveStadiumId ? "activeStadium" : "free",
                        "kit"     => c.ResourceId == cosClub.ActiveHomeKitId ? "activeHomeKit"
                                   : c.ResourceId == cosClub.ActiveAwayKitId ? "activeAwayKit" : "free",
                        "badge"   => c.ResourceId == cosClub.ActiveBadgeId ? "activeBadge" : "free",
                        _         => "free",
                    };
                    csb.Append(ClubItems.BuildJson(c, cnow, cState));
                }
                csb.Append(']');
                return ("application/json; charset=utf-8", "{\"itemData\":" + csb + "}");
            }
            if (typeFilter == "manager")
            {
                int mgrNation = int.TryParse(req.QueryString["nation"], out int mnf) ? mnf : -1;
                int mgrLeague = int.TryParse(req.QueryString["league"], out int mlf) ? mlf : -1;
                string mgrLevel = (req.QueryString["level"] ?? "").ToLowerInvariant();
                long mnow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return ("application/json; charset=utf-8",
                    "{\"itemData\":" + ManagerItemsJson(offset, countLimit, mnow, 6, mgrNation, mgrLeague, mgrLevel) + "}");
            }
            if (typeFilter == "staff" || typeFilter == "headcoach" || typeFilter == "gkcoach"
                || typeFilter == "physio" || typeFilter == "fitnesscoach")
            {
                string staffTypeFilter = typeFilter switch
                {
                    "headcoach" => "headCoach",
                    "gkcoach" => "gkCoach",
                    "fitnesscoach" => "fitnessCoach",
                    "physio" => "physio",
                    _ => null, // "staff": all managers + staff
                };
                string staffLevel = (req.QueryString["level"] ?? "").ToLowerInvariant();
                long snow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return ("application/json; charset=utf-8",
                    "{\"itemData\":" + StaffItemsJson(offset, countLimit, snow, 6, staffTypeFilter, staffLevel) + "}");
            }

            string posFilter = req.QueryString["position"] ?? "any";
            int nationFilter = int.TryParse(req.QueryString["nation"], out int nf) ? nf : -1;
            int teamFilter = int.TryParse(req.QueryString["team"], out int tf) ? tf : -1;
            string levelFilter = (req.QueryString["level"] ?? "").ToLowerInvariant();
            int leagueFilter = int.TryParse(req.QueryString["league"], out int lf) ? lf : -1;
            int playStyleFilter = int.TryParse(req.QueryString["playStyle"], out int psf) ? psf : 0;

            var clubData = ClubStore.Get();
            var inventory = clubData.Inventory;
            int EffectivePlayStyle(ClubData d, long id) =>
                d.PlayerMods.TryGetValue(id, out var pm) && pm != null && pm.PlayStyle >= 0 ? pm.PlayStyle : 250;
            var matches = inventory
                .Where(c => c.Pile != 3 && c.Pile != 0)   // transfer list AND unassigned items stay out of the club
                .Where(c => (posFilter == "any" || posFilter == "" || EffectivePosition(clubData, c.ItemId, c.Player.Position) == posFilter)
                    && (nationFilter == -1 || c.Player.NationId == nationFilter)
                    && (teamFilter == -1 || c.Player.TeamId == teamFilter)
                    && (leagueFilter == -1 || TeamLeagues.LeagueOf(c.Player.TeamId) == leagueFilter)
                    && (playStyleFilter <= 0 || EffectivePlayStyle(clubData, c.ItemId) == playStyleFilter)
                    && levelFilter switch
                    {
                        "bronze" => c.Player.Rating < 65,
                        "silver" => c.Player.Rating is >= 65 and < 75,
                        "gold" => c.Player.Rating >= 75,
                        _ => true,
                    })
                .DistinctBy(c => c.ItemId)
                .OrderByDescending(c => c.Player.Rating)
                .Skip(offset).Take(countLimit)
                .ToArray();

            var clubRnd = new Random();
            long clubNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var itemsSb = new StringBuilder("[");
            for (int i = 0; i < matches.Length; i++)
            {
                if (i > 0) itemsSb.Append(',');
                itemsSb.Append(BuildRealPlayerItem(clubRnd, matches[i].Player, matches[i].ItemId, clubNow, matches[i].Pile));
            }
            itemsSb.Append(']');
            return ("application/json; charset=utf-8", "{\"itemData\":" + itemsSb + "}");
        }

        if (path.EndsWith("/transfermarket"))
        {
            int tmStart = int.TryParse(req.QueryString["start"], out int ts) ? ts : 0;
            int tmCount = int.TryParse(req.QueryString["num"], out int tc) ? tc : 12;
            long tmNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int tmMinB = int.TryParse(req.QueryString["minb"], out int tmb) ? tmb : 0;
            int tmMaxB = int.TryParse(req.QueryString["maxb"], out int tmxb) ? tmxb : 0;
            int tmMinC = int.TryParse(req.QueryString["micr"], out int tmc) ? tmc : 0;
            int tmMaxC = int.TryParse(req.QueryString["macr"], out int tmxac) ? tmxac : 0;
            string tmSig = Market.SearchSignature(req.QueryString);

            string tmType = (req.QueryString["type"] ?? "").ToLowerInvariant();
            string tmCat = req.QueryString["cat"] ?? "";
            string tmLev = (req.QueryString["lev"] ?? "").ToLowerInvariant();
            string tmPos = req.QueryString["pos"] ?? "";
            int tmStyle = int.TryParse(req.QueryString["playStyle"], out int tms) ? tms : 0;
            long tmDefId = long.TryParse(req.QueryString["maskedDefId"], out long tmd) ? tmd : 0;
            if (tmDefId <= 0) tmDefId = long.TryParse(req.QueryString["definitionId"], out long tdd) ? tdd : 0;
            long tmCoins = FutProfileStore.Get().Coins - Market.HeldCoins;
            if (tmType is "clubinfo" or "stadium" or "stadiums" or "ball" or "balls"
                or "kit" or "kits" or "badge" or "badges" or "custom")
            {
                string eCat = tmType == "clubinfo" ? tmCat : tmType;
                int tmLeag = int.TryParse(req.QueryString["leag"], out int tml) ? tml : 0;
                int tmTeam = int.TryParse(req.QueryString["team"], out int tmt) ? tmt : 0;
                string ePage = Market.ClubItemPageJson(tmStart, tmCount, tmNow, eCat, tmLev,
                    tmLeag, tmTeam, tmMinB, tmMaxB, tmMinC, tmMaxC, tmSig, tmDefId);
                return ("application/json; charset=utf-8",
                        "{\"errorState\":null,\"credits\":" + tmCoins + ",\"auctionInfo\":" + ePage +
                        ",\"duplicateItemIdList\":null,\"bidTokens\":{}}");
            }
            if (tmType == "staff")
            {
                int tmNat = int.TryParse(req.QueryString["nat"], out int tmn) ? tmn : 0;
                int tmLeag = int.TryParse(req.QueryString["leag"], out int tml2) ? tml2 : 0;
                string sPage = Market.StaffPageJson(tmStart, tmCount, tmNow, tmCat, tmLev,
                    tmNat, tmLeag, tmMinB, tmMaxB, tmMinC, tmMaxC, tmSig, tmDefId);
                return ("application/json; charset=utf-8",
                        "{\"errorState\":null,\"credits\":" + tmCoins + ",\"auctionInfo\":" + sPage +
                        ",\"duplicateItemIdList\":null,\"bidTokens\":{}}");
            }
            bool consumableSearch = tmType is "training" or "development"
                                    || tmType.Length == 0 && Market.IsConsumableCat(tmCat);
            if (consumableSearch)
            {
                string cPage = Market.ConsumablePageJson(tmStart, tmCount, tmNow, tmCat, tmLev, tmPos, tmStyle,
                    tmMinB, tmMaxB, tmMinC, tmMaxC, tmSig, tmType, tmDefId);
                return ("application/json; charset=utf-8",
                        "{\"errorState\":null,\"credits\":" + tmCoins + ",\"auctionInfo\":" + cPage +
                        ",\"duplicateItemIdList\":null,\"bidTokens\":{}}");
            }

            var tmMatch = MarketFilter(req.QueryString);
            string[] tmWantPos = MarketWantPositions(req.QueryString);
            string page = Market.PageJson(tmStart, tmCount, tmNow, tmMatch, tmMinB, tmMaxB, tmMinC, tmMaxC, tmStyle, tmSig, tmWantPos);
            return ("application/json; charset=utf-8",
                    "{\"errorState\":null,\"credits\":" + tmCoins + ",\"auctionInfo\":" + page +
                    ",\"duplicateItemIdList\":null,\"bidTokens\":{}}");
        }

        if (path.Contains("/club/stats/newcards"))
        {
            string ncStats = ClubStatBlockJson(5, 0);
            string ncPurchase = _lastPurchaseResponseBody.Length > 0 ? _lastPurchaseResponseBody
                : _lastPackItemList.Length > 0 ? "{\"itemList\":" + _lastPackItemList + "}"
                : "{}";
            return ("application/json; charset=utf-8", ncStats[..^1] + "," + ncPurchase[1..]);
        }

        if ((req.HttpMethod == "POST" || req.HttpMethod == "PUT") &&
            System.Text.RegularExpressions.Regex.IsMatch(path, @"item/(?:resource/)?\d+/?$"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, @"item/(?:resource/)?(\d+)/?$");
            long itemPathNum = 0;
            long.TryParse(m.Groups[1].Value, out itemPathNum);
            bool resourceForm = path.Contains("item/resource/", StringComparison.Ordinal);

            var applyTargets = new List<long>();
            var bodyItemData = new List<long>();
            var bodyItemTypes = new List<string>();
            string activateSlot = "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("activateSlotNumber", out var slotEl)
                    && slotEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    activateSlot = slotEl.GetString() ?? "";
                if (root.TryGetProperty("apply", out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var el in arr.EnumerateArray())
                        if (el.TryGetProperty("id", out var idEl)
                            && idEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            applyTargets.Add(idEl.GetInt64());
                if (root.TryGetProperty("itemData", out var itd)
                    && itd.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var el in itd.EnumerateArray())
                        if (el.TryGetProperty("resourceId", out var rEl)
                            && rEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            bodyItemData.Add(rEl.GetInt64());
                            bodyItemTypes.Add(el.TryGetProperty("itemType", out var tEl)
                                && tEl.ValueKind == System.Text.Json.JsonValueKind.String
                                ? tEl.GetString() ?? "" : "");
                        }
            }
            catch (Exception ex) { _log.LogWarning("[FUT] item apply body parse failed: {0}", ex.Message); }

            if (applyTargets.Count > 0)
            {
                var data0 = ClubStore.Get();
                int owned = resourceForm
                    ? data0.Consumables.FindIndex(c => c.ResourceId == itemPathNum)
                    : data0.Consumables.FindIndex(c => c.ItemId == itemPathNum);
                if (owned < 0 && !resourceForm)
                    owned = data0.Consumables.FindIndex(c => c.ResourceId == itemPathNum);
                if (owned < 0)
                {
                    Console.WriteLine($"[FUT] consumable apply: no owned copy of {(resourceForm ? "resource " : "item ") + itemPathNum} - the menu offered it but the club has none; consuming nothing");
                    return ("application/json; charset=utf-8",
                        "{\"success\":false,\"resourceId\":" + itemPathNum + ",\"itemData\":[]}");
                }
                long useResource = data0.Consumables[owned].ResourceId;
                long usedItemId = data0.Consumables[owned].ItemId;
                if (data0.Listings.ContainsKey(usedItemId) || data0.TransferList.Contains(usedItemId))
                {
                    Console.WriteLine($"[FUT] consumable apply: item {usedItemId} is on the transfer list - ignoring");
                    return ("application/json; charset=utf-8",
                        "{\"success\":false,\"resourceId\":" + itemPathNum + ",\"itemData\":[]}");
                }
                var changedIds = ApplyConsumable(useResource, applyTargets);
                if (changedIds.Count > 0)
                    ClubStore.Mutate(d =>
                    {
                        int at = d.Consumables.FindIndex(c => c.ResourceId == useResource);
                        if (at >= 0) d.Consumables.RemoveAt(at);
                    });
                lock (_pendingLock)
                {
                    _pendingPackItems.RemoveAll(p => p.Id == usedItemId);
                    _pendingDuplicates.RemoveAll(d => d.NewId == usedItemId);
                }
                Console.WriteLine($"[FUT] consumable {useResource} applied to {changedIds.Count} player(s); owned copy {usedItemId} consumed");
                return ("application/json; charset=utf-8", AppliedItemsJson(useResource, changedIds));
            }

            Console.WriteLine($"[FUT] BALL PUT receivedItemId={itemPathNum}");
            CosmeticItem equip = default;
            string equipSource = "";
            var data1 = ClubStore.Get();

            if (!resourceForm)
            {
                equip = data1.Cosmetics.FirstOrDefault(c => c.ItemId == itemPathNum);
                if (equip.ItemId != 0) equipSource = "OWNED";
            }
            if (equip.ItemId == 0 && ClubItems.TryResolveCatalogId(itemPathNum, out var catByPath))
            {
                equip = catByPath;
                equipSource = "CATALOG";
            }
            if (equip.ItemId == 0 && !resourceForm
                && itemPathNum >= ClubItems.ActiveItemIdBase
                && itemPathNum < ClubItems.ActiveItemIdBase + 5)
            {
                long activeRes = itemPathNum switch
                {
                    800001 => FutProfileStore.Get().Club.ActiveStadiumId,
                    800002 => FutProfileStore.Get().Club.ActiveBallId,
                    800003 => FutProfileStore.Get().Club.ActiveHomeKitId,
                    800004 => FutProfileStore.Get().Club.ActiveAwayKitId,
                    _      => FutProfileStore.Get().Club.ActiveBadgeId,
                };
                equip = data1.Cosmetics.FirstOrDefault(c => c.ResourceId == activeRes);
                if (equip.ItemId == 0)
                {
                    var activeCat = ClubItems.Catalog.FirstOrDefault(c => c.ResourceId == activeRes);
                    if (activeCat.ItemId != 0) equip = activeCat;
                }
                if (equip.ItemId != 0) equipSource = "ACTIVE";
            }
            if (equip.ItemId == 0 && resourceForm)
            {
                equip = data1.Cosmetics.FirstOrDefault(c => c.ResourceId == itemPathNum);
                if (equip.ItemId != 0) equipSource = "OWNED";
            }
            if (equip.ItemId == 0 && bodyItemData.Count > 0)
            {
                for (int bi = 0; bi < bodyItemData.Count && equip.ItemId == 0; bi++)
                {
                    string ty = string.Equals(bodyItemTypes[bi], "custom", StringComparison.OrdinalIgnoreCase)
                        ? "badge" : bodyItemTypes[bi];
                    var ownedByRes = ty.Length == 0
                        ? data1.Cosmetics.FirstOrDefault(c => c.ResourceId == bodyItemData[bi])
                        : data1.Cosmetics.FirstOrDefault(c => c.ResourceId == bodyItemData[bi]
                            && string.Equals(c.Type, ty, StringComparison.OrdinalIgnoreCase));
                    if (ownedByRes.ItemId != 0) { equip = ownedByRes; equipSource = "OWNED"; }
                    else
                    {
                        var catalogByRes = ty.Length == 0
                            ? ClubItems.Catalog.FirstOrDefault(c => c.ResourceId == bodyItemData[bi])
                            : ClubItems.Catalog.FirstOrDefault(c => c.ResourceId == bodyItemData[bi]
                                && string.Equals(c.Type, ty, StringComparison.OrdinalIgnoreCase));
                        if (catalogByRes.ItemId != 0) { equip = catalogByRes; equipSource = "CATALOG"; }
                    }
                }
            }
            if (equip.ItemId != 0 && (data1.Listings.ContainsKey(equip.ItemId) || data1.TransferList.Contains(equip.ItemId)))
            {
                Console.WriteLine($"[FUT] club item apply: id {itemPathNum} is on the transfer list - ignoring");
                return ("application/json; charset=utf-8",
                    "{\"success\":false,\"resourceId\":" + itemPathNum + ",\"itemData\":[]}");
            }
            if (equip.ItemId == 0)
            {
                Console.WriteLine($"[FUT] club item apply: id {itemPathNum} is neither an owned item nor a known catalogue id - doing nothing");
                return ("application/json; charset=utf-8",
                    "{\"success\":false,\"resourceId\":" + itemPathNum + ",\"itemData\":[]}");
            }
            string equipType = equip.Type;
            long equipRes = equip.ResourceId;
            if (equipType == "ball")
                Console.WriteLine($"[FUT] BALL RESOLVE source={equipSource} itemId={equip.ItemId} resourceId={equipRes} assetId={equip.AssetId}");
            ClubStore.Mutate(d =>
            {
                if (!d.Cosmetics.Any(c => c.ResourceId == equipRes && string.Equals(c.Type, equipType, StringComparison.OrdinalIgnoreCase)))
                    d.Cosmetics.Add(equip);
            });
            FutProfileStore.Mutate(p =>
            {
                if (equipType == "badge")
                {
                    p.Club.ActiveBadgeId = equipRes;
                    p.Club.BadgeId = equip.AssetId;   // sync account info (badgeId/assetId in /user, hub, etc.)
                }
                else if (equipType == "kit")
                {
                    if (string.Equals(activateSlot, "102", StringComparison.Ordinal))
                        p.Club.ActiveAwayKitId = equipRes;   // slot 102 = away kit
                    else
                        p.Club.ActiveHomeKitId = equipRes;
                }
                else if (equipType == "stadium") p.Club.ActiveStadiumId = equipRes;
                else if (equipType == "ball") p.Club.ActiveBallId = equipRes;
            });
            lock (_pendingLock)
            {
                _pendingPackItems.RemoveAll(p => p.Id == equip.ItemId);
                _pendingDuplicates.RemoveAll(d => d.NewId == equip.ItemId);
            }
            long enow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var equipped = FutProfileStore.Get().Club;
            Console.WriteLine($"[FUT] ACTIVE CLUB ITEMS LOADED: badge={equipped.ActiveBadgeId} stadium={equipped.ActiveStadiumId} " +
                $"homeKit={equipped.ActiveHomeKitId} awayKit={equipped.ActiveAwayKitId} ball={equipped.ActiveBallId}");
            Console.WriteLine($"[FUT] BALL ACTIVE activeBallItemId={equipped.ActiveBallId}");
            Console.WriteLine($"[FUT] equipped {equipType} {equipRes} (source={equipSource}, slot \"{activateSlot}\")");
            return ("application/json; charset=utf-8",
                "{\"success\":true,\"resourceId\":" + equipRes + ",\"itemData\":[" +
                ClubItems.BuildJson(equip, enow, ClubItems.ActiveStateName(equipType, activateSlot)) + "]}");
        }

        if (path.Contains("/delete/") && path.EndsWith("/item") && req.HttpMethod == "POST")
        {
            var sold = new List<long>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                var root = doc.RootElement;
                if (root.TryGetProperty("itemId", out var itemId))
                {
                    if (itemId.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var el in itemId.EnumerateArray())
                            if (el.ValueKind == System.Text.Json.JsonValueKind.Number) sold.Add(el.GetInt64());
                    else if (itemId.ValueKind == System.Text.Json.JsonValueKind.Number)
                        sold.Add(itemId.GetInt64());
                }
                else if (root.TryGetProperty("itemIds", out var itemIds)
                         && itemIds.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in itemIds.EnumerateArray())
                        if (el.ValueKind == System.Text.Json.JsonValueKind.Number) sold.Add(el.GetInt64());
                }
                else if (root.TryGetProperty("itemData", out var itemData)
                         && itemData.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in itemData.EnumerateArray())
                        if (item.TryGetProperty("id", out var id)
                            && id.ValueKind == System.Text.Json.JsonValueKind.Number)
                            sold.Add(id.GetInt64());
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[FUT] discard body parse failed: {0}", ex.Message);
            }

            long earned = 0;
            ClubStore.Mutate(data =>
            {
                foreach (long id in sold)
                {
                    int idx = data.Inventory.FindIndex(c => c.ItemId == id);
                    if (idx < 0) continue;
                    earned += data.Inventory[idx].Player.Rating * 4;
                    data.Inventory.RemoveAt(idx);
                    data.Listings.Remove(id);   // sold from inventory -> drop any listing
                }
                data.Consumables.RemoveAll(c => sold.Contains(c.ItemId));
                data.Cosmetics.RemoveAll(c => sold.Contains(c.ItemId));
            });
            long balance = 0;
            FutProfileStore.Mutate(p => { p.Coins += earned; balance = p.Coins; });
            lock (_pendingLock)
            {
                _pendingPackItems.RemoveAll(p => sold.Contains(p.Id));
                _pendingDuplicates.RemoveAll(d => sold.Contains(d.NewId));
            }
            _log.LogInformation("[FUT] quick sold {0} item(s) for {1} coins; balance {2}",
                sold.Count, earned, balance);

            var soldSb = new StringBuilder("[");
            for (int i = 0; i < sold.Count; i++)
            {
                if (i > 0) soldSb.Append(',');
                soldSb.Append("{\"id\":" + sold[i] + "}");
            }
            soldSb.Append(']');
            return ("application/json; charset=utf-8",
                    "{\"totalCredits\":" + balance + ",\"currencies\":" + CurrenciesJson(balance) +
                    ",\"items\":" + soldSb + "}");
        }

        if (path.EndsWith("/item") && req.HttpMethod == "GET")
        {
            var wanted = new List<long>();
            foreach (string part in (req.QueryString["idList"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part.Trim(), out long wid)) wanted.Add(wid);

            var clubData = ClubStore.Get();
            var itemInventory = clubData.Inventory;
            var itemCosmetics = clubData.Cosmetics;
            var itemRnd = new Random();
            long itemNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var itemSb = new StringBuilder("[");
            int written = 0;
            foreach (long wid in wanted)
            {
                int at = itemInventory.FindIndex(c => c.ItemId == wid);
                if (at >= 0)
                {
                    if (written > 0) itemSb.Append(',');
                    itemSb.Append(BuildRealPlayerItem(itemRnd, itemInventory[at].Player, wid, itemNow, itemInventory[at].Pile));
                    written++;
                    continue;
                }
                int cat = itemCosmetics.FindIndex(c => c.ItemId == wid);
                if (cat >= 0)
                {
                    if (written > 0) itemSb.Append(',');
                    itemSb.Append(ClubItems.BuildJson(itemCosmetics[cat], itemNow));
                    written++;
                    continue;
                }
                if (wid >= ManagerItemIdBase && wid < StaffItemIdBase)
                {
                    int mi = (int)(wid - ManagerItemIdBase);
                    if (mi >= 0 && mi < clubData.Managers.Count)
                    {
                        if (written > 0) itemSb.Append(',');
                        itemSb.Append(BuildManagerItem(clubData.Managers[mi], wid, itemNow, 6));
                        written++;
                        continue;
                    }
                }
                else if (wid >= StaffItemIdBase && wid < StaffItemIdBase + 10_000)
                {
                    int si = (int)(wid - StaffItemIdBase);
                    if (si >= 0 && si < clubData.Staff.Count)
                    {
                        if (written > 0) itemSb.Append(',');
                        itemSb.Append(BuildStaffItem(clubData.Staff[si], wid, itemNow, 6));
                        written++;
                        continue;
                    }
                }
                if (ItemIds.TryResolve(wid, out RealPlayer player))
                {
                    if (written > 0) itemSb.Append(',');
                    itemSb.Append(BuildRealPlayerItem(itemRnd, player, wid, itemNow, 1));
                    written++;
                    continue;
                }
                string pendingJson = null;
                lock (_pendingLock)
                {
                    int pi = _pendingPackItems.FindIndex(p => p.Id == wid);
                    if (pi >= 0) pendingJson = _pendingPackItems[pi].Json;
                }
                if (pendingJson != null)
                {
                    if (written > 0) itemSb.Append(',');
                    itemSb.Append(pendingJson);
                    written++;
                }
            }
            itemSb.Append(']');
            return ("application/json; charset=utf-8", "{\"itemData\":" + itemSb + "}");
        }

        if (path.EndsWith("/item") && req.HttpMethod == "PUT")
        {
            var moves = new List<(long Id, string Pile)>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                if (doc.RootElement.TryGetProperty("itemData", out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (!el.TryGetProperty("id", out var idEl)) continue;
                        string pileName = el.TryGetProperty("pile", out var pEl)
                            && pEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? pEl.GetString() ?? "club" : "club";
                        moves.Add((idEl.GetInt64(), pileName));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[FUT] item PUT body parse failed: {0}", ex.Message);
            }

            if (moves.Count > 0)
            {
                ClubStore.Mutate(data =>
                {
                    foreach (var (id, pileName) in moves)
                    {
                        int want = pileName switch { "club" => 6, "trade" => 3, _ => 0 };
                        if (want == 0) continue;
                        int idx = data.Inventory.FindIndex(c => c.ItemId == id);
                        if (idx >= 0)
                        {
                            if (data.Inventory[idx].Pile != want)
                                data.Inventory[idx] = new ClubItem(id, data.Inventory[idx].Player, want);
                        }
                        else if (IsClubItem(data, id))
                        {
                            if (want == 3) data.TransferList.Add(id);
                            else { data.TransferList.Remove(id); data.Listings.Remove(id); }
                        }
                        if (want != 3) data.Listings.Remove(id);   // left the transfer list -> drop any listing
                    }
                });
                int left;
                lock (_pendingLock)
                {
                    var claimedIds = new HashSet<long>(moves.Select(m => m.Id));
                    _pendingPackItems.RemoveAll(p => claimedIds.Contains(p.Id));
                    left = _pendingPackItems.Count;
                }
                _log.LogInformation("[FUT] claimed {0} item(s) -> {1}; {2} left to deal with",
                    moves.Count, moves[0].Pile, left);
            }

            var claimed = new StringBuilder("[");
            for (int i = 0; i < moves.Count; i++)
            {
                if (i > 0) claimed.Append(',');
                claimed.Append("{\"id\":" + moves[i].Id + ",\"pile\":\"" + Esc(moves[i].Pile) +
                               "\",\"success\":true}");
            }
            claimed.Append(']');
            return ("application/json; charset=utf-8", "{\"itemData\":" + claimed + "}");
        }

        if (path.Contains("/purchased/items"))
        {
            if (req.HttpMethod == "POST")
            {
                var rnd = new Random();
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                int packId = 0;
                var packIdMatch = System.Text.RegularExpressions.Regex.Match(req.Body, "\"packId\"\\s*:\\s*(\\d+)");
                if (packIdMatch.Success) int.TryParse(packIdMatch.Groups[1].Value, out packId);

                int packPrice = StorePacks.FirstOrDefault(p => p.Id == packId).Coins;
                long coinsAfter = 0;
                FutProfileStore.Mutate(p =>
                {
                    p.Coins = Math.Max(0, p.Coins - packPrice);
                    coinsAfter = p.Coins;
                });
                _log.LogInformation("[FUT] pack {0} opened for {1} coins; balance {2}", packId, packPrice, coinsAfter);

                var picks = PackEngine.Open(packId, rnd, out _);

                var drawn = new List<(long Id, string Json)>();
                var dupes = new List<(long NewId, long OwnedId)>();
                ClubStore.Mutate(data =>
                {
                    var ownedByCard = new Dictionary<int, long>();
                    foreach (var c in data.Inventory)
                        if (OwnedInClub(data, c.ItemId) && !ownedByCard.ContainsKey(c.Player.CardId))
                            ownedByCard[c.Player.CardId] = c.ItemId;

                    long nextPackItemId = ClubStore.NextPlayerItemId(data);

                    foreach (var pick in picks)
                    {
                        switch (pick.Kind)
                        {
                            case PackPick.ItemKind.Player:
                            {
                                long itemId = nextPackItemId++;
                                var player = pick.Player;
                                if (ownedByCard.TryGetValue(player.CardId, out long ownedId))
                                    dupes.Add((itemId, ownedId));
                                else
                                    ownedByCard[player.CardId] = itemId;
                                drawn.Add((itemId, BuildRealPlayerItem(rnd, player, itemId, nowUnix, 1)));
                                data.Inventory.Add(new ClubItem(itemId, player, 6));
                                break;
                            }
                            case PackPick.ItemKind.Consumable:
                            {
                                long id = Interlocked.Increment(ref _nextPackExtraId);
                                var inst = pick.Consumable with { ItemId = id };
                                drawn.Add((id, ConsumableItems.BuildJson(inst, nowUnix)));
                                data.Consumables.Add(inst);
                                break;
                            }
                            case PackPick.ItemKind.Cosmetic:
                            {
                                long id = Interlocked.Increment(ref _nextPackExtraId);
                                var inst = pick.Cosmetic with { ItemId = id };
                                drawn.Add((id, ClubItems.BuildJson(inst, nowUnix)));
                                data.Cosmetics.Add(inst);
                                break;
                            }
                            case PackPick.ItemKind.Manager:
                            {
                                long id = Interlocked.Increment(ref _nextPackExtraId);
                                drawn.Add((id, BuildManagerItem(pick.Manager, id, nowUnix, 6, pick.ManagerRareFlag)));
                                data.Managers.Add(pick.Manager);
                                break;
                            }
                            case PackPick.ItemKind.Staff:
                            {
                                long id = Interlocked.Increment(ref _nextPackExtraId);
                                drawn.Add((id, BuildStaffItem(pick.Staff, id, nowUnix, 6)));
                                data.Staff.Add(pick.Staff);
                                break;
                            }
                        }
                    }
                    if (nextPackItemId > data.MarketBuySeq) data.MarketBuySeq = nextPackItemId;
                });

                var itemIds = new StringBuilder("[");
                var items = new StringBuilder("[");
                for (int i = 0; i < drawn.Count; i++)
                {
                    if (i > 0) { itemIds.Append(','); items.Append(','); }
                    itemIds.Append(drawn[i].Id);
                    items.Append(drawn[i].Json);
                }
                itemIds.Append(']');
                items.Append(']');
                _lastPackItemList = items.ToString();
                lock (_pendingLock)
                {
                    _pendingPackItems.Clear();
                    _pendingPackItems.AddRange(drawn);
                    _pendingDuplicates.Clear();
                    _pendingDuplicates.AddRange(dupes);
                }
                if (dupes.Count > 0)
                    _log.LogInformation("[FUT] {0} of {1} cards are duplicates", dupes.Count, drawn.Count);

                string purchasedBody = "{\"duplicateItemIdList\":" + DuplicateListJson(dupes) +
                    ",\"itemIdList\":" + itemIds +
                    ",\"itemList\":" + items + ",\"numberItems\":" + drawn.Count +
                    ",\"purchasedPackId\":" + packId + "," +
                    "\"entitlementQuantities\":null,\"awardSetIds\":[]" +
                    ",\"coins\":" + coinsAfter + ",\"credits\":" + coinsAfter +
                    ",\"currencies\":" + CurrenciesJson(coinsAfter) + "}";
                _lastPurchaseResponseBody = purchasedBody;
                return ("application/json; charset=utf-8", purchasedBody);
            }
            var pending = new StringBuilder("[");
            string pendingDupes;
            lock (_pendingLock)
            {
                for (int i = 0; i < _pendingPackItems.Count; i++)
                {
                    if (i > 0) pending.Append(',');
                    pending.Append(_pendingPackItems[i].Json);
                }
                pendingDupes = DuplicateListJson(
                    _pendingDuplicates.Where(d => _pendingPackItems.Any(p => p.Id == d.NewId)));
            }
            pending.Append(']');
            return ("application/json; charset=utf-8",
                    "{\"duplicateItemIdList\":" + pendingDupes + ",\"itemData\":" + pending + "}");
        }

        if (path.Contains("/delete/") && System.Text.RegularExpressions.Regex.IsMatch(path, @"squad/(\d+)"))
        {
            int delId = int.Parse(System.Text.RegularExpressions.Regex.Match(path, @"squad/(\d+)").Groups[1].Value);
            ClubStore.Mutate(data =>
            {
                data.Squads.RemoveAll(s => s.Id == delId);
                if (data.ActiveSquadId == delId)
                    data.ActiveSquadId = data.Squads.Count > 0 ? data.Squads[0].Id : 0;
            });
            return ("application/json; charset=utf-8", "{}");
        }

        if (path.EndsWith("/squad") && req.HttpMethod == "POST")
        {
            Squad created = null;
            ClubStore.Mutate(data =>
            {
                int newId = data.Squads.Count > 0 ? data.Squads.Max(s => s.Id) + 1 : 0;
                string name = null, formation = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        && idEl.TryGetInt32(out int wantId) && wantId >= 0 && data.Squads.All(s => s.Id != wantId))
                        newId = wantId;
                    if (root.TryGetProperty("squadName", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        name = nEl.GetString();
                    if (root.TryGetProperty("formation", out var fEl) && fEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        formation = fEl.GetString();
                }
                catch (Exception ex) { _log.LogWarning("Squad POST body parse failed: {0}", ex.Message); }

                created = new Squad { Id = newId };
                if (!string.IsNullOrWhiteSpace(name)) created.Name = name;
                if (!string.IsNullOrWhiteSpace(formation)) created.Formation = formation;
                data.Squads.Add(created);
            });
            return ("application/json; charset=utf-8", BuildFullSquadJson(created));
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"squad/(\d+)") && req.HttpMethod == "PUT")
        {
            int putId = int.Parse(System.Text.RegularExpressions.Regex.Match(path, @"squad/(\d+)").Groups[1].Value);
            Squad target = null;
            ClubStore.Mutate(data =>
            {
                if (data.Inventory.Count == 0) return;

                target = data.Squads.FirstOrDefault(s => s.Id == putId);
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(req.Body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("players", out var probe) && probe.ValueKind == System.Text.Json.JsonValueKind.Array
                        && probe.GetArrayLength() > 0)
                    {
                        bool anyOwned = false;
                        foreach (var pl in probe.EnumerateArray())
                        {
                            if (!pl.TryGetProperty("itemData", out var it)) continue;
                            if (!it.TryGetProperty("id", out var idp)) continue;
                            long sid = idp.GetInt64();
                            if (sid != 0 && data.Inventory.Any(c => c.ItemId == sid)) { anyOwned = true; break; }
                        }
                        if (!anyOwned) return;
                    }

                    if (target == null)
                    {
                        target = new Squad { Id = putId };
                        data.Squads.Add(target);
                    }

                    if (root.TryGetProperty("squadName", out var nameEl) && nameEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        target.Name = nameEl.GetString() ?? target.Name;
                    if (root.TryGetProperty("formation", out var formEl) && formEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        target.Formation = formEl.GetString() ?? target.Formation;
                    if (root.TryGetProperty("manager", out var mgrEl) && mgrEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var mEl in mgrEl.EnumerateArray())
                        {
                            if (mEl.TryGetProperty("id", out var mgrIdEl)
                                && mgrIdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                            { target.ManagerId = mgrIdEl.GetInt64(); break; }
                        }
                    if (root.TryGetProperty("chemistry", out var chemEl) && chemEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        target.Chemistry = chemEl.GetInt32();
                    if (root.TryGetProperty("starRating", out var starEl) && starEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        target.StarRating = starEl.GetInt32();
                    if (root.TryGetProperty("players", out var playersEl) && playersEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var newSlots = new Dictionary<int, long>();
                        var newKits = new Dictionary<int, int>();
                        foreach (var pl in playersEl.EnumerateArray())
                        {
                            if (!pl.TryGetProperty("index", out var idxEl)) continue;
                            if (!pl.TryGetProperty("itemData", out var itemDataEl)) continue;
                            if (!itemDataEl.TryGetProperty("id", out var idEl)) continue;
                            long slotItemId = idEl.GetInt64();
                            if (slotItemId != 0 && data.Inventory.Any(c => c.ItemId == slotItemId))
                                newSlots[idxEl.GetInt32()] = slotItemId;
                            if (pl.TryGetProperty("kitNumber", out var kitEl)
                                && kitEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                                newKits[idxEl.GetInt32()] = kitEl.GetInt32();
                        }
                        if (newSlots.Count > 0)
                        {
                            int had = target.Slots.Count(s => s.Value != 0);
                            if (newSlots.Count < had)
                                _log.LogWarning("[FUT] squad PUT {0} shrinks the squad: {1} slot(s) in, {2} saved - the client dropped entries from our last squad body",
                                    putId, newSlots.Count, had);
                            else
                                _log.LogInformation("[FUT] squad PUT {0}: {1} slot(s) (was {2})", putId, newSlots.Count, had);

                            target.Slots.Clear();
                            foreach (var kv in newSlots) target.Slots[kv.Key] = kv.Value;
                        }
                        target.KitNumbers = newKits;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Squad PUT body parse failed: {0}", ex.Message);
                }

                var assigned = new HashSet<long>(data.Squads.SelectMany(s => s.Slots.Values).Where(v => v != 0));
                for (int i = 0; i < data.Inventory.Count; i++)
                {
                    if (data.Inventory[i].Pile is 3 or 0) continue;   // leave transfer-list/unassigned items alone
                    int want = assigned.Contains(data.Inventory[i].ItemId) ? 7 : 6;
                    if (data.Inventory[i].Pile != want)
                        data.Inventory[i] = new ClubItem(data.Inventory[i].ItemId, data.Inventory[i].Player, want);
                }
                if (target != null && target.Slots.Count > 0) data.ActiveSquadId = putId;
            });
            target ??= new Squad { Id = putId };   // nothing owned/persisted: respond with an empty squad
            return ("application/json; charset=utf-8", BuildFullSquadJson(target));
        }

        {
            var mTotw = System.Text.RegularExpressions.Regex.Match(path, @"squad/(\d+)/user/(\d+)");
            if (mTotw.Success && req.HttpMethod == "GET" && mTotw.Groups[2].Value == Totw.ClubPersona.ToString())
            {
                int week = int.Parse(mTotw.Groups[1].Value);
                _log.LogInformation("[TOTW] squad fetch: week {0}", week);
                return ("application/json; charset=utf-8", Totw.SquadForWeek(week));
            }
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(path, @"squad/(\d+)") && req.HttpMethod == "GET")
        {
            int getId = int.Parse(System.Text.RegularExpressions.Regex.Match(path, @"squad/(\d+)").Groups[1].Value);
            Squad target = null;
            ClubStore.Mutate(data =>
            {
                target = data.Squads.FirstOrDefault(s => s.Id == getId);
                if (target != null && data.ActiveSquadId != getId)
                {
                    data.ActiveSquadId = getId;
                    _log.LogInformation("[FUT] active squad -> {0} (loaded/equipped)", getId);
                }
            });
            target ??= new Squad { Id = getId };
            return ("application/json; charset=utf-8", BuildFullSquadJson(target));
        }

        if (path.EndsWith("/squad/active"))
        {
            var data = ClubStore.Get();
            var active = data.Squads.FirstOrDefault(s => s.Id == data.ActiveSquadId)
                ?? data.Squads.FirstOrDefault(s => s.Slots.Count > 0)
                ?? (data.Squads.Count > 0 ? data.Squads[0] : new Squad { Id = 0 });
            return ("application/json; charset=utf-8", BuildFullSquadJson(active));
        }

        if (path.EndsWith("/squad/list"))
        {
            var data = ClubStore.Get();
            var sb = new StringBuilder("[");
            for (int i = 0; i < data.Squads.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var s = data.Squads[i];
                string sqName = string.IsNullOrWhiteSpace(s.Name) ? "Squad 1" : s.Name;
                sb.Append("{\"id\":" + s.Id + ",\"squadId\":" + s.Id + ",\"squadName\":\"" + Esc(sqName) +
                    "\",\"formation\":\"" + s.Formation +
                    "\",\"chemistry\":" + s.Chemistry + ",\"rating\":" + s.StarRating +
                    ",\"starRating\":" + s.StarRating + ",\"squadType\":\"REGULAR_SQUAD\"}");
            }
            sb.Append(']');
            return ("application/json; charset=utf-8",
                    "{\"squadList\":" + sb + ",\"squad\":" + sb + "}");
        }

        if (path.Contains("/delete/game/") && path.EndsWith("/user"))
            DeleteClub();

        // FUT user profile (/fut/rs4/ut/game/fifa14/user, .../userdata). Data-driven from the
        // profile: isReturningUser=false => NEW player (client state STATE_WELCOME, not
        // WELCOMEBACK — field name confirmed in fifa14.exe @ 0x1019992c). The parser hashes
        // field names and skips unknown ones, so extra fields are harmless.
        if (path.EndsWith("/user") || path.EndsWith("/userdata"))
        {
            if (req.HttpMethod == "POST" && req.Body.Contains("clubName"))
            {
                bool firstTime = !FutProfileStore.Get().Club.Established;
                FutProfileStore.Mutate(p =>
                {
                    p.IsReturningUser = true;
                    p.Club.Established = true;
                    if (firstTime) p.Club.EstablishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();   // stamp the founding date once
                    var nm = System.Text.RegularExpressions.Regex.Match(req.Body, "\"clubName\"\\s*:\\s*\"([^\"]*)\"");
                    if (nm.Success) p.Club.Name = nm.Groups[1].Value;
                    var ab = System.Text.RegularExpressions.Regex.Match(req.Body, "\"clubAbbr\"\\s*:\\s*\"([^\"]*)\"");
                    if (ab.Success) p.Club.Abbr = ab.Groups[1].Value;
                });
                _log.LogInformation("[FUT] club established: '{0}'", FutProfileStore.Get().Club.Name);
                if (firstTime) ClubStore.SeedStarterSquad();   // grant the bronze starter squad once
            }

            var prof = FutProfileStore.Get();
            return ("application/json; charset=utf-8",
                    "{\"isReturningUser\":" + (prof.IsReturningUser ? "true" : "false") +
                    ",\"established\":\"" + prof.Club.EstablishedAt + "\"" +
                    ",\"divisionOnline\":" + prof.OnlineDivision +
                    ",\"divisionOffline\":" + prof.OfflineDivision +
                    ",\"coins\":" + prof.Coins + ",\"credits\":" + prof.Coins +
                    ",\"currencies\":" + CurrenciesJson(prof.Coins) +
                    ",\"clubName\":\"" + Esc(prof.Club.Name) + "\",\"clubAbbr\":\"" + Esc(prof.Club.Abbr) + "\"" +
                    ",\"won\":" + prof.Wins + ",\"draw\":" + prof.Draws + ",\"loss\":" + prof.Losses +
                    ",\"userAccountInfo\":" + UserAccountInfoJson(BlazePersonaId) + "}");
        }

        // Default JSON endpoints
        if (wantsJson || path.StartsWith("/fut"))
            return ("application/json; charset=utf-8", "{}");

        return ("text/xml; charset=utf-8", "");
    }

    private (string, string) ServeFile(string fileName, string contentType)
    {
        var full = Path.Combine(_contentRoot, fileName);
        try
        {
            return (contentType, File.ReadAllText(full));
        }
        catch (Exception ex)
        {
            _log.LogWarning("WebServer missing content file {0}: {1}", full, ex.Message);
            return (contentType, "");
        }
    }

    private string PowBody(string path, WebReq req)
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

        if (path.EndsWith("/auth"))
            return $"{{\"lastOnlineTime\":\"{now}\",\"serverTime\":\"{now}\",\"sid\":\"{PowSid}\"}}";
        if (path.Contains("/healthcheck"))                        return "{\"status\":\"ok\"}";

        if (path.Contains("/lvl/weight"))                         return "{\"level\":1,\"xp_per_level\":100}";
        if (path.Contains("/lvl/user"))
            return "{\"level\":1,\"leveledUp\":false,\"xp\":0,\"xpGained\":0,\"xpLoyalty\":0," +
                   "\"challengesDone\":0,\"xpCapCurrLevel\":0,\"xpCapNextLevel\":100," +
                   "\"funds\":[],\"notifications\":[],\"tier_gp\":\"businessunit\",\"tier_tp\":\"fifa\"}";

        if (path.Contains("/bank/user/account"))                  return "{\"currency\":\"COINS\",\"balance\":" + FutProfileStore.Get().Coins + "}";
        if (path.Contains("/bank/currency") && path.Contains("cap/info")) return "{\"currency\":\"pow_funds\",\"cap\":1000000}";
        if (path.Contains("/bank/"))
            return "{\"currencies\":[{\"currency\":\"pow_funds\",\"funds\":0," +
                   "\"fundsCapInfo\":[{\"period\":\"daily\",\"fundsEarned\":0},{\"period\":\"weekly\",\"fundsEarned\":0}]}]}";

        if (path.Contains("catalog/list"))
            return "{\"catalogs\":[{\"catalogId\":1,\"name\":\"FIFA 14 Store\"}]}";
        if (path.Contains("/store/") && path.Contains("catalog"))
            return "{\"catalogId\":1,\"name\":\"FIFA 14 Store\",\"items\":[]}";
        if (path.Contains("/store/gift"))                         return "{\"gifts\":[]}";
        if (path.Contains("/store/"))                             return "{\"items\":[]}";

        if (path.Contains("/inventory/item"))                     return "[]";

        if (path.Contains("/chal/"))                              return "{\"challenges\":[]}";

        if (path.Contains("/pfyc/") && path.EndsWith("/info"))
        {
            var pc = FutProfileStore.Get().Club;
            return "{\"clubId\":" + pc.TeamId + ",\"clubName\":\"" + Esc(pc.Name) + "\",\"leagueId\":0," +
                   "\"globalLeagueId\":0,\"division\":1,\"newDivision\":1,\"prevLeagueId\":0}";
        }
        if (path.Contains("/pfyc/schedule"))                      return "{\"schedule\":[]}";
        if (path.Contains("/pfyc/user/club"))
        {
            var pc = FutProfileStore.Get().Club;
            return "{\"clubId\":" + pc.TeamId + ",\"clubName\":\"" + Esc(pc.Name) + "\",\"leagueId\":0,\"globalLeagueId\":0,\"division\":1}";
        }
        if (path.Contains("/pfyc/user"))
        {
            var pc = FutProfileStore.Get().Club;
            long nuc = ParseLong(req.QueryString["friendtiertp"], BlazePersonaId);
            long pfycClubId = pc.TeamId;
            return "{\"users\":[{\"nucId\":" + nuc + ",\"clubId\":" + pfycClubId + ",\"assetId\":" +
                   pc.BadgeId + ",\"badgeId\":" + pc.BadgeId + ",\"pendingClubId\":0," +
                   "\"numChangesAllowed\":0,\"leagueId\":0,\"globalLeagueId\":0}]}";
        }
        if (path.Contains("/pfyc/"))                              return "{}";

        if (path.Contains("/lb/"))                                return "{\"entries\":[]}";

        if (path.Contains("/communication/"))                     return "{\"communications\":[]}";
        if (path.Contains("/mm/") && path.Contains("message/list"))
            return "{\"messageList\":[],\"messagesAvailable\":0,\"messagesRead\":0,\"promoUpdate\":[]}";
        if (path.Contains("/news/"))                              return "{}";

        if (path.Contains("/user/friends"))                       return "{\"friends\":[]}";

        return "{}";
    }

    private static readonly (int Id, string Group, int Coins, bool Premium, int Art,
                             int Gold, int Silver, int Bronze, int Rare, int Special,
                             int SpecialMin, int MinRating, string SpecialSet)[] StorePacks =
    {
        // id     tab        coins  prem  art  gold silv bron  rare  spc  min  floor  set
        (100, "bronze",     400, false,   1,   0,   0,   4,   1,   0,   0,   0, ""),  // Bronze Pack
        (103, "bronze",     750, true,    1,   0,   0,   4,   1,   0,   0,   0, ""),  // Premium Bronze Pack
        (200, "silver",    2500, false,   2,   0,   4,   0,   1,   0,   0,   0, ""),  // Silver Pack
        (203, "silver",    3750, true,    2,   0,   4,   0,   1,   0,   0,   0, ""),  // Premium Silver Pack
        (300, "gold",      5000, false,   3,   4,   0,   0,   1,   0,   0,   0, ""),  // Gold Pack
        (304, "gold",      7500, true,    3,   4,   0,   0,   1,   3,   0,   0, ""),  // Premium Gold Pack
        (405, "special",  35000, true,    4,  12,   0,   0,   8,   6,   0,   0, ""),  // 30k Pack - 12 players, 8 rare minimum; 20% chance of one silver
        (406, "special",  50000, true,    5,  24,   0,   0,  24,   9,   0,  75, ""),  // Jumbo Rare Players - 24 rare gold
        (404, "special", 100000, true,    6,  30,   0,   0,  30,  12,   0,  76, ""),  // Mega Pack - the 50k, a bit better: 30 items, floor 76, slightly better special odds
    };

    private static readonly Dictionary<int,
        (int Contracts, int Fitness, int Training, int Healing, int Special, int RareExtras)> PackExtras =
        new()
        {
            //     contracts, fitness, training, healing, special, rareExtras
            [100] = (4, 1, 1, 1, 1, 0),   // Bronze Pack
            [103] = (4, 1, 1, 1, 1, 2),   // Premium Bronze Pack (3 rares)
            [200] = (4, 1, 1, 1, 1, 0),   // Silver Pack
            [203] = (4, 1, 1, 1, 1, 2),   // Premium Silver Pack (3 rares)
            [300] = (4, 1, 1, 1, 1, 0),   // Gold Pack
            [304] = (4, 1, 1, 1, 1, 2),   // Premium Gold Pack (3 rares)
        };

    private static int PackExtrasCount(int packId) =>
        PackExtras.TryGetValue(packId, out var e)
            ? e.Contracts + e.Fitness + e.Training + e.Healing + e.Special : 0;

    // Displayed rare count on the store tile: guaranteed rare players plus the rare consumables.
    private static int PackRareCount(int packId, int playerRare) =>
        playerRare + (PackExtras.TryGetValue(packId, out var e) ? e.RareExtras : 0);

    private static long _nextPackExtraId = 970_000_000L;


    private static bool OwnedInClub(ClubData data, long itemId)
        => !data.Listings.TryGetValue(itemId, out var au) || au.State != "sold";

    private static string EffectivePosition(ClubData data, long itemId, string basePosition)
    {
        if (data.PlayerMods.TryGetValue(itemId, out var mod) && mod != null
            && !string.IsNullOrEmpty(mod.Position)) return mod.Position;
        return basePosition;
    }

    private string DuplicateListJson(IEnumerable<(long NewId, long OwnedId)> dupes)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var (newId, ownedId) in dupes)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"duplicateItemId\":" + ownedId + ",\"itemId\":" + newId + "}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string BodyRx(string s, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s ?? "", pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string MatchEndBody(long balance, int matchCoins, int tournamentCoins = 0) =>
        "{\"allCoins\":" + balance + ",\"matchCoins\":" + matchCoins + ",\"seasonCoins\":" + balance +
        ",\"tournamentCoins\":" + tournamentCoins + ",\"boostConis\":0,\"boostCountLeft\":0,\"participationAward\":" + matchCoins +
        ",\"matchCoinPartials\":[],\"matchCoinMultipliers\":[],\"matchParamsKeyValues\":{}," +
        "\"endReason\":\"FT\",\"credits\":" + balance + ",\"coins\":" + balance +
        ",\"currencies\":" + CurrenciesJson(balance) +
        ",\"userData\":{\"credits\":" + balance + ",\"coins\":" + balance + "}}";

    private static string NoTransactionBody() =>
        "{\"transactionId\":0,\"state\":\"NOTRANSACTION\",\"packId\":0,\"purchasePackType\":\"\"," +
        "\"firstPartyStoreId\":0,\"useAuth\":0,\"useCount\":0,\"useTime\":0}";

    private void DeleteClub()
    {
        FutProfileStore.Reset();
        ClubStore.Wipe();
        Market.ClearAll();
        FutProfileStore.Mutate(_ => { });   // persist the cleared market state
        ClientDataStore.Clear();
        Tournaments.CurrentMatchTournamentId = null;
        _log.LogInformation("[FUT] club deleted -> account reset to new player");
    }

    private static string UserAccountInfoJson(long nucleusId)
    {
        var prof = FutProfileStore.Get();
        string ret = prof.IsReturningUser ? "true" : "false";
        int est = prof.Club.Established ? 1 : 0;
        const string Sku = "FFA14PCC";
        string clubList =
            "{\"year\":2014,\"teamId\":" + prof.Club.TeamId +
            ",\"teamName\":\"" + Esc(prof.Club.Name) + "\",\"clubName\":\"" + Esc(prof.Club.Name) + "\"," +
            "\"clubAbbr\":\"" + Esc(prof.Club.Abbr) + "\",\"clubId\":" + prof.Club.TeamId +
            ",\"platform\":\"pc\",\"assetId\":" + prof.Club.BadgeId + ",\"badgeId\":" + prof.Club.BadgeId +
            ",\"seasonId\":1,\"status\":" + est + ",\"established\":\"" + prof.Club.EstablishedAt + "\",\"divisionOnline\":" + prof.OnlineDivision +
            ",\"divisionOffline\":" + prof.OfflineDivision + ",\"lastAccessTime\":1400000000," +
            "\"skuAccessList\":{\"" + Sku + "\":1,\"FFA14PS3\":1,\"FFA14XBX\":1}}";
        string clubListEntries = prof.Club.Established ? clubList : "";
        string persona =
            "{\"personaId\":" + nucleusId + ",\"personaName\":\"" + BlazePersonaName + "\"," +
            "\"returningUser\":" + ret + ",\"isReturningUser\":" + ret + ",\"trial\":false,\"userState\":\"\"," +
            "\"userClubList\":[" + clubListEntries + "]}";
        return "{\"personas\":[" + persona + "],\"userPersonaInfos\":[]}";
    }

    private static string StorePurchaseGroupBody()
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"cardpack\",\"timestamp\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        sb.Append(",\"purchase\":[");
        for (int i = 0; i < StorePacks.Length; i++)
        {
            var p = StorePacks[i];
            // Category tabs order left->right by ascending displayGroup.priority: bronze -> special.
            int prio   = p.Group switch { "bronze" => 0, "silver" => 1, "gold" => 2, "special" => 3, _ => 2 };
            // Pack tier (bronze/silver/gold art) is carried by packContentInfo's *Quantity fields.
            int gold   = p.Gold;
            int silver = p.Silver;
            int bronze = p.Bronze;
            int rare   = PackRareCount(p.Id, p.Rare);
            int items  = gold + silver + bronze + PackExtrasCount(p.Id);
            int art    = p.Art;
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(p.Id)
              .Append(",\"state\":\"active\",\"type\":\"cardpack\",\"description\":\"\"")
              .Append(",\"assetId\":").Append(art).Append(",\"coins\":").Append(p.Coins)
              .Append(",\"actionType\":\"CREATEPACK\",\"productId\":\"0\",\"quantity\":-1")
              .Append(",\"currencies\":[{\"name\":\"COINS\",\"funds\":").Append(p.Coins)
              .Append(",\"finalFunds\":").Append(p.Coins).Append("}]")
              .Append(",\"saleType\":\"NONE\",\"dealType\":\"CARDPACK\",\"saleId\":0")
              .Append(",\"displayGroup\":{\"value\":\"").Append(p.Group).Append("\",\"priority\":").Append(prio).Append('}')
              .Append(",\"sortPriority\":").Append(i)
              .Append(",\"limited\":false,\"purchaseLimit\":0,\"purchaseCount\":0")
              .Append(",\"isPremium\":").Append(p.Premium ? "true" : "false")
              .Append(",\"isSeasonTicketDiscount\":false,\"useDefaultImage\":true")
              .Append(",\"purchaseMethod\":\"COIN\",\"displayGroupAssetId\":").Append(art).Append(",\"lastPurchasedTime\":0")
              .Append(",\"displayGroupUseDefaultImage\":true,\"unopened\":false,\"packType\":\"CARDPACK\"")
              .Append(",\"packContentInfo\":{\"itemQuantity\":").Append(items).Append(",\"goldQuantity\":").Append(gold)
              .Append(",\"silverQuantity\":").Append(silver).Append(",\"bronzeQuantity\":").Append(bronze)
              .Append(",\"rareQuantity\":").Append(rare).Append("}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static Func<RealPlayer, bool> MarketFilter(NameValueCollection q)
    {
        var preds = new List<Func<RealPlayer, bool>>();

        int Int(string key, int fallback) => int.TryParse(q[key], out int v) ? v : fallback;

        int minRating = Int("minRating", -1);
        int maxRating = Int("maxRating", -1);
        if (minRating >= 0) preds.Add(p => p.Rating >= minRating);
        if (maxRating >= 0) preds.Add(p => p.Rating <= maxRating);

        int nation = Int("nat", -1);
        if (nation >= 0) preds.Add(p => p.NationId == nation);

        int league = Int("leag", -1);
        if (league >= 0) preds.Add(p => TeamLeagues.LeagueOf(p.TeamId) == league);

        int team = Int("team", -1);
        if (team >= 0) preds.Add(p => p.TeamId == team);

        int maskedDefId = Int("maskedDefId", -1);
        if (maskedDefId > 0) preds.Add(p => p.Id == maskedDefId);

        int defId = Int("definitionId", -1);
        if (defId > 0) preds.Add(p => p.Id == defId);


        string lev = (q["lev"] ?? "").ToLowerInvariant();
        if (lev is "bronze" or "silver" or "gold")
            preds.Add(p => lev switch
            {
                "bronze" => p.Rating < 65,
                "silver" => p.Rating is >= 65 and < 75,
                _ => p.Rating >= 75,
            });

        if (preds.Count == 0) return null;
        return p => preds.All(f => f(p));
    }

    private static string[] MarketWantPositions(NameValueCollection q)
    {
        string pos = (q["pos"] ?? "").Trim();
        if (pos.Length > 0) return new[] { pos };
        string zone = (q["zone"] ?? "").ToLowerInvariant();
        return zone switch
        {
            "gk" or "goal" or "goalkeeper" => new[] { "GK" },
            "defense" or "defence" or "defenders" => new[] { "CB", "RB", "LB", "RWB", "LWB" },
            "midfield" or "midfielders" => new[] { "CDM", "CM", "CAM", "RM", "LM" },
            "attack" or "attacker" or "forwards" or "strikers" => new[] { "ST", "CF", "RW", "LW", "RF", "LF" },
            _ => Array.Empty<string>(),
        };
    }

    private (string, string) MarketBuy(long tradeId, int amount, long offeredItemId = 0)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (tradeId >= Market.StaffTradeIdBase)
            return StaffBuy(tradeId, amount);
        if (tradeId >= Market.ClubItemTradeIdBase)
            return ClubItemBuy(tradeId, amount);
        if (tradeId >= Market.ConsumableTradeIdBase)
            return ConsumableBuy(tradeId, amount);
        if (!Market.ResolveTradeId(tradeId, out var card, out int startingBid, out int buyNow))
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        long coins = FutProfileStore.Get().Coins;
        if (amount <= 0) amount = buyNow;
        long escrowed = Market.EscrowHeld(tradeId);   // coins we already hold for this trade
        bool won = amount >= buyNow;
        long extraCharge = won ? Math.Max(0, (long)buyNow - escrowed)
                               : Math.Max(0, (long)amount - escrowed);
        long available = coins - Market.HeldCoins;    // profile minus coins committed elsewhere
        if (extraCharge > available)
            return ("application/json; charset=utf-8",
                "{\"reason\":\"INSUFFICIENT_COINS\",\"tradeId\":" + tradeId + ",\"credits\":" + available +
                ",\"currencies\":" + CurrenciesJson(available) + ",\"bidTokens\":{}}");

        var rnd = new Random();
        Market.AcceptedOffers.TryRemove(tradeId, out _);   // fresh terms replace any pending swap
        Market.MyBids.TryRemove(tradeId, out _);
        Market.RefundedBids.TryRemove(tradeId, out _);     // fresh terms = fresh escrow

        ClubItem? offeredCard = null;
        if (!won && offeredItemId != 0)
        {
            ClubStore.Mutate(d =>
            {
                var hit = d.Inventory.FirstOrDefault(c => c.ItemId == offeredItemId
                    && !d.Listings.ContainsKey(c.ItemId));
                if (hit.ItemId != 0) offeredCard = hit;
            });
            if (offeredCard != null)
            {
                if (escrowed > 0)
                {
                    FutProfileStore.Mutate(p => p.Coins += escrowed);
                    Market.ChangeHeld(-escrowed);
                }
                long cardValue = Market.CheapestLiveBuyNow(offeredCard.Value.Player, now);
                if (cardValue <= 0) cardValue = Market.MarketValue(offeredCard.Value.Player);
                long total = amount + cardValue;
                long acceptIn = Market.OfferAcceptDelay(total, buyNow, rnd);
                if (acceptIn < 0)
                {
                    _log.LogInformation("[Market] OFFER declined trade {0}: {1} coins + {2} (worth {3}) vs buy-now {4}",
                        tradeId, amount, offeredCard.Value.Player.Name, cardValue, buyNow);
                    return ("application/json; charset=utf-8",
                        "{\"reason\":\"OFFER_TOO_LOW\",\"tradeId\":" + tradeId + ",\"credits\":" + coins +
                        ",\"currencies\":" + CurrenciesJson(coins) + ",\"bidTokens\":{}}");
                }
                Market.AcceptedOffers[tradeId] = new Market.PendingOffer(amount, offeredItemId, now + acceptIn);
                FutProfileStore.Mutate(_ => { });   // persist the pending offer across restarts
                var opt = Market.AuctionState(tradeId, now);
                string offerAuc = "{\"tradeId\":" + tradeId + ",\"itemData\":" +
                    BuildRealPlayerItem(rnd, card, 3_000_000_000L + (tradeId - 2_000_000_000L), now, 0) +
                    ",\"tradeState\":\"active\",\"expires\":3600" +
                    ",\"buyNowPrice\":" + buyNow + ",\"startingBid\":" + startingBid +
                    ",\"currentBid\":" + opt.CurrentBid + ",\"offers\":" + opt.Offers +
                    ",\"watched\":true,\"bidState\":\"" + opt.BidState + "\",\"sellerName\":\"FUT\"," +
                    "\"sellerEstablished\":2013,\"sellerId\":1,\"confidenceValue\":100}";
                _log.LogInformation("[Market] OFFER on trade {0}: {1} coins + {2} (worth {3}) for buy-now {4} - seller accepts in {5}s",
                    tradeId, amount, offeredCard.Value.Player.Name, cardValue, buyNow, acceptIn);
                return ("application/json; charset=utf-8",
                    "{\"auctionInfo\":[" + offerAuc + "],\"errorState\":null,\"duplicateItemIdList\":[]," +
                    "\"credits\":" + (coins - Market.HeldCoins) + ",\"totalCredits\":" + (coins - Market.HeldCoins) + ",\"coins\":" + (coins - Market.HeldCoins) +
                    ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
            }
            else
            {
                _log.LogInformation("[Market] OFFER on trade {0} declined - card {1} not found in the club",
                    tradeId, offeredItemId);
                return ("application/json; charset=utf-8",
                    "{\"reason\":\"OFFER_TOO_LOW\",\"tradeId\":" + tradeId + ",\"credits\":" + coins +
                    ",\"currencies\":" + CurrenciesJson(coins) + ",\"bidTokens\":{}}");
            }
        }
        var dupes = new List<(long NewId, long OwnedId)>();
        long itemId;
        if (won)
        {
            Market.MarkBought(tradeId, now);
            if (escrowed > 0)
            {
                FutProfileStore.Mutate(p => p.Coins += escrowed);
                Market.ChangeHeld(-escrowed);
            }
            FutProfileStore.Mutate(p => p.Coins -= buyNow);
            coins = coins + escrowed - buyNow;
            long newId = 0;
            ClubStore.Mutate(d =>
            {
                var owned = new Dictionary<int, long>();
                foreach (var it in d.Inventory)
                    if (OwnedInClub(d, it.ItemId) && !owned.ContainsKey(it.Player.CardId)) owned[it.Player.CardId] = it.ItemId;
                newId = ClubStore.NextPlayerItemId(d);
                if (owned.TryGetValue(card.CardId, out long ownedId)) dupes.Add((newId, ownedId));
                d.Inventory.Add(new ClubItem(newId, card, 6));
                var listMod = Market.ListingMods(tradeId, card, now);
                if (listMod != null) d.PlayerMods[newId] = listMod;
            });
            itemId = newId;   // pack-range id -> survives id migration, matches the claim PUT /item id
        }
        else
        {
            itemId = 3_000_000_000L + (tradeId - 2_000_000_000L);
        }

        string item = BuildRealPlayerItem(rnd, card, itemId, now, won ? 6 : 0);
        if (won)
        {
            lock (_pendingLock)
            {
                _pendingPackItems.Add((itemId, item));
                _pendingDuplicates.AddRange(dupes);
            }
            _lastPackItemList = "[" + item + "]";
            Market.Watched.TryRemove(tradeId, out _);
            Market.MyBids.TryRemove(tradeId, out _);
            Market.AcceptedOffers.TryRemove(tradeId, out _);
            FutProfileStore.Mutate(_ => { });   // persist the buy-now cleanup across restarts
            _log.LogInformation("[Market] BUY-NOW asset {0} (rating {1}) for {2} -> club item {3}, balance {4}{5}",
                card.Id, card.Rating, buyNow, itemId, coins, dupes.Count > 0 ? " (duplicate)" : "");
        }
        else
        {
            if (Market.Bought(tradeId, now))   // already sold to someone else this cycle
                return ("application/json; charset=utf-8",
                    "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + ",\"credits\":" + (coins - Market.HeldCoins) +
                    ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
            if (amount > escrowed)
            {
                FutProfileStore.Mutate(p => p.Coins -= (amount - escrowed));
                Market.ChangeHeld(amount - escrowed);
            }
            else if (amount < escrowed)
            {
                FutProfileStore.Mutate(p => p.Coins += (escrowed - amount));
                Market.ChangeHeld(-(escrowed - amount));
            }
            coins = coins - Math.Max(0, amount - escrowed) + Math.Max(0, escrowed - amount);
            Market.MyBids[tradeId] = amount;   // user's bid becomes the listing's current price
            _log.LogInformation("[Market] BID {0} on asset {1} (rating {2}) - buy-now to purchase now",
                amount, card.Id, card.Rating);
        }

        var st = Market.AuctionState(tradeId, now);
        string state = won ? "closed" : "active";
        int offers = won ? 0 : st.Offers;
        int expiresOut = won ? -1 : 3600;
        string auction = "{\"tradeId\":" + tradeId + ",\"itemData\":" + item +
            ",\"tradeState\":\"" + state + "\",\"expires\":" + expiresOut +
            ",\"buyNowPrice\":" + buyNow + ",\"startingBid\":" + startingBid +
            ",\"currentBid\":" + (won ? amount : st.CurrentBid) + ",\"offers\":" + offers +
            ",\"watched\":false,\"bidState\":\"" + (won ? "highest" : st.BidState) + "\",\"sellerName\":\"FUT\"," +
            "\"sellerEstablished\":2013,\"sellerId\":1,\"confidenceValue\":100}";
        return ("application/json; charset=utf-8",
            "{\"auctionInfo\":[" + auction + "],\"errorState\":null,\"duplicateItemIdList\":" + DuplicateListJson(dupes) + "," +
            "\"credits\":" + (coins - Market.HeldCoins) + ",\"totalCredits\":" + (coins - Market.HeldCoins) + ",\"coins\":" + (coins - Market.HeldCoins) +
            ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
    }

    private (string, string) ConsumableBuy(long tradeId, int amount)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Market.CResolveTradeId(tradeId, out var item, out int startingBid, out int buyNow))
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        long coins = FutProfileStore.Get().Coins;
        if (amount <= 0) amount = buyNow;
        if (amount > coins)
            return ("application/json; charset=utf-8",
                "{\"reason\":\"INSUFFICIENT_COINS\",\"tradeId\":" + tradeId + ",\"credits\":" + coins +
                ",\"currencies\":" + CurrenciesJson(coins) + ",\"bidTokens\":{}}");
        if (amount < buyNow)   // consumables have no bidding wars - placement bids are invalid
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        Market.CMarkBought(tradeId, now);
        FutProfileStore.Mutate(p => p.Coins -= buyNow);
        coins -= buyNow;
        long itemId = 0;
        var dupes = new List<(long NewId, long OwnedId)>();
        ClubStore.Mutate(d =>
        {
            var owned = new Dictionary<long, long>();
            foreach (var c in d.Consumables)
                if (!owned.ContainsKey(c.ResourceId)) owned[c.ResourceId] = c.ItemId;
            itemId = Interlocked.Increment(ref _nextPackExtraId);
            var inst = item with { ItemId = itemId };
            if (owned.TryGetValue(inst.ResourceId, out long ownedId)) dupes.Add((itemId, ownedId));
            d.Consumables.Add(inst);
        });
        string itemJson = ConsumableItems.BuildJson(item with { ItemId = itemId }, now);
        lock (_pendingLock)
        {
            _pendingPackItems.Add((itemId, itemJson));
            _pendingDuplicates.AddRange(dupes);
        }
        _lastPackItemList = "[" + itemJson + "]";
        _log.LogInformation("[Market] BUY consumable res {0} ({1}) for {2} -> club item {3}, balance {4}{5}",
            item.ResourceId, item.Name, buyNow, itemId, coins, dupes.Count > 0 ? " (duplicate)" : "");

        string seller = Market.CSellerFor(tradeId - Market.ConsumableTradeIdBase, now);
        string auction = "{\"tradeId\":" + tradeId + ",\"itemData\":" + itemJson +
            ",\"tradeState\":\"closed\",\"expires\":-1" +
            ",\"buyNowPrice\":" + buyNow + ",\"startingBid\":" + startingBid +
            ",\"currentBid\":" + amount + ",\"offers\":0,\"watched\":false,\"bidState\":\"highest\"" +
            ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013,\"sellerId\":1,\"confidenceValue\":100}";
        return ("application/json; charset=utf-8",
            "{\"auctionInfo\":[" + auction + "],\"errorState\":null,\"duplicateItemIdList\":" + DuplicateListJson(dupes) + "," +
            "\"credits\":" + (coins - Market.HeldCoins) + ",\"totalCredits\":" + (coins - Market.HeldCoins) + ",\"coins\":" + (coins - Market.HeldCoins) +
            ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
    }

    private (string, string) ClubItemBuy(long tradeId, int amount)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Market.EResolveTradeId(tradeId, out var item, out int startingBid, out int buyNow))
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        long coins = FutProfileStore.Get().Coins;
        if (amount <= 0) amount = buyNow;
        if (amount > coins)
            return ("application/json; charset=utf-8",
                "{\"reason\":\"INSUFFICIENT_COINS\",\"tradeId\":" + tradeId + ",\"credits\":" + coins +
                ",\"currencies\":" + CurrenciesJson(coins) + ",\"bidTokens\":{}}");
        if (amount < buyNow)   // no bidding wars on club items - placement bids are invalid
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        Market.EMarkBought(tradeId, now);
        FutProfileStore.Mutate(p => p.Coins -= buyNow);
        coins -= buyNow;
        long itemId = 0;
        var dupes = new List<(long NewId, long OwnedId)>();
        ClubStore.Mutate(d =>
        {
            var owned = new Dictionary<(string, long), long>();
            foreach (var c in d.Cosmetics)
                if (!owned.ContainsKey((c.Type, c.ResourceId))) owned[(c.Type, c.ResourceId)] = c.ItemId;
            itemId = Interlocked.Increment(ref _nextPackExtraId);
            var inst = item with { ItemId = itemId };
            if (owned.TryGetValue((inst.Type, inst.ResourceId), out long ownedId)) dupes.Add((itemId, ownedId));
            d.Cosmetics.Add(inst);
        });
        string itemJson = ClubItems.BuildJson(item with { ItemId = itemId }, now);
        lock (_pendingLock)
        {
            _pendingPackItems.Add((itemId, itemJson));
            _pendingDuplicates.AddRange(dupes);
        }
        _lastPackItemList = "[" + itemJson + "]";
        _log.LogInformation("[Market] BUY club item res:{0} ({1}) for {2} -> club item {3}, balance {4}{5}",
            item.ResourceId, item.Name, buyNow, itemId, coins, dupes.Count > 0 ? " (duplicate)" : "");

        string seller = Market.ESellerFor(tradeId - Market.ClubItemTradeIdBase, now);
        string auction = "{\"tradeId\":" + tradeId + ",\"itemData\":" + itemJson +
            ",\"tradeState\":\"closed\",\"expires\":-1" +
            ",\"buyNowPrice\":" + buyNow + ",\"startingBid\":" + startingBid +
            ",\"currentBid\":" + amount + ",\"offers\":0,\"watched\":false,\"bidState\":\"highest\"" +
            ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013,\"sellerId\":1,\"confidenceValue\":100}";
        return ("application/json; charset=utf-8",
            "{\"auctionInfo\":[" + auction + "],\"errorState\":null,\"duplicateItemIdList\":" + DuplicateListJson(dupes) + "," +
            "\"credits\":" + (coins - Market.HeldCoins) + ",\"totalCredits\":" + (coins - Market.HeldCoins) + ",\"coins\":" + (coins - Market.HeldCoins) +
            ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
    }

    private (string, string) StaffBuy(long tradeId, int amount)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Market.FResolveTradeId(tradeId, out bool isManager, out Manager mgr, out StaffCard stf,
                out int startingBid, out int buyNow))
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        long coins = FutProfileStore.Get().Coins;
        if (amount <= 0) amount = buyNow;
        if (amount > coins)
            return ("application/json; charset=utf-8",
                "{\"reason\":\"INSUFFICIENT_COINS\",\"tradeId\":" + tradeId + ",\"credits\":" + coins +
                ",\"currencies\":" + CurrenciesJson(coins) + ",\"bidTokens\":{}}");
        if (amount < buyNow)
            return ("application/json; charset=utf-8", "{\"reason\":\"INVALID_REQUEST\",\"tradeId\":" + tradeId + "}");

        Market.FMarkBought(tradeId, now);
        FutProfileStore.Mutate(p => p.Coins -= buyNow);
        coins -= buyNow;
        long itemId = 0;
        var dupes = new List<(long NewId, long OwnedId)>();
        string itemJson = "";
        ClubStore.Mutate(d =>
        {
            itemId = Interlocked.Increment(ref _nextPackExtraId);
            if (isManager)
            {
                int idx = d.Managers.FindIndex(m => m.ResourceId == mgr.ResourceId);
                if (idx >= 0) dupes.Add((itemId, ManagerItemIdBase + idx));
                d.Managers.Add(mgr);
            }
            else
            {
                int idx = d.Staff.FindIndex(s => s.ResourceId == stf.ResourceId);
                if (idx >= 0) dupes.Add((itemId, StaffItemIdBase + idx));
                d.Staff.Add(stf);
            }
        });
        if (isManager)
        {
            int rareFlag = mgr.Rating >= 80 ? 1 : 0;
            itemJson = BuildManagerItem(mgr, itemId, now, 6, rareFlag);
        }
        else
            itemJson = BuildStaffItem(stf, itemId, now, 6);
        lock (_pendingLock)
        {
            _pendingPackItems.Add((itemId, itemJson));
            _pendingDuplicates.AddRange(dupes);
        }
        _lastPackItemList = "[" + itemJson + "]";
        _log.LogInformation("[Market] BUY staff {0} ({1}, {2}) for {3} -> club item {4}, balance {5}{6}",
            isManager ? mgr.ResourceId : stf.ResourceId,
            isManager ? mgr.Name : stf.Name,
            isManager ? mgr.Rating : stf.Rating,
            buyNow, itemId, coins, dupes.Count > 0 ? " (duplicate)" : "");

        string seller = Market.FSellerFor(tradeId - Market.StaffTradeIdBase, now);
        string auction = "{\"tradeId\":" + tradeId + ",\"itemData\":" + itemJson +
            ",\"tradeState\":\"closed\",\"expires\":-1" +
            ",\"buyNowPrice\":" + buyNow + ",\"startingBid\":" + startingBid +
            ",\"currentBid\":" + amount + ",\"offers\":0,\"watched\":false,\"bidState\":\"highest\"" +
            ",\"sellerName\":\"" + seller + "\",\"sellerEstablished\":2013,\"sellerId\":1,\"confidenceValue\":100}";
        return ("application/json; charset=utf-8",
            "{\"auctionInfo\":[" + auction + "],\"errorState\":null,\"duplicateItemIdList\":" + DuplicateListJson(dupes) + "," +
            "\"credits\":" + (coins - Market.HeldCoins) + ",\"totalCredits\":" + (coins - Market.HeldCoins) + ",\"coins\":" + (coins - Market.HeldCoins) +
            ",\"currencies\":" + CurrenciesJson(coins - Market.HeldCoins) + ",\"bidTokens\":{}}");
    }

    private void SettleWonBids(long now)
    {
        var results = Market.CollectMyBidResults(now);
        if (results.Count == 0) return;
        var rnd = new Random();
        foreach (var r in results)
        {
            long esc = Market.EscrowHeld(r.TradeId);   // read before the bid record goes away
            Market.RemoveMyBid(r.TradeId);
            if (!r.Won)
            {
                if (esc > 0)
                {
                    FutProfileStore.Mutate(p => p.Coins += esc);
                    Market.ChangeHeld(-esc);
                }
                _log.LogInformation("[Market] bid {0} on trade {1} ({2}) was outbid when it sold - {3} coins returned",
                    r.MyBid, r.TradeId, r.Card.Name, esc);
                continue;
            }
            if (esc <= 0)   // pre-escrow bids still pay on win; escrowed bids already paid
                FutProfileStore.Mutate(p => p.Coins = Math.Max(0, p.Coins - r.MyBid));
            else
                Market.ChangeHeld(-esc);   // escrowed coins paid for the card - release the hold
            long itemId = 0;
            var winDupes = new List<(long NewId, long OwnedId)>();
            ClubStore.Mutate(d =>
            {
                var owned = new Dictionary<int, long>();
                foreach (var it in d.Inventory)
                    if (OwnedInClub(d, it.ItemId) && !owned.ContainsKey(it.Player.CardId)) owned[it.Player.CardId] = it.ItemId;
                itemId = ClubStore.NextPlayerItemId(d);
                if (owned.TryGetValue(r.Card.CardId, out long ownedId)) winDupes.Add((itemId, ownedId));
                d.Inventory.Add(new ClubItem(itemId, r.Card, 6));
                var winMod = Market.ListingMods(r.TradeId, r.Card, now);
                if (winMod != null) d.PlayerMods[itemId] = winMod;
            });
            string wonItem = BuildRealPlayerItem(rnd, r.Card, itemId, now, 6);
            lock (_pendingLock)
            {
                _pendingPackItems.Add((itemId, wonItem));
                _pendingDuplicates.AddRange(winDupes);
            }
            _log.LogInformation("[Market] WON auction {0}: {1} (rating {2}) for {3} coins -> club item {4}{5}",
                r.TradeId, r.Card.Name, r.Card.Rating, r.MyBid, itemId, winDupes.Count > 0 ? " (duplicate)" : "");
        }
        FutProfileStore.Mutate(_ => { });   // persist bid settlement so a restart can't re-settle
    }

    private void ReconcileBids(long now)
    {
        bool any = false;
        foreach (long tid in Market.CollectRefundableBids(now))
        {
            long esc = Market.RefundEscrow(tid);
            if (esc > 0)
            {
                any = true;
                FutProfileStore.Mutate(p => p.Coins += esc);
                _log.LogInformation("[Market] bid on trade {0} lost - {1} coins returned", tid, esc);
            }
        }
        if (any) FutProfileStore.Mutate(_ => { });   // persist refunds so a restart can't double-refund
    }

    private static Manager? ManagerByGlobalId(long id)
    {
        long idx = id - ManagerItemIdBase;
        if (idx >= 0 && idx < Managers.All.Length) return Managers.All[(int)idx];
        return null;
    }

    private static StaffCard? StaffByGlobalId(long id)
    {
        long idx = id - StaffItemIdBase;
        if (idx >= 0 && idx < Staff.All.Length) return Staff.All[(int)idx];
        return null;
    }

    private static bool IsClubItem(ClubData data, long id)
    {
        if (data.Cosmetics.Any(c => c.ItemId == id)) return true;
        if (data.Consumables.Any(c => c.ItemId == id)) return true;
        if (ManagerByGlobalId(id) is { } mgr && data.Managers.Contains(mgr)) return true;
        if (StaffByGlobalId(id) is { } stf && data.Staff.Contains(stf)) return true;
        return false;
    }

    private static string ListedItemName(ClubData data, Auction au)
    {
        switch (au.Kind)
        {
            case "player":
            {
                int idx = data.Inventory.FindIndex(c => c.ItemId == au.ItemId);
                return idx >= 0 ? data.Inventory[idx].Player.Name : ("item " + au.ItemId);
            }
            case "cosmetic":
            {
                var c = data.Cosmetics.FirstOrDefault(x => x.ItemId == au.ItemId);
                return c.ItemId == au.ItemId ? c.Name : ("item " + au.ItemId);
            }
            case "consumable":
            {
                var c = data.Consumables.FirstOrDefault(x => x.ItemId == au.ItemId);
                return c.ItemId == au.ItemId ? c.Name : ("item " + au.ItemId);
            }
            case "staff":
            {
                if (ManagerByGlobalId(au.ItemId) is { } mgr && data.Managers.Contains(mgr)) return mgr.Name;
                if (StaffByGlobalId(au.ItemId) is { } stf && data.Staff.Contains(stf)) return stf.Name;
                return "item " + au.ItemId;
            }
            default: return "item " + au.ItemId;
        }
    }

    private static int ListedItemRating(ClubData data, Auction au)
    {
        switch (au.Kind)
        {
            case "player":
            {
                int idx = data.Inventory.FindIndex(c => c.ItemId == au.ItemId);
                return idx >= 0 ? data.Inventory[idx].Player.Rating : 0;
            }
            case "cosmetic":
            {
                var c = data.Cosmetics.FirstOrDefault(x => x.ItemId == au.ItemId);
                return c.ItemId == au.ItemId ? c.Rating : 0;
            }
            case "staff":
            {
                if (ManagerByGlobalId(au.ItemId) is { } mgr && data.Managers.Contains(mgr)) return mgr.Rating;
                if (StaffByGlobalId(au.ItemId) is { } stf && data.Staff.Contains(stf)) return stf.Rating;
                return 0;
            }
            default: return 0;
        }
    }

    private void SettleBotBuys(long now)
    {
        var sold = new List<Auction>();
        ClubStore.Mutate(data =>
        {
            foreach (var au in data.Listings.Values)
            {
                if (au.State != "active") continue;
                if (au.BuyNowPrice <= 0)
                {
                    int bidIdx = data.Inventory.FindIndex(c => c.ItemId == au.ItemId);
                    if (bidIdx >= 0)
                    {
                        var (cur, offers) = UserAuctionBids(au, data.Inventory[bidIdx].Player, now);
                        au.CurrentBid = cur;
                        au.Offers = offers;
                    }
                    continue;
                }
                if (au.BotBuyAtUnix <= 0) continue;
                if (now < au.BotBuyAtUnix) continue;
                if (au.ExpiresAtUnix > 0 && now >= au.ExpiresAtUnix) continue;   // never buy an expired listing
                int price = au.BuyNowPrice > 0 ? au.BuyNowPrice
                          : au.CurrentBid > 0 ? au.CurrentBid
                          : au.StartingBid;
                if (price <= 0) continue;
                au.State = "sold";
                au.SoldFor = price;
                au.CurrentBid = price;
                sold.Add(au);
            }
        });
        if (sold.Count == 0) return;

        long totalNet = 0;
        foreach (var au in sold) totalNet += au.SoldFor * 95 / 100;
        FutProfileStore.Mutate(p => p.Coins += totalNet);

        var soldData = ClubStore.Get();
        foreach (var au in sold)
        {
            string name = ListedItemName(soldData, au);
            int rating = ListedItemRating(soldData, au);
            long net = au.SoldFor * 95 / 100;
            Market.PushUserSale(name, rating, au.SoldFor, au.TradeId);
            _log.LogInformation("[Market] BOT bought your listing {0} ({1}) for {2} coins, {3} after the 5% cut",
                au.TradeId, name, au.SoldFor, net);
        }
    }

    private void SettleExpiredListings(long now)
    {
        var sold = new List<Auction>();
        ClubStore.Mutate(data =>
        {
            foreach (var kv in data.Listings.ToList())
            {
                var au = kv.Value;
                if (au.State != "active") continue;
                if (au.ExpiresAtUnix <= 0 || now < au.ExpiresAtUnix) continue;
                if (au.CurrentBid > 0)   // auction with bids: the highest bidder wins at expiry
                {
                    au.State = "sold";
                    au.SoldFor = au.CurrentBid;
                    sold.Add(au);
                    continue;
                }
                au.State = "expired";   // stays in the trade pile marked "expired" until the user clears it
                _log.LogInformation("[Market] listing {0} ({1}) expired - awaiting pick-up in the transfer list",
                    au.TradeId, ListedItemName(data, au));
            }
        });
        if (sold.Count == 0) return;

        long totalNet = 0;
        foreach (var au in sold) totalNet += au.SoldFor * 95 / 100;
        FutProfileStore.Mutate(p => p.Coins += totalNet);

        var expSoldData = ClubStore.Get();
        foreach (var au in sold)
        {
            string name = ListedItemName(expSoldData, au);
            int rating = ListedItemRating(expSoldData, au);
            long net = au.SoldFor * 95 / 100;
            Market.PushUserSale(name, rating, au.SoldFor, au.TradeId);
            _log.LogInformation("[Market] auction {0} ({1}) ended at the top bid of {2} - {3} coins after the 5% cut",
                au.TradeId, name, au.SoldFor, net);
        }
    }

    private void SettleAcceptedOffers(long now)
    {
        var due = Market.AcceptedOffers
            .Where(kv => now >= kv.Value.AcceptAtUnix && kv.Value.SettledAt <= 0)
            .Select(kv => kv.Key).ToList();
        bool changed = false;
        if (due.Count > 0)
        {
            var rnd = new Random();
            foreach (long tradeId in due)
            {
                if (!Market.AcceptedOffers.TryGetValue(tradeId, out var offer) || offer.SettledAt > 0) continue;
                var card = Market.ListingCard(tradeId);
                if (card == null || !Market.LiveAt(tradeId - Market.TradeIdBase, now))
                {
                    _log.LogInformation("[Market] OFFER on trade {0} voided - listing gone by accept time", tradeId);
                    Market.AcceptedOffers.TryRemove(tradeId, out _);
                    changed = true;
                    continue;
                }
                RealPlayer cp = card.Value;
                Market.MarkBought(tradeId, now);
                Market.AcceptedOffers[tradeId] = offer with { SettledAt = now };   // keep rendering the won trade
                long esc = Market.EscrowHeld(tradeId);
                if (esc > 0)   // leftover escrowed bid on the same trade: return it, the offer pays in full
                {
                    Market.RefundEscrow(tradeId);
                    FutProfileStore.Mutate(p => p.Coins += esc);
                }
                FutProfileStore.Mutate(p => p.Coins = Math.Max(0, p.Coins - offer.Bid));
                long itemId = 0;
                var dupes = new List<(long NewId, long OwnedId)>();
                ClubStore.Mutate(d =>
                {
                    var owned = new Dictionary<int, long>();
                    foreach (var it in d.Inventory)
                        if (OwnedInClub(d, it.ItemId) && !owned.ContainsKey(it.Player.CardId)) owned[it.Player.CardId] = it.ItemId;
                    itemId = ClubStore.NextPlayerItemId(d);
                    if (owned.TryGetValue(cp.CardId, out long ownedId)) dupes.Add((itemId, ownedId));
                    d.Inventory.Add(new ClubItem(itemId, cp, 6));
                    var offerMod = Market.ListingMods(tradeId, cp, now);
                    if (offerMod != null) d.PlayerMods[itemId] = offerMod;
                    d.Inventory.RemoveAll(c => c.ItemId == offer.OfferedItemId);   // the offered card leaves the club
                });
                string wonItem = BuildRealPlayerItem(rnd, cp, itemId, now, 6);
                lock (_pendingLock)
                {
                    _pendingPackItems.Add((itemId, wonItem));
                    _pendingDuplicates.AddRange(dupes);
                }
                _log.LogInformation("[Market] OFFER ACCEPTED trade {0}: sent {1} (rating {2}) for {3} coins + card {4}",
                    tradeId, cp.Name, cp.Rating, offer.Bid, offer.OfferedItemId);
                changed = true;
            }
        }
        foreach (var kv in Market.AcceptedOffers.ToList())
            if (kv.Value.SettledAt > 0 && !Market.LiveAt(kv.Key - Market.TradeIdBase, now))
            {
                Market.AcceptedOffers.TryRemove(kv.Key, out _);
                changed = true;
            }
        if (changed) FutProfileStore.Mutate(_ => { });   // persist offer settlement across restarts
    }

    private static (int CurrentBid, int Offers) UserAuctionBids(Auction au, RealPlayer p, long now)
    {
        if (au.State != "active" || au.BuyNowPrice > 0) return (au.CurrentBid, au.Offers);
        if (au.BotBidCeiling <= au.StartingBid) return (0, 0);   // listed above market - nobody bids
        if (now < au.ListedAtUnix) return (0, 0);
        bool hot = p.Rating >= 85 || p.IsSpecial || (p.Rating >= 80 && p.Rare != 0);
        uint s = (uint)au.TradeId;
        s ^= (uint)au.StartingBid * 0x9E3779B1u;
        s ^= (uint)(au.ListedAtUnix & 0x7FFFFFFF) * 0x85EBCA6Bu;
        s ^= s >> 16;
        long firstDelay = hot ? 30 + (s % 150) : 60 + (s % 540);
        long gap = hot ? 40 + ((s >> 8) % 120) : 120 + ((s >> 8) % 480);
        long bidStart = au.ListedAtUnix + firstDelay;
        if (now < bidStart) return (0, 0);
        long k = (now - bidStart) / gap + 1;
        long incr = Math.Max(50, Market.Step(au.StartingBid));
        long bid = Math.Min(au.BotBidCeiling, Market.Snap(au.StartingBid + k * incr));
        return ((int)bid, 0);
    }

    private string TradePileEntryJson(long itemId, Auction au, long now, Random rnd)
    {
        var tpData = ClubStore.Get();
        string item = null;
        bool isPlayer = false;
        int pIdx = tpData.Inventory.FindIndex(c => c.ItemId == itemId);
        if (pIdx >= 0)
        {
            item = BuildRealPlayerItem(rnd, tpData.Inventory[pIdx].Player, itemId, now, 3);
            isPlayer = true;
        }
        else
        {
            var cos = tpData.Cosmetics.FirstOrDefault(c => c.ItemId == itemId);
            if (cos.ItemId == itemId) item = ClubItems.BuildJson(cos, now, "forSale", 3);
            else
            {
                var con = tpData.Consumables.FirstOrDefault(c => c.ItemId == itemId);
                if (con.ItemId == itemId) item = ConsumableItems.BuildJson(con, now, 3, "forSale");
                else
                {
                    if (ManagerByGlobalId(itemId) is { } mgr && tpData.Managers.Contains(mgr))
                        item = BuildManagerItem(mgr, itemId, now, 3);
                    else if (StaffByGlobalId(itemId) is { } stf && tpData.Staff.Contains(stf))
                        item = BuildStaffItem(stf, itemId, now, 3);
                }
            }
        }
        if (item == null) return null;
        if (au == null)
            return "{\"tradeId\":0,\"itemData\":" + item +
                ",\"tradeState\":null,\"buyNowPrice\":0,\"currentBid\":0,\"offers\":0,\"watched\":false," +
                "\"bidState\":\"none\",\"startingBid\":0,\"confidenceValue\":0,\"expires\":-1," +
                "\"sellerName\":\"\",\"seller\":0,\"tradeOwner\":true}";
        int curBid = au.CurrentBid;
        if (isPlayer && au.State == "active")
        {
            var (cb, of) = UserAuctionBids(au, tpData.Inventory[pIdx].Player, now);
            curBid = cb;
            au.Offers = of;
        }
        int offers = isPlayer ? au.Offers : 0;
        if (au.State == "sold")
            return "{\"tradeId\":" + au.TradeId + ",\"itemData\":" + item +
                ",\"tradeState\":\"closed\",\"buyNowPrice\":" + au.BuyNowPrice +
                ",\"currentBid\":" + au.SoldFor + ",\"offers\":0,\"watched\":false," +
                "\"bidState\":\"none\",\"startingBid\":" + au.StartingBid + ",\"confidenceValue\":0," +
                "\"expires\":-1,\"sellerName\":\"\",\"seller\":0,\"tradeOwner\":true,\"coinsProcessed\":true}";
        long remain = au.ExpiresAtUnix - now;
        bool live = remain > 0;
        string state = live ? "active" : "expired";
        long expiresOut = live ? remain : -1;
        return "{\"tradeId\":" + au.TradeId + ",\"itemData\":" + item +
            ",\"tradeState\":\"" + state + "\",\"buyNowPrice\":" + au.BuyNowPrice +
            ",\"currentBid\":" + curBid + ",\"offers\":" + offers + ",\"watched\":false," +
            "\"bidState\":\"none\",\"startingBid\":" + au.StartingBid + ",\"confidenceValue\":100," +
            "\"expires\":" + expiresOut + ",\"sellerName\":\"\",\"sellerEstablished\":0,\"sellerId\":0," +
            "\"tradeOwner\":true,\"tradeIdStr\":\"" + au.TradeId + "\",\"lastSalePrice\":0,\"coinsProcessed\":false}";
    }

    internal static string BuildRealPlayerItem(Random rnd, RealPlayer player, long id, long timestamp, int pile,
                                               string itemState = "free",
                                               int? contractV = null, int? fitnessV = null, int? moraleV = null,
                                               int? playStyleV = null, string positionV = null, int trainingV = -1,
                                               int[] attrBoostV = null, string injuryV = "", int injuryGamesV = 0)
    {
        int rating = player.Rating;
        int assetId = player.Id;
        int resourceId = player.CardId;
        int rareflag = player.Rare;
        int[] attrs = { player.Pace, player.Shooting, player.Passing, player.Dribbling, player.Defending, player.Physical };

        int contract = 7, fitness = 99, playStyle = 250, injuryGames = 0, training = 0, morale = 50, suspension = 0;
        string position = player.Position, injuryType = "none";
        if (ClubStore.Get().PlayerMods.TryGetValue(id, out var mod) && mod != null)
        {
            if (mod.PlayStyle >= 0) playStyle = mod.PlayStyle;
            if (!string.IsNullOrEmpty(mod.Position)) position = mod.Position;
            if (mod.Contract >= 0) contract = mod.Contract;
            if (mod.Fitness >= 0) fitness = mod.Fitness;
            if (mod.AttrBoost != null)
                for (int a = 0; a < 6 && a < mod.AttrBoost.Length; a++)
                    attrs[a] = Math.Clamp(attrs[a] + mod.AttrBoost[a], 1, 99);
            if (mod.TrainingFlag > 0) training = mod.TrainingFlag;   // flags an active "next match" boost
            if (!string.IsNullOrEmpty(mod.Injury) && mod.InjuryGames > 0)
            {
                injuryType = mod.Injury;
                injuryGames = mod.InjuryGames;
            }
            if (mod.Suspension > 0) suspension = mod.Suspension;
        }

        if (contractV.HasValue) contract = contractV.Value;
        if (fitnessV.HasValue) fitness = fitnessV.Value;
        if (moraleV.HasValue) morale = moraleV.Value;
        if (playStyleV.HasValue) playStyle = playStyleV.Value;
        if (positionV != null) position = positionV;
        if (trainingV > 0) training = trainingV;
        if (attrBoostV != null)
            for (int a = 0; a < 6 && a < attrBoostV.Length; a++)
                attrs[a] = Math.Clamp(attrs[a] + attrBoostV[a], 1, 99);
        if (!string.IsNullOrEmpty(injuryV))
        {
            injuryType = injuryV;
            injuryGames = injuryGamesV;
        }

        var attrList = new StringBuilder("[");
        for (int a = 0; a < 6; a++)
        {
            if (a > 0) attrList.Append(',');
            attrList.Append("{\"value\":" + attrs[a] + ",\"index\":" + a + "}");
        }
        attrList.Append(']');
        string zeroStats = "[{\"value\":0,\"index\":0},{\"value\":0,\"index\":1},{\"value\":0,\"index\":2},{\"value\":0,\"index\":3},{\"value\":0,\"index\":4}]";
        return "{\"id\":" + id + ",\"timestamp\":" + timestamp + ",\"formation\":\"f442\"," +
            "\"untradeable\":false,\"assetId\":" + assetId + ",\"rating\":" + rating + "," +
            "\"itemType\":\"player\",\"dream\":false,\"resourceId\":" + resourceId + ",\"owners\":1," +
            "\"discardValue\":" + (rating * 4) + ",\"itemState\":\"" + itemState + "\",\"cardsubtypeid\":3," +
            "\"lastSalePrice\":0,\"morale\":" + morale + ",\"fitness\":" + fitness + ",\"injuryType\":\"" + injuryType + "\",\"injuryGames\":" + injuryGames + "," +
            "\"preferredPosition\":\"" + position + "\",\"statsList\":" + zeroStats +
            ",\"lifetimeStats\":" + zeroStats + ",\"training\":" + training + ",\"contract\":" + contract + ",\"suspension\":" + suspension + "," +
            "\"marketDataMinPrice\":150,\"marketDataMaxPrice\":15000000,\"attributeList\":" + attrList +
            ",\"teamid\":" + player.TeamId + ",\"rareflag\":" + rareflag + ",\"playStyle\":" + playStyle + "," +
            "\"playstyle\":" + playStyle + "," +
            "\"leagueId\":1,\"assists\":0,\"lifetimeAssists\":0," +
            "\"loyaltyBonus\":1,\"pile\":" + pile + ",\"loans\":0,\"nation\":" + player.NationId +
            ",\"resourceGameYear\":2014,\"amount\":0}";
    }

    private static List<ConsumableItem> AvailableConsumables()
    {
        var data = ClubStore.Get();
        return data.Consumables
            .Where(c => !data.Listings.ContainsKey(c.ItemId) && !data.TransferList.Contains(c.ItemId))
            .ToList();
    }

    private static Func<ConsumableItem, bool> ConsumableTabFilter(string tab)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        static bool Is(string it, string p) => (it ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase);
        if (tab.Contains("contract")) return c => Is(c.ItemType, "Contract");
        if (tab.Contains("fitness")) return c => Is(c.ItemType, "Fitness");
        if (tab.Contains("heal") || tab.Contains("health")) return c => Is(c.ItemType, "Health");
        if (tab.Contains("position")) return c => Is(c.ItemType, "TrainingPlayerPos");
        if (tab.Contains("chem") || tab.Contains("style") || tab.Contains("playstyle"))
            return c => string.Equals(c.ItemType, "playStyle", OIC);
        if (tab.Contains("manager") || tab.Contains("league") || tab.Contains("staff"))
            return c => string.Equals(c.ItemType, "managerLeagueModifier", OIC);
        if (tab.Contains("training"))
            return c => (Is(c.ItemType, "TrainingPlayer") || Is(c.ItemType, "TrainingGk"))
                        && !Is(c.ItemType, "TrainingPlayerPos");
        return null;   // bare /consumables or an unrecognised tab -> the whole catalog
    }

    private static List<long> ApplyConsumable(long resourceId, List<long> targets)
    {
        var changed = new List<long>();
        if (resourceId <= 0) return changed;
        var c = ConsumableItems.Catalog.FirstOrDefault(x => x.ResourceId == resourceId);
        if (c.ResourceId != resourceId)
        {
            Console.WriteLine($"[FUT] apply consumable: unknown resourceId {resourceId}");
            return changed;
        }
        ConsumableItems.Effects.TryGetValue(resourceId, out var def);
        bool teamFitness = def.Category == "fitness"
                           && string.Equals(def.Kind, "Squad", StringComparison.OrdinalIgnoreCase);
        var applyTo = teamFitness ? ActiveSquadItemIds() : targets;
        if (applyTo == null || applyTo.Count == 0) return changed;

        ClubStore.Mutate(data =>
        {
            foreach (long tid in applyTo)
            {
                int pi = data.Inventory.FindIndex(x => x.ItemId == tid);
                if (pi >= 0)
                {
                    int rating = data.Inventory[pi].Player.Rating;
                    if (!data.PlayerMods.TryGetValue(tid, out var mod) || mod == null)
                    {
                        mod = new PlayerMod();
                        data.PlayerMods[tid] = mod;
                    }
                    ApplyEffect(mod, rating, c);
                    changed.Add(tid);
                    continue;
                }
                if (ManagerByGlobalId(tid) is { } mgr && data.Managers.Contains(mgr))
                {
                    if (!data.ManagerMods.TryGetValue(tid, out var mmod) || mmod == null)
                    {
                        mmod = new ManagerMod();
                        data.ManagerMods[tid] = mmod;
                    }
                    if (ApplyManagerEffect(mmod, mgr, c, def))
                        changed.Add(tid);
                }
            }
        });
        Console.WriteLine($"[FUT] applied consumable {resourceId} ({def.Category}/{def.Kind}) to {changed.Count} item(s)");
        return changed;
    }

    private static bool ApplyManagerEffect(ManagerMod mod, Manager mgr, ConsumableItem c, ConsumableItems.ConsumableDef def)
    {
        if (string.Equals(c.ItemType, "managerLeagueModifier", StringComparison.OrdinalIgnoreCase))
        {
            int league = LeagueIdForLeagueModifier(c.SubType);
            if (league <= 0) return false;
            mod.LeagueModifier = league;
            return true;
        }
        if (def.Category == "contract" && string.Equals(def.Kind, "Manager", StringComparison.OrdinalIgnoreCase))
        {
            int gain = mgr.Rating <= 64 ? def.Bronze : mgr.Rating <= 74 ? def.Silver : def.Gold;
            if (gain <= 0) gain = Math.Max(0, def.Amount);
            int cur = mod.Contract >= 0 ? mod.Contract : 7;
            mod.Contract = Math.Min(99, cur + Math.Max(0, gain));
            return true;
        }
        return false;
    }

    private static int LeagueIdForLeagueModifier(int subType) => subType switch
    {
        300 => 10,   // Denmark Superliga
        301 => 4,    // Belgium Jupiler Pro League
        302 => 7,    // Brazil Campeonato Brasileiro
        303 => 56,   // Holland Eredivisie
        304 => 13,   // England Premier League
        305 => 14,   // England Championship
        306 => 16,   // France Ligue 1
        307 => 17,   // France Ligue 2
        308 => 31,   // Germany 1. Bundesliga
        309 => 32,   // Germany 2. Bundesliga
        310 => 53,   // Italy Serie A
        311 => 54,   // Italy Serie B
        312 => 39,   // USA Major League Soccer
        313 => 80,   // Norway Tippeligaen
        314 => 50,   // Scotland Premier League
        315 => 41,   // Spain Primera Division
        316 => 42,   // Spain Segunda A
        317 => 78,   // Sweden Allsvenskan
        318 => 19,   // England League One
        319 => 20,   // England League Two
        320 => 76,   // Greece A'Ethniki
        321 => 83,   // Rep. Ireland Airtricity League
        322 => 67,   // Poland T-Mobile Ekstraklasa
        323 => 65,   // Russia Premier League
        324 => 61,   // Turkey Süper Lig
        325 => 66,   // Austria tipp3-Bundesliga
        326 => 111,  // Korea K League Classic
        _ => 0,      // unnamed "League 100" placeholders -> leave the manager's league unchanged
    };

    private static string AppliedItemsJson(long resourceId, List<long> changedIds)
    {
        var data = ClubStore.Get();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rnd = new Random();
        var sb = new StringBuilder("[");
        int n = 0;
        foreach (long tid in changedIds)
        {
            int pi = data.Inventory.FindIndex(x => x.ItemId == tid);
            if (pi >= 0)
            {
                if (n++ > 0) sb.Append(',');
                sb.Append(BuildRealPlayerItem(rnd, data.Inventory[pi].Player, tid, now, data.Inventory[pi].Pile));
                continue;
            }
            if (ManagerByGlobalId(tid) is { } mgr && data.Managers.Contains(mgr))
            {
                if (n++ > 0) sb.Append(',');
                sb.Append(BuildManagerItem(mgr, tid, now, 6));
            }
        }
        sb.Append(']');
        return "{\"success\":true,\"resourceId\":" + resourceId + ",\"itemData\":" + sb + "}";
    }

    private static void ApplyEffect(PlayerMod mod, int targetRating, ConsumableItem c)
    {
        if (string.Equals(c.ItemType, "playStyle", StringComparison.OrdinalIgnoreCase))
        {
            mod.PlayStyle = c.SubType;
            return;
        }
        if (!ConsumableItems.Effects.TryGetValue(c.ResourceId, out var def))
            return;   // no modifier def and not a chem style -> nothing to apply

        switch (def.Category)
        {
            case "contract":   // gain depends on the TARGET player's quality tier
            {
                int gain = targetRating <= 64 ? def.Bronze : targetRating <= 74 ? def.Silver : def.Gold;
                int cur = mod.Contract >= 0 ? mod.Contract : 7;
                mod.Contract = Math.Min(99, cur + Math.Max(0, gain));
                break;
            }
            case "fitness":
            {
                int cur = mod.Fitness >= 0 ? mod.Fitness : 99;
                mod.Fitness = Math.Min(99, cur + Math.Max(0, def.Amount));
                break;
            }
            case "healing":            // reduce the injury only if the card's body part matches
            {
                if (mod.InjuryGames > 0
                    && (string.Equals(def.Kind, "All", StringComparison.OrdinalIgnoreCase)
                        || InjuryMatches(mod.Injury, def.Kind)))
                {
                    mod.InjuryGames = Math.Max(0, mod.InjuryGames - Math.Max(1, def.Amount));
                    if (mod.InjuryGames == 0) mod.Injury = "";
                }
                break;
            }
            case "position":
            {
                string pos = NewPositionFromKind(def.Kind);
                if (pos.Length > 0) mod.Position = pos;
                break;
            }
            case "chemstyle":
                mod.PlayStyle = def.CardSubtypeId != 0 ? def.CardSubtypeId : c.SubType;
                break;
            case "training":
            case "gktraining":
            {
                int amount = Math.Max(0, def.Amount);
                int idx = AttrIndexForKind(def.Kind);        // -1 = ALL, -2 = unmapped
                if (idx == -1) for (int a = 0; a < 6; a++) mod.AttrBoost[a] = amount;
                else if (idx >= 0) mod.AttrBoost[idx] = amount;   // one active boost per attribute (replace)
                if (idx >= -1) mod.TrainingFlag = c.SubType;  // flag the active "next match" boost
                break;
            }
        }
    }

    private static string NewPositionFromKind(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return "";
        var parts = kind.Split(new[] { '→', '>', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1].Trim().ToUpperInvariant() : "";
    }

    private static int AttrIndexForKind(string kind) => (kind ?? "").ToUpperInvariant() switch
    {
        "ALL" => -1,
        "PAC" or "DIV" => 0,
        "SHO" or "HAN" => 1,
        "PAS" or "KIC" => 2,
        "DRI" or "REF" => 3,
        "DEF" or "SPD" => 4,
        "PHY" or "POS" => 5,
        _ => -2,
    };

    private static List<long> ActiveSquadItemIds()
    {
        var data = ClubStore.Get();
        var sq = data.Squads.FirstOrDefault(s => s.Id == data.ActiveSquadId)
                 ?? data.Squads.FirstOrDefault();
        if (sq == null) return new List<long>();
        return sq.Slots.Values.Where(v => v > 0).Distinct().ToList();
    }

    private static List<long> ActiveSquadStarterIds()
    {
        var data = ClubStore.Get();
        var sq = data.Squads.FirstOrDefault(s => s.Id == data.ActiveSquadId)
                 ?? data.Squads.FirstOrDefault();
        if (sq == null) return new List<long>();
        return sq.Slots.Where(kv => kv.Key < 11 && kv.Value > 0).Select(kv => kv.Value).Distinct().ToList();
    }

    private static List<long> ActiveSquadBenchIds()
    {
        var data = ClubStore.Get();
        var sq = data.Squads.FirstOrDefault(s => s.Id == data.ActiveSquadId)
                 ?? data.Squads.FirstOrDefault();
        if (sq == null) return new List<long>();
        return sq.Slots.Where(kv => kv.Key >= 11 && kv.Key <= 17 && kv.Value > 0)
                       .Select(kv => kv.Value).Distinct().ToList();
    }

    private static readonly (string Kind, string[] Tokens)[] InjuryGroups =
    {
        ("Head",      new[] { "head", "concussion" }),
        ("Arm",       new[] { "wrist", "hand", "elbow" }),
        ("UpperBody", new[] { "shoulder", "back", "rib" }),
        ("Knee",      new[] { "knee" }),
        ("Leg",       new[] { "hamstring", "thigh", "calf", "groin" }),
        ("Foot",      new[] { "ankle", "toe", "foot" }),
    };

    private sealed class MatchItemReport
    {
        public int Fitness = -1;
        public string Injury = "";
        public int InjuryGames = 0;
        public int Yellow = 0;
        public int Red = 0;
    }

    private static Dictionary<long, MatchItemReport> ParseMatchItems(string body)
    {
        var map = new Dictionary<long, MatchItemReport>();
        var items = System.Text.RegularExpressions.Regex.Match(body ?? "", "\"items\"\\s*:\\s*\\[(.*?)\\]",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!items.Success) return map;
        foreach (System.Text.RegularExpressions.Match obj in
                 System.Text.RegularExpressions.Regex.Matches(items.Groups[1].Value, "\\{([^}]*)\\}"))
        {
            string o = obj.Groups[1].Value;
            if (!long.TryParse(BodyRx(o, "\"id\"\\s*:\\s*(\\d+)"), out long id)) continue;
            var r = new MatchItemReport();
            if (int.TryParse(BodyRx(o, "\"fitness\"\\s*:\\s*(\\d+)"), out int f)) r.Fitness = f;
            if (int.TryParse(BodyRx(o, "\"injuryGames\"\\s*:\\s*(\\d+)"), out int ig)) r.InjuryGames = ig;
            r.Injury = BodyRx(o, "\"injuryType\"\\s*:\\s*\"([^\"]*)\"");
            if (int.TryParse(BodyRx(o, "\"yellowCards\"\\s*:\\s*(\\d+)"), out int yc)) r.Yellow = yc;
            if (int.TryParse(BodyRx(o, "\"redCards\"\\s*:\\s*(\\d+)"), out int rc)) r.Red = rc;
            map[id] = r;
        }
        return map;
    }

    private static void ApplyMatchConsequences(string body)
    {
        var xi = ActiveSquadStarterIds();
        var bench = ActiveSquadBenchIds();
        if (xi.Count == 0 && bench.Count == 0) return;
        var reports = ParseMatchItems(body);
        var rnd = new Random();
        int played = 0, benched = 0, injuries = 0, bans = 0;
        ClubStore.Mutate(data =>
        {
            PlayerMod ModFor(long tid)
            {
                if (data.Inventory.FindIndex(x => x.ItemId == tid) < 0) return null;   // owned players only
                if (!data.PlayerMods.TryGetValue(tid, out var m) || m == null)
                {
                    m = new PlayerMod();
                    data.PlayerMods[tid] = m;
                }
                return m;
            }

            foreach (var m in data.PlayerMods.Values)
            {
                if (m == null) continue;
                if (m.InjuryGames > 0 && --m.InjuryGames <= 0) { m.InjuryGames = 0; m.Injury = ""; }
                if (m.Suspension > 0) m.Suspension--;
            }

            void ApplyReport(PlayerMod mod, long tid)
            {
                if (!reports.TryGetValue(tid, out var r) || r == null) return;
                if (r.Fitness >= 0) mod.Fitness = r.Fitness;
                if (r.InjuryGames > 0 && !string.IsNullOrEmpty(r.Injury) && r.Injury != "none")
                {
                    mod.Injury = r.Injury;
                    mod.InjuryGames = r.InjuryGames;
                    injuries++;
                }
                if (r.Yellow > 0) mod.YellowCards += r.Yellow;
                if (r.Red > 0) { mod.Suspension += 1; mod.YellowCards = 0; bans++; }
                while (mod.YellowCards >= 5) { mod.Suspension += 1; mod.YellowCards -= 5; bans++; }
            }

            foreach (long tid in xi)
            {
                var mod = ModFor(tid);
                if (mod == null) continue;
                mod.Contract = Math.Max(0, (mod.Contract >= 0 ? mod.Contract : 7) - 1);
                if (reports.TryGetValue(tid, out var r) && r.Fitness >= 0) mod.Fitness = r.Fitness;
                else mod.Fitness = Math.Max(0, (mod.Fitness >= 0 ? mod.Fitness : 99) - rnd.Next(8, 13)); // fallback if not reported
                if (mod.TrainingFlag > 0) { System.Array.Clear(mod.AttrBoost, 0, mod.AttrBoost.Length); mod.TrainingFlag = 0; }
                ApplyReport(mod, tid);
                played++;
            }
            foreach (long tid in bench)
            {
                var mod = ModFor(tid);
                if (mod == null) continue;
                mod.Contract = Math.Max(0, (mod.Contract >= 0 ? mod.Contract : 7) - 1);   // rostered -> contract only
                ApplyReport(mod, tid);                                                    // a sub can still be carded/hurt
                benched++;
            }

            var handled = new HashSet<long>(xi);
            handled.UnionWith(bench);
            foreach (var (tid, r) in reports)
            {
                if (handled.Contains(tid)) continue;
                var mod = ModFor(tid);
                if (mod == null) continue;
                mod.Contract = Math.Max(0, (mod.Contract >= 0 ? mod.Contract : 7) - 1);
                if (r.Fitness >= 0) mod.Fitness = r.Fitness;
                if (mod.TrainingFlag > 0) { System.Array.Clear(mod.AttrBoost, 0, mod.AttrBoost.Length); mod.TrainingFlag = 0; }
                ApplyReport(mod, tid);
                played++;
            }
        });
        Console.WriteLine($"[FUT] match consequences: {played} played, {benched} subs, {injuries} injuries, {bans} bans");
    }

    private static bool InjuryMatches(string injury, string cardKind)
    {
        if (string.IsNullOrEmpty(injury)) return false;
        foreach (var (kind, tokens) in InjuryGroups)
            if (string.Equals(kind, cardKind, StringComparison.OrdinalIgnoreCase))
                return System.Array.Exists(tokens, t => string.Equals(t, injury, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static string ConsumableStatsJson()
    {
        int contractPlayer = 0, contractManager = 0, fitnessPlayer = 0, fitnessTeam = 0, healing = 0;
        int trainingPlayer = 0, trainingGk = 0, position = 0, playerPlayStyle = 0, gkPlayStyle = 0, managerLeague = 0, formation = 0;
        foreach (var c in AvailableConsumables())
        {
            string t = c.ItemType ?? "";
            if (t.StartsWith("ContractStaff", StringComparison.OrdinalIgnoreCase)) contractManager++;
            else if (t.StartsWith("Contract", StringComparison.OrdinalIgnoreCase)) contractPlayer++;
            else if (t.StartsWith("FitnessTeam", StringComparison.OrdinalIgnoreCase)) fitnessTeam++;
            else if (t.StartsWith("Fitness", StringComparison.OrdinalIgnoreCase)) fitnessPlayer++;
            else if (t.StartsWith("Health", StringComparison.OrdinalIgnoreCase)) healing++;
            else if (t.StartsWith("TrainingPlayerPos", StringComparison.OrdinalIgnoreCase)) position++;
            else if (t.StartsWith("TrainingGk", StringComparison.OrdinalIgnoreCase)) trainingGk++;
            else if (t.StartsWith("TrainingPlayer", StringComparison.OrdinalIgnoreCase)) trainingPlayer++;
            else if (t.Equals("playStyle", StringComparison.OrdinalIgnoreCase))
            {
                if (c.SubType >= 269) gkPlayStyle++;
                else playerPlayStyle++;
            }
            else if (t.Equals("managerLeagueModifier", StringComparison.OrdinalIgnoreCase)) managerLeague++;
            else if (t.Equals("formation", StringComparison.OrdinalIgnoreCase)) formation++;
        }
        int total = AvailableConsumables().Count;
        int training = trainingPlayer + trainingGk + position + playerPlayStyle + gkPlayStyle;
        var members = new (string Key, int Val)[]
        {
            ("consumablesContractPlayer", contractPlayer),
            ("consumablesContractManager", contractManager),
            ("consumablesFitnessPlayer", fitnessPlayer),
            ("consumablesFitnessTeam", fitnessTeam),
            ("consumablesHealing", healing),
            ("consumablesTrainingPlayer", trainingPlayer),
            ("consumablesTrainingGk", trainingGk),
            ("consumablesTrainingPlayerPlayStyle", playerPlayStyle),
            ("consumablesTrainingGkPlayStyle", gkPlayStyle),
            ("consumablesPosition", position),
            ("consumablesTrainingManager", managerLeague),
            ("consumablesTrainingManagerLeagueModifier", managerLeague),
            ("consumablesFormationManager", formation),
            ("consumablesContract", contractPlayer + contractManager),
            ("consumablesFitness", fitnessPlayer + fitnessTeam),
            ("consumablesTraining", training),
            ("consumables", total),
        };
        var scalars = string.Join(",", members.Select(x => "\"" + x.Key + "\":" + x.Val));
        var statArr = "[" + string.Join(",", members.Select(x =>
            "{\"contextId\":1,\"contextValue\":0,\"type\":\"" + x.Key + "\",\"typeValue\":" + x.Val + "}")) + "]";
        long tdnow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var itemSb = new StringBuilder("[");
        int wn = 0;
        foreach (var c in AvailableConsumables())
        {
            if (wn++ > 0) itemSb.Append(',');
            itemSb.Append(ConsumableItems.BuildJson(c, tdnow, 7));
        }
        itemSb.Append(']');
        return "{" + scalars + ",\"count\":" + total + ",\"total\":" + total + ",\"numberItems\":" + total +
            ",\"consumableCount\":" + total + ",\"totalResults\":" + total +
            ",\"hasConsumables\":" + (total > 0 ? "true" : "false") +
            ",\"stat\":" + statArr + ",\"entries\":" + statArr + ",\"itemData\":" + itemSb + "}";
    }

    private static string ClubStatBlockJson(int contextId, int contextValue)
    {
        var data = ClubStore.Get();
        var inClub = data.Inventory.Where(c => c.Pile != 3 && c.Pile != 0).ToArray();
        int players = inClub.Length;
        int managers = data.Managers.Count;
        int staff = managers + data.Staff.Count;
        int numberItems = players + data.Consumables.Count + data.Cosmetics.Count + staff;
        var members = new (string Key, int Val)[]
        {
            ("playerCount", players),
            ("totalPlayers", players),
            ("players", players),
            ("rarePlayers", inClub.Count(c => c.Player.Rare > 0)),
            ("playersBronze", inClub.Count(c => c.Player.Rating < 65)),
            ("playersSilver", inClub.Count(c => c.Player.Rating is >= 65 and < 75)),
            ("playersGold", inClub.Count(c => c.Player.Rating >= 75)),
            ("staff", staff),
            ("numberItems", numberItems),
            ("staffManager", managers),
            ("staffHeadCoach", data.Staff.Count(s => s.ItemType == "headCoach")),
            ("staffGKCoach", data.Staff.Count(s => s.ItemType == "gkCoach")),
            ("staffFitnessCoach", data.Staff.Count(s => s.ItemType == "fitnessCoach")),
            ("staffPhysio", data.Staff.Count(s => s.ItemType == "physio")),
            ("stadia", data.Cosmetics.Count(c => c.Type == "stadium")),
            ("balls", data.Cosmetics.Count(c => c.Type == "ball")),
            ("kits", data.Cosmetics.Count(c => c.Type == "kit")),
            ("badges", data.Cosmetics.Count(c => c.Type == "badge")),
            ("trophies", 0),
        };
        var scalars = string.Join(",", members.Select(x => "\"" + x.Key + "\":" + x.Val));
        var statArr = "[" + string.Join(",", members.Select(x =>
            "{\"contextId\":" + contextId + ",\"contextValue\":" + contextValue +
            ",\"type\":\"" + x.Key + "\",\"typeValue\":" + x.Val + "}")) + "]";
        return "{" + scalars + ",\"stat\":" + statArr + ",\"entries\":" + statArr + "}";
    }

    internal static string BuildManagerItem(Manager m, long id, long timestamp, int pile, int rareflag = 1,
                                           string itemState = "free", int leagueIdV = -1, int contractV = -1)
    {
        int league = leagueIdV >= 0 ? leagueIdV : m.LeagueId;
        int contract = contractV >= 0 ? contractV : 7;
        if (ClubStore.Get().ManagerMods.TryGetValue(id, out var mm) && mm != null)
        {
            if (leagueIdV < 0 && mm.LeagueModifier > 0) league = mm.LeagueModifier;
            if (contractV < 0 && mm.Contract >= 0) contract = mm.Contract;
        }
        return "{\"id\":" + id + ",\"timestamp\":" + timestamp + ",\"formation\":\"f442\"," +
            "\"untradeable\":false,\"assetId\":" + m.ResourceId + ",\"rating\":" + m.Rating + "," +
            "\"itemType\":\"manager\",\"dream\":false,\"resourceId\":" + m.ResourceId + ",\"owners\":1," +
            "\"discardValue\":" + (m.Rating * 4) + ",\"itemState\":\"" + itemState + "\",\"cardsubtypeid\":4," +
            "\"lastSalePrice\":0,\"morale\":0,\"fitness\":0,\"injuryType\":\"none\",\"injuryGames\":0," +
            "\"preferredPosition\":\"\",\"statsList\":[],\"lifetimeStats\":[],\"training\":0," +
            "\"contract\":" + contract + ",\"suspension\":0,\"marketDataMinPrice\":150,\"marketDataMaxPrice\":15000000," +
            "\"attributeList\":[],\"teamid\":0,\"rareflag\":" + rareflag + ",\"playStyle\":0," +
            "\"leagueId\":" + league + ",\"leagueid\":" + league + "," +
            "\"assists\":0,\"lifetimeAssists\":0,\"loyaltyBonus\":1,\"pile\":" + pile + ",\"loans\":0," +
            // nationid = manager card's flag slot (lowercase). Send nation too for other consumers.
            "\"nation\":" + m.NationId + ",\"nationid\":" + m.NationId +
            ",\"resourceGameYear\":2014,\"amount\":0}";
    }

    internal const long ManagerItemIdBase = 640_000L;

    private static string ManagerItemsJson(int offset, int countLimit, long now, int pile,
        int nationFilter = -1, int leagueFilter = -1, string levelFilter = "")
    {
        var mData = ClubStore.Get();
        var page = mData.Managers
            .Select(m => (m, gidx: System.Array.IndexOf(Managers.All, m)))
            .Where(t => t.gidx >= 0)
            .Where(t => !mData.Listings.ContainsKey(ManagerItemIdBase + t.gidx)
                && !mData.TransferList.Contains(ManagerItemIdBase + t.gidx))
            .Where(t => (nationFilter == -1 || t.m.NationId == nationFilter)
                && (leagueFilter == -1 || t.m.LeagueId == leagueFilter)
                && levelFilter switch
                {
                    "bronze" => t.m.Rating < 65,
                    "silver" => t.m.Rating is >= 65 and < 75,
                    "gold" => t.m.Rating >= 75,
                    _ => true,
                })
            .Skip(offset).Take(countLimit).ToArray();
        var sb = new StringBuilder("[");
        for (int i = 0; i < page.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(BuildManagerItem(page[i].m, ManagerItemIdBase + page[i].gidx, now, pile));
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static string BuildStaffItem(StaffCard s, long id, long timestamp, int pile,
                                           string itemState = "free")
    {
        bool boost = s.ItemType == "physio" || s.ItemType == "fitnessCoach";
        string attrList = "[]";
        if (boost)
        {
            var sb = new StringBuilder("[");
            for (int a = 0; a <= 6; a++)
            {
                if (a > 0) sb.Append(',');
                sb.Append("{\"value\":" + (a == s.Attr ? s.Amount : 0) + ",\"index\":" + a + "}");
            }
            sb.Append(']');
            attrList = sb.ToString();
        }
        int amount = boost ? s.Amount : 0;

        string extra = "";
        if (s.ItemType == "physio")
        {
            // physio DB: attribute (body part 0-6) + amount (heal). Put amount on the matching Attribute slot.
            var a = new int[6];
            if (s.Attr >= 0 && s.Attr < 6) a[s.Attr] = s.Amount;
            extra = ",\"Attribute1\":" + a[0] + ",\"Attribute2\":" + a[1] + ",\"Attribute3\":" + a[2] +
                    ",\"Attribute4\":" + a[3] + ",\"Attribute5\":" + a[4] + ",\"Attribute6\":" + a[5] +
                    ",\"statBonus\":" + s.Amount + ",\"bonus\":" + s.Amount;
        }
        else if (s.ItemType == "fitnessCoach")
        {
            // fitnessCoach DB: amount + posbonus + fieldpos (no attribute).
            extra = ",\"statBonus\":" + s.Amount + ",\"bonus\":" + s.PosBonus + ",\"posMods\":" + s.PosBonus +
                    ",\"position\":" + s.FieldPos + ",\"gkPositioning\":" + s.FieldPos;
        }
        return "{\"id\":" + id + ",\"timestamp\":" + timestamp + ",\"formation\":\"f442\"," +
            "\"untradeable\":false,\"assetId\":" + s.ResourceId + ",\"rating\":" + s.Rating + "," +
            "\"itemType\":\"" + Esc(s.ItemType) + "\",\"dream\":false,\"resourceId\":" + s.ResourceId + ",\"owners\":1," +
            "\"discardValue\":" + (s.Rating * 4) + ",\"itemState\":\"" + itemState + "\",\"cardsubtypeid\":" + s.CardSubType + "," +
            "\"lastSalePrice\":0,\"morale\":0,\"fitness\":0,\"injuryType\":\"none\",\"injuryGames\":0," +
            "\"preferredPosition\":\"\",\"statsList\":[],\"lifetimeStats\":[],\"training\":0," +
            "\"contract\":7,\"suspension\":0,\"marketDataMinPrice\":150,\"marketDataMaxPrice\":15000000," +
            "\"attributeList\":" + attrList + ",\"teamid\":0,\"rareflag\":" + s.Rare + ",\"playStyle\":0," +
            "\"leagueId\":0,\"leagueid\":0,\"assists\":0,\"lifetimeAssists\":0,\"loyaltyBonus\":1," +
            "\"pile\":" + pile + ",\"loans\":0,\"nation\":0,\"nationid\":0," +
            "\"resourceGameYear\":2014,\"amount\":" + amount + extra + "}";
    }

    internal const long StaffItemIdBase = 650_000L;

    private static string StaffItemsJson(int offset, int countLimit, long now, int pile, string typeFilter = null, string levelFilter = "")
    {
        var data = ClubStore.Get();
        var all = new List<string>(data.Managers.Count + data.Staff.Count);
        if (typeFilter == null)
        {
            for (int i = 0; i < data.Managers.Count; i++)
            {
                int gidx = System.Array.IndexOf(Managers.All, data.Managers[i]);
                if (gidx < 0) continue;
                long id = ManagerItemIdBase + gidx;
                if (!data.Listings.ContainsKey(id) && !data.TransferList.Contains(id))
                    all.Add(BuildManagerItem(data.Managers[i], id, now, pile));
            }
            for (int i = 0; i < data.Staff.Count; i++)
            {
                int gidx = System.Array.IndexOf(Staff.All, data.Staff[i]);
                if (gidx < 0) continue;
                long id = StaffItemIdBase + gidx;
                if (!data.Listings.ContainsKey(id) && !data.TransferList.Contains(id))
                    all.Add(BuildStaffItem(data.Staff[i], id, now, pile));
            }
        }
        else
        {
            for (int i = 0; i < data.Staff.Count; i++)
            {
                var s = data.Staff[i];
                if (!string.Equals(s.ItemType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                int gidx = System.Array.IndexOf(Staff.All, s);
                if (gidx < 0) continue;
                long id = StaffItemIdBase + gidx;
                if (data.Listings.ContainsKey(id) || data.TransferList.Contains(id)) continue;
                bool levelOk = levelFilter switch
                {
                    "bronze" => s.Rating < 65,
                    "silver" => s.Rating is >= 65 and < 75,
                    "gold" => s.Rating >= 75,
                    _ => true,
                };
                if (!levelOk) continue;
                all.Add(BuildStaffItem(s, id, now, pile));
            }
        }
        var page = all.Skip(offset).Take(countLimit);
        return "[" + string.Join(",", page) + "]";
    }

    private static int _matchIdSeq = 1000;

    private Squad ResolveMatchSquad(string body)
    {
        int? namedId = null;
        string sq = BodyRx(body ?? "", "\"squadId\"\\s*:\\s*(\\d+)");
        if (!string.IsNullOrEmpty(sq) && int.TryParse(sq, out int sid)) namedId = sid;

        Squad chosen = null;
        ClubStore.Mutate(data =>
        {
            if (namedId is int want)
                chosen = data.Squads.FirstOrDefault(s => s.Id == want);
            chosen ??= data.Squads.FirstOrDefault(s => s.Id == data.ActiveSquadId);
            chosen ??= data.Squads.FirstOrDefault(s => s.Slots.Count > 0);
            chosen ??= (data.Squads.Count > 0 ? data.Squads[0] : null);
            if (chosen != null && data.ActiveSquadId != chosen.Id)
            {
                data.ActiveSquadId = chosen.Id;
                _log.LogInformation("[FUT] match: active squad -> {0} (from create/ready body)", chosen.Id);
            }
        });
        return chosen ?? new Squad { Id = 0 };
    }

    private static string BuildFullSquadJson(Squad squad)
    {
        var inventory = ClubStore.Get().Inventory;
        var rnd = new Random();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int slotCount = SquadSlots;
        if (squad.Slots.Count > 0) slotCount = Math.Max(slotCount, squad.Slots.Keys.Max() + 1);

        var playersSb = new StringBuilder("[");
        long captainId = 0;
        int filled = 0;
        int KitFor(int idx) => squad.KitNumbers.TryGetValue(idx, out int kn) ? kn : 0;
        for (int idx = 0; idx < slotCount; idx++)
        {
            if (idx > 0) playersSb.Append(',');
            squad.Slots.TryGetValue(idx, out long itemId);

            RealPlayer player = default;
            bool has = false;
            if (itemId != 0)
            {
                var member = inventory.FirstOrDefault(c => c.ItemId == itemId);
                if (member.ItemId != 0) { player = member.Player; has = true; }
                else has = ItemIds.TryResolve(itemId, out player);
            }

            if (!has)
            {
                playersSb.Append("{\"index\":" + idx + ",\"loyaltyBonus\":0,\"kitNumber\":" + KitFor(idx) + ",\"chemistry\":0," +
                                 "\"itemData\":{\"id\":0}}");
                continue;
            }

            filled++;
            string item = BuildRealPlayerItem(rnd, player, itemId, now, 7);
            playersSb.Append("{\"index\":" + idx + ",\"loyaltyBonus\":1,\"kitNumber\":" + KitFor(idx) + ",\"chemistry\":10,\"itemData\":" + item + "}");
            if (captainId == 0 || player.Position == "ST") captainId = itemId;
        }
        playersSb.Append(']');
        Console.WriteLine($"[FUT] squad {squad.Id}: {filled} of {slotCount} slots filled");
        // The client computes and PUTs its own chemistry/rating/starRating, so we just
        // persist and echo those back rather than recomputing server-side.
        int rating = squad.StarRating;

        string actives = ActivesJson(now);

        var equipProf = FutProfileStore.Get().Club;
        long squadStadiumId = ClubItems.Catalog.FirstOrDefault(c => c.Type == "stadium" && c.ResourceId == equipProf.ActiveStadiumId).AssetId;
        long squadBallItemId = equipProf.ActiveBallId;
        long squadBallId = ClubItems.Catalog.FirstOrDefault(c => c.Type == "ball" && c.ResourceId == equipProf.ActiveBallId).AssetId;
        long squadHomeKitId = ClubItems.Catalog.FirstOrDefault(c => c.Type == "kit" && c.ResourceId == equipProf.ActiveHomeKitId).AssetId;
        long squadAwayKitId = ClubItems.Catalog.FirstOrDefault(c => c.Type == "kit" && c.ResourceId == equipProf.ActiveAwayKitId).AssetId;
        Console.WriteLine($"[FUT] SQUAD ACTIVE EQUIPPABLES: stadiumId={squadStadiumId} homeKitId={squadHomeKitId} " +
            $"awayKitId={squadAwayKitId} ballItemId={squadBallItemId} ballId={squadBallId}");

        string kicktakers = "[{\"id\":" + captainId + ",\"index\":0},{\"id\":" + captainId + ",\"index\":1}," +
            "{\"id\":" + captainId + ",\"index\":2},{\"id\":" + captainId + ",\"index\":3}," +
            "{\"id\":" + captainId + ",\"index\":4}]";

        string squadManager = "{\"id\":0,\"itemType\":\"manager\"}";
        if (squad.ManagerId != 0)
        {
            int mIdx = (int)(squad.ManagerId - ManagerItemIdBase);
            if (mIdx >= 0 && mIdx < Managers.All.Length)
                squadManager = BuildManagerItem(Managers.All[mIdx], squad.ManagerId, now, 7);
        }

        return "{\"id\":" + squad.Id + ",\"valid\":true,\"personaId\":" + FutSquadPersonaId + ",\"formation\":\"" + squad.Formation +
            "\",\"rating\":" + rating + ",\"chemistry\":" + squad.Chemistry +
            ",\"manager\":[" + squadManager + "],\"players\":" + playersSb +
            ",\"actives\":" + actives + ",\"dreamSquad\":false,\"changed\":0,\"squadName\":\"" + Esc(squad.Name) + "\"," +
            "\"starRating\":" + rating + ",\"captain\":" + captainId + ",\"kicktakers\":" + kicktakers +
            ",\"squadType\":\"REGULAR_SQUAD\",\"newSquad\":null,\"custom\":null}";
    }


    private static string ClubVisualNode(string type, long resourceId)
    {
        var item = ClubItems.Catalog.FirstOrDefault(c => c.Type == type && c.ResourceId == resourceId);
        if (item.ResourceId != resourceId)
            return "{\"resourceId\":" + resourceId + ",\"teamId\":0,\"categoryId\":0,\"year\":0}";
        return "{\"resourceId\":" + resourceId + ",\"teamId\":" + item.TeamId + ",\"categoryId\":" +
               item.Category + ",\"year\":0}";
    }

    private static string ActivesJson(long now)
    {
        var prof = FutProfileStore.Get();
        var sb = new StringBuilder("[");
        sb.Append(ActiveJson(800001, prof.Club.ActiveStadiumId, "stadium", "activeStadium", 10, now));
        sb.Append(',').Append(ActiveJson(800002, prof.Club.ActiveBallId, "ball", "activeBall", 30, now));
        sb.Append(',').Append(ActiveJson(800003, prof.Club.ActiveHomeKitId, "kit", "activeHomeKit", 9, now));
        sb.Append(',').Append(ActiveJson(800004, prof.Club.ActiveAwayKitId, "kit", "activeAwayKit", 9, now));
        sb.Append(',').Append(ActiveJson(800005, prof.Club.ActiveBadgeId, "badge", "activeBadge", 11, now));
        return sb.Append(']').ToString();
    }

    private static string ActiveJson(long itemId, long resourceId, string type, string state, int subType, long now)
    {
        var item = ClubItems.Catalog.FirstOrDefault(c => c.ResourceId == resourceId);
        if (item.ResourceId != resourceId)
        {
            Console.WriteLine($"[FUT] active {type} {resourceId} is not in the club item catalog");
            return "{}";
        }

        string head =
            "{\"id\":" + itemId + ",\"timestamp\":" + now + ",\"formation\":\"f442\",\"untradeable\":false," +
            "\"assetId\":" + item.AssetId + ",\"rating\":" + item.Rating + "," +
            "\"itemType\":\"" + (type == "badge" ? "custom" : type) + "\"," +
            "\"resourceId\":" + resourceId + ",\"owners\":1,\"discardValue\":110,\"itemState\":\"" + state + "\"," +
            "\"cardsubtypeid\":" + subType + ",\"lastSalePrice\":0,\"statsList\":[],\"lifetimeStats\":[]," +
            "\"attributeList\":[],\"teamid\":" + item.TeamId + ",\"rareflag\":" + item.Rare + ",\"leagueId\":0," +
            "\"pile\":7,\"resourceGameYear\":2014";

        return head + type switch
        {
            "stadium" => ",\"cardassetid\":36,\"category\":" + item.Category + ",\"name\":\"" + Esc(item.Name) +
                         "\",\"description\":\"StadiumDesc_" + item.AssetId + "\"," +
                         "\"biodescription\":\"StadiumDetailDesc\"," +
                         "\"stadiumid\":" + item.AssetId + ",\"capacity\":30000}",
            "ball"    => ",\"cardassetid\":37,\"category\":" + item.Category + ",\"name\":\"" + Esc(item.Name) +
                         "\",\"value\":" + item.Rating + ",\"manufacturer\":\"ManufacturerGeneric\"}",
            "kit"     => ",\"category\":" + item.Category + ",\"year\":0}",
            _         => ",\"category\":" + item.Category + ",\"value\":" + item.Rating +
                         ",\"weightrare\":" + (item.Rare * 10) + ",\"header\":\"Badge\"}",
        };
    }

    private static string CurrenciesJson(long coins) =>
        "[{\"name\":\"COINS\",\"funds\":" + coins + ",\"finalFunds\":" + coins + ",\"originalPrice\":" + coins + "}," +
        "{\"name\":\"POINTS\",\"funds\":0,\"finalFunds\":0,\"originalPrice\":0}," +
        "{\"name\":\"DRAFT_TOKEN\",\"funds\":0,\"finalFunds\":0,\"originalPrice\":0}]";

    // Keep these in sync with AuthenticationComponent (UserId / PersonaName) so the EASFC
    // web identity matches the Blaze-authenticated persona.
    private const int SquadSlots = 23;

    private const long FutSquadPersonaId = 0;

    private const long BlazePersonaId = 1000;
    private static readonly string BlazePersonaName = UserConfig.Username;

    private const string SessionId = "FIFA14SERVERSESSION0000000000000";

    private const string PowSid = "f1fa14f1fa14f1fa14f1fa14f1fa14f1fa14f1fa14f1fa14f1fa14f1fa14beef";

    private static long ParseLong(string s, long dflt) => long.TryParse(s, out var v) ? v : dflt;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...<truncated>";

    private static string Esc(string s) => (s ?? "")
        .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
