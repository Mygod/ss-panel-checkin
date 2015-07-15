﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using Mygod.Text;
// ReSharper disable InconsistentlySynchronizedField

namespace Mygod.SSPanel.Checkin
{
    class Config : List<Site>
    {
        public Config(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            using (var parser = new TextFieldParser(path))
            {
                parser.TextFieldType = FieldType.Delimited;
                parser.SetDelimiters(",");
                var proxy = parser.ReadLine();
                Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new WebProxy(new Uri(proxy));
                while (!parser.EndOfData)
                {
                    var fields = parser.ReadFields();
                    if (fields == null || fields.Length <= 0) continue;   // ignore empty lines
                    var site = new Site(fields, Proxy);
                    Add(site);
                    queue.Add(site);
                }
            }
        }

        private readonly SortedSet<Site> queue = new SortedSet<Site>();
        private readonly string path;
        public volatile bool NeedsRefetch;
        public readonly WebProxy Proxy;

        private void Save()
        {
            File.WriteAllText(path, (Proxy == null ? string.Empty : Proxy.Address.ToString()) + Environment.NewLine +
                string.Join(Environment.NewLine, this.Select(s => s.ToString())));
        }

        public DateTime DoCheckin()
        {
            var modified = false;
            if (NeedsRefetch)
            {
                queue.Clear();
                Parallel.ForEach(this, site =>
                {
                    try
                    {
                        if (site.Init()) modified = true;
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
            else Parallel.ForEach(queue.TakeWhile(site => site.NextCheckinTime <= DateTime.Now).ToList(), site =>
            {
                lock (queue) queue.Remove(site);
                try
                {
                    if (site.DoCheckin()) modified = true;
                    lock (queue) queue.Add(site);
                }
                catch (Exception exc)
                {
                    Log.WriteLine("FATAL", site.ID, exc.GetMessage());
                }
            });
            if (modified) Save();
            return queue.Count == 0 ? DateTime.MinValue : queue.First().NextCheckinTime;
        }
    }

    class Site : IComparable<Site>
    {
        public Site(IReadOnlyList<string> fields, WebProxy proxy)
        {
            ID = fields[0];
            if (ID == "Main") Log.WriteLine("WARN", ID,
                "The ID of this site is Main. While this is acceptable, it could make log file confusing.");
            UseProxy = !string.IsNullOrWhiteSpace(fields[1]);
            Domain = fields[2].TrimEnd('/');
            UID = fields[3];
            UserEmail = fields[4];
            UserName = fields[5];
            UserPwd = fields[6];
            if (fields.Count <= 7) return;
            DateTime.TryParse(fields[7], out LastCheckinTime);
            double.TryParse(fields[8], out Interval);
            long.TryParse(fields[9], out BandwidthCount);
            long.TryParse(fields[10], out CheckinCount);
            Proxy = proxy;
        }

        public readonly WebProxy Proxy;
        public readonly string ID, Domain, UID, UserEmail, UserName, UserPwd;
        public readonly bool UseProxy;
        public DateTime LastCheckinTime = DateTime.MinValue;
        public double Interval = 22;
        public long BandwidthCount, CheckinCount;
        public DateTime NextCheckinTime { get { return LastCheckinTime.AddHours(Interval); } }
        public bool Ready { get { return LastCheckinTime > DateTime.MinValue; } }

        public int CompareTo(Site other)
        {
            return NextCheckinTime.CompareTo(other.NextCheckinTime);
        }

        public override string ToString()
        {
            return string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}", Csv.Escape(ID),
                UseProxy ? "proxy" : string.Empty, Csv.Escape(Domain), Csv.Escape(UID), Csv.Escape(UserEmail),
                Csv.Escape(UserName), Csv.Escape(UserPwd), LastCheckinTime, Interval, BandwidthCount, CheckinCount);
        }

        private CookieContainer cookie;
        private CookieContainer Cookie
        {
            get
            {
                if (cookie == null)
                {
                    var domain = new Uri(Domain).GetComponents(UriComponents.Host, UriFormat.Unescaped);
                    cookie = new CookieContainer();
                    cookie.Add(new Cookie("uid", UID) { Domain = domain });
                    cookie.Add(new Cookie("user_uid", UID) { Domain = domain });    // old style
                    if (!string.IsNullOrWhiteSpace(UserEmail))
                        cookie.Add(new Cookie("user_email", UserEmail) { Domain = domain });
                    if (!string.IsNullOrWhiteSpace(UserName))   // old style
                        cookie.Add(new Cookie("user_name", UserName) { Domain = domain });
                    cookie.Add(new Cookie("user_pwd", UserPwd) { Domain = domain });
                }
                return cookie;
            }
        }

        private static readonly Regex IntervalFinder = new Regex(@"(\d+)小时内可以签到一次", RegexOptions.Compiled),
            LastCheckinTimeFinder = new Regex("上次签到时间：?<code>([^<]+?)</code>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline),
            ResultAnalyzer = new Regex("({\"msg\":\"\\\\u83b7\\\\u5f97\\\\u4e86|alert\\(\"签到成功，获得了)(\\d+) ?MB" +
                "(\\\\u6d41\\\\u91cf\"}|流量!\"\\))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool Init()
        {
            var request = WebRequest.CreateHttp(Domain + "/user/index.php");
            request.CookieContainer = Cookie;
            request.Proxy = UseProxy ? Proxy : null;
            try
            {
                using (var response = request.GetResponse())
                if (response.ResponseUri != request.RequestUri) Log.WriteLine("ERROR", ID,
                    "Initialization failed, possibly login failed. Redirected to: {0}", response.ResponseUri);
                else using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    var str = reader.ReadToEnd();
                    var match = IntervalFinder.Match(str);
                    if (match.Success) Interval = int.Parse(match.Groups[1].Value);
                    else Log.WriteLine("WARN", ID,
                        "Unable to find checkin interval. Please report this site if possible.");
                    match = LastCheckinTimeFinder.Match(str);
                    if (match.Success) LastCheckinTime = DateTime.Parse(match.Groups[1].Value);
                    else throw new FormatException("Unable to find last checkin time.");
                }
                return true;
            }
            catch (WebException exc)
            {
                var response = exc.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) throw;
                Log.WriteLine("ERROR", ID, "Initialization failed. Message: {0}", exc.Message);
                return false;
            }
            catch (IOException exc)
            {
                Log.WriteLine("ERROR", ID, "Checkin failed. Message: {0}", exc.Message);
                return false;
            }
        }

        public bool DoCheckin()
        {
            if (!Ready) return Init();
            var request = WebRequest.CreateHttp(Domain + "/user/" +
                (string.IsNullOrWhiteSpace(UserName) ? "_" : "do") + "checkin.php");    // old style
            request.CookieContainer = Cookie;
            request.Proxy = UseProxy ? Proxy : null;
            try
            {
                using (var response = request.GetResponse())
                if (response.ResponseUri != request.RequestUri) Log.WriteLine("ERROR", ID,
                    "Checkin failed, possibly login failed. Redirected to: {0}", response.ResponseUri);
                else using (var stream = response.GetResponseStream())
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
                            return Init();
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
                        return Init();
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
}
