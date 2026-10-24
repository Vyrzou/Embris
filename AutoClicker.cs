// The simple auto clicker: clicks a mouse button at a steady rate, where the pointer is or at a set
// spot, until stopped or a number of times. Runs on its own thread with exact timing.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Embris
{
    class AutoClicker
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT p);

        volatile bool running;
        volatile int clicks;
        public IntPtr OwnWindow;   // clicks are skipped while the pointer is over Embris
        public event Action Finished; // raised on the clicker thread when it stops by itself

        public bool Running { get { return running; } }
        public int Clicks { get { return clicks; } }

        public void Start(double intervalMs, int button, bool twice, int times, bool fixedSpot, int x, int y)
        {
            if (running) return;
            running = true;
            clicks = 0;
            var t = new Thread(() => Run(Math.Max(0, intervalMs), button, twice, times, fixedSpot, x, y)) { IsBackground = true, Name = "Embris auto clicker" };
            t.Start();
        }

        public void Stop() { running = false; }

        void Run(double intervalMs, int button, bool twice, int times, bool fixedSpot, int x, int y)
        {
            timeBeginPeriod(1);
            var priority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            var clock = Stopwatch.StartNew();
            double due = 0;
            try
            {
                while (running && (times == 0 || clicks < times))
                {
                    if (fixedSpot) Native.SetCursorPos(x, y);
                    if (!OverEmbris())
                    {
                        ClickOnce(button);
                        if (twice) { Thread.Sleep(15); ClickOnce(button); }
                        clicks++;
                    }
                    due += Math.Max(intervalMs, 1);
                    // Far behind (the PC was busy): carry on from now instead of clicking in a burst.
                    if (clock.Elapsed.TotalMilliseconds - due > 250) due = clock.Elapsed.TotalMilliseconds;
                    while (running)
                    {
                        double left = due - clock.Elapsed.TotalMilliseconds;
                        if (left <= 0) break;
                        if (left > 20) Thread.Sleep(Math.Min(50, (int)(left - 15)));
                        else if (left > 2.5) Thread.Sleep(0);
                        else Thread.SpinWait(200);
                    }
                }
            }
            finally
            {
                Thread.CurrentThread.Priority = priority;
                timeEndPeriod(1);
                bool byItself = running;
                running = false;
                if (byItself && Finished != null) Finished();
            }
        }

        static void ClickOnce(int button)
        {
            Input.Button(button, false);
            Input.Button(button, true);
        }

        bool OverEmbris()
        {
            Native.POINT p;
            if (OwnWindow == IntPtr.Zero || !GetCursorPos(out p)) return false;
            return Native.GetAncestor(Native.WindowFromPoint(p), 2) == OwnWindow; // GA_ROOT
        }
    }
}
