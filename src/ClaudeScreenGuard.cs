// =====================================================================
//  ClaudeScreenGuard —— Claude 工作时，用磨砂玻璃遮住屏幕；答完自动淡出并提醒。
//  ClaudeScreenGuard — frosts your screen while Claude is working, fades out
//  and chimes when it's done.
//
//  非官方工具，与 Anthropic 无关。 Unofficial; not affiliated with Anthropic.
//  https://github.com/faithadam/ClaudeScreenGuard      License: MIT
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Globalization;

// 界面语言：中文系统显示中文，其他显示英文
[assembly: System.Reflection.AssemblyTitle("Claude Screen Guard")]
[assembly: System.Reflection.AssemblyProduct("ClaudeScreenGuard")]
[assembly: System.Reflection.AssemblyDescription("Frosts your screen while Claude is working. Unofficial.")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

static class L {
    public static readonly bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    public static string T(string zh, string en) { return Zh ? zh : en; }
}

static class Native {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint aff);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static void MakeDpiAware() {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    // 指定进程的所有有标题的 Chromium 顶层窗口（隐藏的也算）
    public static List<IntPtr> ChromeWindowsOf(HashSet<uint> pids) {
        var r = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (!pids.Contains(p)) return true;
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin")) return true;
            var t = new StringBuilder(256); GetWindowText(h, t, 256);
            if (t.Length == 0) return true;
            r.Add(h);
            return true;
        }, IntPtr.Zero);
        return r;
    }
}

class Config {
    public const int Version = 8;
    public string Style = "frosted";        // frosted = 柔和磨砂（推荐），glass = 系统毛玻璃，black = 全黑
    public string OverlayText = "";
    public Color TextColor = ColorTranslator.FromHtml("#5A5A5A");
    public Color BackColor = Color.Black;  // 全黑模式的颜色
    public Color TintColor = ColorTranslator.FromHtml("#F0F2F5");
    public int TintAlpha = 80;
    public int GlassTintAlpha = 64;          // 毛玻璃上叠加的白雾浓度 0~255
    public bool CaptureVisible = false;      // 排查用：让遮罩出现在截图里
    public bool ShowCard = true;             // 屏幕中间的小卡片（状态、用时、掀开按钮）
    public double CardScale = 1.8;           // 小卡片大小
    public string CardFont = "auto";          // 小卡片字体，auto = 自动挑一款好看的黑体             // 磨砂玻璃的“白雾”浓度 0~255，越大越看不清
    public double BlurRadius = 7;           // 模糊程度（以 1400 像素宽的屏幕为基准）
    public double Saturation = 1.3;         // 颜色鲜艳度，苹果毛玻璃会稍微提一点
    public double FrostFps = 2;             // 磨砂玻璃每秒刷新几次
    public float FontSize = 15;
    public int FadeInMs = 700;
    public int FadeOutMs = 1200;
    public double TimeoutMinutes = 20;
    public double ShowDelaySec = 5;          // 回答超过几秒才遮（很快答完就不遮）
    public bool PlaySound = true;
    public string SoundFile = @"C:\Windows\Media\Windows Notify System Generic.wav";
    public int PollMs = 500;
    public bool SavePreview = false;
    public string[] BusyButtons = { "停止回复", "Stop response", "Stop responding", "Stop" };
    public string[] AskButtons = { "允许", "拒绝", "始终允许", "仅此一次", "批准", "Allow", "Deny", "Always allow", "Allow once", "Approve", "跳过", "Skip" };

    public const string DefaultIni =
"; Claude 屏幕遮罩 设置（改完后在托盘图标右键“退出”，再从开始菜单打开 ClaudeScreenGuard）\r\n" +
"ConfigVersion=8\r\n" +
"\r\n" +
"; 遮罩样式：\r\n" +
";   frosted = 柔和磨砂（推荐）：隐约能看到后面在动，看不清内容\r\n" +
";   glass   = 系统毛玻璃：几乎不占 CPU，但模糊更重，方形图标会有圆形光晕\r\n" +
";   black   = 全黑\r\n" +
"Style=frosted\r\n" +
"\r\n" +
"; ---- 系统毛玻璃 ----\r\n" +
"; 毛玻璃上叠加的白雾浓度（0~255，越大越朦胧，越小越通透）\r\n" +
"GlassTintAlpha=64\r\n" +
"\r\n" +
"; ---- 柔和磨砂 ----\r\n" +
"; 模糊程度（4 = 轻度，7 = 中度，11 = 较重）\r\n" +
"BlurRadius=7\r\n" +
"; 白雾颜色和浓度（0~255，越大越朦胧，越小越通透）\r\n" +
"TintColor=#F0F2F5\r\n" +
"TintAlpha=80\r\n" +
"; 颜色鲜艳度（1 = 原样，1.3 = 稍微鲜艳一点）\r\n" +
"Saturation=1.3\r\n" +
"; 每秒刷新几次（1~6，越小越省电，越大画面变化越连贯）\r\n" +
"FrostFps=2\r\n" +
"\r\n" +
"; ---- 全黑 ----\r\n" +
"BackColor=#000000\r\n" +
"\r\n" +
"; ---- 通用 ----\r\n" +
"; 屏幕中间的小卡片：显示“Claude 工作中”、已用时、当前时间和“掀开遮罩”按钮（true / false）\r\n" +
"ShowCard=true\r\n" +
"; 小卡片大小（1 = 小，1.8 = 默认，3 = 很大）\r\n" +
"CardScale=1.8\r\n" +
"; 小卡片字体（auto = 自动挑选：HarmonyOS Sans / MiSans / 思源黑体 / 微软雅黑；也可以写字体名）\r\n" +
"CardFont=auto\r\n" +
"; 遮罩中间的文字（默认不显示；想显示就写在等号后面，例如：正在处理，完成后会提醒你）\r\n" +
"OverlayText=\r\n" +
"TextColor=#5A5A5A\r\n" +
"FontSize=15\r\n" +
"; 盖上 / 掀开遮罩的过渡时间（毫秒）\r\n" +
"FadeInMs=700\r\n" +
"FadeOutMs=1200\r\n" +
"; Claude 回答超过几秒才盖上遮罩（很快答完的就不遮，也不响；0 = 立刻遮）\r\n" +
"ShowDelaySec=5\r\n" +
"; 超过多少分钟自动掀开\r\n" +
"TimeoutMinutes=20\r\n" +
"; 完成时是否响提示音（true / false）和声音文件\r\n" +
"PlaySound=true\r\n" +
"SoundFile=C:\\Windows\\Media\\Windows Notify System Generic.wav\r\n" +
"; 每隔多少毫秒检查一次 Claude 状态\r\n" +
"PollMs=500\r\n" +
"; Claude 回答中才会出现的按钮名字（用 | 分隔）\r\n" +
"BusyButtons=停止回复|Stop response|Stop responding|Stop\r\n" +
"; 出现这些按钮时说明 Claude 在等你确认，遮罩会暂时掀开（用 | 分隔）\r\n" +
"AskButtons=允许|拒绝|始终允许|仅此一次|批准|Allow|Deny|Always allow|Allow once|Approve|跳过|Skip\r\n" +
"; 排查用：每次遮罩时把磨砂画面存成 frost_preview.png\r\n" +
"SavePreview=false\r\n" +
"; 排查用：让遮罩出现在截图里（平时务必保持 false，否则 Claude 操作电脑时会看不清屏幕）\r\n" +
"CaptureVisible=false\r\n";

    public static Dictionary<string, string> ReadPairs(string path) {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8)) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            int i = line.IndexOf('=');
            if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        return d;
    }

    // 只替换对应的“键=值”行，注释原样保留
    public static void Save(string path, Dictionary<string, string> values) {
        var lines = new List<string>(File.ReadAllLines(path, Encoding.UTF8));
        var done = new HashSet<string>();
        for (int i = 0; i < lines.Count; i++) {
            var t = lines[i].Trim();
            if (t.StartsWith(";")) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            string k = t.Substring(0, eq).Trim();
            if (values.ContainsKey(k)) { lines[i] = k + "=" + values[k]; done.Add(k); }
        }
        foreach (var kv in values) if (!done.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
        File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(true));
    }

    public static Config Load(string path) {
        var c = new Config();
        try {
            bool rewrite = !File.Exists(path);
            if (!rewrite) {
                string all = File.ReadAllText(path, Encoding.UTF8);
                if (!all.Contains("ConfigVersion=" + Version)) {
                    File.Copy(path, path + ".old", true);   // 旧版设置备份后换成新版，并保留你改过的值
                    var keep = ReadPairs(path);
                    keep.Remove("ConfigVersion"); keep.Remove("FrostFps"); keep.Remove("CaptureVisible"); keep.Remove("BusyButtons");
                    File.WriteAllText(path, DefaultIni, new UTF8Encoding(true));
                    Save(path, keep);
                    Program.Log("设置文件已升级为新版（旧的备份为 config.ini.old）");
                }
            }
            if (rewrite) File.WriteAllText(path, DefaultIni, new UTF8Encoding(true));
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8)) {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                try {
                    switch (k) {
                        case "Style": c.Style = v.ToLower(); break;
                        case "OverlayText": c.OverlayText = v; break;
                        case "TextColor": c.TextColor = ColorTranslator.FromHtml(v); break;
                        case "BackColor": c.BackColor = ColorTranslator.FromHtml(v); break;
                        case "TintColor": c.TintColor = ColorTranslator.FromHtml(v); break;
                        case "GlassTintAlpha": c.GlassTintAlpha = Math.Max(1, Math.Min(255, int.Parse(v))); break;
                        case "CardScale": c.CardScale = Math.Max(0.8, Math.Min(3.5, double.Parse(v))); break;
                        case "CardFont": c.CardFont = v; break;
                        case "ShowCard": c.ShowCard = v.ToLower() == "true"; break;
                        case "CaptureVisible": c.CaptureVisible = v.ToLower() == "true"; break;
                        case "TintAlpha": c.TintAlpha = Math.Max(0, Math.Min(255, int.Parse(v))); break;
                        case "BlurRadius": c.BlurRadius = Math.Max(1, Math.Min(40, double.Parse(v))); break;
                        case "Saturation": c.Saturation = Math.Max(0, Math.Min(3, double.Parse(v))); break;
                        case "FrostFps": c.FrostFps = Math.Max(0.5, Math.Min(10, double.Parse(v))); break;
                        case "FontSize": c.FontSize = float.Parse(v); break;
                        case "FadeInMs": c.FadeInMs = Math.Max(30, int.Parse(v)); break;
                        case "FadeOutMs": c.FadeOutMs = Math.Max(30, int.Parse(v)); break;
                        case "ShowDelaySec": c.ShowDelaySec = Math.Max(0, Math.Min(120, double.Parse(v))); break;
                        case "TimeoutMinutes": c.TimeoutMinutes = double.Parse(v); break;
                        case "PlaySound": c.PlaySound = v.ToLower() == "true"; break;
                        case "SoundFile": c.SoundFile = v; break;
                        case "PollMs": c.PollMs = Math.Max(200, int.Parse(v)); break;
                        case "SavePreview": c.SavePreview = v.ToLower() == "true"; break;
                        case "BusyButtons": c.BusyButtons = v.Split('|'); break;
                        case "AskButtons": c.AskButtons = v.Split('|'); break;
                    }
                } catch { Program.Log("设置项有误，已忽略：" + line); }
            }
        } catch (Exception e) { Program.Log("读取设置出错：" + e.Message); }
        return c;
    }
}

static class Accent {
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WCAData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCAData d);

    // 给窗口打开系统亚克力毛玻璃（和开始菜单、任务栏同一种），color 为 AABBGGRR
    public static bool EnableAcrylic(IntPtr h, Color tint, int alpha) {
        int color = (alpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;
        var ap = new AccentPolicy { AccentState = 4, AccentFlags = 2, GradientColor = color };
        int size = Marshal.SizeOf(ap);
        IntPtr p = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(ap, p, false);
            var d = new WCAData { Attribute = 19, Data = p, SizeOfData = size };
            return SetWindowCompositionAttribute(h, ref d) != 0;
        } finally { Marshal.FreeHGlobal(p); }
    }
}

static class MouseHook {
    public delegate IntPtr Proc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct MSLL { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, Proc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
}

// 一块 GDI 内存画布：GDI 可以直接用它快速缩放贴图，GDI+ 也能在同一块内存上画
class Dib : IDisposable {
    [StructLayout(LayoutKind.Sequential)] struct BIH { public int size, w, h; public short planes, bits; public int comp, img, xppm, yppm, used, imp; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BIH bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    public readonly int W, H;
    public readonly IntPtr Hdc, Bits;
    readonly IntPtr hbmp, old;
    public readonly Bitmap View;     // 指向同一块内存的 GDI+ 位图
    public Dib(int w, int h) {
        W = w; H = h;
        var bi = new BIH { size = 40, w = w, h = -h, planes = 1, bits = 32 };
        IntPtr bits;
        hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
        Bits = bits;
        Hdc = CreateCompatibleDC(IntPtr.Zero);
        old = SelectObject(Hdc, hbmp);
        View = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, bits);
    }
    public void Dispose() { View.Dispose(); SelectObject(Hdc, old); DeleteObject(hbmp); DeleteDC(Hdc); }
}

static class Gdi {
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
}

class Overlay : Form {
    public bool ExcludedFromCapture;
    readonly Config cfg;
    readonly bool frosted, glass;
    public bool GlassOk;
    readonly Rectangle screenRect;
    readonly string text;
    Dib small, mid;          // small：缩小后的截图；mid：模糊+白雾后、1/3 尺寸的画面
    public double LastFrameMs, LastPaintMs;

    // ---- 中间的小卡片 ----
    public bool CardEnabled;
    public DateTime CoverStart = DateTime.Now;
    public bool ButtonHover, ButtonDown;
    public Rectangle CardRect, ButtonRect;      // 屏幕坐标（物理像素）
    float ui = 1f;                              // 界面缩放（显示器 DPI × 卡片大小）
    string cardFont = "Microsoft YaHei UI";

    public static readonly string[] NiceFonts = { "HarmonyOS Sans SC", "MiSans", "MiSans VF", "思源黑体 CN", "Source Han Sans SC", "Source Han Sans CN", "Noto Sans SC", "Noto Sans CJK SC", "阿里巴巴普惠体 2.0", "Alibaba PuHuiTi 2.0", "苹方-简", "PingFang SC", "Microsoft YaHei UI" };

    public static List<string> InstalledNiceFonts() {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var ifc = new System.Drawing.Text.InstalledFontCollection()) foreach (var ff in ifc.Families) have.Add(ff.Name);
        var r = new List<string>();
        foreach (var n in NiceFonts) if (have.Contains(n)) r.Add(n);
        return r;
    }

    static string PickFont(string want) {
        var list = InstalledNiceFonts();
        if (want != null && want != "auto") {
            using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                foreach (var ff in ifc.Families) if (string.Equals(ff.Name, want, StringComparison.OrdinalIgnoreCase)) return ff.Name;
        }
        if (!L.Zh) {
            using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                foreach (var ff in ifc.Families) if (ff.Name == "Segoe UI Variable Display" || ff.Name == "Segoe UI") return ff.Name;
        }
        return list.Count > 0 ? list[0] : "Microsoft YaHei UI";
    }

    public string CardFontName { get { return cardFont; } }

    public Overlay(Rectangle bounds, Config c, string txt) {
        cfg = c; text = txt; screenRect = bounds;
        glass = c.Style == "glass";
        frosted = c.Style == "frosted";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = glass ? Color.Black : (frosted ? c.TintColor : c.BackColor);
        Opacity = 0;
        DoubleBuffered = true;
        Text = "ClaudeScreenGuard";
        if (frosted) {
            // 先把画面缩小到模糊半径约 2.5 像素的尺寸，在小图上做高斯模糊，计算量很小
            double sigma = c.BlurRadius * bounds.Width / 1400.0;
            double f = Math.Max(1.0, sigma / 2.5);
            small = new Dib(Math.Max(16, (int)(bounds.Width / f)), Math.Max(9, (int)(bounds.Height / f)));
            mid = new Dib(Math.Max(64, bounds.Width / 3), Math.Max(36, bounds.Height / 3));
        }
    }

    public bool Frosted { get { return frosted; } }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            // 分层 | 鼠标穿透 | 不进 Alt+Tab | 不抢焦点 | 置顶
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        ExcludedFromCapture = cfg.CaptureVisible || Native.SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE
        using (var g = CreateGraphics()) ui = g.DpiX / 96f * (float)cfg.CardScale;
        cardFont = PickFont(cfg.CardFont);
        int cw = (int)(300 * ui), ch = (int)(150 * ui);
        CardRect = new Rectangle(screenRect.X + (screenRect.Width - cw) / 2, screenRect.Y + (screenRect.Height - ch) / 2, cw, ch);
        int bw = (int)(120 * ui), bh = (int)(34 * ui);
        ButtonRect = new Rectangle(CardRect.Right - bw - (int)(18 * ui), CardRect.Bottom - bh - (int)(16 * ui), bw, bh);
        if (glass) GlassOk = Accent.EnableAcrylic(Handle, cfg.TintColor, cfg.GlassTintAlpha);   // 黑色像素会变成透明，露出毛玻璃
    }

    // 拍下遮罩下面的画面 → 缩小 → 模糊 → 加白雾。遮罩本身不会被拍进去（已排除截屏）。
    public void UpdateFrost() {
        if (!frosted) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr scr = Gdi.GetDC(IntPtr.Zero);
        try {
            Gdi.SetStretchBltMode(small.Hdc, 4); // HALFTONE：缩小时取平均，本身就有模糊效果
            Gdi.SetBrushOrgEx(small.Hdc, 0, 0, IntPtr.Zero);
            Gdi.StretchBlt(small.Hdc, 0, 0, small.W, small.H, scr, screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height, 0x00CC0020 | 0x40000000);
            Gdi.GdiFlush();
        } finally { Gdi.ReleaseDC(IntPtr.Zero, scr); }

        GaussianTint(small.View, 2, cfg.Saturation, cfg.TintColor, cfg.TintAlpha);

        using (var g = Graphics.FromImage(mid.View))
        using (var ia = new System.Drawing.Imaging.ImageAttributes()) {
            ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(small.View, new Rectangle(0, 0, mid.W, mid.H), 0, 0, small.W, small.H, GraphicsUnit.Pixel, ia);
        }
        LastFrameMs = sw.Elapsed.TotalMilliseconds;
        Invalidate();
    }

    public void SavePreview(string path) {
        try { if (mid != null) mid.View.Save(path, System.Drawing.Imaging.ImageFormat.Png); } catch { }
    }

    // 近似高斯模糊：横竖各做 3 遍滑动平均（半径 r），最后统一提鲜艳度、叠白雾
    static void GaussianTint(Bitmap bmp, int r, double sat, Color tint, int tintAlpha) {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        int w = bmp.Width, h = bmp.Height, stride = data.Stride;
        var buf = new byte[stride * h];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        var tmp = new byte[buf.Length];
        for (int pass = 0; pass < 3; pass++) {
            BlurLines(buf, tmp, w, h, stride, r, true);
            BlurLines(tmp, buf, w, h, stride, r, false);
        }
        double ta = tintAlpha / 255.0, keep = 1 - ta;
        double tr = tint.R * ta, tg = tint.G * ta, tb = tint.B * ta;
        for (int y = 0; y < h; y++) {
            int row = y * stride;
            for (int x = 0; x < w; x++) {
                int o = row + x * 4;
                double B = buf[o], G = buf[o + 1], R = buf[o + 2];
                double l = 0.299 * R + 0.587 * G + 0.114 * B;
                R = l + (R - l) * sat; G = l + (G - l) * sat; B = l + (B - l) * sat;
                buf[o + 2] = Clamp(R * keep + tr); buf[o + 1] = Clamp(G * keep + tg); buf[o] = Clamp(B * keep + tb); buf[o + 3] = 255;
            }
        }
        Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
    }

    static byte Clamp(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

    // 一个方向上的滑动平均（边缘按“复制边缘像素”处理）
    static void BlurLines(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horiz) {
        int lines = horiz ? h : w, len = horiz ? w : h, step = horiz ? 4 : stride, n = 2 * r + 1;
        for (int i = 0; i < lines; i++) {
            int start = horiz ? i * stride : i * 4;
            for (int c = 0; c < 3; c++) {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[start + Math.Min(len - 1, Math.Max(0, k)) * step + c];
                for (int j = 0; j < len; j++) {
                    dst[start + j * step + c] = (byte)(sum / n);
                    int add = Math.Min(len - 1, j + r + 1), rem = Math.Max(0, j - r);
                    sum += src[start + add * step + c] - src[start + rem * step + c];
                }
            }
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) {
        if (!frosted) { base.OnPaintBackground(e); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hdc = e.Graphics.GetHdc();
        try {
            Gdi.SetStretchBltMode(hdc, 3);   // COLORONCOLOR：放大很快；画面本身已很柔和，看不出块
            Gdi.StretchBlt(hdc, 0, 0, ClientSize.Width, ClientSize.Height, mid.Hdc, 0, 0, mid.W, mid.H, 0x00CC0020);
        } finally { e.Graphics.ReleaseHdc(hdc); }
        LastPaintMs = sw.Elapsed.TotalMilliseconds;
    }

    Rectangle Local(Rectangle r) { return new Rectangle(r.X - screenRect.X, r.Y - screenRect.Y, r.Width, r.Height); }

    public void InvalidateCard() { if (CardEnabled) Invalidate(Local(CardRect)); }

    static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad) {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    void PaintCard(Graphics g) {
        var c = Local(CardRect); var b = Local(ButtonRect);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        // 卡片：半透明白底 + 细亮边 + 很淡的阴影
        using (var sp = Round(new Rectangle(c.X, c.Y + (int)(3 * ui), c.Width, c.Height), (int)(20 * ui)))
        using (var sb = new SolidBrush(Color.FromArgb(28, 0, 0, 0))) g.FillPath(sb, sp);
        using (var p = Round(c, (int)(20 * ui))) {
            using (var fb = new SolidBrush(Color.FromArgb(222, 255, 255, 255))) g.FillPath(fb, p);
            using (var pen = new Pen(Color.FromArgb(255, 255, 255, 255), 1.5f * ui)) g.DrawPath(pen, p);
        }
        // 呼吸的小圆点（约 3 秒一个来回）
        double t = (DateTime.Now - CoverStart).TotalSeconds;
        int a = (int)(110 + 145 * (0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 3.0)));
        int dot = (int)(12 * ui), dx = c.X + (int)(24 * ui), dy = c.Y + (int)(30 * ui);
        using (var glow = new SolidBrush(Color.FromArgb(a / 4, 217, 119, 87))) g.FillEllipse(glow, dx - dot / 2, dy - dot / 2, dot * 2, dot * 2);
        using (var db = new SolidBrush(Color.FromArgb(a, 217, 119, 87))) g.FillEllipse(db, dx, dy, dot, dot);
        float fs = (float)cfg.CardScale;
        using (var f1 = new Font(cardFont, 13f * fs, FontStyle.Bold))
        using (var f2 = new Font(cardFont, 10.5f * fs))
        using (var dark = new SolidBrush(Color.FromArgb(60, 60, 60)))
        using (var gray = new SolidBrush(Color.FromArgb(135, 135, 135))) {
            float tx = dx + dot + 12 * ui;
            g.DrawString(L.T("Claude 工作中", "Claude is working"), f1, dark, tx, c.Y + 20 * ui);
            var el = DateTime.Now - CoverStart;
            g.DrawString(string.Format(L.T("已用时 {0}:{1:00}", "Elapsed {0}:{1:00}"), (int)el.TotalMinutes, el.Seconds), f2, gray, tx, c.Y + 52 * ui);
            var now = DateTime.Now.ToString("HH:mm");
            var sz = g.MeasureString(now, f2);
            g.DrawString(now, f2, gray, c.Right - sz.Width - 18 * ui, c.Y + 22 * ui);
            // 按钮
            Color bf = ButtonDown ? Color.FromArgb(225, 225, 228) : ButtonHover ? Color.FromArgb(236, 236, 240) : Color.FromArgb(246, 246, 248);
            using (var bp = Round(b, (int)(10 * ui)))
            using (var bb = new SolidBrush(bf))
            using (var bpen = new Pen(Color.FromArgb(ButtonHover ? 170 : 120, 150, 150, 155), 1f * ui)) { g.FillPath(bb, bp); g.DrawPath(bpen, bp); }
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(L.T("掀开遮罩", "Show screen"), f2, dark, b, sf);
            g.DrawString(L.T("或连按 3 次 Esc", "or press Esc ×3"), f2, gray, c.X + 22 * ui, b.Y + (b.Height - sz.Height) / 2f);
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        if (CardEnabled) PaintCard(e.Graphics);
        if (string.IsNullOrEmpty(text)) return;
        using (var f = new Font("Microsoft YaHei UI", cfg.FontSize))
        using (var b = new SolidBrush(cfg.TextColor))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            e.Graphics.DrawString(text, f, b, ClientRectangle, sf);
        }
    }

    public void ShowOnTop() {
        if (!Visible) Show();
        Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
    }

    public void HideNow() { if (Visible) Hide(); }

    // 上限 0.99：等于 1 时 WinForms 会去掉分层样式，鼠标穿透就失效
    public void SetAlpha(double a) { Opacity = Math.Max(0.0, Math.Min(0.99, a)); }

    public bool ReallyVisible { get { return Native.IsWindowVisible(Handle) && !Native.IsIconic(Handle); } }
}

enum State { Idle, Pending, Covering, Dismissed, Asking, Test }

class GuardApp {
    readonly Config cfg;
    readonly List<Overlay> overlays = new List<Overlay>();
    readonly bool captureOk;
    readonly Condition busyCond, askCond, allBtnCond;
    List<IntPtr> claudeWins = new List<IntPtr>();
    DateTime lastWinScan = DateTime.MinValue;

    State state = State.Idle;
    bool paused;
    DateTime coverStart;
    int idleCount;
    double opacity, target;          // opacity：当前不透明度（0~1）
    double fadeFrom; DateTime fadeStart; int fadeDur;
    bool soundAfter;
    int frostFrames; double frostMsSum, paintMsSum; bool previewSaved;
    int hiddenStrikes;
    readonly List<DateTime> escTimes = new List<DateTime>();
    bool escWasDown;

    Control invoker;   // 用来把后台线程的结果交回界面线程
    readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer keyTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer frostTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer cardTimer = new System.Windows.Forms.Timer();
    MouseHook.Proc hookProc; IntPtr hook = IntPtr.Zero;
    Overlay primaryOverlay;
    readonly NotifyIcon tray = new NotifyIcon();
    ToolStripMenuItem miPause;

    public GuardApp() {
        cfg = Config.Load(Path.Combine(Program.Dir, "config.ini"));

        busyCond = ButtonCondition(cfg.BusyButtons);
        askCond = ButtonCondition(cfg.AskButtons);
        allBtnCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

        foreach (var scr in Screen.AllScreens) {
            var o = new Overlay(scr.Bounds, cfg, scr.Primary ? cfg.OverlayText : "");
            if (scr.Primary) primaryOverlay = o;
            var h = o.Handle; // 立刻创建窗口
            overlays.Add(o);
        }
        captureOk = overlays.TrueForAll(delegate(Overlay o) { return o.ExcludedFromCapture; });
        Program.Log(string.Format("启动。显示器数={0}，截屏排除={1}，样式={2}{3}", overlays.Count, captureOk, cfg.Style,
            cfg.Style == "glass" ? "，毛玻璃=" + overlays.TrueForAll(delegate(Overlay o) { return o.GlassOk; }) : ""));
        if (primaryOverlay != null && cfg.ShowCard) Program.Log("卡片字体：" + primaryOverlay.CardFontName);
        if (cfg.CaptureVisible) Program.Log("注意：CaptureVisible=true，遮罩会出现在截图里（仅供排查）");

        BuildTray();

        animTimer.Interval = 16; animTimer.Tick += AnimTick; animTimer.Start();
        frostTimer.Interval = (int)(1000 / cfg.FrostFps); frostTimer.Tick += FrostTick;
        cardTimer.Interval = 66; cardTimer.Tick += delegate { foreach (var o in overlays) o.InvalidateCard(); };
        hookProc = HookCallback;
        keyTimer.Interval = 40; keyTimer.Tick += KeyTick; keyTimer.Start();
        invoker = new Control(); invoker.CreateControl();
        var pt = new Thread(PollLoop) { IsBackground = true, Name = "ClaudeStatePoll" };
        pt.SetApartmentState(ApartmentState.MTA);
        pt.Start();
    }

    static Condition AnyName(string[] names) {
        var list = new List<Condition>();
        foreach (var n in names) if (n.Trim().Length > 0) list.Add(new PropertyCondition(AutomationElement.NameProperty, n.Trim()));
        return list.Count == 1 ? list[0] : new OrCondition(list.ToArray());
    }

    static Condition ButtonCondition(string[] names) {
        var list = new List<Condition>();
        foreach (var n in names) if (n.Trim().Length > 0) list.Add(new PropertyCondition(AutomationElement.NameProperty, n.Trim()));
        Condition nameCond = list.Count == 1 ? list[0] : new OrCondition(list.ToArray());
        return new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), nameCond);
    }

    // ===== 以下在后台线程运行：检查 Claude 是否在回答（不会卡住界面） =====
    // 做法：找到 Claude 的输入框所在的那一小块区域，只在里面看有没有“停止”按钮。
    // 不依赖 Claude 的安装位置，也尽量不依赖界面语言。
    readonly Dictionary<IntPtr, AutomationElement> composer = new Dictionary<IntPtr, AutomationElement>();
    readonly Dictionary<IntPtr, DateTime> composerTime = new Dictionary<IntPtr, DateTime>();
    static readonly string[] StopWords = { "停止", "停止生成", "Stop", "Detener", "Arrêter", "Stopp", "Interrompi", "Parar", "Остановить", "중지", "停止する" };
    readonly HashSet<string> learnedStop = new HashSet<string>();
    int pollCount;

    void ScanWindows() {
        var pids = new HashSet<uint>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude")) { pids.Add((uint)p.Id); p.Dispose(); }
        claudeWins = Native.ChromeWindowsOf(pids);
        lastWinScan = DateTime.Now;
    }

    static AutomationElement Up(AutomationElement e, int levels) {
        var w = TreeWalker.ControlViewWalker;
        for (int i = 0; i < levels && e != null; i++) { var p = w.GetParent(e); if (p == null) break; e = p; }
        return e;
    }

    bool IsStopName(string n) {
        if (string.IsNullOrEmpty(n)) return false;
        foreach (var b in cfg.BusyButtons) if (n == b.Trim()) return true;
        foreach (var w in StopWords) if (n.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 输入框区域：从页面里最后一个文本输入框往上找，直到包含好几个按钮的那一层
    AutomationElement Composer(IntPtr h, AutomationElement win) {
        AutomationElement root; DateTime t;
        if (composer.TryGetValue(h, out root) && composerTime.TryGetValue(h, out t) && (DateTime.Now - t).TotalSeconds < 30) return root;
        root = null;
        var stop = win.FindFirst(TreeScope.Descendants, busyCond);
        if (stop != null) root = Up(stop, 3);
        else {
            var edits = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edits.Count > 0) {
                var e = edits[edits.Count - 1];
                var w = TreeWalker.ControlViewWalker;
                for (int i = 0; i < 7 && e != null; i++) {
                    e = w.GetParent(e);
                    if (e != null && e.FindAll(TreeScope.Descendants, allBtnCond).Count >= 3) { root = Up(e, 1); break; }
                }
            }
        }
        composer[h] = root; composerTime[h] = DateTime.Now;
        return root;
    }

    bool RootHasStop(AutomationElement root) {
        foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, allBtnCond)) {
            string n;
            try { n = b.Current.Name; } catch { continue; }
            if (IsStopName(n)) {
                if (learnedStop.Add(n)) Program.Log("识别到 Claude 的停止按钮：[" + n + "]");
                return true;
            }
        }
        return false;
    }

    // 返回 "busy" / "ask" / "idle"
    string ClaudeState() {
        if ((DateTime.Now - lastWinScan).TotalSeconds > 3) ScanWindows();
        pollCount++;
        bool busy = false;
        foreach (var h in claudeWins) {
            try {
                var win = AutomationElement.FromHandle(h);
                var root = Composer(h, win);
                bool b = false;
                if (root != null) {
                    try { b = RootHasStop(root); }
                    catch { composer.Remove(h); root = null; }
                }
                // 找不到输入框区域，或每隔约 10 秒，整窗口核对一次（防止区域找错）
                if (root == null || (!b && pollCount % 20 == 0)) {
                    var stop = win.FindFirst(TreeScope.Descendants, busyCond);
                    if (stop != null) { b = true; composer[h] = Up(stop, 3); composerTime[h] = DateTime.Now; }
                }
                if (b) {
                    busy = true;
                    if (pollCount % 4 == 0 && win.FindFirst(TreeScope.Descendants, askCond) != null) return "ask";
                }
            } catch { lastWinScan = DateTime.MinValue; composer.Remove(h); }
        }
        return busy ? "busy" : "idle";
    }

    void PollLoop() {
        while (true) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string cs = "idle";
            try { cs = ClaudeState(); } catch (Exception ex) { Program.Log("检查状态出错：" + ex.Message); }
            long ms = sw.ElapsedMilliseconds;
            if (ms > 1500) Program.Log("提示：这次检查 Claude 状态用了 " + ms + " 毫秒");
            try { invoker.BeginInvoke((MethodInvoker)delegate { ApplyState(cs); }); } catch { }
            Thread.Sleep((int)Math.Max(cfg.PollMs, Math.Min(3000, ms)));
        }
    }
    // ===== 后台线程部分结束 =====

    void DumpButtons() {
        ScanWindows();
        Program.Log("---- 按钮清单开始（窗口数 " + claudeWins.Count + "） ----");
        foreach (var h in claudeWins) {
            try {
                foreach (AutomationElement b in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, allBtnCond)) {
                    try { Program.Log("  按钮: [" + b.Current.Name + "]"); } catch { }
                }
            } catch { }
        }
        Program.Log("---- 按钮清单结束 ----");
    }

    void StartFade(double to, int ms) {
        if (target == to) return;
        fadeFrom = opacity; target = to; fadeStart = DateTime.Now;
        fadeDur = Math.Max(30, (int)(ms * Math.Abs(to - opacity)));   // 中途反向时按剩余距离缩短时间
    }

    // 只认你自己的鼠标：Claude 操作电脑时的点击带有“模拟输入”标记，会直接穿过去
    IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam) {
        if (code >= 0 && opacity > 0.5 && (state == State.Covering || state == State.Test)) {
            var m = (MouseHook.MSLL)Marshal.PtrToStructure(lParam, typeof(MouseHook.MSLL));
            bool injected = (m.flags & 1) != 0;
            if (!injected) {
                int msg = wParam.ToInt32();
                foreach (var o in overlays) {
                    if (!o.CardEnabled) continue;
                    bool inside = o.ButtonRect.Contains(m.x, m.y);
                    if (msg == 0x0200 && inside != o.ButtonHover) { o.ButtonHover = inside; if (!inside) o.ButtonDown = false; o.InvalidateCard(); }
                    if (inside && msg == 0x0201) { o.ButtonDown = true; o.InvalidateCard(); return new IntPtr(1); }   // 按下：吞掉，不传给下面的窗口
                    if (inside && msg == 0x0202) {                                                                   // 松开：执行
                        bool was = o.ButtonDown; o.ButtonDown = false;
                        if (was) o.BeginInvoke((MethodInvoker)delegate { DismissByUser("点击了“掀开遮罩”"); });
                        return new IntPtr(1);
                    }
                }
            }
        }
        return MouseHook.CallNextHookEx(hook, code, wParam, lParam);
    }

    void InstallHook() {
        if (hook != IntPtr.Zero) return;
        hook = MouseHook.SetWindowsHookEx(14, hookProc, MouseHook.GetModuleHandle(null), 0);   // WH_MOUSE_LL
    }

    void RemoveHook() {
        if (hook == IntPtr.Zero) return;
        MouseHook.UnhookWindowsHookEx(hook); hook = IntPtr.Zero;
        foreach (var o in overlays) { o.ButtonHover = false; o.ButtonDown = false; }
    }

    void DismissByUser(string why) {
        if (state != State.Covering && state != State.Test) return;
        Program.Log(why + "，掀开遮罩");
        state = State.Dismissed;
        soundAfter = false;
        StartFade(0.0, 150);   // 你主动掀开：立即生效
    }

    void ShowOverlay() {
        if (!captureOk) return; // 截屏排除不可用时不盖，避免挡住 Claude 的视线
        if (opacity <= 0) {
            frostFrames = 0; frostMsSum = 0; paintMsSum = 0; previewSaved = false;
            foreach (var o in overlays) o.UpdateFrost();   // 先拍好第一帧再显示
        }
        foreach (var o in overlays) { o.CoverStart = coverStart; o.CardEnabled = cfg.ShowCard && o == primaryOverlay; o.ShowOnTop(); }
        if (overlays.Count > 0 && overlays[0].Frosted) frostTimer.Start();
        if (cfg.ShowCard) { cardTimer.Start(); InstallHook(); }
        soundAfter = false;
        StartFade(1.0, cfg.FadeInMs);
    }

    void HideOverlay(bool withSound) { soundAfter = withSound; StartFade(0.0, cfg.FadeOutMs); }

    void FrostTick(object s, EventArgs e) {
        if (opacity <= 0 && target <= 0) { frostTimer.Stop(); return; }
        foreach (var o in overlays) {
            o.UpdateFrost();
            frostMsSum += o.LastFrameMs; paintMsSum += o.LastPaintMs; frostFrames++;
        }
        if (cfg.SavePreview && !previewSaved && overlays.Count > 0) {
            overlays[0].SavePreview(Path.Combine(Program.Dir, "frost_preview.png")); previewSaved = true;
        }
    }

    static double Ease(double t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; } // 缓入缓出

    void PlayDone() {
        if (!cfg.PlaySound) return;
        try {
            if (File.Exists(cfg.SoundFile)) new SoundPlayer(cfg.SoundFile).Play();
            else SystemSounds.Asterisk.Play();
        } catch { }
    }

    void AnimTick(object s, EventArgs e) {
        if (opacity == target) return;
        double t = Math.Min(1.0, (DateTime.Now - fadeStart).TotalMilliseconds / fadeDur);
        opacity = t >= 1 ? target : fadeFrom + (target - fadeFrom) * Ease(t);
        foreach (var o in overlays) o.SetAlpha(opacity);
        if (opacity <= 0) {
            foreach (var o in overlays) o.HideNow();
            frostTimer.Stop(); cardTimer.Stop(); RemoveHook();
            if (frostFrames > 0) Program.Log(string.Format("磨砂玻璃：共 {0} 帧，平均每帧计算 {1:N1} 毫秒 + 绘制 {2:N1} 毫秒", frostFrames, frostMsSum / frostFrames, paintMsSum / frostFrames));
            frostFrames = 0;
            if (soundAfter) { soundAfter = false; PlayDone(); }
        }
    }

    void KeyTick(object s, EventArgs e) {
        bool down = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
        if (down && !escWasDown) {
            var now = DateTime.Now;
            escTimes.Add(now);
            escTimes.RemoveAll(delegate(DateTime t) { return (now - t).TotalMilliseconds > 1500; });
            if (escTimes.Count >= 3) {
                escTimes.Clear();
                DismissByUser("按了 3 次 Esc");
            }
        }
        escWasDown = down;
    }

    void ApplyState(string cs) {
        if (state == State.Test) return;
        if (paused) {
            if (state == State.Covering) HideOverlay(false);
            state = State.Idle; return;
        }

        if (cs == "idle") {
            idleCount++;
            if (idleCount >= 2) { // 连续约 1 秒没在回答，才算真的完成
                if (state == State.Pending)
                    Program.Log(string.Format("回答很快就结束了（{0:N0} 秒），没有遮罩", (DateTime.Now - coverStart).TotalSeconds));
                if (state == State.Covering) {
                    Program.Log(string.Format("回答完成，用时 {0:N0} 秒，掀开遮罩", (DateTime.Now - coverStart).TotalSeconds));
                    HideOverlay(true);
                }
                state = State.Idle;
            }
            return;
        }
        idleCount = 0;

        if (cs == "ask") {
            if (state == State.Covering) {
                Program.Log("Claude 在等你确认，暂时掀开遮罩");
                HideOverlay(true);
                state = State.Asking;
            }
            return;
        }

        // busy
        switch (state) {
            case State.Idle:
                coverStart = DateTime.Now; hiddenStrikes = 0;
                if (cfg.ShowDelaySec <= 0) { Program.Log("Claude 开始回答，盖上遮罩"); state = State.Covering; ShowOverlay(); }
                else state = State.Pending;   // 先等一等：很快答完就不遮了
                break;
            case State.Pending:
                if ((DateTime.Now - coverStart).TotalSeconds >= cfg.ShowDelaySec) {
                    Program.Log(string.Format("回答超过 {0} 秒，盖上遮罩", cfg.ShowDelaySec));
                    state = State.Covering; ShowOverlay();
                }
                break;
            case State.Asking:
                Program.Log("确认完成，重新盖上遮罩");
                state = State.Covering; ShowOverlay();
                break;
            case State.Covering:
                if ((DateTime.Now - coverStart).TotalMinutes >= cfg.TimeoutMinutes) {
                    Program.Log("超时，自动掀开遮罩");
                    state = State.Dismissed; HideOverlay(true);
                } else if (opacity > 0.5 && !overlays.TrueForAll(delegate(Overlay o) { return o.ReallyVisible; }) && ++hiddenStrikes < 3) {
                    // 偶尔被藏一下（比如刚启动时），先放回去再看
                    foreach (var o in overlays) o.ShowOnTop();
                } else if (hiddenStrikes >= 3) {
                    // 连续被 Claude 藏起来：说明这次操作电脑没有授权 ClaudeScreenGuard。
                    // 不跟它抢，否则 Claude 截图会被涂成灰块、看不见屏幕。
                    hiddenStrikes = 0;
                    Program.Log("遮罩被 Claude 隐藏（本次未授权 ClaudeScreenGuard），这次回答不再遮罩");
                    state = State.Dismissed;
                    target = 0; opacity = 0; frostTimer.Stop(); cardTimer.Stop(); RemoveHook();
                    foreach (var o in overlays) { o.SetAlpha(0); o.HideNow(); }
                } else {
                    if (overlays.TrueForAll(delegate(Overlay o) { return o.ReallyVisible; })) hiddenStrikes = 0;
                    foreach (var o in overlays) Native.SetWindowPos(o.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                }
                break;
        }
    }

    void BuildTray() {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(Color.FromArgb(217, 119, 87))) g.FillEllipse(br, 2, 2, 28, 28);
            g.FillEllipse(Brushes.Black, 10, 10, 12, 12);
        }
        tray.Icon = Icon.FromHandle(bmp.GetHicon());
        tray.Text = L.T("Claude 屏幕遮罩", "Claude Screen Guard");
        tray.Visible = true;

        var menu = new ContextMenuStrip();
        miPause = new ToolStripMenuItem(L.T("暂停自动遮罩", "Pause"));
        miPause.Click += delegate {
            paused = !paused; miPause.Checked = paused;
            tray.Text = paused ? L.T("Claude 屏幕遮罩（已暂停）", "Claude Screen Guard (paused)") : L.T("Claude 屏幕遮罩", "Claude Screen Guard");
            Program.Log("暂停=" + paused);
        };
        var miSettings = new ToolStripMenuItem(L.T("设置…", "Settings…"), null, delegate { OpenSettings(); });
        miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
        menu.Items.Add(miSettings);
        menu.Items.Add(miPause);
        menu.Items.Add(L.T("测试遮罩（4 秒）", "Test (4 s)"), null, delegate { StartTest(4000); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("用记事本打开设置文件", "Open config file"), null, delegate { OpenInNotepad(Path.Combine(Program.Dir, "config.ini")); });
        menu.Items.Add(L.T("记录 Claude 当前按钮（排查用）", "Log Claude's buttons (debug)"), null, delegate { ThreadPool.QueueUserWorkItem(delegate { DumpButtons(); OpenInNotepad(Program.LogPath); }); });
        menu.Items.Add(L.T("打开日志", "Open log"), null, delegate { OpenInNotepad(Program.LogPath); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("退出", "Exit"), null, delegate {
            Program.Log("退出");
            keyTimer.Stop(); animTimer.Stop(); frostTimer.Stop(); cardTimer.Stop(); RemoveHook();
            foreach (var o in overlays) { o.HideNow(); o.Close(); }
            tray.Visible = false; tray.Dispose();
            Application.Exit();
        });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { OpenSettings(); };

        if (captureOk)
            tray.ShowBalloonTip(3000, L.T("Claude 屏幕遮罩已启动", "Claude Screen Guard is running"), L.T("Claude 回答时会自动遮住屏幕。双击托盘图标打开设置。", "Your screen will be frosted while Claude works. Double-click the tray icon for settings."), ToolTipIcon.None);
        else {
            Program.Log("警告：此系统不支持“截屏排除”，遮罩已停用（需要 Windows 10 2004 或更新版本）");
            tray.ShowBalloonTip(8000, L.T("Claude 屏幕遮罩无法使用", "Claude Screen Guard can't run"), L.T("需要 Windows 10 2004 或更新版本，遮罩才能对截屏隐身。为了不挡住 Claude 操作电脑，遮罩已停用。", "Windows 10 2004 or later is required so the overlay stays invisible to screenshots. The overlay is disabled."), ToolTipIcon.Warning);
        }
    }

    SettingsForm settingsForm;

    void OpenSettings() {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(Path.Combine(Program.Dir, "config.ini"));
        settingsForm.Show();
    }

    public void StartTest(int ms) {
        if (state != State.Idle) return;
        state = State.Test; coverStart = DateTime.Now; ShowOverlay();
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += delegate { t.Stop(); t.Dispose(); if (state == State.Test) { state = State.Idle; HideOverlay(true); } };
        t.Start();
    }

    static void OpenInNotepad(string path) {
        try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
    }
}

class SettingsForm : Form {
    readonly string path;
    readonly ComboBox style = new ComboBox(), font = new ComboBox();
    readonly TrackBar blur = new TrackBar(), tint = new TrackBar(), fps = new TrackBar(), cardSize = new TrackBar();
    readonly Label blurV = new Label(), tintV = new Label(), fpsV = new Label(), cardSizeV = new Label();
    readonly CheckBox showCard = new CheckBox(), sound = new CheckBox();
    readonly NumericUpDown fadeIn = new NumericUpDown(), fadeOut = new NumericUpDown(), timeout = new NumericUpDown(), delay = new NumericUpDown();
    static readonly string[] StyleKeys = { "frosted", "glass", "black" };
    static readonly string AutoFont = L.T("自动挑选", "Automatic");

    public SettingsForm(string configPath) {
        path = configPath;
        var c = Config.Load(path);
        float k; using (var g = CreateGraphics()) k = g.DpiX / 96f;
        Text = L.T("Claude 屏幕遮罩 · 设置", "Claude Screen Guard · Settings");
        Font = new Font(L.Zh ? "Microsoft YaHei UI" : "Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White; Padding = new Padding((int)(14 * k));

        var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260 * k));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(t);

        Action<string> Section = delegate(string title) {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(217, 119, 87), Margin = new Padding(0, (int)(12 * k), 0, (int)(4 * k)) };
            t.Controls.Add(l); t.SetColumnSpan(l, 3);
        };
        Action<string, Control, Control> Row = delegate(string name, Control ctl, Control val) {
            t.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(6 * k), (int)(10 * k), 0) });
            ctl.Dock = DockStyle.Fill; t.Controls.Add(ctl);
            if (val != null) { val.AutoSize = true; val.Anchor = AnchorStyles.Left; val.ForeColor = Color.Gray; t.Controls.Add(val); }
            else t.Controls.Add(new Label { AutoSize = true });
        };
        Func<TrackBar, int, int, int, TrackBar> Setup = delegate(TrackBar tb, int min, int max, int v) {
            tb.Minimum = min; tb.Maximum = max; tb.Value = Math.Max(min, Math.Min(max, v)); tb.TickStyle = TickStyle.None; tb.AutoSize = false; tb.Height = (int)(30 * k); return tb;
        };

        Section(L.T("遮罩", "Overlay"));
        style.DropDownStyle = ComboBoxStyle.DropDownList;
        style.Items.AddRange(new object[] { L.T("柔和磨砂（推荐）", "Soft frosted glass (recommended)"), L.T("系统毛玻璃（省电，但图标有光晕）", "System acrylic (lighter on CPU, blurrier)"), L.T("全黑", "Solid black") });
        style.SelectedIndex = Math.Max(0, Array.IndexOf(StyleKeys, c.Style));
        Row(L.T("样式", "Style"), style, null);
        Row(L.T("模糊程度", "Blur"), Setup(blur, 2, 16, (int)Math.Round(c.BlurRadius)), blurV);
        Row(L.T("白雾浓度", "Frost tint"), Setup(tint, 0, 200, c.TintAlpha), tintV);
        Row(L.T("刷新频率", "Refresh rate"), Setup(fps, 1, 6, (int)Math.Round(c.FrostFps)), fpsV);

        Section(L.T("中间的小卡片", "Status card"));
        showCard.Text = L.T("显示小卡片", "Show status card"); showCard.AutoSize = true; showCard.Checked = c.ShowCard;
        Row("", showCard, null);
        Row(L.T("卡片大小", "Card size"), Setup(cardSize, 10, 30, (int)Math.Round(c.CardScale * 10)), cardSizeV);
        font.DropDownStyle = ComboBoxStyle.DropDownList;
        font.Items.Add(AutoFont);
        foreach (var n in Overlay.InstalledNiceFonts()) font.Items.Add(n);
        if (c.CardFont != "auto" && !font.Items.Contains(c.CardFont)) font.Items.Add(c.CardFont);
        font.SelectedItem = c.CardFont == "auto" ? AutoFont : (object)c.CardFont;
        if (font.SelectedIndex < 0) font.SelectedIndex = 0;
        Row(L.T("卡片字体", "Card font"), font, null);

        Func<NumericUpDown, int, int, int, int, NumericUpDown> Num = delegate(NumericUpDown n, int min, int max, int inc, int v) {
            n.Minimum = min; n.Maximum = max; n.Increment = inc; n.Value = Math.Max(min, Math.Min(max, v)); return n;
        };
        Section(L.T("什么时候遮", "When to cover"));
        Row(L.T("回答超过几秒才遮", "Cover after (seconds)"), Num(delay, 0, 60, 1, (int)Math.Round(c.ShowDelaySec)), new Label { Text = L.T("很快答完的不遮；0 = 立刻遮", "short replies stay uncovered; 0 = immediately") });

        Section(L.T("其他", "Other"));
        sound.Text = L.T("回答完成时响一声", "Chime when Claude finishes"); sound.AutoSize = true; sound.Checked = c.PlaySound;
        Row("", sound, null);
        Row(L.T("盖上过渡（毫秒）", "Fade in (ms)"), Num(fadeIn, 0, 3000, 100, c.FadeInMs), null);
        Row(L.T("掀开过渡（毫秒）", "Fade out (ms)"), Num(fadeOut, 0, 3000, 100, c.FadeOutMs), null);
        Row(L.T("超时自动掀开（分钟）", "Give up after (minutes)"), Num(timeout, 1, 240, 5, (int)c.TimeoutMinutes), null);

        Section(L.T("Claude 操作电脑时也要遮住", "Keep covered during computer use"));
        var help = new Label { AutoSize = true, MaximumSize = new Size((int)(420 * k), 0), ForeColor = Color.DimGray,
            Text = L.T("Claude 操作电脑时，只有被授权的程序才不会被它藏起来。点下面的按钮复制一句话，粘贴到 Claude 的 设置 → 账户 → 个人偏好 里，Claude 以后就会在授权框里自动带上本程序。",
                       "During computer use, Claude hides apps it hasn't been granted. Copy the sentence below into Claude → Settings → personal preferences so Claude includes this app in its access request.") };
        t.Controls.Add(help); t.SetColumnSpan(help, 3);
        var copy = new Button { Text = L.T("复制给 Claude 的授权提示语", "Copy instruction for Claude"), AutoSize = true, Padding = new Padding((int)(8 * k), (int)(2 * k), (int)(8 * k), (int)(2 * k)), Margin = new Padding(0, (int)(6 * k), 0, 0) };
        copy.Click += delegate {
            string exe = Application.ExecutablePath.ToLowerInvariant();
            Clipboard.SetText(L.T("使用电脑（computer use）时，申请应用权限时请同时申请屏幕遮罩程序 ClaudeScreenGuard 的权限，路径是 " + exe,
                                  "When using computer use, always include the screen-guard app ClaudeScreenGuard in the access request as well. Its path is " + exe));
            copy.Text = L.T("已复制 ✓", "Copied ✓");
        };
        t.Controls.Add(copy); t.SetColumnSpan(copy, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, (int)(16 * k), 0, 0) };
        var preview = new Button { Text = L.T("保存并预览", "Save && preview"), AutoSize = true, Padding = new Padding((int)(8 * k), (int)(2 * k), (int)(8 * k), (int)(2 * k)) };
        var save = new Button { Text = L.T("保存", "Save"), AutoSize = true, Padding = preview.Padding };
        var cancel = new Button { Text = L.T("取消", "Cancel"), AutoSize = true, Padding = preview.Padding };
        preview.Click += delegate { SaveAll(); Program.Restart(true); };
        save.Click += delegate { SaveAll(); Program.Restart(false); };
        cancel.Click += delegate { Close(); };
        buttons.Controls.AddRange(new Control[] { preview, save, cancel });
        t.Controls.Add(buttons); t.SetColumnSpan(buttons, 3);
        AcceptButton = preview; CancelButton = cancel;

        EventHandler upd = delegate { UpdateLabels(); };
        blur.ValueChanged += upd; tint.ValueChanged += upd; fps.ValueChanged += upd; cardSize.ValueChanged += upd;
        style.SelectedIndexChanged += upd; showCard.CheckedChanged += upd;
        UpdateLabels();
    }

    void UpdateLabels() {
        int b = blur.Value;
        blurV.Text = b + (b <= 4 ? L.T("（轻度）", " (light)") : b <= 8 ? L.T("（中度）", " (medium)") : L.T("（较重）", " (heavy)"));
        tintV.Text = tint.Value + (tint.Value < 60 ? L.T("（通透）", " (clear)") : tint.Value < 120 ? L.T("（适中）", " (medium)") : L.T("（朦胧）", " (misty)"));
        fpsV.Text = L.T("每秒 " + fps.Value + " 次", fps.Value + " per second") + (fps.Value <= 2 ? L.T("（省电）", " (saves power)") : "");
        cardSizeV.Text = (cardSize.Value / 10.0).ToString("0.0") + " ×";
        bool frosted = style.SelectedIndex == 0;
        blur.Enabled = tint.Enabled = fps.Enabled = frosted;
        cardSize.Enabled = font.Enabled = showCard.Checked;
    }

    void SaveAll() {
        var v = new Dictionary<string, string>();
        v["Style"] = StyleKeys[Math.Max(0, style.SelectedIndex)];
        v["BlurRadius"] = blur.Value.ToString();
        v["TintAlpha"] = tint.Value.ToString();
        v["FrostFps"] = fps.Value.ToString();
        v["ShowCard"] = showCard.Checked ? "true" : "false";
        v["CardScale"] = (cardSize.Value / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        v["CardFont"] = font.SelectedIndex <= 0 ? "auto" : font.SelectedItem.ToString();
        v["PlaySound"] = sound.Checked ? "true" : "false";
        v["FadeInMs"] = ((int)fadeIn.Value).ToString();
        v["FadeOutMs"] = ((int)fadeOut.Value).ToString();
        v["TimeoutMinutes"] = ((int)timeout.Value).ToString();
        v["ShowDelaySec"] = ((int)delay.Value).ToString();
        Config.Save(path, v);
        Program.Log("通过设置窗口保存了设置");
    }
}

static class Program {
    public static string Dir = AppDomain.CurrentDomain.BaseDirectory;
    public static string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "guard.log");
    static readonly object logLock = new object();

    public static void Log(string msg) {
        lock (logLock) {
            try {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 300 * 1024) File.Copy(LogPath, LogPath + ".old", true);
                if (fi.Exists && fi.Length > 300 * 1024) File.Delete(LogPath);
                File.AppendAllText(LogPath, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + msg + "\r\n", new UTF8Encoding(true));
            } catch { }
        }
    }

    static Mutex mutex;

    // 保存设置后重启自己，让新设置生效；preview = 启动后马上演示一次遮罩
    public static void Restart(bool preview) {
        try { mutex.ReleaseMutex(); } catch { }
        System.Diagnostics.Process.Start(Application.ExecutablePath, preview ? "--preview" : "");
        Environment.Exit(0);
    }

    [STAThread]
    static void Main(string[] args) {
        bool created;
        mutex = new Mutex(true, "Global\\ClaudeScreenGuard_Mutex", out created);
        if (!created) {
            // 可能是旧的正在退出（比如刚保存设置），最多等 5 秒
            try { if (!mutex.WaitOne(5000)) return; } catch (AbandonedMutexException) { }
        }
        Native.MakeDpiAware();
        Application.EnableVisualStyles();
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log("出错：" + e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log("严重错误：" + e.ExceptionObject); };
        var app = new GuardApp();
        if (Array.IndexOf(args, "--preview") >= 0) {
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += delegate { t.Stop(); t.Dispose(); app.StartTest(4000); };
            t.Start();
        }
        Application.Run();
        GC.KeepAlive(app);
        GC.KeepAlive(mutex);
    }
}
