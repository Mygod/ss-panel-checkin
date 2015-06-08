using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using Mygod.Net;
using Mygod.Net.NetworkInformation;
using Mygod.Windows;

namespace Mygod.SSPanel.Checkin
{
    static class Program
    {
        private static Config config;
        private static volatile bool running = true;
        private static readonly AutoResetEvent Terminator = new AutoResetEvent(false);
        private static readonly TimeSpan Day = TimeSpan.FromDays(1);

        private static void Init(IReadOnlyList<string> args)
        {
            config = new Config(args == null || args.Count <= 0 ? "config.csv" : args[0]);
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
            Log.ConsoleLine("Available actions:{0}Re[f]etch all sites' checkin time{0}[R]eload config{0}[S]tatistics" +
                            "{0}[Q]uit", Environment.NewLine + "  ");
            var key = Console.ReadKey(true).Key;
            while (key != ConsoleKey.Q)
            {
                switch (key)
                {
                    case ConsoleKey.F:
                        config.NeedsRefetch = true;
                        Terminator.Set();
                        break;
                    case ConsoleKey.R:
                        Init(args);
                        Terminator.Set();
                        break;
                    case ConsoleKey.S:
                        Log.ConsoleLine("ID\tAverage\tTotal{0}{1}", Environment.NewLine, string.Join(
                            Environment.NewLine, from site in config
                                                 let avg = (double)site.BandwidthCount / site.CheckinCount
                                                 orderby avg descending
                                                 select string.Format("{0}\t{1}\t{2}", site.ID, avg, 
                                                                      site.BandwidthCount)));
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
                    var next = config.DoCheckin();
                    if (next == DateTime.MinValue)
                    {
                        Log.WriteLine("WARN", "Main", "No sites configured or all of them has failed.");
                        span = TimeSpan.MaxValue;
                    }
                    else
                    {
                        if (next > DateTime.Now && next != nextCheckinTime)
                        {
                            failCount = 0;  // reset counter
                            Log.ConsoleLine("Checkin finished. Next checkin time: {0}", nextCheckinTime = next);
                        }
                        else if (failCount < 10) ++failCount;
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
                        }
                    var t = DateTime.Now - lastUpdateCheckTime + Day;
                    if (t < span) span = t;
                    var min = TimeSpan.FromMilliseconds(random.Next(1000, 1000 << failCount));
                    if (running) Terminator.WaitOne(span < min ? min : span);
                }
                else
                {
                    failCount = 0;  // reset counter
                    if (running) Terminator.WaitOne(-1);
                }
        }
    }
}
