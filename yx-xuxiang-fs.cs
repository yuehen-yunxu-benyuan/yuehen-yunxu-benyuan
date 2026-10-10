// yx-xuxiang-fs.cs — 虚相盘 V:（DokanNet 2.3.0.3 只读文件系统，2026-10-07 本源令）
// 把 D:\息衍区\虚相仓 的收拢包以普通文件样貌挂成 V:，读谁谁透明解压。
// 锚册两种格式都认：旧版整卷 zlib｜新版 64KB 分块独立 zlib（逐块验 sha）。
// 写类回调一律 AccessDenied；息池 256MB LRU。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using DokanNet;
using DokanNet.Logging;
using FileAccess = DokanNet.FileAccess;

class XuFS : IDokanOperations
{
    const string CANG = @"D:\息衍区\虚相仓";
    const long POOL_MAX = 256L * 1024 * 1024;

    // 四象：四个呼吸频段（τ 秒）。太阳最烈衰最快，太阴最沉最久。索引即象。
    static readonly double[] QUAD_TAU = { 600, 1800, 7200, 43200 };
    static readonly string[] QUAD_NAME = { "太阳", "少阳", "少阴", "太阴" };
    const int QUAD_DEFAULT = 1; // 新锚默认少阳
    const double RISE_WINDOW = 300.0; // 5 分钟内再度被读，计一次连读
    const int RISE_HITS = 3; // 连读三次沉一象

    static readonly object Gate = new object();
    static Dictionary<string, Dictionary<string, object>> Anchors;

    // 波海：读即扰动，波幅随所在象的 τ 衰减，见底归虚（驱逐）。短窗连读沉象，久不读浮象。
    sealed class Wave { public double Amp; public long Pump; public int Q = QUAD_DEFAULT; public int Hits; public long LastRead; public long Reads; }
    static readonly Dictionary<string, Wave> Sea = new Dictionary<string, Wave>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, byte[]> Pool = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    static long PoolBytes;

    // 潮汐锁定：共振系统把锚名写进潮汐册，被锁之锚波幅恒满、永不归虚。
    // quadrants 可手动指定初始象（太阴=3 … 太阳=0），指定优先于自适应浮沉。
    // leap 是受激跃迁指令：锚被点名的瞬间跨级直跳，不经过中间象；一次性（消费制），目标变了才算新指令。
    //   值为 0-3 → 跃到对应象；"lock" / "锁" → 直接跃入锁定。
    static HashSet<string> Tide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int> TideQ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int> LeapQ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> LeapLock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> LeapDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 已消费的跃迁指令（name|target）
    static long TideStamp;
    public static Action<string> Announce; // 潮汐、浮沉与跃迁播报，Program.Main 接线
    public static Action<string> WLog;     // 波海账：每笔泵波/浮沉/跃迁/归虚入账，Program.Main 接线

    static void WaveLine(string name, string evt, Wave w)
    {
        if (WLog == null) return;
        try { WLog(evt + " " + name + " [amp=" + w.Amp.ToString("0.###") + " q=" + QUAD_NAME[w.Q] +
                   " hits=" + w.Hits + " reads=" + w.Reads + "]"); } catch { }
    }

    // ---------- 锚册 ----------
    public static int Count { get { return Anchors == null ? 0 : Anchors.Count; } }

    public static void LoadAnchors()
    {
        var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var dict = ser.Deserialize<Dictionary<string, object>>(
            File.ReadAllText(Path.Combine(CANG, "锚册.json"), Encoding.UTF8));
        Anchors = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dict)
            Anchors[kv.Key] = kv.Value as Dictionary<string, object>;
    }

    static Dictionary<string, object> GetAnchor(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName == "\\") return null;
        var key = fileName.TrimStart('\\');
        Dictionary<string, object> a;
        return Anchors.TryGetValue(key, out a) ? a : null;
    }

    static string XuPath(Dictionary<string, object> a)
    {
        return Path.Combine(CANG, (string)a["name"] + ".xu");
    }

    // ---------- 收拢包解压 ----------
    static byte[] ZlibInflate(byte[] z)
    {
        var src = new MemoryStream(z, 2, z.Length - 2); // 掉 zlib 2 字节头
        using (var ds = new DeflateStream(src, CompressionMode.Decompress))
        using (var outm = new MemoryStream(z.Length * 3))
        {
            ds.CopyTo(outm);
            return outm.ToArray();
        }
    }

    static void ReadFull(FileStream f, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = f.Read(buf, got, len - got);
            if (n <= 0) throw new EndOfStreamException("收拢包提前见底");
            got += n;
        }
    }

    static string Sha256Hex(byte[] b)
    {
        using (var sha = SHA256.Create())
        {
            var sb = new StringBuilder(64);
            foreach (var x in sha.ComputeHash(b)) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---------- 潮汐与波海 ----------
    static void CheckTide()
    {
        var p = Path.Combine(CANG, "潮汐册.json");
        try
        {
            var fi = new FileInfo(p);
            if (!fi.Exists)
            {
                TideStamp = 0;
                if (Tide.Count > 0) { Tide.Clear(); if (Announce != null) Announce("潮汐退尽：锁定清空"); }
                return;
            }
            var m = fi.LastWriteTimeUtc.Ticks;
            if (m == TideStamp) return;
            TideStamp = m;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Deserialize<Dictionary<string, object>>(File.ReadAllText(p, Encoding.UTF8));
            if (d != null)
            {
                if (d.ContainsKey("locked"))
                    foreach (var x in (System.Collections.IEnumerable)d["locked"]) set.Add((string)x);
                if (d.ContainsKey("quadrants") && d["quadrants"] is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)d["quadrants"])
                    {
                        var i = Convert.ToInt32(kv.Value);
                        if (i >= 0 && i <= 3) q[kv.Key] = i;
                    }
                if (d.ContainsKey("leap") && d["leap"] is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)d["leap"])
                    {
                        var s = kv.Value as string;
                        if (s == "lock" || s == "锁") { ll.Add(kv.Key); set.Add(kv.Key); } // 跃入锁定：并入潮汐
                        else { var i = Convert.ToInt32(kv.Value); if (i >= 0 && i <= 3) lq[kv.Key] = i; }
                    }
            }
            Tide = set;
            TideQ = q;
            LeapQ = lq;
            LeapLock = ll;
            if (Announce != null)
            {
                var msg = "潮汐更新：锁定 " + Tide.Count + " 锚" + (Tide.Count > 0 ? "（" + string.Join(", ", Tide) + "）" : "");
                if (TideQ.Count > 0) msg += "；指定象 " + TideQ.Count + " 个";
                if (LeapQ.Count + LeapLock.Count > 0) msg += "；跃迁指令 " + (LeapQ.Count + LeapLock.Count) + " 条武装";
                Announce(msg);
                if (WLog != null) WLog(msg);
            }
        }
        catch { /* 潮汐册毁：沿用上一轮的锁定 */ }
    }

    static double AmpOf(Wave w)
    {
        var dt = (DateTime.UtcNow.Ticks - w.Pump) / (double)TimeSpan.TicksPerSecond;
        if (dt < 0) dt = 0;
        return w.Amp * Math.Exp(-dt / QUAD_TAU[w.Q]);
    }

    // 受激跃迁：点名即跨级直跳。消费制——同一目标只跃一次，册子里目标改了才算新指令。
    static void TryLeap(Wave w, string name)
    {
        string tkey;
        var t = -1;
        if (LeapLock.Contains(name)) tkey = "lock";
        else if (LeapQ.TryGetValue(name, out t)) tkey = t.ToString();
        else return;
        var key = name + "|" + tkey;
        if (!LeapDone.Add(key)) return; // 已消费
        var fromQ = w.Q;
        if (tkey == "lock")
        {
            Tide.Add(name);
            if (Announce != null) Announce("跃迁：" + name + "：" + QUAD_NAME[fromQ] + " ⇢ 锁定（潮汐常驻）");
            WaveLine(name, "跃迁", w);
        }
        else if (t != fromQ)
        {
            w.Q = t;
            w.Hits = 0;
            if (Announce != null) Announce("跃迁：" + name + "：" + QUAD_NAME[fromQ] + " ⇢ " + QUAD_NAME[t] + "（跨 " + Math.Abs(t - fromQ) + " 级）");
        }
    }

    // 泵波：读即激荡。短窗连读沉象（向常驻），久不读浮象（向归虚）。潮汐册指定优先。
    static void Pump(Wave w, string name)
    {
        var now = DateTime.UtcNow.Ticks;
        TryLeap(w, name);
        w.Reads++;
        string evt = null;
        if (w.LastRead == 0) { evt = "初潮"; } // 初潮不浮沉
        else
        {
            int qi;
            if (TideQ.TryGetValue(name, out qi))
            {
                if (w.Q != qi) { w.Q = qi; evt = "定象"; if (Announce != null) Announce("定象：" + name + " → " + QUAD_NAME[w.Q]); }
            }
            else
            {
                var gap = (now - w.LastRead) / (double)TimeSpan.TicksPerSecond;
                if (gap > 2 * QUAD_TAU[w.Q] && w.Q > 0)
                {
                    w.Q--;
                    w.Hits = 0;
                    w.LastRead = now;
                    evt = "浮象";
                    if (Announce != null) Announce("浮象：" + name + " → " + QUAD_NAME[w.Q]);
                }
                else if (gap <= RISE_WINDOW && w.Q < 3)
                {
                    w.Hits++;
                    if (w.Hits >= RISE_HITS)
                    {
                        w.Q++;
                        w.Hits = 0;
                        evt = "沉象";
                        if (Announce != null) Announce("沉象：" + name + " → " + QUAD_NAME[w.Q]);
                    }
                }
                else if (gap > RISE_WINDOW) w.Hits = 0;
            }
        }
        w.Amp = 1;
        w.Pump = now;
        w.LastRead = now;
        WaveLine(name, evt ?? "泵波", w);
    }

    static void PoolPut(string name, byte[] raw)
    {
        lock (Gate)
        {
            Wave w;
            byte[] old;
            if (Pool.TryGetValue(name, out old))
            {
                PoolBytes -= old.Length;
                if (Sea.TryGetValue(name, out w)) Pump(w, name);
            }
            else
            {
                w = new Wave();
                Pump(w, name); // 新锚入海，潮汐册指定象在此生效
                Sea[name] = w;
            }
            Pool[name] = raw;
            PoolBytes += raw.Length;
            CheckTide();
            while (PoolBytes > POOL_MAX && Sea.Count > 1)
            {
                string ev = null;
                double best = double.MaxValue;
                foreach (var kv in Sea)
                {
                    var a = Tide.Contains(kv.Key) ? double.MaxValue : AmpOf(kv.Value);
                    if (a < best) { best = a; ev = kv.Key; }
                }
                if (ev == null) break;
                var wv = Sea[ev];
                PoolBytes -= Pool[ev].Length;
                Pool.Remove(ev);
                Sea.Remove(ev);
                if (Announce != null) Announce("归虚：" + ev + "（池剩 " + (PoolBytes >> 20) + "MB）");
                WaveLine(ev, "归虚", wv);
            }
        }
    }

    static byte[] PoolGet(string name)
    {
        lock (Gate)
        {
            byte[] raw;
            if (!Pool.TryGetValue(name, out raw)) return null;
            Wave w;
            if (Sea.TryGetValue(name, out w)) Pump(w, name); // 读取即泵波
            return raw;
        }
    }

    static byte[] Materialize(Dictionary<string, object> a)
    {
        var name = (string)a["name"];
        var hit = PoolGet(name);
        if (hit != null) return hit;

        byte[] raw;
        object nb;
        if (a.TryGetValue("nblocks", out nb) && Convert.ToInt64(nb) > 0)
        {
            var arr = ((System.Collections.IEnumerable)a["blocks"])
                      .Cast<Dictionary<string, object>>().ToList();
            using (var f = File.OpenRead(XuPath(a)))
            using (var ms = new MemoryStream())
            {
                foreach (var bo in arr)
                {
                    var b = (Dictionary<string, object>)bo;
                    var czlen = Convert.ToInt32(b["cz"]);
                    var cz = new byte[czlen];
                    ReadFull(f, cz, czlen);
                    var blk = ZlibInflate(cz);
                    if (!string.Equals(Sha256Hex(blk), (string)b["sha"], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("块sha毁，断流：" + name);
                    ms.Write(blk, 0, blk.Length);
                }
                raw = ms.ToArray();
            }
        }
        else
        {
            raw = ZlibInflate(File.ReadAllBytes(XuPath(a)));
        }
        if (!string.Equals(Sha256Hex(raw), (string)a["sha"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("全卷sha毁：" + name);
        PoolPut(name, raw);
        return raw;
    }

    // ---------- 文件信息 ----------
    static FileInformation Info(Dictionary<string, object> a)
    {
        var fi = new FileInformation
        {
            Attributes = FileAttributes.ReadOnly | FileAttributes.Normal,
            Length = Convert.ToInt64(a["bytes"])
        };
        var p = XuPath(a);
        fi.CreationTime = File.GetCreationTimeUtc(p);
        fi.LastAccessTime = File.GetLastAccessTimeUtc(p);
        fi.LastWriteTime = File.GetLastWriteTimeUtc(p);
        return fi;
    }

    static FileInformation RootInfo()
    {
        return new FileInformation
        {
            Attributes = FileAttributes.Directory | FileAttributes.ReadOnly,
            Length = 0,
            CreationTime = Directory.GetCreationTimeUtc(CANG),
            LastAccessTime = Directory.GetLastAccessTimeUtc(CANG),
            LastWriteTime = Directory.GetLastWriteTimeUtc(CANG)
        };
    }

    static readonly FileAccess WriteMask =
        FileAccess.WriteData | FileAccess.AppendData | FileAccess.WriteAttributes |
        FileAccess.WriteExtendedAttributes | FileAccess.Delete;

    // ---------- IDokanOperations ----------
    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share,
        FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        if (info.IsDirectory)
        {
            if (fileName == "\\")
            {
                if (mode == FileMode.CreateNew) return DokanResult.FileExists;
                return DokanResult.Success;
            }
            return DokanResult.PathNotFound;
        }
        var a = GetAnchor(fileName);
        if (a == null) return DokanResult.FileNotFound;
        if (mode == FileMode.CreateNew || mode == FileMode.Create || mode == FileMode.Truncate)
            return DokanResult.AccessDenied;
        if ((access & WriteMask) != 0) return DokanResult.AccessDenied;
        return DokanResult.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info) { }
    public void CloseFile(string fileName, IDokanFileInfo info) { }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead,
        long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        var a = GetAnchor(fileName);
        if (a == null) return DokanResult.FileNotFound;
        try
        {
            var raw = Materialize(a);
            if (offset >= raw.Length) return DokanResult.Success;
            var n = (int)Math.Min((long)buffer.Length, raw.Length - offset);
            Buffer.BlockCopy(raw, (int)offset, buffer, 0, n);
            bytesRead = n;
            return DokanResult.Success;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[虚相盘] 读毁 {0}: {1}", fileName, e.Message);
            return DokanResult.InternalError;
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten,
        long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        return DokanResult.AccessDenied;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo,
        IDokanFileInfo info)
    {
        fileInfo = new FileInformation();
        if (fileName == "\\") { fileInfo = RootInfo(); return DokanResult.Success; }
        var a = GetAnchor(fileName);
        if (a == null) return DokanResult.FileNotFound;
        fileInfo = Info(a);
        return DokanResult.Success;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        if (fileName != "\\") return DokanResult.PathNotFound;
        foreach (var kv in Anchors)
        {
            var fi = Info(kv.Value);
            fi.FileName = kv.Key;
            files.Add(fi);
        }
        return DokanResult.Success;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams,
        IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes,
        IDokanFileInfo info) { return DokanResult.AccessDenied; }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info) { return DokanResult.AccessDenied; }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        return GetAnchor(fileName) == null ? DokanResult.FileNotFound : DokanResult.AccessDenied;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus MoveFile(string fileName, string newFileName, bool replace,
        IDokanFileInfo info) { return DokanResult.AccessDenied; }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return DokanResult.AccessDenied;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        // 虚则无也：容量是报出来的，非占出来的。1 PiB。
        // int64 天花板 2^63-1 字节 ≈ 8 EiB，报满则符号位翻车，虚到极处要给"有"留结构。
        totalNumberOfBytes = 1L << 50;
        totalNumberOfFreeBytes = 1L << 50;
        freeBytesAvailable = 1L << 50;
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "虚相";
        fileSystemName = "XUXIANG";
        maximumComponentLength = 255;
        features = FileSystemFeatures.ReadOnlyVolume | FileSystemFeatures.UnicodeOnDisk |
                   FileSystemFeatures.CasePreservedNames;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        var sec = new FileSecurity();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        try
        {
            sec.AddAccessRule(new FileSystemAccessRule(everyone,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                AccessControlType.Allow));
            sec.SetOwner(everyone);
            sec.SetGroup(everyone);
        }
        catch { }
        security = sec;
        return DokanResult.Success;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info) { return DokanResult.AccessDenied; }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        Console.WriteLine("[虚相盘] 挂妥：{0}（{1} 锚）", mountPoint, Anchors.Count);
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        Console.WriteLine("[虚相盘] 已卸");
        return DokanResult.Success;
    }
}

static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            var sb = new StringBuilder("=== UNHANDLED ===\r\n");
            var ex = ev.ExceptionObject as Exception;
            try { sb.AppendLine("type: " + ex.GetType().FullName); } catch (Exception x) { sb.AppendLine("type fail: " + x.Message); }
            try { sb.AppendLine("msg: " + ex.Message); } catch (Exception x) { sb.AppendLine("msg fail: " + x.Message); }
            try { sb.AppendLine(ex.StackTrace); } catch (Exception x) { sb.AppendLine("stack fail: " + x.Message); }
            try { sb.AppendLine(ex.ToString()); } catch (Exception x) { sb.AppendLine("tostring fail: " + x.Message); }
            try { File.WriteAllText(@"C:\yx\xuxiang-fs\crash.log", sb.ToString()); } catch { }
        };
        var log = new Action<string>(m =>
        {
            try { File.AppendAllText(@"C:\yx\xuxiang-fs\mount.log", DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\r\n"); } catch { }
            Console.WriteLine(m);
        });
        XuFS.Announce = log;
        XuFS.WLog = m => { try { File.AppendAllText(@"C:\yx\xuxiang-fs\waveledger.log", DateTime.Now.ToString("HH:mm:ss.fff ") + m + "\r\n"); } catch { } };
        // 哨兵：监锚仓。凡非我方之笔动锚册/锚包者，账上留痕（哈希存证）。
        try
        {
            var slock = new object();
            Action<string> slog = m => { lock (slock) { try { File.AppendAllText(@"C:\yx\xuxiang-fs\sentinel.log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + m + "\r\n"); } catch { } } };
            var fsw = new FileSystemWatcher(@"D:\息衍区\虚相仓")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            Action<string, FileSystemEventArgs> snap = (kind, e) =>
            {
                var line = kind + " " + e.Name;
                try { var fi = new FileInfo(e.FullPath); if (fi.Exists) line += " size=" + fi.Length; } catch { }
                try
                {
                    if (File.Exists(e.FullPath))
                        using (var fs = new FileStream(e.FullPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
                        using (var sh = System.Security.Cryptography.SHA256.Create())
                            line += " sha16=" + BitConverter.ToString(sh.ComputeHash(fs)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
                    else line += " (无)";
                }
                catch { line += " sha16=?"; }
                slog(line);
            };
            fsw.Created += (s, e) => snap("生", e);
            fsw.Changed += (s, e) => snap("动", e);
            fsw.Deleted += (s, e) => snap("灭", e);
            fsw.Renamed += (s, e) => snap("易 " + e.OldName + " →", e);
            fsw.Error += (s, e) => slog("哨兵误：" + e.GetException().Message);
            fsw.EnableRaisingEvents = true;
            log("哨兵就位：监 " + fsw.Path);
        }
        catch (Exception e) { log("哨兵起不来：" + e.GetType().FullName + " " + e.Message); }
        try { XuFS.LoadAnchors(); log("锚册载入：" + XuFS.Count + " 锚"); }
        catch (Exception e)
        {
            log("锚册读不了：" + e.GetType().FullName + " " + e.Message);
            return 2;
        }
        var mountPoint = args.Length > 0 ? args[0] : @"V:\";
        if (mountPoint == "/u")
        {
            var target = args.Length > 1 ? args[1] : @"V:\";
            using (var dokan = new Dokan(new NullLogger()))
            {
                var ok = dokan.RemoveMountPoint(target);
                log("卸载 " + target + "：" + ok);
                return ok ? 0 : 1;
            }
        }
        var op = new XuFS();
        try
        {
            using (var dokan = new Dokan(new NullLogger()))
            {
                log("Dokan 版本: " + dokan.Version + " / 驱动: " + dokan.DriverVersion);
                var builder = new DokanInstanceBuilder(dokan)
                    .ConfigureLogger(() => new NullLogger())
                    .ConfigureOptions(o =>
                    {
                        o.MountPoint = mountPoint;
                        o.Options = DokanOptions.WriteProtection | DokanOptions.MountManager;
                        o.TimeOut = TimeSpan.FromSeconds(10);
                        log("选项已配: " + mountPoint);
                    })
                    .Validate();
                log("validate 完");
                var instance = builder.Build(op);
                log("build 完，等待卸载信号…（yx-xuxiang-fs /u " + mountPoint + "）");
                var selfTest = new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(800);
                    try
                    {
                        var names = Directory.GetFiles(mountPoint).Select(Path.GetFileName).ToArray();
                        log("自检·根目录(" + names.Length + "): " + string.Join(", ", names));
                        var data = File.ReadAllBytes(Path.Combine(mountPoint, "shell32"));
                        using (var sha = SHA256.Create())
                            log("自检·shell32: " + data.Length + "B sha256=" +
                                BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant());
                    }
                    catch (Exception e2)
                    {
                        log("自检失败: " + e2.GetType().Name + " " + e2.Message);
                    }
                });
                selfTest.IsBackground = true;
                selfTest.Start();
                instance.WaitForFileSystemClosed(uint.MaxValue);
                log("盘已收");
            }
        }
        catch (Exception e)
        {
            var sb = new StringBuilder("=== MOUNT FAIL ===\r\n");
            try { sb.AppendLine("type: " + e.GetType().FullName); } catch (Exception x) { sb.AppendLine("type fail: " + x.Message); }
            try { sb.AppendLine("msg: " + e.Message); } catch (Exception x) { sb.AppendLine("msg fail: " + x.Message); }
            try { sb.AppendLine(e.StackTrace); } catch (Exception x) { sb.AppendLine("stack fail: " + x.Message); }
            try { sb.AppendLine(e.ToString()); } catch (Exception x) { sb.AppendLine("tostring fail: " + x.Message); }
            try { File.WriteAllText(@"C:\yx\xuxiang-fs\crash.log", sb.ToString()); } catch { }
            return 1;
        }
        return 0;
    }
}

/// <summary>PowerShell 宿主引导口：子 AppDomain 里经代理调 Main（绑定重定向由域的 ConfigurationFile 供）。</summary>
public class Boot : MarshalByRefObject
{
    public int Run(string[] args) { return Program.Main(args ?? new string[0]); }
}
