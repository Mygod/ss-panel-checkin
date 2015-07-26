using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
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

        public void Init()
        {
            queue = new SortedSet<Site>(Sites.Where(site => !site.Disabled));
        }

        public DateTime DoCheckin()
        {
            if (NeedsRefetch)
            {
                queue.Clear();
                Parallel.ForEach(queue, site =>
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
                Parallel.ForEach(queue.TakeWhile(site => site.NextCheckinTime <= DateTime.Now).ToList(), site =>
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
    }

    public class Site : IComparable<Site>
    {
        [XmlAttribute] public string ID, Root, UID, UserEmail, UserName, UserPwd;
        [XmlAttribute, DefaultValue("Default")] public string Proxy = "Default";
        [XmlAttribute, DefaultValue(false)] public bool Disabled;
        [XmlAttribute] public DateTime LastCheckinTime = DateTime.MinValue;
        [XmlAttribute] public int Interval = 22;
        [XmlAttribute] public long BandwidthCount, CheckinCount;
        public DateTime NextCheckinTime => Interval == -1
            ? LastCheckinTime.Date.AddDays(1) : LastCheckinTime.AddHours(Interval);
        public bool Ready => LastCheckinTime > DateTime.MinValue;

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
                    cookie.Add(domain, new Cookie("uid", UID));
                    cookie.Add(domain, new Cookie("user_uid", UID));    // old style
                    if (!string.IsNullOrWhiteSpace(UserEmail)) cookie.Add(domain, new Cookie("user_email", UserEmail));
                    // old style
                    if (!string.IsNullOrWhiteSpace(UserName)) cookie.Add(domain, new Cookie("user_name", UserName));
                    cookie.Add(domain, new Cookie("user_pwd", UserPwd));
                }
                return cookie;
            }
        }

        private static readonly Regex IntervalFinder = new Regex(@"(\d+)小时内?可以签到一次", RegexOptions.Compiled),
            LastCheckinTimeFinder = new Regex("上次签到时间：?<code>([^<]+?)</code>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline),
            ResultAnalyzer = new Regex("({\"msg\":\"\\\\u83b7\\\\u5f97\\\\u4e86|alert\\(\"签到成功，获得了)(\\d+) ?MB" +
                "(\\\\u6d41\\\\u91cf\"}|流量!\"\\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private HttpWebRequest CreateRequest(string url, ProxyCollection proxies)
        {
            var request = WebRequest.CreateHttp(url);
            request.CookieContainer = Cookie;
            if (proxies.Contains(Proxy)) request.Proxy = proxies[Proxy].ToProxy();
            return request;
        }

        public bool Init(ProxyCollection proxies)
        {
            var request = CreateRequest(Root + "/user/index.php", proxies);
            try
            {
                using (var response = request.GetResponse())
                    if (response.ResponseUri != request.RequestUri)
                        Log.WriteLine("ERROR", ID, "Initialization failed, possibly login failed. Redirected to: {0}",
                                      response.ResponseUri);
                    else
                        using (var stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream))
                        {
                            var str = reader.ReadToEnd();
                            var match = IntervalFinder.Match(str);
                            if (match.Success) Interval = int.Parse(match.Groups[1].Value);
                            else if (str.Contains("每天可以签到一次。GMT+8时间的0点刷新。")) Interval = -1;
                            else
                                Log.WriteLine("WARN", ID,
                               "Unable to find checkin interval. Please report this site if possible.");
                            match = LastCheckinTimeFinder.Match(str);
                            if (!match.Success) throw new FormatException("Unable to find last checkin time.");
                            LastCheckinTime = DateTime.Parse(match.Groups[1].Value);
                            return true;
                        }
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
            var request = CreateRequest(Root + "/user/" + (string.IsNullOrWhiteSpace(UserName) ? "_" : "do") +
                "checkin.php", proxies);    // check for old style
            try
            {
                using (var response = request.GetResponse())
                    if (response.ResponseUri != request.RequestUri)
                        Log.WriteLine("ERROR", ID, "Checkin failed, possibly login failed. Redirected to: {0}",
                                      response.ResponseUri);
                    else
                        using (var stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream))
                        {
                            var str = reader.ReadToEnd();
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
}
