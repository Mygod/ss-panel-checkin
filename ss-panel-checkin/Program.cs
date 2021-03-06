﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Mygod.Net;
using Mygod.Net.NetworkInformation;
using Mygod.Windows;
using Mygod.Xml.Serialization;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static readonly object BusyLock = new object();
        private static string path;
        private static volatile Config config;
        private static volatile bool running = true, forceUpdate;
        private static readonly AutoResetEvent Terminator = new AutoResetEvent(false);
        private static readonly TimeSpan Day = TimeSpan.FromDays(1);

        private static void Init(IReadOnlyList<string> args)
        {
            (config = XmlSerialization.DeserializeFromFile<Config>
                (path = args == null || args.Count <= 0 ? "config.xml" : args[0]) ?? new Config()).Init();
            Log.WriteLine("INFO", "Main",
                $"ss-panel-checkin V{CurrentApp.Version} initialized, compiled on {CurrentApp.CompilationTime}.");
        }

        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += Crashed;
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            Init(args);
            var background = new Thread(BackgroundWork);
            background.Start();
            Log.ConsoleLine("Available actions:{0}[A]ctivate worker (useless if busy){0}" +
                            "Re[f]etch all sites' checkin time{0}Fetch [n]odes{0}[R]eload config{0}[S]tatistics{0}" +
                            "Speed [t]est{0}[Q]uit", Environment.NewLine + "  ");
            var key = Console.ReadKey(true).Key;
            while (key != ConsoleKey.Q)
            {
                switch (key)
                {
                    case ConsoleKey.A:
                        forceUpdate = true;
                        Terminator.Set();
                        break;
                    case ConsoleKey.F:
                        lock (BusyLock)
                        {
                            config.NeedsRefetch = true;
                            Terminator.Set();
                        }
                        break;
                    case ConsoleKey.N:
                        config.FetchNodes("nodes.json");
                        Log.ConsoleLine("Saved to nodes.json.");
                        break;
                    case ConsoleKey.R:
                        lock (BusyLock)
                        {
                            Init(args);
                            forceUpdate = true;
                            Terminator.Set();
                        }
                        break;
                    case ConsoleKey.S:
                        Log.ConsoleLine("ID\tAverage/day\tAverage/checkin\tTotal" + Environment.NewLine + string.Join(
                            Environment.NewLine, from site in config.Sites where site.CheckinCount != 0
                                                 let avg = site.BandwidthCount * 24D /
                                                    (site.Interval < 0 ? 24 : site.Interval) / site.CheckinCount
                                                 orderby avg descending select site.ID +
                                                    (site.Status == SiteStatus.Disabled ? "*" : string.Empty) + '\t' + 
                                                 $"{avg:0.##}\t{(double)site.BandwidthCount / site.CheckinCount:0.##}" +
                                                 '\t' + site.BandwidthCount));
                        break;
                    case ConsoleKey.T:
                        config.TestProxies();
                        break;
                }
                key = Console.ReadKey(true).Key;
            }
            running = false;
            Terminator.Set();
            background.Join();
            Log.WriteLine("INFO", "Main", "ss-panel-checkin has been closed.");
        }

        private static void Crashed(object sender, UnhandledExceptionEventArgs e)
        {
            var exc = e.ExceptionObject as Exception;
            if (exc == null) return;
            Log.WriteLine("FATAL", "Main", exc.GetMessage());
        }

        private static void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable) Terminator.Set();    // wake up the background thread to test network connection
        }

        private static void BackgroundWork()
        {
            DateTime lastUpdateCheckTime = default(DateTime), nextCheckinTime = default(DateTime);
            var failCount = 0;
            var random = new Random();
            bool failed;
            Action checkForUpdates = () =>
            {
                try
                {
                    var url = WebsiteManager.Url;
                    if (!string.IsNullOrWhiteSpace(url))
                        Log.WriteLine("INFO", "Main", "Update available. Download at: " + url);
                    lastUpdateCheckTime = DateTime.Now;
                }
                catch (Exception exc)
                {
                    Log.WriteLine("WARN", "Main", "Checking for updates failed. Message: " + exc.GetMessage());
                    failed = true;
                }
            };
            while (running)
                if (NetworkTester.IsNetworkAvailable())
                {
                    TimeSpan span;
                    Task task = null;
                    failed = false;
                    lock (BusyLock)
                    {
                        if (DateTime.Now - lastUpdateCheckTime > Day) task = Task.Factory.StartNew(checkForUpdates);
                        var next = config.DoCheckin();
                        if (config.IsDirty)
                        {
                            config.IsDirty = false;
                            XmlSerialization.SerializeToFile(path, config);
                        }
                        if (next == default(DateTime))
                        {
                            Log.WriteLine("WARN", "Main", "No sites configured or all of them has failed.");
                            span = TimeSpan.MaxValue;
                        }
                        else
                        {
                            span = next - DateTime.Now;
                            if (config.NextCheckinTime <= DateTime.Now) failed = true;
                            else if (forceUpdate || next != nextCheckinTime)
                                Log.ConsoleLine("Checkin finished. Next checkin time: {0}", nextCheckinTime = next);
                        }
                        task?.Wait();
                        var t = lastUpdateCheckTime + Day - DateTime.Now;
                        if (t < span) span = t;
                        if (failed)
                        {
                            if (failCount < 10) ++failCount;
                        }
                        else failCount = 0; // reset counter
                        if (failed || span < TimeSpan.Zero)
                        {
                            var max = TimeSpan.FromMilliseconds(random.Next(1000, 1000 << failCount));
                            if (span < TimeSpan.Zero || span > max) span = max;
                            Log.ConsoleLine($"Something has failed. Retrying in {span.TotalSeconds} seconds...");
                        }
                    }
                    if (running && !config.NeedsRefetch) Terminator.WaitOne(span);
                }
                else
                {
                    failCount = 0;  // reset counter
                    if (running) Terminator.WaitOne(-1);
                }
        }
    }
}
