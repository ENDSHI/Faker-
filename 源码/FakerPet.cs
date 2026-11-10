using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FakerPet
{
    internal static class Native
    {
        internal const string Title = "FakerDesktopPet.v1";
        internal const int Wake = 0x8001, Status = 0x8002;
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { internal int W, H; internal Size(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { internal byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
        {
            internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
            internal uint Compression, SizeImage; internal int XPels, YPels; internal uint Used, Important;
        }
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string text);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder name, int count);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmRect(IntPtr h, int attr, out Rect value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmInt(IntPtr h, int attr, out int value, int size);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr h, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dest, ref Point position, ref Size size, IntPtr src, ref Point origin, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] internal static extern int GetGuiResources(IntPtr process, int flags);
    }

    internal static class Program
    {
        [STAThread] private static void Main(string[] args)
        {
            Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--self-test") { Tests.Run(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--demo") { Tests.Demo(args[1]); return; }
            if (args.Length >= 2 && args[0] == "--probe")
            {
                IntPtr h = Native.FindWindow(null, Native.Title); Native.Rect r; Native.GetWindowRect(h, out r);
                File.WriteAllText(args[1], "Found=" + (h != IntPtr.Zero) + "\r\nVisible=" + Native.IsWindowVisible(h) + "\r\nBounds=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "\r\nState=" + Native.SendMessage(h, Native.Status, IntPtr.Zero, IntPtr.Zero), Encoding.UTF8); return;
            }
            if (args.Length == 1 && args[0] == "--quit") { Native.PostMessage(Native.FindWindow(null, Native.Title), 0x10, IntPtr.Zero, IntPtr.Zero); return; }
            bool fresh;
            using (Mutex mutex = new Mutex(true, "Local\\FakerDesktopPet.v1", out fresh))
            {
                if (!fresh) { Native.PostMessage(Native.FindWindow(null, Native.Title), Native.Wake, IntPtr.Zero, IntPtr.Zero); return; }
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Report(e.Exception); Application.Exit(); };
                try { using (Assets assets = new Assets(AppDomain.CurrentDomain.BaseDirectory)) using (PetWindow pet = new PetWindow(assets, false)) { Application.Run(pet); } }
                catch (Exception e) { Report(e); }
            }
        }
        private static void Report(Exception e)
        {
            try { Directory.CreateDirectory(Settings.Folder); File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine); } catch { }
            MessageBox.Show("飞鼠暂时没能启动。请将程序与 spritesheet.png 放在同一个文件夹。\n\n" + e.Message, "Faker 飞鼠", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal enum PetAnim { Idle, RunRight, RunLeft, Wave, Jump, Failed, Look }

    internal sealed class Assets : IDisposable
    {
        internal const int Cols = 8, Rows = 11;
        internal const double IdleFrame = 0.18, RunFrame = 0.11, WaveFrame = 0.15, JumpFrame = 0.15, FailedFrame = 0.15;
        internal readonly int Width, Height;
        internal readonly Bitmap[] Idle, RunRight, RunLeft, Wave, Jump, Failed, Wait, Run, Review;
        internal readonly Bitmap[] Look; // 16 directions
        private bool disposed;

        internal Assets(string folder)
        {
            string path = Path.Combine(folder, "spritesheet.png");
            if (!File.Exists(path)) throw new FileNotFoundException("缺少素材 spritesheet.png");
            using (Bitmap sheet = (Bitmap)Bitmap.FromFile(path))
            {
                if (sheet.Width % Cols != 0 || sheet.Height % Rows != 0) throw new InvalidDataException("spritesheet 尺寸与 V2 网格不符");
                int cw = sheet.Width / Cols, ch = sheet.Height / Rows;
                int minX, minY, maxX, maxY; ContentBounds(sheet, cw, ch, out minX, out minY, out maxX, out maxY);
                Width = maxX - minX + 1; Height = maxY - minY + 1;
                Idle = Row(sheet, cw, ch, minX, minY, 0, 6);
                RunRight = Row(sheet, cw, ch, minX, minY, 1, 8);
                RunLeft = Row(sheet, cw, ch, minX, minY, 2, 8);
                Wave = Row(sheet, cw, ch, minX, minY, 3, 4);
                Jump = Row(sheet, cw, ch, minX, minY, 4, 5);
                Failed = Row(sheet, cw, ch, minX, minY, 5, 8);
                Wait = Row(sheet, cw, ch, minX, minY, 6, 6);
                Run = Row(sheet, cw, ch, minX, minY, 7, 6);
                Review = Row(sheet, cw, ch, minX, minY, 8, 6);
                Look = new Bitmap[16];
                for (int i = 0; i < 8; i++) Look[i] = Crop(sheet, cw, ch, minX, minY, i, 9);
                for (int i = 0; i < 8; i++) Look[8 + i] = Crop(sheet, cw, ch, minX, minY, i, 10);
            }
        }
        private Bitmap[] Row(Bitmap sheet, int cw, int ch, int minX, int minY, int row, int count)
        {
            Bitmap[] result = new Bitmap[count];
            for (int i = 0; i < count; i++) result[i] = Crop(sheet, cw, ch, minX, minY, i, row);
            return result;
        }
        private Bitmap Crop(Bitmap sheet, int cw, int ch, int minX, int minY, int col, int row)
        {
            Bitmap b = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(sheet, new Rectangle(0, 0, Width, Height), col * cw + minX, row * ch + minY, Width, Height, GraphicsUnit.Pixel);
            }
            return b;
        }
        private static void ContentBounds(Bitmap sheet, int cw, int ch, out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = int.MaxValue; minY = int.MaxValue; maxX = -1; maxY = -1;
            int w = sheet.Width, h = sheet.Height;
            BitmapData data = sheet.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] buf = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int row = 0; row < Rows; row++)
                    for (int col = 0; col < Cols; col++)
                    {
                        int ox = col * cw, oy = row * ch;
                        for (int y = 0; y < ch; y++)
                        {
                            int rowBase = (oy + y) * data.Stride;
                            for (int x = 0; x < cw; x++)
                            {
                                if (buf[rowBase + (ox + x) * 4 + 3] > 0)
                                {
                                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                                }
                            }
                        }
                    }
            }
            finally { sheet.UnlockBits(data); }
            if (maxX < 0) throw new InvalidDataException("spritesheet 全透明");
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            Dispose(Idle); Dispose(RunRight); Dispose(RunLeft); Dispose(Wave); Dispose(Jump);
            Dispose(Failed); Dispose(Wait); Dispose(Run); Dispose(Review); Dispose(Look);
        }
        private static void Dispose(Bitmap[] arr) { if (arr != null) foreach (Bitmap b in arr) if (b != null) b.Dispose(); }
    }

    internal sealed class Motion
    {
        private const double PokeTilt = 0; // reserved
        internal PetAnim Mode = PetAnim.Idle;
        internal double Clock, ActionStart = -1, LandingStart = -1;
        internal bool Holding;
        internal double DragAngle, DragVel, DragTarget;
        internal int LookIndex;
        private double previous = -1;

        internal bool Landing { get { return LandingStart >= 0; } }
        internal bool Moving { get { return Holding || Landing || Math.Abs(DragAngle) > .02; } }
        internal double Squash { get { double t = Clock - LandingStart; return Landing && t >= 0 ? .055 * Math.Exp(-6 * t) * Math.Sin(19 * t) : 0; } }

        internal double Duration(PetAnim a)
        {
            switch (a)
            {
                case PetAnim.Wave: return 4 * Assets.WaveFrame;
                case PetAnim.Jump: return 5 * Assets.JumpFrame;
                case PetAnim.Failed: return 8 * Assets.FailedFrame;
            }
            return 0;
        }

        internal void Update(double now)
        {
            Clock = now; double dt = previous < 0 ? 0 : Math.Min(.05, Math.Max(0, now - previous)); previous = now;
            if ((Mode == PetAnim.Wave || Mode == PetAnim.Jump || Mode == PetAnim.Failed) && ActionStart >= 0 && Clock - ActionStart >= Duration(Mode)) { Mode = PetAnim.Idle; ActionStart = -1; }
            if (Landing && now - LandingStart >= .9) LandingStart = -1;
            if (!Holding && Math.Abs(DragAngle) < .02 && Math.Abs(DragVel) < .05) { DragAngle = 0; DragVel = 0; }
            int steps = Math.Max(1, (int)Math.Ceiling(dt / .008)); double stepTime = dt / steps;
            for (int i = 0; i < steps; i++) { DragVel += (160 * (DragTarget - DragAngle) - 25 * DragVel) * stepTime; DragAngle += DragVel * stepTime; }
            DragAngle = Math.Max(-7, Math.Min(7, DragAngle));
        }
        private void Clear(double now)
        {
            Clock = now; ActionStart = LandingStart = -1; Holding = false; DragAngle = DragTarget = DragVel = 0;
        }
        internal bool Act(PetAnim a, double now)
        {
            if (a != PetAnim.Wave && a != PetAnim.Jump && a != PetAnim.Failed) return false;
            if (Holding) return false;
            if (Mode == PetAnim.Jump || Mode == PetAnim.Failed) return false;
            if (a == PetAnim.Wave && Mode == PetAnim.Wave) return false;
            Clear(now); Mode = a; ActionStart = now; return true;
        }
        internal void Look(double now, int index) { Mode = PetAnim.Look; LookIndex = index; Clock = now; }
        internal void Idle(double now) { if (Mode == PetAnim.Look) { Mode = PetAnim.Idle; Clock = now; } }
        internal void BeginDrag(double now, bool right) { Clear(now); Holding = true; Mode = right ? PetAnim.RunRight : PetAnim.RunLeft; }
        internal void Drag(double now, bool right, double velocity) { if (!Holding) BeginDrag(now, right); Mode = right ? PetAnim.RunRight : PetAnim.RunLeft; DragTarget = Math.Max(-7, Math.Min(7, velocity)); Clock = now; }
        internal void EndDrag(double now) { if (!Holding) return; Holding = false; DragTarget = 0; Clock = now; LandingStart = now; Mode = PetAnim.Idle; }
        internal void Suspend(double now) { Clear(now); Mode = PetAnim.Idle; }
    }

    internal static class Fullscreen
    {
        internal static bool Covers(Rectangle window, Rectangle screen, int style, string name)
        {
            if ((style & 0x00c00000) != 0 || name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            const int tolerance = 2;
            return window.Left <= screen.Left + tolerance && window.Top <= screen.Top + tolerance && window.Right >= screen.Right - tolerance && window.Bottom >= screen.Bottom - tolerance;
        }
        internal static bool IsActive(IntPtr window, IntPtr pet, Rectangle screen)
        {
            if (window == IntPtr.Zero || window == pet || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return false;
            Native.Rect r; if (!Native.GetWindowRect(window, out r)) return false;
            try { int cloaked; if (Native.DwmInt(window, 14, out cloaked, 4) == 0 && cloaked != 0) return false; Native.Rect frame; if (Native.DwmRect(window, 9, out frame, 16) == 0) r = frame; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            StringBuilder cls = new StringBuilder(256); Native.GetClassName(window, cls, cls.Capacity);
            return Covers(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), screen, Native.GetWindowLong(window, -16), cls.ToString());
        }
    }

    internal sealed class FullscreenGate
    {
        internal bool Hidden;
        private bool candidate;
        private double since;
        internal bool Update(bool detected, double now)
        {
            if (candidate != detected) { candidate = detected; since = now; }
            if (candidate != Hidden && now - since >= (candidate ? .15 : .25)) { Hidden = candidate; return true; }
            return false;
        }
        internal void Reset() { Hidden = candidate = false; since = 0; }
        internal void Set(bool value, double now) { Hidden = candidate = value; since = now; }
    }

    internal sealed class Gesture
    {
        internal bool Pressed, Dragged;
        internal Point Origin;
        private Point lastClickPoint;
        private double lastClick = -100;
        internal void Down(Point p) { Pressed = true; Dragged = false; Origin = p; }
        internal void Move(Point p) { if (Pressed && (Math.Abs(p.X - Origin.X) > 5 || Math.Abs(p.Y - Origin.Y) > 5)) Dragged = true; }
        internal bool Up(Point p, double now)
        {
            if (!Pressed) return false; Pressed = false;
            if (Dragged) { lastClick = -100; return false; }
            bool twice = now - lastClick <= SystemInformation.DoubleClickTime / 1000.0 && Math.Abs(p.X - lastClickPoint.X) <= SystemInformation.DoubleClickSize.Width && Math.Abs(p.Y - lastClickPoint.Y) <= SystemInformation.DoubleClickSize.Height;
            lastClick = twice ? -100 : now; lastClickPoint = p; return twice;
        }
        internal void Cancel() { Pressed = false; Dragged = false; lastClick = -100; }
    }

    internal sealed class Settings
    {
        internal int Scale = 85, X, Y; internal bool Position, OnTop = true, AutoHide = true;
        internal static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FakerDesktopPet"); } }
        internal static Settings Load()
        {
            Settings s = new Settings(); try
            {
                string file = Path.Combine(Folder, "settings.ini"); if (!File.Exists(file)) return s;
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] p = line.Split('='); int n; if (p.Length != 2) continue;
                    if (p[0] == "scale" && int.TryParse(p[1], out n)) s.Scale = Math.Max(50, Math.Min(150, n));
                    if (p[0] == "x" && int.TryParse(p[1], out n)) { s.X = n; s.Position = true; }
                    if (p[0] == "y" && int.TryParse(p[1], out n)) s.Y = n;
                    if (p[0] == "top") s.OnTop = p[1] != "False";
                    if (p[0] == "fullscreen") s.AutoHide = p[1] != "False";
                }
            } catch { } return s;
        }
        internal void Save() { try { Directory.CreateDirectory(Folder); File.WriteAllLines(Path.Combine(Folder, "settings.ini"), new string[] { "scale=" + Scale, "x=" + X, "y=" + Y, "top=" + OnTop, "fullscreen=" + AutoHide }); } catch { } }
    }

    internal sealed class LayeredSurface : IDisposable
    {
        private IntPtr dc, bmp, old, bits;
        private byte[] buffer;
        private int width, height;
        internal void Update(IntPtr window, Bitmap image, Point location)
        {
            if (width != image.Width || height != image.Height) Create(image.Width, image.Height);
            BitmapData locked = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { Marshal.Copy(locked.Scan0, buffer, 0, buffer.Length); Marshal.Copy(buffer, 0, bits, buffer.Length); } finally { image.UnlockBits(locked); }
            Native.Point destination = new Native.Point(location.X, location.Y), origin = new Native.Point(0, 0); Native.Size size = new Native.Size(width, height);
            Native.Blend blend = new Native.Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            if (!Native.UpdateLayeredWindow(window, IntPtr.Zero, ref destination, ref size, dc, ref origin, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private void Create(int w, int h)
        {
            Dispose(); width = w; height = h; buffer = new byte[w * h * 4]; dc = Native.CreateCompatibleDC(IntPtr.Zero);
            Native.BitmapInfo info = new Native.BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)(w * h * 4) };
            bmp = Native.CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) throw new InvalidOperationException("无法创建透明窗口"); old = Native.SelectObject(dc, bmp);
        }
        internal bool Hit(int x, int y) { return buffer != null && x >= 0 && y >= 0 && x < width && y < height && buffer[(y * width + x) * 4 + 3] > 24; }
        public void Dispose()
        {
            if (dc != IntPtr.Zero) { if (old != IntPtr.Zero) Native.SelectObject(dc, old); if (bmp != IntPtr.Zero) Native.DeleteObject(bmp); Native.DeleteDC(dc); }
            dc = bmp = old = bits = IntPtr.Zero; width = height = 0; buffer = null;
        }
    }

    internal static class Renderer
    {
        internal static Icon CreateIcon(Bitmap image)
        {
            using (Bitmap small = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float scale = Math.Min(58f / image.Width, 58f / image.Height);
                    int w = (int)(image.Width * scale), h = (int)(image.Height * scale);
                    g.DrawImage(image, new Rectangle((64 - w) / 2, (64 - h) / 2, w, h), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
                IntPtr hIcon = small.GetHicon(); try { using (Icon borrowed = Icon.FromHandle(hIcon)) return (Icon)borrowed.Clone(); } finally { Native.DestroyIcon(hIcon); }
            }
        }
        internal static Bitmap Select(Assets a, Motion m)
        {
            switch (m.Mode)
            {
                case PetAnim.Look: return a.Look[Math.Max(0, Math.Min(15, m.LookIndex))];
                case PetAnim.Wave: return a.Wave[OneShotIndex(m, Assets.WaveFrame, 4)];
                case PetAnim.Jump: return a.Jump[OneShotIndex(m, Assets.JumpFrame, 5)];
                case PetAnim.Failed: return a.Failed[OneShotIndex(m, Assets.FailedFrame, 8)];
                case PetAnim.RunRight: return a.RunRight[(int)(m.Clock / Assets.RunFrame) % 8];
                case PetAnim.RunLeft: return a.RunLeft[(int)(m.Clock / Assets.RunFrame) % 8];
                default: return a.Idle[(int)(m.Clock / Assets.IdleFrame) % 6];
            }
        }
        private static int OneShotIndex(Motion m, double frame, int count)
        {
            double t = m.Clock - m.ActionStart;
            return Math.Max(0, Math.Min(count - 1, (int)(t / frame)));
        }
        internal static Bitmap Render(Assets a, Motion m, int scale)
        {
            Bitmap frame = Select(a, m);
            int w = frame.Width * scale / 100, h = frame.Height * scale / 100;
            Bitmap canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (m.Moving)
                {
                    float px = w * .5f, py = h * .9565f, s = (float)m.Squash;
                    g.TranslateTransform(px, py); g.RotateTransform((float)m.DragAngle);
                    g.ScaleTransform(m.Holding ? 1.008f : 1 + s, m.Holding ? 1.018f : 1 - s); g.TranslateTransform(-px, -py);
                }
                g.DrawImage(frame, new Rectangle(0, 0, w, h), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
            }
            return canvas;
        }
    }

    internal sealed class PetWindow : Form
    {
        private readonly Assets assets;
        private readonly bool testing;
        private readonly Settings settings;
        internal readonly Motion Motion = new Motion();
        private readonly Gesture gesture = new Gesture();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly LayeredSurface surface = new LayeredSurface();
        private readonly Icon petIcon;
        private readonly ContextMenuStrip menu;
        private readonly NotifyIcon tray;
        private ToolStripMenuItem topItem, fullscreenItem;
        private Point dragWindow, lastDragPointer;
        private readonly FullscreenGate fullscreenGate = new FullscreenGate();
        private double nextFullscreenCheck;
        private string lastVisual = "";
        private bool disposing, menuOpen, ready;
        internal bool HiddenForFullscreen { get; private set; }
        internal int ScalePercent { get { return settings.Scale; } }
        private double Now { get { return watch.Elapsed.TotalSeconds; } }

        internal PetWindow(Assets a, bool test)
        {
            assets = a; testing = test; settings = testing ? new Settings() : Settings.Load();
            Text = Native.Title; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            ClientSize = SizeFor(settings.Scale); TopMost = settings.OnTop; Cursor = Cursors.Hand;
            if (settings.Position) Location = new Point(settings.X, settings.Y); else Home();
            KeepVisible();
            petIcon = Renderer.CreateIcon(assets.Idle[0]);
            Icon = petIcon;
            menu = BuildMenu();
            if (!testing) { tray = new NotifyIcon { Icon = petIcon, Text = "Faker 飞鼠 · 单击挥手 / 双击跳 / 拖动", ContextMenuStrip = menu, Visible = true }; tray.DoubleClick += delegate { ReturnHome(); }; }
            timer.Interval = 16; timer.Tick += delegate { Tick(); };
            Shown += delegate { ready = true; RenderNow(true); if (!testing) { CheckFullscreen(true); timer.Start(); } };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80000 | 0x80 | 0x08000000; p.ClassStyle |= 8; return p; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.Wake) { ReturnHome(); return; }
            if (m.Msg == Native.Status) { m.Result = (IntPtr)(HiddenForFullscreen ? 4 : Motion.Mode == PetAnim.Failed ? 3 : Motion.Mode == PetAnim.Jump || Motion.Mode == PetAnim.Wave ? 2 : Motion.Mode == PetAnim.Look ? 1 : 0); return; }
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
            if (m.Msg == 0x84 && ready && !gesture.Pressed)
            {
                long value = m.LParam.ToInt64(); Point p = PointToClient(new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff)));
                m.Result = surface.Hit(p.X, p.Y) ? (IntPtr)1 : new IntPtr(-1); return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x7e && ready) { KeepVisible(); RenderNow(true); Save(); }
        }
        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip result = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 10f), ShowImageMargin = false };
            result.Items.Add(new ToolStripMenuItem("Faker 飞鼠 · 桌宠") { Enabled = false }); result.Items.Add(new ToolStripSeparator());
            result.Items.Add("跳跃（双击）", null, delegate { Act(PetAnim.Jump); });
            result.Items.Add("挥手", null, delegate { Act(PetAnim.Wave); });
            result.Items.Add("沮丧", null, delegate { Act(PetAnim.Failed); });
            result.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem sizes = new ToolStripMenuItem("大小");
            int[] values = { 65, 85, 110 }; string[] labels = { "小号", "标准", "大号" };
            for (int i = 0; i < values.Length; i++) { int v = values[i]; ToolStripMenuItem entry = new ToolStripMenuItem(labels[i], null, delegate { SetSize(v); }); entry.Tag = v; sizes.DropDownItems.Add(entry); } result.Items.Add(sizes);
            topItem = new ToolStripMenuItem("总在最上面", null, delegate { TopMost = !TopMost; settings.OnTop = TopMost; Save(); UpdateMenu(); }); result.Items.Add(topItem);
            fullscreenItem = new ToolStripMenuItem("全屏时自动隐藏", null, delegate { SetAutoHide(!settings.AutoHide); }); result.Items.Add(fullscreenItem);
            result.Items.Add("回到屏幕右下角", null, delegate { ReturnHome(); });
            result.Items.Add("打开使用说明", null, delegate { Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "使用说明.txt")) { UseShellExecute = true }); });
            result.Items.Add(new ToolStripSeparator()); result.Items.Add("退出飞鼠", null, delegate { Close(); });
            result.Opening += delegate { menuOpen = true; UpdateMenu(); }; result.Closed += delegate { menuOpen = false; }; return result;
        }
        private void UpdateMenu()
        {
            if (menu == null) return; SetToggleMark(topItem, TopMost); SetToggleMark(fullscreenItem, settings.AutoHide);
            foreach (ToolStripItem entry in menu.Items) { ToolStripMenuItem p = entry as ToolStripMenuItem; if (p == null) continue; foreach (ToolStripItem child in p.DropDownItems) { ToolStripMenuItem c = child as ToolStripMenuItem; if (c != null && c.Tag is int) c.Checked = (int)c.Tag == settings.Scale; } }
        }
        private static void SetToggleMark(ToolStripMenuItem item, bool selected)
        {
            item.Checked = selected; item.ShowShortcutKeys = true; item.ShortcutKeyDisplayString = selected ? "✓" : " ";
        }
        internal bool Act(PetAnim a) { if (HiddenForFullscreen) return false; if (!Motion.Act(a, Now)) return false; RenderNow(true); return true; }
        internal void Tick() { TickWithForeground(Native.GetForegroundWindow()); }
        internal void TickWithForeground(IntPtr foreground)
        {
            if (Now >= nextFullscreenCheck) { nextFullscreenCheck = Now + .12; CheckFullscreenWindow(foreground, false); }
            if (HiddenForFullscreen) return;
            Point p = PointToClient(Cursor.Position); bool over = !menuOpen && surface.Hit(p.X, p.Y);
            Motion.Update(Now);
            if (over && (Motion.Mode == PetAnim.Idle || Motion.Mode == PetAnim.Look)) Motion.Look(Now, ComputeLook());
            else if (!over && Motion.Mode == PetAnim.Look) Motion.Idle(Now);
            RenderNow(false);
        }
        private int ComputeLook()
        {
            int cx = Left + Width / 2, cy = Top + Height / 2;
            double dx = Cursor.Position.X - cx, dy = Cursor.Position.Y - cy;
            if (dx == 0 && dy == 0) return 0;
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            double s = (a + 90.0 + 360.0) % 360.0;
            return ((int)Math.Round(s / 22.5)) % 16;
        }
        internal void RenderNow(bool force)
        {
            if (!IsHandleCreated || disposing || HiddenForFullscreen) return;
            string key = Motion.Mode + "/" + (Motion.Mode == PetAnim.Look ? Motion.LookIndex : (int)(Motion.Clock / 0.05)) + "/" + settings.Scale;
            if (!force && key == lastVisual) return;
            using (Bitmap frame = Renderer.Render(assets, Motion, settings.Scale)) surface.Update(Handle, frame, Location);
            lastVisual = key;
        }
        internal void SetAutoHide(bool value)
        {
            settings.AutoHide = value; fullscreenGate.Reset();
            if (!value) ApplyFullscreen(false); else if (!testing) CheckFullscreen(true);
            Save(); UpdateMenu();
        }
        private void CheckFullscreen(bool immediate) { CheckFullscreenWindow(Native.GetForegroundWindow(), immediate); }
        private void CheckFullscreenWindow(IntPtr foreground, bool immediate)
        {
            if (!ready || menuOpen || gesture.Pressed) return;
            bool detected = settings.AutoHide && Fullscreen.IsActive(foreground, Handle, Screen.FromRectangle(Bounds).Bounds);
            if (immediate) { fullscreenGate.Set(detected, Now); ApplyFullscreen(detected); }
            else if (fullscreenGate.Update(detected, Now)) ApplyFullscreen(fullscreenGate.Hidden);
        }
        internal void ApplyFullscreen(bool hidden)
        {
            if (hidden == HiddenForFullscreen) return;
            HiddenForFullscreen = hidden;
            if (hidden)
            {
                gesture.Cancel(); Capture = false; Motion.Suspend(Now);
                Hide(); timer.Interval = 150;
            }
            else
            {
                Motion.Suspend(Now); timer.Interval = 16; Show(); RenderNow(true);
            }
        }
        private void ReturnHome() { Home(); if (!testing) CheckFullscreen(true); if (!HiddenForFullscreen) { Show(); RenderNow(true); } Save(); }
        private Size SizeFor(int scale) { return new Size(assets.Width * scale / 100, assets.Height * scale / 100); }
        internal void SetSize(int scale)
        {
            int center = Left + Width / 2, bottom = Top + Height; settings.Scale = Math.Max(50, Math.Min(150, scale)); ClientSize = SizeFor(settings.Scale);
            Location = new Point(center - Width / 2, bottom - Height); KeepVisible(); RenderNow(true); Save(); UpdateMenu();
        }
        internal void Home()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 24, area.Bottom - Height + (int)(12 * settings.Scale / 100.0)); KeepVisible();
        }
        internal static Point Clamp(Point p, Size size, Rectangle area, int allowance)
        { return new Point(Math.Max(area.Left, Math.Min(p.X, area.Right - size.Width)), Math.Max(area.Top, Math.Min(p.Y, area.Bottom - size.Height + allowance))); }
        private void KeepVisible() { Location = Clamp(Location, Size, Screen.FromRectangle(Bounds).WorkingArea, (int)(12 * settings.Scale / 100.0)); }
        private void Save() { if (testing) return; settings.X = Left; settings.Y = Top; settings.Save(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { Motion.EndDrag(Now); gesture.Cancel(); Capture = false; menu.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            gesture.Down(PointToScreen(e.Location)); lastDragPointer = gesture.Origin; dragWindow = Location; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (!gesture.Pressed) return; Point p = PointToScreen(e.Location); gesture.Move(p);
            if (gesture.Dragged)
            {
                if (!Motion.Holding) Motion.BeginDrag(Now, true);
                bool right = (p.X - gesture.Origin.X) >= 0;
                double velocity = (p.X - lastDragPointer.X) / Math.Max(1.0 / 120, Now - (lastDragPointer == gesture.Origin ? Now - 0.016 : Now)); // crude velocity
                Motion.Drag(Now, right, (p.X - lastDragPointer.X) * .04);
                lastDragPointer = p;
                Location = new Point(dragWindow.X + p.X - gesture.Origin.X, dragWindow.Y + p.Y - gesture.Origin.Y); RenderNow(true);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !gesture.Pressed) return;
            bool moved = gesture.Dragged, twice = gesture.Up(PointToScreen(e.Location), Now); Capture = false;
            if (moved) { Motion.EndDrag(Now); KeepVisible(); RenderNow(true); Save(); }
            else if (twice) Act(PetAnim.Jump);
            else Act(PetAnim.Wave);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && gesture.Pressed) { Motion.EndDrag(Now); gesture.Cancel(); KeepVisible(); RenderNow(true); Save(); } }
        protected override void OnFormClosing(FormClosingEventArgs e) { Save(); timer.Stop(); base.OnFormClosing(e); }
        protected override void Dispose(bool disposingNow)
        {
            if (disposingNow && !disposing) { disposing = true; timer.Dispose(); if (tray != null) { tray.Visible = false; tray.Dispose(); } if (menu != null) { Font f = menu.Font; menu.Dispose(); f.Dispose(); } surface.Dispose(); if (petIcon != null) petIcon.Dispose(); }
            base.Dispose(disposingNow);
        }
    }

    internal static class Tests
    {
        private static void Check(bool value, string name, List<string> log) { if (!value) throw new Exception("FAIL: " + name); log.Add("PASS: " + name); }
        internal static void Run(string folder)
        {
            Directory.CreateDirectory(folder); List<string> log = new List<string>();
            try
            {
                using (Assets a = new Assets(AppDomain.CurrentDomain.BaseDirectory))
                {
                    Check(a.Width > 0 && a.Height > 0, "spritesheet decoded with valid frame size " + a.Width + "x" + a.Height, log);
                    Check(a.Idle.Length == 6 && a.RunRight.Length == 8 && a.RunLeft.Length == 8, "idle/run animation frame counts", log);
                    Check(a.Wave.Length == 4 && a.Jump.Length == 5 && a.Failed.Length == 8, "wave/jump/failed frame counts", log);
                    Check(a.Look.Length == 16, "16 look-around directions", log);
                    Check(a.Idle[0].GetPixel(0, 0).A == 0, "transparent idle background", log);
                    int diff = 0; for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.Idle[0].GetPixel(x, y) != a.Idle[5].GetPixel(x, y)) diff++;
                    Check(diff > 20, "idle animation varies across frames", log);
                    Check(a.RunRight[0].GetPixel(0, 0).A == 0, "transparent run background", log);
                    Motion m = new Motion();
                    Check(m.Act(PetAnim.Wave, 0) && m.Mode == PetAnim.Wave, "single click starts wave", log);
                    m.Update(0.65); Check(m.Mode == PetAnim.Idle, "wave returns to idle", log);
                    Check(m.Act(PetAnim.Jump, 1) && !m.Act(PetAnim.Wave, 1.01), "double click jump does not overlap", log);
                    m.Update(1.8); Check(m.Mode == PetAnim.Idle, "jump returns to idle", log);
                    m.BeginDrag(2, true); Check(m.Mode == PetAnim.RunRight, "drag right runs right", log);
                    m.BeginDrag(2, false); Check(m.Mode == PetAnim.RunLeft, "drag left runs left", log);
                    m.EndDrag(2.1); Check(m.Mode == PetAnim.Idle && m.Landing, "drag release bounces", log);
                    m.Update(3.2); Check(!m.Landing, "landing settles", log);
                    m.Look(4, 0); Check(m.Mode == PetAnim.Look && m.LookIndex == 0, "hover enters look mode", log);
                    m.Idle(4.1); Check(m.Mode == PetAnim.Idle, "mouse leave restores idle", log);
                    using (PetWindow form = new PetWindow(a, true))
                    {
                        form.Show(); Application.DoEvents(); form.RenderNow(true);
                        Check(Native.IsWindowVisible(form.Handle), "native pet window is visible", log);
                        int style = Native.GetWindowLong(form.Handle, -20); Check((style & 0x80000) != 0 && (style & 0x80) != 0 && (style & 0x08000000) != 0, "alpha transparency, no taskbar, no focus stealing", log);
                        form.SetSize(65); Check(form.Width == a.Width * 65 / 100, "small size renders", log);
                        form.SetSize(110); Check(form.Width == a.Width * 110 / 100, "large size renders", log);
                        form.SetSize(85);
                        form.Act(PetAnim.Jump); Check(form.Motion.Mode == PetAnim.Jump, "native jump action triggers", log);
                        form.Motion.Suspend(0);
                        form.Act(PetAnim.Wave); Check(form.Motion.Mode == PetAnim.Wave, "native wave action triggers", log);
                        form.Motion.Suspend(0);
                        form.Act(PetAnim.Failed); Check(form.Motion.Mode == PetAnim.Failed, "native failed action triggers", log);
                        Point saved = form.Location; form.ApplyFullscreen(true);
                        Check(form.HiddenForFullscreen && !Native.IsWindowVisible(form.Handle), "fullscreen hides pet", log);
                        form.ApplyFullscreen(false);
                        Check(!form.HiddenForFullscreen && Native.IsWindowVisible(form.Handle) && form.Location == saved, "fullscreen exit restores position", log);
                        form.Close();
                    }
                    using (Bitmap b = Renderer.Render(a, new Motion(), 100)) b.Save(Path.Combine(folder, "faker-idle.png"), ImageFormat.Png);
                }
                log.Add("RESULT: PASS");
            }
            catch (Exception e) { log.Add(e.ToString()); log.Add("RESULT: FAIL"); Environment.ExitCode = 1; }
            File.WriteAllLines(Path.Combine(folder, "self-test.txt"), log.ToArray(), Encoding.UTF8);
        }
        internal static void Demo(string folder)
        {
            Directory.CreateDirectory(folder); using (Assets a = new Assets(AppDomain.CurrentDomain.BaseDirectory))
            {
                using (Bitmap b = Renderer.Render(a, new Motion(), 100)) b.Save(Path.Combine(folder, "idle.png"), ImageFormat.Png);
                for (int i = 0; i < 16; i++)
                {
                    Motion m = new Motion(); m.Look(0, i);
                    using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "look" + i.ToString("00") + ".png"), ImageFormat.Png);
                }
                for (int f = 0; f < 4; f++) { Motion m = new Motion(); m.Act(PetAnim.Wave, 0); m.Clock = f * Assets.WaveFrame; using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "wave" + f + ".png"), ImageFormat.Png); }
                for (int f = 0; f < 5; f++) { Motion m = new Motion(); m.Act(PetAnim.Jump, 0); m.Clock = f * Assets.JumpFrame; using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "jump" + f + ".png"), ImageFormat.Png); }
                for (int f = 0; f < 8; f++) { Motion m = new Motion(); m.Act(PetAnim.Failed, 0); m.Clock = f * Assets.FailedFrame; using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "failed" + f + ".png"), ImageFormat.Png); }
                for (int f = 0; f < 8; f++) { Motion m = new Motion(); m.BeginDrag(0, true); m.Clock = f * Assets.RunFrame; using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "runR" + f + ".png"), ImageFormat.Png); }
                for (int f = 0; f < 8; f++) { Motion m = new Motion(); m.BeginDrag(0, false); m.Clock = f * Assets.RunFrame; using (Bitmap b = Renderer.Render(a, m, 100)) b.Save(Path.Combine(folder, "runL" + f + ".png"), ImageFormat.Png); }
            }
        }
    }
}
