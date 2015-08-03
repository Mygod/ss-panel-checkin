﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using Mygod.Net;
using Mygod.Net.NetworkInformation;
using Mygod.Windows;
using Mygod.Xml.Serialization;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static string path;
        private static Config config;
        private static volatile bool running = true, forceUpdate;
        private static readonly AutoResetEvent Terminator = new AutoResetEvent(false);
        private static readonly TimeSpan Day = TimeSpan.FromDays(1);

        private static void Init(IReadOnlyList<string> args)
        {
            (config = XmlSerialization.DeserializeFromFile<Config>
                (path = args == null || args.Count <= 0 ? "config.xml" : args[0]) ?? new Config()).Init();
            Log.WriteLine("INFO", "Main", "ss-panel-checkin V{0} initialized, compiled on {1}.",
                          CurrentApp.Version, CurrentApp.CompilationTime);
        }

        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += Crashed;
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            Init(args);
            var background = new Thread(BackgroundWork);
            background.Start();
            Log.ConsoleLine("Available actions:{0}[A]ctivate worker{0}Re[f]etch all sites' checkin time{0}" +
                            "Fetch [n]odes{0}[R]eload config{0}[S]tatistics{0}[Q]uit", Environment.NewLine + "  ");
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
                        config.NeedsRefetch = true;
                        Terminator.Set();
                        break;
                    case ConsoleKey.N:
                        config.FetchNodes("nodes.json");
                        Log.ConsoleLine("Saved to nodes.json.");
                        break;
                    case ConsoleKey.R:
                        Init(args);
                        forceUpdate = true;
                        Terminator.Set();
                        break;
                    case ConsoleKey.S:
                        Log.ConsoleLine("ID\tAverage\tTotal{0}{1}", Environment.NewLine, string.Join(
                            Environment.NewLine, from site in config.Sites
                                                 let avg = (double)site.BandwidthCount / site.CheckinCount
                                                 orderby avg descending
                                                 select $"{site.ID}\t{avg}\t{site.BandwidthCount}"));
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
            DateTime lastUpdateCheckTime = DateTime.MinValue, nextCheckinTime = DateTime.MinValue;
            var failCount = 0;
            var random = new Random();
            while (running)
                if (NetworkTester.IsNetworkAvailable())
                {
                    TimeSpan span;
                    var failed = false;
                    var next = config.DoCheckin();
                    if (config.IsDirty)
                    {
                        config.IsDirty = false;
                        XmlSerialization.SerializeToFile(path, config);
                    }
                    if (next == DateTime.MinValue)
                    {
                        Log.WriteLine("WARN", "Main", "No sites configured or all of them has failed.");
                        span = TimeSpan.MaxValue;
                    }
                    else
                    {
                        if (next <= DateTime.Now) failed = true;
                        else if (forceUpdate || next != nextCheckinTime)
                            Log.ConsoleLine("Checkin finished. Next checkin time: {0}", nextCheckinTime = next);
                        span = nextCheckinTime - DateTime.Now;
                    }
                    if (DateTime.Now - lastUpdateCheckTime > Day)
                        try
                        {
                            var url = WebsiteManager.Url;
                            if (!string.IsNullOrWhiteSpace(url))
                                Log.WriteLine("INFO", "Main", "Update available. Download at: {0}", url);
                            lastUpdateCheckTime = DateTime.Now;
                        }
                        catch (Exception exc)
                        {
                            Log.WriteLine("WARN", "Main", "Checking for updates failed. Message: {0}",
                                          exc.GetMessage());
                            failed = true;
                        }
                    if (failed)
                    {
                        if (failCount < 10) ++failCount;
                    }
                    else failCount = 0; // reset counter
                    var t = DateTime.Now - lastUpdateCheckTime + Day;
                    if (t < span) span = t;
                    var min = TimeSpan.FromMilliseconds(random.Next(1000, 1000 << failCount));
                    if (span < min) span = min;
                    if (failed) Log.ConsoleLine("Some sites has failed. Retrying in {0} seconds...", span.TotalSeconds);
                    if (running) Terminator.WaitOne(span);
                }
                else
                {
                    failCount = 0;  // reset counter
                    if (running) Terminator.WaitOne(-1);
                }
        }
    }
}
