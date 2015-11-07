using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        public DateTime NextCheckinTime => queue.First().NextCheckinTime;

        public void Init()
        {
            queue = new SortedSet<Site>(Sites.Where(site => site.Status == SiteStatus.Enabled));
        }

        public DateTime DoCheckin()
        {
            var result = default(DateTime);
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
            else Parallel.ForEach(queue.TakeWhile(site => (result = site.NextCheckinTime) <= DateTime.Now).ToList(),
                ParallelOptions, site =>
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
            return result;
        }

        public void FetchNodes(string path)
        {
            using (var writer = new StreamWriter(path) {AutoFlush = true})
                Parallel.ForEach(queue.SkipWhile(site => site.NextCheckinTime <= DateTime.Now)
                                      .Concat(Sites.Where(site => site.Status == SiteStatus.NodesOnly)).ToList(),
                                 ParallelOptions, site =>
                {
                    var result = site.FetchNodes(Proxies, ParallelOptions);
                    // ReSharper disable AccessToDisposedClosure
                    lock (writer) writer.Write(result);
                    // ReSharper restore AccessToDisposedClosure
                });
        }

        public void TestProxies()
        {
            var nothing = true;
            foreach (var proxy in Proxies.Where(proxy => !string.IsNullOrWhiteSpace(proxy.TestUrl)))
            {
                Log.ConsoleLine("INFO: Testing " + proxy.ID);
                proxy.Test();
                nothing = false;
            }
            if (nothing) Log.ConsoleLine("INFO: No proxy needs testing. Are you missing /Config/Proxy/@TestUrl?");
        }
    }

    public class Site : IComparable<Site>
    {
        [XmlAttribute] public string ID, Root, UID, UserEmail, UserName, UserPwd;
        [XmlAttribute, DefaultValue("/user/index.php")] public string UrlMain = "/user/index.php";
        [XmlAttribute] public string UrlCheckin, PostCheckin;
        [XmlAttribute, DefaultValue("/user/node.php")] public string UrlNodes = "/user/node.php";
        [XmlAttribute, DefaultValue(@"node_qr\.php\?id=\d+")] public string NodeFinder = @"node_qr\.php\?id=\d+";
        [XmlAttribute, DefaultValue("/user/$&")] public string UrlNode = "/user/$&";
        [XmlAttribute, DefaultValue("Default")] public string Proxy = "Default";
        [XmlAttribute, DefaultValue(SiteStatus.Enabled)] public SiteStatus Status;
        [XmlAttribute, DefaultValue(typeof(DateTime), "")] public DateTime LastCheckinTime;
        [XmlAttribute, DefaultValue(0)] public int Interval;
        [XmlAttribute, DefaultValue(0)] public long BandwidthCount, CheckinCount;
        public DateTime NextCheckinTime => Interval == -1
            ? LastCheckinTime.Date.AddDays(1) : LastCheckinTime.AddHours(Interval);
        public bool Ready => Interval != 0;

        [XmlElement("Cookie")] public CustomCookie[] AdditionalCookies;

        public int CompareTo(Site other)
        {
            var result = NextCheckinTime.CompareTo(other.NextCheckinTime);
            return result == 0 ? string.Compare(ID, other.ID, StringComparison.Ordinal) : result;
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

        private static readonly Regex IntervalFinder = new Regex(@"(\d+)(小时|天)内?，?只?可以(签到|领取)一次",
                RegexOptions.Compiled),
            LastCheckinTimeFinder = new Regex("(上次(签到|领取)时间：?|Last Time: )(<code>)?(.+?)</",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline),
            ResultAnalyzer = new Regex("(\\\\u83b7\\\\u5f97\\\\u4e86|Won |alert\\(\"签到成功，获得了)(\\d+) ?(MB|Coin)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            NodeRawAnalyzer = new Regex(
                @"ss://(.+?):(.+?)@(\[([0-9a-f:]+)\]|[0-9a-f:]+|[a-z0-9_\-]+\.[a-z0-9_\.\-]+):(\d+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase),
            NodeAnalyzer = new Regex(@"ss://([A-Za-z0-9+/=]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private string ReadAll(string url, ProxyCollection proxies, string post = null, int retries = 1)
        {
        retry:
            try
            {
                var request = WebRequest.CreateHttp(url);
                request.CookieContainer = Cookie;
                request.Referer = Root + UrlMain;
                if (proxies.Contains(Proxy)) request.Proxy = proxies[Proxy].ToProxy();
                if (post != null)
                {
                    request.Method = "POST";
                    request.ContentType = "application/x-www-form-urlencoded";
                    var data = Encoding.UTF8.GetBytes(post);
                    request.ContentLength = data.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(data, 0, data.Length);
                }
                using (var response = request.GetResponse())
                    if (response.ResponseUri != request.RequestUri)
                        throw new IOException($"Redirected to: {response.ResponseUri}. Possibly login failed.");
                    else
                        using (var stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
            catch (WebException)
            {
                if (--retries > 0) goto retry;
                throw;
            }
        }

        public bool Init(ProxyCollection proxies)
        {
            try
            {
                var str = ReadAll(Root + UrlMain, proxies);
                var match = IntervalFinder.Match(str);
                if (match.Success)
                {
                    Interval = int.Parse(match.Groups[1].Value);
                    if (match.Groups[2].Value == "天") Interval *= 24;
                }
                else if (str.Contains("每天可以签到一次。GMT+8时间的0点刷新。")) Interval = -1;
                else
                {
                    Interval = 22;
                    Log.WriteLine("WARN", ID,
                        "Unable to find checkin interval, assuming 22h. Please report this site if possible.");
                }
                match = LastCheckinTimeFinder.Match(str);
                if (match.Success)
                {
                    LastCheckinTime = DateTime.Parse(match.Groups[4].Value);
                    if (Ready) DoCheckin(proxies);
                }
                else Log.WriteLine("WARN", ID, "Unable to find last checkin time.");
                return true;
            }
            catch (WebException exc)
            {
                var response = exc.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw;
                Log.WriteLine("ERROR", ID, $"Initialization failed. Message: {exc.Message}");
            }
            catch (IOException exc)
            {
                Log.WriteLine("ERROR", ID, $"Checkin failed. Message: {exc.Message}");
            }
            return false;
        }

        public bool DoCheckin(ProxyCollection proxies)
        {
            if (!Ready) return Init(proxies);
            if (NextCheckinTime > DateTime.Now) return false;
            var url = UrlCheckin;
            if (string.IsNullOrWhiteSpace(url)) url = "/user/" + (string.IsNullOrWhiteSpace(UserName) ? "_" : "do") +
                    "checkin.php";  // check for old style
            try
            {
                var str = ReadAll(Root + url, proxies, PostCheckin);
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
                    Log.WriteLine("INFO", ID, $"Checkin succeeded, got {bandwidth}MB.");
                    return true;
                }
                if (str == "null" || str.Contains("window.location='index.php';") ||
                    str.Contains("请等待至您的签到时间再进行签到") || str.Contains(@"\u7b7e\u8fc7\u5230\u4e86"))
                {
                    Log.WriteLine("WARN", ID, "Checkin failed. Reiniting.");
                    return Init(proxies);
                }
                Log.WriteLine("ERROR", ID, "Checkin failed. Unknown response: " + str);
            }
            catch (WebException exc)
            {
                var response = exc.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw;
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: " + exc.Message);
            }
            catch (IOException exc)
            {
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: " + exc.Message);
            }
            return false;
        }

        public string FetchNodes(ProxyCollection proxies, ParallelOptions options)
        {
            var result = new StringBuilder();
            try
            {
                var nothing = true;
                Parallel.ForEach(Regex.Matches(ReadAll(Root + UrlNodes, proxies, null, 4), NodeFinder).OfType<Match>()
                    .Select(match => match.Result(UrlNode)).Distinct(), options, path =>
                {
                    var str = ReadAll(Root + path, proxies, null, 4);
                    var node = NodeRawAnalyzer.Match(str);
                    if (!node.Success)
                    {
                        node = NodeAnalyzer.Matches(str).OfType<Match>().Select(m =>
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
                        if (node == null) return;
                    }
                    nothing = false;
                    var server = node.Groups[node.Groups[4].Success ? 4 : 3].Value;
                    var remarks = server.Contains(ID, StringComparison.OrdinalIgnoreCase) ? string.Empty : ID;
                    lock (result) result.AppendLine($"{{\"server\":\"{server}\",\"server_port\":" +
                        $"{node.Groups[5].Value},\"password\":\"{node.Groups[2].Value}\",\"method\":\"" +
                        $"{node.Groups[1].Value.Trim()}\",\"remarks\":\"{remarks}\"}},");
                });
                if (nothing) throw new Exception("Nothing found on this site.");
            }
            catch (Exception exc)
            {
                Log.ConsoleLine($"({ID}) WARNING: {exc.Message}");
            }
            return result.ToString();
        }

        public override string ToString()
        {
            return $"{ID} ({NextCheckinTime})";
        }
    }

    public enum SiteStatus
    {
        // ReSharper disable once UnusedMember.Global
        Enabled, Disabled, NodesOnly
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

        [XmlAttribute] public string TestUrl;
        [XmlAttribute, DefaultValue(4000)] public int TestTimeout = 4000;

        public void Test()
        {
            var request = WebRequest.CreateHttp(TestUrl);
            request.Proxy = ToProxy();
            long size = 0;
            var thread = new Thread(() =>
            {
                try
                {
                    var buffer = new byte[4096];
                    using (var response = request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, 4096)) > 0) size += read;
                    }
                }
                catch { }
            });
            var stopwatch = Stopwatch.StartNew();
            thread.Start();
            if (thread.Join(TestTimeout)) stopwatch.Stop();
            else
            {
                thread.Abort();
                stopwatch.Stop();
                Log.ConsoleLine("Test timed out.");
            }
            var secs = stopwatch.Elapsed.TotalSeconds;
            Log.ConsoleLine($"Downloaded {Helper.GetSize(size)} in {secs}s, average {Helper.GetSize(size / secs)}/s");
        }

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
