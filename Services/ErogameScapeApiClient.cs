using ErogameScapeMetadata.Models;
using HtmlAgilityPack;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ErogameScapeMetadata.Services
{
    public class ErogameScapeApiClient
    {
        private static readonly HttpClient HttpClient;
        private static DateTime _lastRequestTime = DateTime.MinValue;
        private static readonly object _rateLimitLock = new object();
        private const int RateLimitMs = 2500;

        private const string SqlEndpoint =
            "https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/sql_for_erogamer_form.php";

        private const string VndbApiEndpoint = "https://api.vndb.org/kana/vn";

        private const int TbaYearThreshold = 2030;

        private readonly ILogger _logger;

        static ErogameScapeApiClient()
        {
            HttpClient = new HttpClient();
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            HttpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        public ErogameScapeApiClient(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<ErogameScapeGameInfo>> SearchGamesAsync(
            string keyword, CancellationToken ct = default)
        {
            var escapedKeyword = keyword
                .Replace("'", "''")
                .Replace("!", "!!")
                .Replace("%", "!%")
                .Replace("_", "!_");
            var sql = $"SELECT g.id, g.gamename, g.furigana, g.sellday, g.median, g.count2, " +
                      $"b.brandname " +
                      $"FROM gamelist g LEFT JOIN brandlist b ON g.brandname = b.id " +
                      $"WHERE g.gamename LIKE '%{escapedKeyword}%' ESCAPE '!' " +
                      $"ORDER BY g.count2 DESC LIMIT 30";

            var rows = await ExecuteSqlAsync(sql, ct);
            return rows.Select(row => new ErogameScapeGameInfo
            {
                Id = int.Parse(row["id"]),
                GameName = row.GetValueOrDefault("gamename"),
                Furigana = row.GetValueOrDefault("furigana"),
                BrandName = row.GetValueOrDefault("brandname"),
                SellDay = ParseDate(row.GetValueOrDefault("sellday")),
                Median = ParseInt(row.GetValueOrDefault("median")),
                ReviewCount = ParseInt(row.GetValueOrDefault("count2")),
            }).ToList();
        }

        public async Task<ErogameScapeGameInfo> GetGameByIdAsync(
            int gameId, CancellationToken ct = default)
        {
            var sql = $"SELECT g.id, g.gamename, g.furigana, g.sellday, g.median, g.average2, g.count2, " +
                      $"g.dmm, g.dmm_subsc, g.genre, g.shoukai, g.dlsite_id, g.dlsite_domain, " +
                      $"g.erogame, b.brandname, b.url " +
                      $"FROM gamelist g LEFT JOIN brandlist b ON g.brandname = b.id " +
                      $"WHERE g.id = {gameId}";

            var rows = await ExecuteSqlAsync(sql, ct);
            if (rows.Count == 0)
                return null;

            var row = rows[0];
            var game = new ErogameScapeGameInfo
            {
                Id = int.Parse(row["id"]),
                GameName = row.GetValueOrDefault("gamename"),
                Furigana = row.GetValueOrDefault("furigana"),
                BrandName = row.GetValueOrDefault("brandname"),
                BrandUrl = row.GetValueOrDefault("url"),
                SellDay = ParseDate(row.GetValueOrDefault("sellday")),
                Median = ParseInt(row.GetValueOrDefault("median")),
                Average = ParseInt(row.GetValueOrDefault("average2")),
                ReviewCount = ParseInt(row.GetValueOrDefault("count2")),
                DmmId = NullIfEmpty(row.GetValueOrDefault("dmm")),
                DmmSubsc = NullIfEmpty(row.GetValueOrDefault("dmm_subsc")),
                OfficialGenre = NullIfEmpty(row.GetValueOrDefault("genre")),
                Shoukai = NullIfEmpty(row.GetValueOrDefault("shoukai")),
                DlsiteId = NullIfEmpty(row.GetValueOrDefault("dlsite_id")),
                DlsiteDomain = NullIfEmpty(row.GetValueOrDefault("dlsite_domain")),
                IsEroge = row.GetValueOrDefault("erogame") == "t",
            };

            // タグ・シリーズ・特徴を1回のSQLで取得
            await EnrichTagsSeriesFeaturesAsync(game, gameId, ct);

            // DLsite と VNDB を並列実行（異なるサービスなので同時リクエスト可能）
            var dlsiteTask = EnrichFromDlsiteAsync(game, ct);
            var vndbTask = FetchVndbDataAsync(game.GameName, ct);
            await Task.WhenAll(dlsiteTask, vndbTask);

            var vndb = vndbTask.Result;
            game.BackgroundImageUrls = vndb.ScreenshotUrls;
            game.VndbCoverImageUrl = vndb.CoverImageUrl;
            game.VndbCoverIsPortrait = vndb.CoverIsPortrait;

            // あらすじのフォールバック: DLsite → Getchu(日本語) → 批評空間shoukai(テキスト) → VNDB(英語)
            if (string.IsNullOrEmpty(game.Description))
            {
                await EnrichDescriptionFromGetchuAsync(game, ct);
            }
            if (string.IsNullOrEmpty(game.Description))
            {
                if (!string.IsNullOrEmpty(game.Shoukai) && !game.Shoukai.StartsWith("http"))
                {
                    game.Description = game.Shoukai;
                }
                else if (!string.IsNullOrEmpty(vndb.Description))
                {
                    game.Description = vndb.Description;
                }
            }

            // DLsiteからジャンルが取れなかった場合、公式ジャンルをフォールバック
            if (game.Genres.Count == 0 && !string.IsNullOrEmpty(game.OfficialGenre))
            {
                game.Genres.Add(game.OfficialGenre);
            }

            return game;
        }

        private async Task EnrichTagsSeriesFeaturesAsync(
            ErogameScapeGameInfo game, int gameId, CancellationToken ct)
        {
            // タグ・シリーズ・特徴を1回のSQLで取得（レート制限の待機を削減）
            var sql = $"(SELECT 'tag' AS src, p.title AS val, p.system_group AS grp, pt.count AS cnt " +
                      $"FROM povgroups_toukei pt JOIN povlist p ON pt.pov = p.id " +
                      $"WHERE pt.game = {gameId} ORDER BY pt.count DESC) " +
                      $"UNION ALL " +
                      $"(SELECT 'series', g.name, '', 0 " +
                      $"FROM belong_to_gamegroup_list b JOIN gamegrouplist g ON b.gamegroup = g.id " +
                      $"WHERE b.game = {gameId} LIMIT 1) " +
                      $"UNION ALL " +
                      $"(SELECT 'feature', al.title, '', 0 " +
                      $"FROM attributegroupsboolean ab JOIN attributelist al ON ab.attribute = al.id " +
                      $"WHERE ab.game = {gameId} AND ab.boolean = true)";

            var rows = await ExecuteSqlAsync(sql, ct);

            foreach (var row in rows)
            {
                var src = row.GetValueOrDefault("src");
                var val = row.GetValueOrDefault("val") ?? "";

                if (src == "tag")
                {
                    var grp = row.GetValueOrDefault("grp") ?? "";
                    var count = ParseInt(row.GetValueOrDefault("cnt")) ?? 0;
                    if (count >= 2 && (grp == "ジャンル" || grp == "背景" || grp == "傾向"))
                    {
                        var tag = SimplifyPovTitle(val);
                        if (!string.IsNullOrEmpty(tag))
                            game.Tags.Add(tag);
                    }
                }
                else if (src == "series")
                {
                    if (string.IsNullOrEmpty(game.SeriesName))
                        game.SeriesName = NullIfEmpty(val);
                }
                else if (src == "feature")
                {
                    if (!string.IsNullOrEmpty(val))
                        game.Features.Add(val);
                }
            }
        }

        public async Task EnrichFromDlsiteAsync(
            ErogameScapeGameInfo game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(game.DlsiteId))
                return;

            try
            {
                // DLsite APIはレート制限の対象外（別サービス）だが礼儀として少し待つ
                var apiUrl = $"https://www.dlsite.com/{game.DlsiteDomain ?? "pro"}/api/=/product.json?workno={game.DlsiteId}";
                _logger.Info($"DLsite API: {apiUrl}");

                using (var response = await HttpClient.GetAsync(apiUrl, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return;

                    var json = await response.Content.ReadAsStringAsync();
                    // JSON内の \/ エスケープを解除（URLやテキスト抽出を容易にする）
                    json = json.Replace("\\/", "/");

                    // intro_s（あらすじ短縮版）を取得
                    game.Description = ExtractJsonString(json, "intro_s");

                    // ジャンルを取得: "genres":[{"name":"お姉さん",...},...]
                    var genreSection = Regex.Match(json, @"""genres""\s*:\s*\[(.*?)\]");
                    if (genreSection.Success)
                    {
                        var genreNameMatches = Regex.Matches(genreSection.Groups[1].Value,
                            @"""name""\s*:\s*""((?:[^""\\]|\\.)*)""");
                        foreach (Match gm in genreNameMatches)
                        {
                            var name = gm.Groups[1].Value;
                            name = Regex.Replace(name, @"\\u([0-9a-fA-F]{4})", m2 =>
                                ((char)int.Parse(m2.Groups[1].Value, NumberStyles.HexNumber)).ToString());
                            if (!string.IsNullOrEmpty(name))
                                game.Genres.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"DLsite API エラー ({game.DlsiteId}): {ex.Message}");
            }
        }

        public async Task EnrichDescriptionFromGetchuAsync(
            ErogameScapeGameInfo game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(game.GameName))
                return;

            try
            {
                // Getchuでゲーム名を検索
                var searchUrl = "https://www.getchu.com/php/nsearch.phtml"
                    + "?genre=pc_soft&search_type=match&search_keyword="
                    + Uri.EscapeDataString(game.GameName);

                _logger.Info($"Getchu検索: {game.GameName}");

                using (var request = new HttpRequestMessage(HttpMethod.Get, searchUrl))
                {
                    request.Headers.Add("Cookie", "getchu_adalt_flag=getchu.com");
                    using (var response = await HttpClient.SendAsync(request, ct))
                    {
                        if (!response.IsSuccessStatusCode)
                            return;

                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        var html = System.Text.Encoding.GetEncoding("EUC-JP").GetString(bytes);

                        // 検索結果からタイトル一致するGetchu IDを探す（複数候補）
                        var getchuIds = FindGetchuIdsByTitle(html, game.GameName);

                        // あらすじが見つかるまで候補を順に試す
                        foreach (var id in getchuIds)
                        {
                            var desc = await FetchGetchuDescriptionAsync(id, ct);
                            if (!string.IsNullOrEmpty(desc))
                            {
                                game.Description = desc;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"Getchu検索エラー ({game.GameName}): {ex.Message}");
            }
        }

        private List<string> FindGetchuIdsByTitle(string html, string gameName)
        {
            var ids = new List<string>();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var normalized = NormalizeForComparison(gameName);

            // <a href="soft.phtml?id=XXXXX" class="blueb">タイトル</a> を探す
            var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'soft.phtml?id=') and @class='blueb']");
            if (links == null)
                return ids;

            foreach (var link in links)
            {
                var title = HtmlEntity.DeEntitize(link.InnerText).Trim();
                var normalizedTitle = NormalizeForComparison(title);
                // 完全一致 or ゲーム名で始まるタイトル（エディション違い対応）
                if (normalizedTitle == normalized
                    || normalizedTitle.StartsWith(normalized + " "))
                {
                    var href = link.GetAttributeValue("href", "");
                    var idMatch = Regex.Match(href, @"id=(\d+)");
                    if (idMatch.Success && !ids.Contains(idMatch.Groups[1].Value))
                    {
                        _logger.Info($"Getchuタイトル候補: {title} (ID:{idMatch.Groups[1].Value})");
                        ids.Add(idMatch.Groups[1].Value);
                    }
                }
            }

            return ids;
        }

        private async Task<string> FetchGetchuDescriptionAsync(
            string getchuId, CancellationToken ct)
        {
            var url = $"https://www.getchu.com/soft.phtml?id={getchuId}";
            _logger.Info($"Getchuあらすじ取得: {url}");

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Add("Cookie", "getchu_adalt_flag=getchu.com");
                using (var response = await HttpClient.SendAsync(request, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    var html = System.Text.Encoding.GetEncoding("EUC-JP").GetString(bytes);

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // 「ストーリー」セクションを探す（新しいゲーム）
                    var storyText = ExtractGetchuSection(doc, "ストーリー");
                    if (!string.IsNullOrEmpty(storyText))
                        return storyText;

                    // 「商品紹介」セクションを探す（古いゲーム）
                    return ExtractGetchuSection(doc, "商品紹介");
                }
            }
        }

        private static string ExtractGetchuSection(HtmlDocument doc, string sectionTitle)
        {
            // <h2 class="tabletitle ...">セクション名</h2> の次の <div class="tablebody"> 内テキスト
            var headers = doc.DocumentNode.SelectNodes("//h2[contains(@class,'tabletitle')]");
            if (headers == null)
                return null;

            foreach (var header in headers)
            {
                if (!HtmlEntity.DeEntitize(header.InnerText).Trim().Contains(sectionTitle))
                    continue;

                // 次の兄弟要素で tablebody を持つ div を探す
                var sibling = header.NextSibling;
                while (sibling != null)
                {
                    if (sibling.Name == "div"
                        && sibling.GetAttributeValue("class", "").Contains("tablebody"))
                    {
                        // span.bootstrap 内のテキストを取得
                        var span = sibling.SelectSingleNode(".//span[@class='bootstrap']");
                        var node = span ?? sibling;

                        var text = HtmlEntity.DeEntitize(node.InnerHtml);
                        // HTMLタグを改行とテキストに変換
                        text = Regex.Replace(text, @"<br\s*/?>", "\n");
                        text = Regex.Replace(text, @"<[^>]+>", "");
                        text = HtmlEntity.DeEntitize(text).Trim();

                        if (!string.IsNullOrEmpty(text))
                            return text;
                    }
                    sibling = sibling.NextSibling;
                }
            }

            return null;
        }

        private static string ExtractJsonString(string json, string key)
        {
            // "key":"value" or "key":null のパターンを抽出
            var pattern = $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)*)""";
            var match = Regex.Match(json, pattern);
            if (!match.Success)
                return null;

            var value = match.Groups[1].Value;
            // JSONのエスケープをデコード
            value = value.Replace("\\\"", "\"")
                         .Replace("\\\\", "\\")
                         .Replace("\\/", "/")
                         .Replace("\\n", "\n")
                         .Replace("\\r", "")
                         .Replace("\\t", "\t");
            // Unicodeエスケープをデコード
            value = Regex.Replace(value, @"\\u([0-9a-fA-F]{4})", m =>
                ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToString());

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string SimplifyPovTitle(string title)
        {
            // 「SF仕立てのゲーム」→「SF」、「夏ゲー」→「夏」等
            var suffixes = new[] {
                "仕立てのゲーム", "のゲーム", "ゲーム", "ゲー",
                "なゲーム", "な作品", "系のゲーム", "系ゲーム"
            };
            foreach (var s in suffixes)
            {
                if (title.EndsWith(s) && title.Length > s.Length)
                    return title.Substring(0, title.Length - s.Length);
            }
            return title;
        }

        public async Task<VndbResult> FetchVndbDataAsync(
            string gameName, CancellationToken ct = default)
        {
            var result = new VndbResult();
            if (string.IsNullOrEmpty(gameName))
                return result;

            try
            {
                // ステップ1: タイトル検索で候補を取得し、タイトル一致するエントリのIDを特定
                var vndbId = await FindVndbIdByTitleAsync(gameName, ct);
                if (vndbId == null)
                    return result;

                // ステップ2: IDで詳細データを取得
                var detailBody = $"{{\"filters\":[\"id\",\"=\",\"{vndbId}\"]," +
                    $"\"fields\":\"title,description,image.url,image.dims,image.sexual,image.violence,screenshots.url,screenshots.sexual,screenshots.violence\"," +
                    $"\"results\":1}}";

                _logger.Info($"VNDB API詳細取得: {vndbId}");

                using (var content = new StringContent(detailBody, System.Text.Encoding.UTF8, "application/json"))
                using (var response = await HttpClient.PostAsync(VndbApiEndpoint, content, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return result;

                    var json = await response.Content.ReadAsStringAsync();
                    json = json.Replace("\\/", "/");

                    // あらすじを取得
                    var descValue = ExtractJsonString(json, "description");
                    if (!string.IsNullOrEmpty(descValue))
                    {
                        // VNDBのBBCodeタグを除去
                        result.Description = Regex.Replace(descValue, @"\[/?[a-z]+(?:=[^\]]+)?\]", "").Trim();
                    }

                    // カバー画像を取得（SFW・縦長優先）
                    // "image":{"dims":[w,h],"sexual":0,"url":"...","violence":0} の形式
                    var imageSection = Regex.Match(json, @"""image""\s*:\s*\{([^{}]*(?:\[[^\]]*\][^{}]*)*)\}");
                    if (imageSection.Success)
                    {
                        var imgBlock = imageSection.Groups[1].Value;
                        var imgUrl = Regex.Match(imgBlock, @"""url""\s*:\s*""([^""]+)""");
                        var imgSexual = Regex.Match(imgBlock, @"""sexual""\s*:\s*([\d.]+)");
                        var imgViolence = Regex.Match(imgBlock, @"""violence""\s*:\s*([\d.]+)");
                        var imgDims = Regex.Match(imgBlock, @"""dims""\s*:\s*\[(\d+)\s*,\s*(\d+)\]");

                        if (imgUrl.Success && imgSexual.Success && imgViolence.Success
                            && double.TryParse(imgSexual.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                            && double.TryParse(imgViolence.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                            && s < 1.0 && v < 1.0)
                        {
                            result.CoverImageUrl = imgUrl.Groups[1].Value;

                            // 縦長判定（dims取得可能な場合）
                            if (imgDims.Success
                                && int.TryParse(imgDims.Groups[1].Value, out var w)
                                && int.TryParse(imgDims.Groups[2].Value, out var h))
                            {
                                result.CoverIsPortrait = h > w;
                            }
                        }
                    }

                    // screenshotsの各エントリを抽出し、SFWのみフィルタリング
                    // カバー画像と同じURLは背景画像から除外
                    var screenshotSection = Regex.Match(json, @"""screenshots""\s*:\s*\[(.*)\]");
                    if (screenshotSection.Success)
                    {
                        var screenshotsJson = screenshotSection.Groups[1].Value;
                        var objectMatches = Regex.Matches(screenshotsJson,
                            @"\{[^{}]*\}");

                        foreach (Match obj in objectMatches)
                        {
                            var block = obj.Value;
                            var urlMatch = Regex.Match(block, @"""url""\s*:\s*""([^""]+)""");
                            var sexualMatch = Regex.Match(block, @"""sexual""\s*:\s*([\d.]+)");
                            var violenceMatch = Regex.Match(block, @"""violence""\s*:\s*([\d.]+)");

                            if (!urlMatch.Success || !sexualMatch.Success || !violenceMatch.Success)
                                continue;

                            var url = urlMatch.Groups[1].Value;
                            if (url == result.CoverImageUrl)
                                continue;

                            if (double.TryParse(sexualMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sexual)
                                && double.TryParse(violenceMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var violence)
                                && sexual < 1.0 && violence < 1.0)
                            {
                                result.ScreenshotUrls.Add(url);
                            }
                        }
                    }

                    _logger.Info($"VNDB: あらすじ={!string.IsNullOrEmpty(result.Description)}, カバー={!string.IsNullOrEmpty(result.CoverImageUrl)}, SFWスクリーンショット={result.ScreenshotUrls.Count}件");
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"VNDBデータ取得エラー: {ex.Message}");
            }

            return result;
        }

        private async Task<string> FindVndbIdByTitleAsync(
            string gameName, CancellationToken ct)
        {
            var searchBody = $"{{\"filters\":[\"search\",\"=\",{EscapeJsonString(gameName)}]," +
                $"\"fields\":\"title,alttitle\"," +
                $"\"results\":5}}";

            _logger.Info($"VNDB APIタイトル検索: {gameName}");

            using (var content = new StringContent(searchBody, System.Text.Encoding.UTF8, "application/json"))
            using (var response = await HttpClient.PostAsync(VndbApiEndpoint, content, ct))
            {
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();

                // 各結果の id, title, alttitle を抽出して照合
                var idMatches = Regex.Matches(json, @"""id""\s*:\s*""(v\d+)""");
                var titleMatches = Regex.Matches(json, @"""title""\s*:\s*""((?:[^""\\]|\\.)*)""");
                var alttitleMatches = Regex.Matches(json, @"""alttitle""\s*:\s*(?:""((?:[^""\\]|\\.)*)""|\bnull\b)");

                var count = Math.Min(idMatches.Count, titleMatches.Count);
                var normalizedGameName = NormalizeForComparison(gameName);

                // 完全一致を優先検索（全角・半角を正規化して比較）
                for (int i = 0; i < count; i++)
                {
                    var title = titleMatches[i].Groups[1].Value;
                    var alttitle = i < alttitleMatches.Count ? alttitleMatches[i].Groups[1].Value : "";

                    if (NormalizeForComparison(title) == normalizedGameName
                        || (!string.IsNullOrEmpty(alttitle) && NormalizeForComparison(alttitle) == normalizedGameName))
                    {
                        var matchedId = idMatches[i].Groups[1].Value;
                        _logger.Info($"VNDBタイトル一致: {title} ({matchedId})");
                        return matchedId;
                    }
                }

                _logger.Info($"VNDBタイトル一致なし: {gameName} (候補{count}件)");
                return null;
            }
        }

        public class VndbResult
        {
            public string Description { get; set; }
            public string CoverImageUrl { get; set; }
            public bool CoverIsPortrait { get; set; }
            public List<string> ScreenshotUrls { get; set; } = new List<string>();
        }

        private static string EscapeJsonString(string value)
        {
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }

        /// <summary>
        /// タイトル比較用に全角英数字・記号を半角に正規化し、小文字化する。
        /// 批評空間「アマカノ2」とVNDB「アマカノ２」のような差異を吸収する。
        /// </summary>
        private static string NormalizeForComparison(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                // 全角英数字・記号（！-～, U+FF01-U+FF5E）を半角（!-~, U+0021-U+007E）に変換
                if (c >= '\uFF01' && c <= '\uFF5E')
                    chars[i] = (char)(c - 0xFEE0);
            }

            return new string(chars).ToLowerInvariant();
        }

        private async Task<List<Dictionary<string, string>>> ExecuteSqlAsync(
            string sql, CancellationToken ct)
        {
            await RateLimitAsync(ct);

            _logger.Info($"ErogameScape SQL: {sql}");

            using (var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("sql", sql)
            }))
            using (var response = await HttpClient.PostAsync(SqlEndpoint, content, ct))
            {
                response.EnsureSuccessStatusCode();

                var html = await response.Content.ReadAsStringAsync();
                return ParseHtmlTable(html);
            }
        }

        private List<Dictionary<string, string>> ParseHtmlTable(string html)
        {
            var results = new List<Dictionary<string, string>>();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var table = doc.DocumentNode.SelectSingleNode("//table");
            if (table == null)
                return results;

            var headerRow = table.SelectSingleNode(".//tr");
            if (headerRow == null)
                return results;

            var headers = headerRow.SelectNodes(".//th|.//td")
                ?.Select(n => HtmlEntity.DeEntitize(n.InnerText).Trim())
                .ToList();

            if (headers == null || headers.Count == 0)
                return results;

            var dataRows = table.SelectNodes(".//tr");
            if (dataRows == null)
                return results;

            foreach (var row in dataRows.Skip(1))
            {
                var cells = row.SelectNodes(".//td");
                if (cells == null)
                    continue;

                var dict = new Dictionary<string, string>();
                for (int i = 0; i < Math.Min(headers.Count, cells.Count); i++)
                {
                    var value = HtmlEntity.DeEntitize(cells[i].InnerText).Trim();
                    dict[headers[i]] = value;
                }

                results.Add(dict);
            }

            return results;
        }

        private async Task RateLimitAsync(CancellationToken ct)
        {
            TimeSpan delay;
            lock (_rateLimitLock)
            {
                var elapsed = DateTime.UtcNow - _lastRequestTime;
                var waitMs = elapsed.TotalMilliseconds < RateLimitMs
                    ? RateLimitMs - (int)elapsed.TotalMilliseconds
                    : 0;
                delay = waitMs > 0 ? TimeSpan.FromMilliseconds(waitMs) : TimeSpan.Zero;
                _lastRequestTime = DateTime.UtcNow + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            if (DateTime.TryParseExact(value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (date.Year >= TbaYearThreshold)
                    return null;
                return date;
            }

            return null;
        }

        private static int? ParseInt(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            if (int.TryParse(value, out var result))
                return result;
            return null;
        }

        public async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using (var response = await HttpClient.GetAsync(url, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;

                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!contentType.StartsWith("image/"))
                        return null;

                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn($"画像ダウンロードエラー ({url}): {ex.Message}");
                return null;
            }
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    internal static class DictionaryExtensions
    {
        public static string GetValueOrDefault(
            this Dictionary<string, string> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value : null;
        }
    }
}
