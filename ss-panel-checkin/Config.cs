using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Serialization;
// ReSharper disable InconsistentlySynchronizedField

namespace Mygod.SSPanel.Checkin
{
    public class Config
    {
        [XmlElement("Proxy")] public ProxyCollection Proxies = new ProxyCollection();
        [XmlElement("Site")] public List<Site> Sites = new List<Site>();

        [XmlIgnore] private SortedSet<Site> queue;
        [XmlIgnore] public volatile bool NeedsRefetch, IsDirty;

        [XmlAttribute, DefaultValue(16)] public int MaxConnections = 16;
        public ParallelOptions ParallelOptions => new ParallelOptions {MaxDegreeOfParallelism = MaxConnections};

        public void Init()
        {
            queue = new SortedSet<Site>(Sites.Where(site => !site.Disabled));
        }

        public DateTime DoCheckin()
        {
            if (NeedsRefetch)
            {
                queue.Clear();
                Parallel.ForEach(queue, ParallelOptions, site =>
                {
                    try
                    {
                        if (site.Init(Proxies)) IsDirty = true;
                        queue.Add(site);
                    }
                    catch (Exception exc)
                    {
                        Log.WriteLine("FATAL", site.ID, exc.GetMessage());
                    }
                });
                Log.WriteLine("INFO", "Main", "Manual refetch finished.");
                NeedsRefetch = false;
            }
            else
                Parallel.ForEach(queue.TakeWhile(site => site.NextCheckinTime <= DateTime.Now).ToList(), ParallelOptions,
                                 site =>
                {
                    lock (queue) queue.Remove(site);
                    try
                    {
                        if (site.DoCheckin(Proxies)) IsDirty = true;
                        lock (queue) queue.Add(site);
                    }
                    catch (Exception exc)
                    {
                        Log.WriteLine("FATAL", site.ID, exc.GetMessage());
                    }
                });
            return queue.Count == 0 ? DateTime.MinValue : queue.First().NextCheckinTime;
        }

        public void FetchNodes(string path)
        {
            using (var writer = new StreamWriter(path) {AutoFlush = true})
                Parallel.ForEach(queue.SkipWhile(site => site.NextCheckinTime <= DateTime.Now).ToList(), ParallelOptions,
                                 site =>
                {
                    var result = site.FetchNodes(Proxies, ParallelOptions);
                    // ReSharper disable AccessToDisposedClosure
                    lock (writer) writer.Write(result);
                    // ReSharper restore AccessToDisposedClosure
                });
        }
    }

    public class Site : IComparable<Site>
    {
        [XmlAttribute] public string ID, Root, UID, UserEmail, UserName, UserPwd;
        [XmlAttribute, DefaultValue("/user/index.php")] public string UrlMain = "/user/index.php";
        [XmlAttribute] public string UrlCheckin;
        [XmlAttribute, DefaultValue("/user/node.php")] public string UrlNodes = "/user/node.php";
        [XmlAttribute, DefaultValue(@"(node_qr\.php\?id=\d+)")] public string NodeFinder = @"(node_qr\.php\?id=\d+)";
        [XmlAttribute, DefaultValue("/user/{0}")] public string UrlNode = "/user/{0}";
        [XmlAttribute, DefaultValue("Default")] public string Proxy = "Default";
        [XmlAttribute, DefaultValue(false)] public bool Disabled;
        [XmlAttribute] public DateTime LastCheckinTime = DateTime.MinValue;
        [XmlAttribute] public int Interval = 22;
        [XmlAttribute] public long BandwidthCount, CheckinCount;
        public DateTime NextCheckinTime => Interval == -1
            ? LastCheckinTime.Date.AddDays(1) : LastCheckinTime.AddHours(Interval);
        public bool Ready => LastCheckinTime > DateTime.MinValue;

        [XmlElement("Cookie")] public CustomCookie[] AdditionalCookies;

        public int CompareTo(Site other)
        {
            return NextCheckinTime.CompareTo(other.NextCheckinTime);
        }

        [XmlIgnore] private CookieContainer cookie;
        private CookieContainer Cookie
        {
            get
            {
                if (cookie == null)
                {
                    var domain = new Uri(Root);
                    cookie = new CookieContainer();
                    if (!string.IsNullOrWhiteSpace(UID))
                    {
                        cookie.Add(domain, new Cookie("uid", UID));
                        cookie.Add(domain, new Cookie("user_uid", UID));    // old style
                    }
                    if (!string.IsNullOrWhiteSpace(UserEmail)) cookie.Add(domain, new Cookie("user_email", UserEmail));
                    // old style
                    if (!string.IsNullOrWhiteSpace(UserName)) cookie.Add(domain, new Cookie("user_name", UserName));
                    if (!string.IsNullOrWhiteSpace(UserPwd)) cookie.Add(domain, new Cookie("user_pwd", UserPwd));
                    if (AdditionalCookies != null) foreach (var c in AdditionalCookies)
                        cookie.Add(domain, new Cookie(c.Name, c.Value));
                }
                return cookie;
            }
        }

        private static readonly Regex IntervalFinder = new Regex(@"(\d+)小时内?可以签到一次", RegexOptions.Compiled),
            LastCheckinTimeFinder = new Regex("上次签到时间：?<code>([^<]+?)</code>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline),
            ResultAnalyzer = new Regex("({\"msg\":\"\\\\u83b7\\\\u5f97\\\\u4e86|alert\\(\"签到成功，获得了)(\\d+) ?MB" +
                "(\\\\u6d41\\\\u91cf\"}|流量!\"\\))", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            NodeRawAnalyzer = new Regex(@"ss://(.+?):(.+?)@([A-Za-z0-9_\.\-]+?):(\d+)",
                                        RegexOptions.Compiled | RegexOptions.IgnoreCase),
            NodeAnalyzer = new Regex(@"ss://([A-Za-z0-9+/=]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private string ReadAll(string url, ProxyCollection proxies)
        {
            var request = WebRequest.CreateHttp(url);
            request.CookieContainer = Cookie;
            if (proxies.Contains(Proxy)) request.Proxy = proxies[Proxy].ToProxy();
            using (var response = request.GetResponse())
                if (response.ResponseUri != request.RequestUri)
                    throw new IOException($"Redirected to: {response.ResponseUri}. Possibly login failed.");
                else
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        public bool Init(ProxyCollection proxies)
        {
            try
            {
                var str = ReadAll(Root + UrlMain, proxies);
                var match = IntervalFinder.Match(str);
                if (match.Success) Interval = int.Parse(match.Groups[1].Value);
                else if (str.Contains("每天可以签到一次。GMT+8时间的0点刷新。")) Interval = -1;
                else Log.WriteLine("WARN", ID,
                   "Unable to find checkin interval. Please report this site if possible.");
                match = LastCheckinTimeFinder.Match(str);
                if (!match.Success) throw new FormatException("Unable to find last checkin time.");
                LastCheckinTime = DateTime.Parse(match.Groups[1].Value);
                return true;
            }
            catch (WebException exc)
            {
                var response = exc.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw;
                Log.WriteLine("ERROR", ID, "Initialization failed. Message: {0}", exc.Message);
            }
            catch (IOException exc)
            {
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: {0}", exc.Message);
            }
            return false;
        }

        public bool DoCheckin(ProxyCollection proxies)
        {
            if (!(Ready || Init(proxies)) || NextCheckinTime > DateTime.Now) return false;
            var url = UrlCheckin;
            if (string.IsNullOrWhiteSpace(url)) url = "/user/" + (string.IsNullOrWhiteSpace(UserName) ? "_" : "do") +
                    "checkin.php";  // check for old style
            try
            {
                var str = ReadAll(Root + url, proxies);
                var match = ResultAnalyzer.Match(str);
                if (match.Success)
                {
                    var bandwidth = long.Parse(match.Groups[2].Value);
                    if (bandwidth == 0)
                    {
                        Log.WriteLine("WARN", ID, "Checkin succeeded but got 0MB. Reiniting.");
                        return Init(proxies);
                    }
                    LastCheckinTime = DateTime.Now;
                    BandwidthCount += bandwidth;
                    ++CheckinCount;
                    Log.WriteLine("INFO", ID, "Checkin succeeded, got {0}MB.", bandwidth);
                    return true;
                }
                if (str.Contains("window.location='index.php';"))
                {
                    Log.WriteLine("WARN", ID, "Checkin failed. Reiniting.");
                    return Init(proxies);
                }
                Log.WriteLine("ERROR", ID, "Checkin failed. Unknown response: {0}", str);
            }
            catch (WebException exc)
            {
                var response = exc.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw;
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: {0}", exc.Message);
            }
            catch (IOException exc)
            {
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: {0}", exc.Message);
            }
            return false;
        }

        public string FetchNodes(ProxyCollection proxies, ParallelOptions options)
        {
            var result = new StringBuilder();
            try
            {
                var nothing = true;
                Parallel.ForEach(Regex.Matches(ReadAll(Root + UrlNodes, proxies), NodeFinder).OfType<Match>()
                    .Select(match => match.Groups[1].Value).Distinct(), options, node =>
                {
                    var str = ReadAll(Root + string.Format(UrlNode, node), proxies);
                    var match = NodeRawAnalyzer.Match(str);
                    if (!match.Success)
                    {
                        match = NodeAnalyzer.Matches(str).OfType<Match>().Select(m =>
                        {
                            try
                            {
                                return NodeRawAnalyzer.Match("ss://" + Encoding.UTF8.GetString(Convert
                                    .FromBase64String(m.Groups[1].Value)));
                            }
                            catch (FormatException)
                            {
                                return null;
                            }
                        }).FirstOrDefault(m => m?.Success == true);
                        if (match == null) return;
                    }
                    nothing = false;
                    lock (result) result.AppendLine($"{{\"server\":\"{match.Groups[3].Value}\",\"server_port\":" +
                        $"{match.Groups[4].Value},\"password\":\"{match.Groups[2].Value}\",\"method\":\"" +
                        $"{match.Groups[1].Value.Trim()}\",\"remarks\":\"{ID}\"}},");
                });
                if (nothing) throw new Exception("Nothing found on this site.");
            }
            catch (Exception exc)
            {
                Log.ConsoleLine($"({ID}) WARNING: {exc.GetMessage()}");
            }
            return result.ToString();
        }
    }

    public class ProxyCollection : KeyedCollection<string, Proxy>
    {
        protected override string GetKeyForItem(Proxy item)
        {
            return item.ID;
        }
    }

    public class Proxy : IWebProxy
    {
        [XmlAttribute] public string ID;
        [XmlIgnore] public Uri Address;

        [XmlAttribute("Address")]
        public string AddressString
        {
            get { return Address?.ToString(); }
            set { Address = new Uri(value); }
        }

        [XmlIgnore] public ICredentials Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            return Address;
        }

        public bool IsBypassed(Uri host)
        {
            return host.IsLoopback;
        }

        public IWebProxy ToProxy()
        {
            return string.IsNullOrWhiteSpace(Address?.ToString()) ? null : this;
        }
    }

    public class CustomCookie
    {
        [XmlAttribute] public string Name, Value;
    }
}
