using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Common.Indexers
{
    public class FunFile : BaseWebIndexer
    {
        private string SearchUrl => SiteLink + "browse.php";
        private string LoginUrl => SiteLink + "takelogin.php";

        private new ConfigurationDataBasicLoginWithRSSAndDisplay configData
        {
            get => (ConfigurationDataBasicLoginWithRSSAndDisplay)base.configData;
            set => base.configData = value;
        }

        public FunFile(IIndexerConfigurationService configService, WebClient w, Logger l, IProtectionService ps)
            : base(name: "FunFile",
                description: "A general tracker",
                link: "https://www.funfile.org/",
                caps: new TorznabCapabilities(),
                configService: configService,
                client: w,
                logger: l,
                p: ps,
                configData: new ConfigurationDataBasicLoginWithRSSAndDisplay())
        {
            Encoding = Encoding.GetEncoding("iso-8859-1");
            Language = "en-us";
            Type = "private";

            AddCategoryMapping(44, TorznabCatType.TVAnime); // Anime
            AddCategoryMapping(22, TorznabCatType.PC); // Applications
            AddCategoryMapping(43, TorznabCatType.AudioAudiobook); // Audio Books
            AddCategoryMapping(27, TorznabCatType.Books); // Ebook
            AddCategoryMapping(4, TorznabCatType.PCGames); // Games
            AddCategoryMapping(40, TorznabCatType.OtherMisc); // Miscellaneous
            AddCategoryMapping(19, TorznabCatType.Movies); // Movies
            AddCategoryMapping(6, TorznabCatType.Audio); // Music
            AddCategoryMapping(31, TorznabCatType.PCPhoneOther); // Portable
            AddCategoryMapping(7, TorznabCatType.TV); // TV
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            var pairs = new Dictionary<string, string> {
                { "username", configData.Username.Value },
                { "password", configData.Password.Value },
                { "login", "Login" },
            };

            var result = await RequestLoginAndFollowRedirect(LoginUrl, pairs, null, true, null, LoginUrl);
            await ConfigureIfOK(result.Cookies, result.Content.Contains("logout.php"), () =>
            {
                var parser = new HtmlParser();
                var dom = parser.ParseDocument(result.Content);
                var errorMessage = dom.QuerySelector("td.mf_content").InnerHtml;
                throw new ExceptionWithConfigData(errorMessage, configData);
            });
            return IndexerConfigurationStatus.RequiresTesting;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            var releases = new List<ReleaseInfo>();

            var searchString = query.GetQueryString();
            var searchUrl = SearchUrl;
            var queryCollection = new NameValueCollection();
            queryCollection.Add("incldead", "1");
            queryCollection.Add("showspam", "1");

            if (!string.IsNullOrWhiteSpace(searchString))
            {
                queryCollection.Add("search", searchString);
            }

            var cats = MapTorznabCapsToTrackers(query);
            var cat = "0";
            if (cats.Count == 1)
            {
                cat = cats[0];
            }
            queryCollection.Add("cat", cat);

            searchUrl += "?" + queryCollection.GetQueryString();

            var results = await RequestStringWithCookiesAndRetry(searchUrl);

            // Occasionally the cookies become invalid, login again if that happens
            if (results.IsRedirect)
            {
                await ApplyConfiguration(null);
                results = await RequestStringWithCookiesAndRetry(searchUrl);
            }

            try
            {
                var parser = new HtmlParser();
                var dom = parser.ParseDocument(results.Content);
                var rows = dom.QuerySelectorAll("table[cellpadding=2] > tbody > tr:has(td.row3)");
                foreach (var row in rows)
                {
                    var release = new ReleaseInfo();
                    release.MinimumRatio = 1;
                    release.MinimumSeedTime = 172800; // 48 hours

                    var qCatLink = row.QuerySelector("a[href^=browse.php?cat=]");
                    var qDetailsLink = row.QuerySelector("a[href^=details.php?id=]");
                    var qSeeders = row.QuerySelector("td:nth-of-type(10)");
                    var qLeechers = row.QuerySelector("td:nth-of-type(11)");
                    var qDownloadLink = row.QuerySelector("a[href^=download.php]");
                    var qTimeAgo = row.QuerySelector("td:nth-of-type(6)");
                    var qSize = row.QuerySelector("td:nth-of-type(8)");

                    var catStr = qCatLink.GetAttribute("href").Split('=')[1].Split('&')[0];
                    release.Category = MapTrackerCatToNewznab(catStr);

                    release.Link = new Uri(SiteLink + qDownloadLink.GetAttribute("href"));
                    release.Title = qDetailsLink.GetAttribute("title").Trim();
                    release.Comments = new Uri(SiteLink + qDetailsLink.GetAttribute("href"));
                    release.Guid = release.Link;

                    var sizeStr = qSize.TextContent;
                    release.Size = ReleaseInfo.GetBytes(sizeStr);

                    release.Seeders = ParseUtil.CoerceInt(qSeeders.TextContent);
                    release.Peers = ParseUtil.CoerceInt(qLeechers.TextContent) + release.Seeders;

                    var dateStr = qTimeAgo.TextContent;
                    release.PublishDate = DateTimeUtil.FromTimeAgo(dateStr);

                    var files = row.QuerySelector("td:nth-child(4)").TextContent;
                    release.Files = ParseUtil.CoerceInt(files);

                    var grabs = row.QuerySelector("td:nth-child(9)").TextContent;
                    release.Grabs = ParseUtil.CoerceInt(grabs);

                    var ka = row.NextElementSibling;
                    var DLFactor = ka.QuerySelector("table > tbody > tr:nth-child(3) > td:nth-child(2)").TextContent.Replace("X", "");
                    var ULFactor = ka.QuerySelector("table > tbody > tr:nth-child(3) > td:nth-child(1)").TextContent.Replace("X", "");
                    release.DownloadVolumeFactor = ParseUtil.CoerceDouble(DLFactor);
                    release.UploadVolumeFactor = ParseUtil.CoerceDouble(ULFactor);

                    releases.Add(release);
                }
            }
            catch (Exception ex)
            {
                OnParseError(results.Content, ex);
            }

            return releases;
        }
    }
}
