// QMC2 algorithms adapted from QM Unlock, MIT. See THIRD-PARTY-NOTICES.txt.
// Windows .NET Framework 4.8, C# 5. No third-party runtime required.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace QQBatchDecoder {
    public class Job {
        public string Source, RelativeDirectory = "", Output = "", Status = "等待", Detail = "";
        public bool Verified;
    }
    public class Options {
        public string OutputDirectory, FFmpeg;
        public bool Verify;
    }
    public class HistoryEntry {
        public long SourceSize, SourceTicks, OutputSize, OutputTicks;
        public string Output;
        public bool Verified;
    }
    public class Report {
        public string Started, Finished, Summary, ReportPath;
        public int Success, Skipped, Failed;
        public bool Cancelled;
        public List<Job> Files = new List<Job>();
    }
    public static class Util {
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        public static bool Supported(string p) {
            return new[] { ".mflac", ".mgg", ".mmp4" }.Contains(Path.GetExtension(p).ToLowerInvariant());
        }
        public static string Quote(string s) {
            var b = new StringBuilder("\""); int slash = 0;
            foreach (char c in s) {
                if (c == '\\') { slash++; continue; }
                b.Append('\\', c == '"' ? slash * 2 + 1 : slash); b.Append(c); slash = 0;
            }
            b.Append('\\', slash * 2); b.Append('"'); return b.ToString();
        }
        public static void WriteJson(string path, object data) {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temp, Json().Serialize(data), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static List<Job> Collect(IEnumerable<string> paths, bool recursive, CancellationToken cancel) {
            var jobs = new List<Job>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths) {
                cancel.ThrowIfCancellationRequested();
                string p = Path.GetFullPath(raw);
                if (File.Exists(p)) { Add(jobs, seen, p, ""); continue; }
                if (!Directory.Exists(p)) throw new IOException("找不到文件或文件夹：" + p);
                string root = p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var stack = new Stack<string>(); stack.Push(p);
                while (stack.Count > 0) {
                    cancel.ThrowIfCancellationRequested(); string dir = stack.Pop();
                    foreach (string f in Directory.GetFiles(dir)) {
                        string relative = Path.GetDirectoryName(f).Substring(root.Length - 1).TrimStart(Path.DirectorySeparatorChar);
                        Add(jobs, seen, f, relative);
                    }
                    if (recursive) foreach (string child in Directory.GetDirectories(dir))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) stack.Push(child);
                }
            }
            return jobs;
        }
        private static void Add(List<Job> jobs, HashSet<string> seen, string f, string relative) {
            if (Supported(f) && seen.Add(f)) jobs.Add(new Job { Source = f, RelativeDirectory = relative });
            if (jobs.Count > 10000) throw new IOException("单次最多添加 10,000 首，请选择更小的文件夹。");
        }
        public static string FindFFmpeg() {
            var candidates = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe") };
            foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (!String.IsNullOrWhiteSpace(p)) try { candidates.Add(Path.Combine(p.Trim('"'), "ffmpeg.exe")); } catch { }
            string jianying = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JianyingPro", "Apps");
            if (Directory.Exists(jianying)) try {
                foreach (string version in Directory.GetDirectories(jianying).OrderByDescending(x => x)) candidates.Add(Path.Combine(version, "ffmpeg.exe"));
            } catch { }
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }
        public static string Error(Exception e) {
            if (e is OperationCanceledException) return "已停止";
            if (e is UnauthorizedAccessException) return "没有文件或进程访问权限，请检查输出目录，并让工具与 QQ 音乐以相同权限运行。";
            if (e is WebException) return "连接 QQ 音乐服务失败，请检查网络及 QQ 音乐登录状态。";
            return e.Message;
        }
    }

    public class MusicEx {
        public string Mid, FileName, ExpectedFormat;
        public long AudioSize;
        public static MusicEx Parse(string path) {
            using (var f = File.OpenRead(path)) {
                if (f.Length < 192) throw new IOException("文件过小或下载尚未完成。");
                var end = new byte[16]; f.Seek(-16, SeekOrigin.End); Read(f, end, 16);
                if (Encoding.ASCII.GetString(end, 8, 8) != "musicex\0")
                    throw new IOException("不是受支持的 musicex V1 文件；旧版 QMC / NCM / KGM 格式不支持。");
                uint size = BitConverter.ToUInt32(end, 0), version = BitConverter.ToUInt32(end, 4);
                if (version != 1 || size < 192 || size > 1048576 || size >= f.Length)
                    throw new IOException("文件尾信息无效，或格式版本暂不支持。");
                // size INCLUDES the final 16-byte descriptor. Do not subtract it twice.
                f.Position = f.Length - size;
                byte[] footer = new byte[size]; Read(f, footer, footer.Length);
                string mid = Encoding.Unicode.GetString(footer, 12, 60).TrimEnd('\0');
                string name = Encoding.Unicode.GetString(footer, 72, 96).TrimEnd('\0');
                if (!Regex.IsMatch(mid, "^[a-zA-Z0-9]{14}$") || !Regex.IsMatch(name, @"^[a-zA-Z0-9]+\.m(flac|gg|mp4)$"))
                    throw new IOException("无法读取这首歌的资源信息。");
                string ext = Path.GetExtension(name).ToLowerInvariant();
                return new MusicEx { Mid = mid, FileName = name, AudioSize = f.Length - size,
                    ExpectedFormat = ext == ".mflac" ? "flac" : ext == ".mgg" ? "ogg" : "m4a" };
            }
        }
        public static void Read(Stream f, byte[] buf, int count) {
            int got = 0; while (got < count) { int n = f.Read(buf, got, count - got); if (n == 0) throw new EndOfStreamException("文件内容不完整。"); got += n; }
        }
    }

    public sealed class Cipher {
        readonly byte[] key, box;
        readonly uint hash;
        public Cipher(byte[] value) {
            key = value; int n = key.Length;
            if (n < 8 || n > 8192) throw new IOException("解密密钥长度异常。");
            if (n <= 300) return;
            box = new byte[n]; for (int i = 0; i < n; i++) box[i] = (byte)(i & 255);
            int j = 0; for (int i = 0; i < n; i++) { j = (j + box[i] + key[i]) % n; Swap(box, i, j); }
            uint h = 1; foreach (byte v in key) {
                if (v == 0) continue; uint next = unchecked(h * v);
                if (next == 0 || next <= h) break; h = next;
            } hash = h;
        }
        static void Swap(byte[] b, int a, int c) { byte t = b[a]; b[a] = b[c]; b[c] = t; }
        int Skip(long id) {
            int seed = key[(int)(id % key.Length)];
            return seed == 0 ? 0 : (int)((long)((double)hash / ((id + 1) * (double)seed) * 100.0) % key.Length);
        }
        public void Decrypt(byte[] data, int count, long offset) {
            int n = key.Length;
            if (n <= 300) {
                for (int i = 0; i < count; i++) {
                    long off = offset + i; if (off > 32767) off %= 32767;
                    int idx = (int)((off * off + 71214) % n), shift = ((idx & 7) + 4) & 7;
                    data[i] ^= (byte)((key[idx] << shift) | (key[idx] >> shift));
                } return;
            }
            int done = 0;
            while (done < count) {
                long at = offset + done;
                if (at < 128) {
                    int length = Math.Min(128 - (int)at, count - done);
                    for (int i = 0; i < length; i++) data[done + i] ^= key[Skip(at + i)];
                    done += length; continue;
                }
                int amount = Math.Min(5120 - (int)(at % 5120), count - done);
                byte[] b = (byte[])box.Clone(); int j = 0, k = 0, skip = (int)(at % 5120) + Skip(at / 5120);
                for (int step = 0; step < skip + amount; step++) {
                    j = (j + 1) % n; k = (k + b[j]) % n; Swap(b, j, k);
                    if (step >= skip) data[done + step - skip] ^= b[(b[j] + b[k]) % n];
                } done += amount;
            }
        }
        public static string Format(byte[] b) {
            if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "fLaC") return "flac";
            if (b.Length >= 4 && Encoding.ASCII.GetString(b, 0, 4) == "OggS") return "ogg";
            if (b.Length >= 8 && Encoding.ASCII.GetString(b, 4, 4) == "ftyp") return "m4a";
            return "";
        }
        static uint BE(byte[] b, int o) { return (uint)b[o] << 24 | (uint)b[o + 1] << 16 | (uint)b[o + 2] << 8 | b[o + 3]; }
        static void Put(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static byte[] Block(byte[] b, byte[] key) {
            uint a = BE(b, 0), c = BE(b, 4), sum = 0xe3779b90;
            uint k0 = BE(key, 0), k1 = BE(key, 4), k2 = BE(key, 8), k3 = BE(key, 12);
            unchecked { for (int i = 0; i < 16; i++) {
                c -= ((a << 4) + k2) ^ (a + sum) ^ ((a >> 5) + k3);
                a -= ((c << 4) + k0) ^ (c + sum) ^ ((c >> 5) + k1);
                sum -= 0x9e3779b9;
            } } byte[] outB = new byte[8]; Put(outB, 0, a); Put(outB, 4, c); return outB;
        }
        sealed class TeaReader {
            readonly byte[] data, key;
            byte[] previous = new byte[8], current, block;
            int input = 8, pos;
            public int Length;
            public TeaReader(byte[] value, byte[] k) {
                data = value; key = k;
                if (data.Length < 16 || data.Length % 8 != 0) throw new FormatException();
                current = data.Take(8).ToArray(); block = Block(current, key);
                int padding = block[0] & 7; Length = data.Length - 10 - padding; pos = 1 + padding;
                if (Length < 0) throw new FormatException(); Next(); Next();
            }
            public byte Next() {
                if (pos == 8) {
                    if (input + 8 > data.Length) throw new FormatException();
                    previous = current; current = data.Skip(input).Take(8).ToArray();
                    for (int i = 0; i < 8; i++) block[i] ^= current[i];
                    block = Block(block, key); input += 8; pos = 0;
                } byte value = (byte)(block[pos] ^ previous[pos]); pos++; return value;
            }
        }
        public static byte[] Tea(byte[] data, byte[] key) {
            var r = new TeaReader(data, key); byte[] value = new byte[r.Length];
            for (int i = 0; i < value.Length; i++) value[i] = r.Next();
            for (int i = 0; i < 7; i++) if (r.Next() != 0) throw new FormatException();
            return value;
        }
        public static byte[] Derive(string encoded) {
            byte[] b = Convert.FromBase64String(encoded.Trim());
            byte[] prefix = Encoding.ASCII.GetBytes("QQMusic EncV2,Key:");
            if (b.Take(prefix.Length).SequenceEqual(prefix)) {
                b = Tea(b.Skip(prefix.Length).ToArray(), Encoding.ASCII.GetBytes("386ZJY!@#*$%^&)("));
                b = Tea(b, Encoding.ASCII.GetBytes("**#!(#$%&^a1cZ,T"));
                b = Convert.FromBase64String(Encoding.ASCII.GetString(b).Trim());
            }
            if (b.Length < 8) throw new IOException("服务返回了无效的密钥。");
            byte[] simple = { 0x69, 0x56, 0x46, 0x38, 0x2b, 0x20, 0x15, 0x0b }, tk = new byte[16];
            for (int i = 0; i < 8; i++) { tk[2 * i] = simple[i]; tk[2 * i + 1] = b[i]; }
            try { return b.Take(8).Concat(Tea(b.Skip(8).ToArray(), tk)).ToArray(); }
            catch (FormatException) { return b; }
        }
    }

    public sealed class Session : IDisposable {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MBI info, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
        [StructLayout(LayoutKind.Sequential)] struct MBI {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }
        static readonly Regex[] Patterns = {
            new Regex("\"authst\"\\s*:\\s*\"([A-Za-z0-9_=-]{21,})", RegexOptions.Compiled),
            new Regex("\\\\\"authst\\\\\"\\s*:\\s*\\\\\"([A-Za-z0-9_=-]{21,})", RegexOptions.Compiled),
            new Regex("authst=([A-Za-z0-9_=-]{21,})", RegexOptions.Compiled),
            new Regex("qqmusic_key=([A-Za-z0-9_=-]{21,})", RegexOptions.Compiled)
        };
        string token, loginType, uin;
        readonly Dictionary<string, byte[]> keys = new Dictionary<string, byte[]>();
        public static IEnumerable<string> ExtractTokens(string text) {
            foreach (Regex p in Patterns) foreach (Match m in p.Matches(text)) yield return m.Groups[1].Value;
        }
        IEnumerable<string> Scan(CancellationToken cancel) {
            var seen = new HashSet<string>(); var watch = Stopwatch.StartNew();
            var processes = Process.GetProcessesByName("QQMusic");
            if (processes.Length == 0) throw new IOException("请先打开桌面版 QQ 音乐，登录能播放这些歌曲的账号，再重试。");
            bool opened = false;
            foreach (Process p in processes) {
                IntPtr handle = OpenProcess(0x410, false, p.Id);
                if (handle == IntPtr.Zero) continue; opened = true;
                try {
                    long address = 0; MBI info;
                    while (VirtualQueryEx(handle, new IntPtr(address), out info, (UIntPtr)Marshal.SizeOf(typeof(MBI))).ToUInt64() != 0) {
                        cancel.ThrowIfCancellationRequested();
                        if (watch.Elapsed.TotalSeconds > 25) yield break;
                        long size = (long)info.RegionSize.ToUInt64(), start = info.BaseAddress.ToInt64();
                        uint protect = info.Protect & 255;
                        if (info.State == 0x1000 && (info.Protect & 0x100) == 0 && new uint[] { 2, 4, 8, 0x20, 0x40, 0x80 }.Contains(protect)) {
                            byte[] carry = new byte[0];
                            for (long off = 0; off < size; off += 1024 * 1024) {
                                cancel.ThrowIfCancellationRequested();
                                if (watch.Elapsed.TotalSeconds > 25) yield break;
                                byte[] buf = new byte[(int)Math.Min(1024 * 1024, size - off)]; UIntPtr read;
                                if (!ReadProcessMemory(handle, new IntPtr(start + off), buf, (UIntPtr)buf.Length, out read) || read == UIntPtr.Zero) { carry = new byte[0]; continue; }
                                byte[] joined = new byte[carry.Length + (int)read.ToUInt64()];
                                Buffer.BlockCopy(carry, 0, joined, 0, carry.Length); Buffer.BlockCopy(buf, 0, joined, carry.Length, joined.Length - carry.Length);
                                string ascii = Encoding.ASCII.GetString(joined);
                                foreach (string value in ExtractTokens(ascii)) if (seen.Add(value)) yield return value;
                                string needle = "a\0u\0t\0h\0s\0t\0"; int pos = ascii.IndexOf(needle, StringComparison.Ordinal);
                                while (pos >= 0) {
                                    int from = Math.Max(0, pos - 4), length = Math.Min(2048, joined.Length - from) & ~1;
                                    string unicode = Encoding.Unicode.GetString(joined, from, length);
                                    foreach (string value in ExtractTokens(unicode)) if (seen.Add(value)) yield return value;
                                    pos = ascii.IndexOf(needle, pos + needle.Length, StringComparison.Ordinal);
                                }
                                carry = joined.Skip(Math.Max(0, joined.Length - 4096)).ToArray();
                            }
                        }
                        long next = start + size; if (next <= address) break; address = next;
                    }
                } finally { CloseHandle(handle); p.Dispose(); }
            }
            if (!opened) throw new IOException("无法读取 QQ 音乐。请让工具与 QQ 音乐以相同权限运行。");
        }
        static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        string Fetch(MusicEx meta, string auth, string type, CancellationToken cancel) {
            var payload = new {
                comm = new { authst = auth, ct = "19", cv = "1859", uin = uin, tmeLoginType = type },
                req_1 = new { module = "music.vkey.GetEVkey", method = "CgiGetEVkey", param = new {
                    filename = new[] { meta.FileName }, guid = "10000", songmid = new[] { meta.Mid },
                    songtype = new[] { 1 }, uin = uin, loginflag = 1, platform = "27", ctx = 1 } }
            };
            var request = (HttpWebRequest)WebRequest.Create("https://u.y.qq.com/cgi-bin/musicu.fcg");
            request.Method = "POST"; request.ContentType = "application/json"; request.UserAgent = "QQMusic/22";
            request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            byte[] body = Encoding.UTF8.GetBytes(Util.Json().Serialize(payload)); request.ContentLength = body.Length;
            using (cancel.Register(request.Abort)) {
                cancel.ThrowIfCancellationRequested();
                using (var s = request.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) {
                    var root = Dict(Util.Json().DeserializeObject(reader.ReadToEnd())); object section, data, list;
                    if (!root.TryGetValue("req_1", out section) || !Dict(section).TryGetValue("data", out data) || !Dict(data).TryGetValue("midurlinfo", out list)) return null;
                    foreach (object row in list as IEnumerable ?? new object[0]) {
                        object value; if (Dict(row).TryGetValue("ekey", out value) && !String.IsNullOrWhiteSpace(value as string)) return value as string;
                    } return null;
                }
            }
        }
        byte[] TryKey(MusicEx meta, byte[] sample, string auth, string type, CancellationToken cancel) {
            string encoded = Fetch(meta, auth, type, cancel); if (encoded == null) return null;
            byte[] key = Cipher.Derive(encoded), plain = (byte[])sample.Clone(); new Cipher(key).Decrypt(plain, plain.Length, 0);
            if (Cipher.Format(plain) == meta.ExpectedFormat) return key;
            Array.Clear(key, 0, key.Length); return null;
        }
        public byte[] Key(MusicEx meta, byte[] sample, CancellationToken cancel) {
            string id = meta.Mid + "|" + meta.FileName; byte[] key;
            if (keys.TryGetValue(id, out key)) return key;
            if (uin == null) {
                string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "QQMusic", "QQMusicServiceConfig.ini");
                if (!File.Exists(config)) throw new IOException("未找到 QQ 音乐登录信息，请先打开并登录桌面版 QQ 音乐。");
                var m = Regex.Match(File.ReadAllText(config), @"(?m)^Uin=(\S+)");
                if (!m.Success) throw new IOException("QQ 音乐未登录，请登录后重试。"); uin = m.Groups[1].Value;
            }
            if (token != null) {
                key = TryKey(meta, sample, token, loginType, cancel);
                if (key != null) { keys[id] = key; return key; }
            }
            int tried = 0;
            foreach (string candidate in Scan(cancel)) {
                foreach (string type in new[] { "3", "1" }) {
                    cancel.ThrowIfCancellationRequested();
                    key = TryKey(meta, sample, candidate, type, cancel);
                    if (key != null) { token = candidate; loginType = type; keys[id] = key; return key; }
                }
                if (++tried >= 8) break;
            }
            throw new IOException("QQ 音乐未返回可用密钥。请确认当前账号能完整播放此歌，并重新登录后重试。");
        }
        public void Dispose() { foreach (byte[] key in keys.Values) Array.Clear(key, 0, key.Length); keys.Clear(); token = null; uin = null; }
    }

    public static class Verification {
        public static string Validate(string path, string ffmpeg, CancellationToken cancel) {
            string expected = null, codec = null;
            using (var f = File.OpenRead(path)) {
                byte[] header = new byte[Math.Min(42, (int)Math.Min(f.Length, 42))]; MusicEx.Read(f, header, header.Length);
                if (Cipher.Format(header) == "") throw new IOException("输出不是有效的音频文件。");
                if (header.Length == 42 && Cipher.Format(header) == "flac" && (header[4] & 127) == 0) {
                    int bits = ((header[20] & 1) << 4 | header[21] >> 4) + 1;
                    if (new[] { 8, 16, 24, 32 }.Contains(bits)) {
                        codec = bits == 8 ? "pcm_s8" : "pcm_s" + bits + "le";
                        expected = BitConverter.ToString(header, 26, 16).Replace("-", "").ToLowerInvariant();
                        if (expected.All(c => c == '0')) expected = null;
                    }
                }
            }
            if (String.IsNullOrEmpty(ffmpeg)) return "音频头校验通过";
            string args = "-hide_banner -v error -xerror -nostdin -i " + Util.Quote(path) + " -map 0:a:0 " +
                (expected != null ? "-c:a " + codec + " -f hash -hash md5 -" : "-f null -");
            var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)) {
                var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
                while (!p.WaitForExit(150)) if (cancel.IsCancellationRequested) { try { p.Kill(); } catch { } cancel.ThrowIfCancellationRequested(); }
                string output = stdout.Result; string error = stderr.Result;
                if (p.ExitCode != 0) throw new IOException("完整性校验失败：音频内容不完整或无法解码。" +
                    (error.Length == 0 ? "" : " " + error.Split('\n')[0].Trim().Substring(0, Math.Min(200, error.Split('\n')[0].Trim().Length))));
                if (expected != null) {
                    var match = Regex.Match(output, "MD5=([a-fA-F0-9]{32})");
                    if (!match.Success || !String.Equals(match.Groups[1].Value, expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("FLAC 无损校验不一致，未保存此输出。");
                    return "完整音频 + FLAC MD5 校验通过";
                }
                return "完整音频解码校验通过";
            }
        }
    }

    public static class Batch {
        public static Report Run(List<Job> jobs, Options options, Action<int, string, int> progress, CancellationToken cancel) {
            if (options.Verify && !File.Exists(options.FFmpeg)) throw new IOException("完整性校验需要有效的 FFmpeg 程序。");
            Directory.CreateDirectory(options.OutputDirectory);
            string historyPath = Path.Combine(options.OutputDirectory, ".qqdecode-history.json");
            var history = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(historyPath)) try {
                var previous = Util.Json().Deserialize<Dictionary<string, HistoryEntry>>(File.ReadAllText(historyPath));
                if (previous != null) foreach (var entry in previous) history[entry.Key] = entry.Value;
            } catch { }
            var report = new Report { Started = DateTimeOffset.Now.ToString("o"), Files = jobs };
            using (var session = new Session()) for (int i = 0; i < jobs.Count; i++) {
                Job job = jobs[i]; string partial = null; long sourceSize = 0, sourceTicks = 0;
                job.Output = ""; job.Verified = false; job.Detail = "";
                try {
                    cancel.ThrowIfCancellationRequested(); progress(i, "检查中", 0);
                    var source = new FileInfo(job.Source); sourceSize = source.Length; sourceTicks = source.LastWriteTimeUtc.Ticks;
                    HistoryEntry old;
                    if (history.TryGetValue(job.Source, out old) && old.SourceSize == sourceSize && old.SourceTicks == sourceTicks && File.Exists(old.Output)) {
                        var output = new FileInfo(old.Output);
                        if (output.Length == old.OutputSize && output.LastWriteTimeUtc.Ticks == old.OutputTicks) {
                            if (options.Verify && !old.Verified) { progress(i, "校验已有文件", 95); Verification.Validate(old.Output, options.FFmpeg, cancel); old.Verified = true; Util.WriteJson(historyPath, history); }
                            job.Output = old.Output; job.Verified = old.Verified; job.Status = "跳过"; job.Detail = "此源文件已成功处理，已有输出保持完整。";
                            report.Skipped++; progress(i, job.Status, 100); continue;
                        }
                    }
                    var meta = MusicEx.Parse(job.Source); byte[] sample = new byte[Math.Min(8192, (int)Math.Min(meta.AudioSize, 8192))];
                    using (var f = File.OpenRead(job.Source)) MusicEx.Read(f, sample, sample.Length);
                    progress(i, "获取密钥", 0); byte[] key = session.Key(meta, sample, cancel);
                    string dir = Path.GetFullPath(Path.Combine(options.OutputDirectory, job.RelativeDirectory)); Directory.CreateDirectory(dir);
                    string name = Path.GetFileNameWithoutExtension(job.Source), target = Path.Combine(dir, name + "." + meta.ExpectedFormat);
                    partial = Path.Combine(dir, ".qqdecode-" + Guid.NewGuid().ToString("N") + ".part");
                    using (var input = File.OpenRead(job.Source)) using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write)) {
                        var cipher = new Cipher(key); byte[] buffer = new byte[5120 * 64]; long offset = 0; var watch = Stopwatch.StartNew();
                        while (offset < meta.AudioSize) {
                            cancel.ThrowIfCancellationRequested(); int count = (int)Math.Min(buffer.Length, meta.AudioSize - offset);
                            MusicEx.Read(input, buffer, count); cipher.Decrypt(buffer, count, offset); output.Write(buffer, 0, count); offset += count;
                            if (watch.ElapsedMilliseconds >= 100 || offset == meta.AudioSize) { progress(i, "解码中", (int)(offset * 90 / meta.AudioSize)); watch.Restart(); }
                        }
                        output.Flush(true);
                    }
                    source.Refresh(); if (source.Length != sourceSize || source.LastWriteTimeUtc.Ticks != sourceTicks) throw new IOException("解码期间源文件发生变化，请等下载完成后重试。");
                    progress(i, options.Verify ? "完整性校验" : "检查音频头", 95);
                    string verified = Verification.Validate(partial, options.Verify ? options.FFmpeg : "", cancel);
                    cancel.ThrowIfCancellationRequested();
                    for (int suffix = 2; File.Exists(target) || Directory.Exists(target); suffix++) target = Path.Combine(dir, name + " (" + suffix + ")." + meta.ExpectedFormat);
                    File.Move(partial, target); partial = null;
                    job.Output = target; job.Verified = options.Verify; job.Status = "完成"; job.Detail = verified;
                    var completed = new FileInfo(target);
                    history[job.Source] = new HistoryEntry { SourceSize = sourceSize, SourceTicks = sourceTicks, Output = target,
                        OutputSize = completed.Length, OutputTicks = completed.LastWriteTimeUtc.Ticks, Verified = options.Verify };
                    try { Util.WriteJson(historyPath, history); } catch { job.Detail += "；处理记录保存失败，重复运行会自动编号。"; }
                    report.Success++; progress(i, job.Status, 100);
                } catch (Exception e) {
                    if (cancel.IsCancellationRequested || e is OperationCanceledException) { job.Status = "已停止"; job.Detail = "未完成的临时输出已清理。"; report.Cancelled = true; progress(i, job.Status, 0); break; }
                    job.Status = "失败"; job.Detail = Util.Error(e); report.Failed++; progress(i, job.Status, 100);
                } finally { if (partial != null) try { File.Delete(partial); } catch { } }
            }
            report.Finished = DateTimeOffset.Now.ToString("o");
            report.Summary = (report.Cancelled ? "已停止。" : "处理完成。") + "成功 " + report.Success + "，跳过 " + report.Skipped + "，失败 " + report.Failed + "。";
            report.ReportPath = Path.Combine(options.OutputDirectory, "解码报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".json");
            try { Util.WriteJson(report.ReportPath, report); } catch { report.Summary += " 报告保存失败。"; }
            return report;
        }
    }

    public class MainForm : Form {
        readonly ListView list = new ListView(); readonly TextBox output = new TextBox();
        readonly CheckBox recurse = new CheckBox(), verify = new CheckBox();
        readonly Label count = new Label(), state = new Label(), validator = new Label();
        readonly ProgressBar bar = new ProgressBar(); readonly List<Control> editable = new List<Control>();
        readonly List<Job> jobs = new List<Job>(); readonly Button start, stop;
        CancellationTokenSource cancellation; string ffmpeg; bool busy; Report lastReport;
        static readonly Color Ink = Color.FromArgb(25, 37, 54), Blue = Color.FromArgb(35, 92, 218), Muted = Color.FromArgb(99, 115, 134);
        public MainForm(string[] initial) {
            Text = "QQ 音乐批量解码工具"; ClientSize = new Size(1020, 735); MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 251); ForeColor = Ink; AllowDrop = true;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 9 };
            int[] heights = { 82, 45, 0, 30, 50, 60, 32, 26, 51 };
            foreach (int h in heights) root.RowStyles.Add(h == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
            Controls.Add(root);
            var hero = new Panel { Dock = DockStyle.Fill };
            hero.Controls.Add(new Label { Text = "把加密歌曲，变成可播放的音频", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), Location = new Point(0, 0), AutoSize = true });
            hero.Controls.Add(new Label { Text = "批量选择 · 保留原音质 · 自动跳过已完成歌曲", ForeColor = Muted, Location = new Point(2, 47), AutoSize = true }); root.Controls.Add(hero, 0, 0);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            AddButton(tools, "添加歌曲", ChooseFiles, true); AddButton(tools, "添加文件夹", ChooseFolder, true);
            AddButton(tools, "移除选中", RemoveSelected, true); AddButton(tools, "清空列表", delegate { list.Items.Clear(); jobs.Clear(); UpdateCount(); }, true);
            tools.Controls.Add(new Label { Text = "或将文件 / 文件夹拖到窗口", AutoSize = true, ForeColor = Muted, Margin = new Padding(16, 10, 0, 0) }); root.Controls.Add(tools, 0, 1);
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.GridLines = false;
            list.AccessibleName = "待解码歌曲队列";
            list.HideSelection = false; list.ShowItemToolTips = true; list.BorderStyle = BorderStyle.FixedSingle;
            list.Columns.Add("歌曲文件", 330); list.Columns.Add("格式", 75); list.Columns.Add("大小", 85); list.Columns.Add("状态", 135); list.Columns.Add("结果 / 输出位置", 300);
            list.DoubleClick += delegate { if (list.SelectedItems.Count > 0) { Job j = (Job)list.SelectedItems[0].Tag; if (File.Exists(j.Output)) Process.Start("explorer.exe", "/select," + Util.Quote(j.Output)); } };
            root.Controls.Add(list, 0, 2);
            count.Dock = DockStyle.Fill; count.TextAlign = ContentAlignment.MiddleLeft; count.ForeColor = Muted; root.Controls.Add(count, 0, 3);
            var destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            destination.Controls.Add(new Label { Text = "保存位置", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "QQ音乐已解码"); output.Dock = DockStyle.Fill; output.Margin = new Padding(0, 9, 10, 0); output.AccessibleName = "保存位置"; editable.Add(output);
            destination.Controls.Add(output, 1, 0); var browse = MakeButton("选择位置", BrowseOutput); browse.Dock = DockStyle.Top; browse.Margin = new Padding(0, 6, 0, 0); editable.Add(browse); destination.Controls.Add(browse, 2, 0); root.Controls.Add(destination, 0, 4);
            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            recurse.Text = "添加文件夹时包含子文件夹"; recurse.Checked = true; recurse.AutoSize = true; editable.Add(recurse); settings.Controls.Add(recurse, 0, 0);
            ffmpeg = Util.FindFFmpeg(); verify.Text = "完整性校验（推荐）"; verify.Checked = ffmpeg.Length > 0; verify.AutoSize = true; editable.Add(verify); settings.Controls.Add(verify, 1, 0);
            var note = new Label { Text = "支持新版 .mflac / .mgg / .mmp4", ForeColor = Muted, AutoSize = true }; settings.Controls.Add(note, 0, 1);
            var validatorRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            validator.AutoSize = true; validator.ForeColor = Muted; validator.Margin = new Padding(0, 3, 8, 0); validator.Text = ffmpeg.Length > 0 ? "校验程序已就绪" : "未找到 FFmpeg，可选配后进行完整校验"; validatorRow.Controls.Add(validator);
            var selectValidator = new LinkLabel { Text = "选择校验程序", AutoSize = true, Margin = new Padding(0, 3, 0, 0) }; selectValidator.Click += SelectValidator; editable.Add(selectValidator); validatorRow.Controls.Add(selectValidator); settings.Controls.Add(validatorRow, 1, 1); root.Controls.Add(settings, 0, 5);
            state.Dock = DockStyle.Fill; state.TextAlign = ContentAlignment.MiddleLeft; state.Text = "先登录桌面版 QQ 音乐，再添加歌曲开始解码。"; root.Controls.Add(state, 0, 6);
            bar.Dock = DockStyle.Top; bar.Height = 12; bar.Maximum = 1000; bar.AccessibleName = "总体进度"; root.Controls.Add(bar, 0, 7);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 374));
            footer.Controls.Add(new Label { Text = "保留源文件 · 同名自动编号\n双击已完成歌曲可定位文件", ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 3, 0, 0) }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            start = MakeButton("开始解码", StartBatch); start.BackColor = Blue; start.ForeColor = Color.White; start.FlatAppearance.BorderSize = 0; start.Width = 116;
            stop = MakeButton("停止", delegate { if (cancellation != null) cancellation.Cancel(); stop.Enabled = false; state.Text = "正在停止并清理临时输出…"; }); stop.Enabled = false; stop.Width = 78;
            var open = MakeButton("打开输出目录", delegate { try { if (Directory.Exists(output.Text)) Process.Start("explorer.exe", Util.Quote(output.Text)); } catch (Exception ex) { MessageBox.Show(this, Util.Error(ex)); } }); open.Width = 126;
            actions.Controls.Add(start); actions.Controls.Add(stop); actions.Controls.Add(open); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 8);
            DragEnter += delegate(object s, DragEventArgs e) { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object s, DragEventArgs e) { if (!busy) AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) { cancellation.Cancel(); e.Cancel = true; state.Text = "正在停止。处理结束后即可关闭。"; } };
            Shown += delegate { if (initial.Length > 0) AddPaths(initial); }; UpdateCount();
        }
        Button MakeButton(string text, EventHandler handler) {
            var b = new Button { Text = text, Height = 34, Width = 106, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(214, 221, 231); b.Click += handler; return b;
        }
        void AddButton(FlowLayoutPanel panel, string text, EventHandler handler, bool edit) { var b = MakeButton(text, handler); panel.Controls.Add(b); if (edit) editable.Add(b); }
        void ChooseFiles(object s, EventArgs e) { using (var dialog = new OpenFileDialog { Title = "选择待解码歌曲", Multiselect = true, Filter = "QQ 音乐加密歌曲|*.mflac;*.mgg;*.mmp4" }) if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames); }
        void ChooseFolder(object s, EventArgs e) { using (var dialog = new FolderBrowserDialog { Description = "选择 QQ 音乐歌曲文件夹" }) if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dialog.SelectedPath }); }
        void BrowseOutput(object s, EventArgs e) { using (var dialog = new FolderBrowserDialog { Description = "选择解码后歌曲的保存位置", SelectedPath = output.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath; }
        void SelectValidator(object s, EventArgs e) { using (var dialog = new OpenFileDialog { Title = "选择 FFmpeg 校验程序", Filter = "FFmpeg|ffmpeg*.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) { ffmpeg = dialog.FileName; verify.Checked = true; validator.Text = "校验程序已就绪"; } }
        async void AddPaths(string[] paths) {
            if (busy) return; SetBusy(true); bool recursive = recurse.Checked;
            try {
                var added = await Task.Run(() => Util.Collect(paths, recursive, CancellationToken.None));
                var seen = new HashSet<string>(jobs.Select(j => j.Source), StringComparer.OrdinalIgnoreCase);
                list.BeginUpdate(); foreach (Job job in added) if (seen.Add(job.Source)) {
                    jobs.Add(job); var fi = new FileInfo(job.Source);
                    var item = new ListViewItem(new[] { fi.Name, fi.Extension.TrimStart('.').ToUpperInvariant(), (fi.Length / 1048576.0).ToString("0.0") + " MB", job.Status, "" });
                    item.Tag = job; item.ToolTipText = job.Source; list.Items.Add(item);
                } list.EndUpdate(); state.Text = added.Count == 0 ? "没有找到支持的加密歌曲。" : "文件已加入队列，点击“开始解码”。";
            } catch (Exception ex) { state.Text = Util.Error(ex); } finally { SetBusy(false); UpdateCount(); }
        }
        void RemoveSelected(object s, EventArgs e) { foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToArray()) { jobs.Remove((Job)item.Tag); list.Items.Remove(item); } UpdateCount(); }
        void UpdateCount() { count.Text = "队列中有 " + jobs.Count + " 首歌曲"; start.Enabled = !busy && jobs.Count > 0; }
        void SetBusy(bool value) { busy = value; foreach (Control c in editable) c.Enabled = !value; start.Enabled = !value && jobs.Count > 0; stop.Enabled = value && cancellation != null; }
        async void StartBatch(object s, EventArgs e) {
            if (busy || jobs.Count == 0) return;
            if (String.IsNullOrWhiteSpace(output.Text)) { MessageBox.Show(this, "请选择保存位置。"); return; }
            if (verify.Checked && !File.Exists(ffmpeg)) { MessageBox.Show(this, "完整性校验需要 FFmpeg。可点击“选择校验程序”，或取消完整性校验后开始。"); return; }
            var options = new Options { OutputDirectory = Path.GetFullPath(output.Text), FFmpeg = ffmpeg, Verify = verify.Checked };
            cancellation = new CancellationTokenSource(); SetBusy(true); bar.Value = 0;
            foreach (ListViewItem item in list.Items) { Job j = (Job)item.Tag; j.Status = "等待"; j.Detail = ""; item.SubItems[3].Text = j.Status; item.SubItems[4].Text = ""; item.ForeColor = Ink; }
            var progress = new Progress<Tuple<int, string, int>>(p => {
                int index = p.Item1; string status = p.Item2; int percent = p.Item3;
                if (index >= list.Items.Count) return; var item = list.Items[index]; Job job = jobs[index];
                item.SubItems[3].Text = status + (percent > 0 && percent < 100 ? " " + percent + "%" : "");
                item.SubItems[4].Text = job.Status == "完成" || job.Status == "跳过" ? job.Output : job.Detail;
                item.ToolTipText = job.Source + "\n" + job.Detail;
                item.ForeColor = job.Status == "失败" ? Color.Firebrick : job.Status == "完成" || job.Status == "跳过" ? Color.FromArgb(28, 115, 83) : Ink;
                bar.Value = Math.Min(1000, (index * 1000 + percent * 10) / Math.Max(1, jobs.Count));
                state.Text = "[" + (index + 1) + "/" + jobs.Count + "] " + Path.GetFileName(job.Source) + " · " + status;
            });
            try {
                lastReport = await Task.Run(() => Batch.Run(jobs.ToList(), options, (i, t, p) => ((IProgress<Tuple<int, string, int>>)progress).Report(Tuple.Create(i, t, p)), cancellation.Token));
                state.Text = lastReport.Summary; if (!lastReport.Cancelled) bar.Value = 1000;
            } catch (Exception ex) { state.Text = Util.Error(ex); }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
        }
    }

    public static class Program {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread] public static int Main(string[] args) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Contains("--self-test")) return SelfTest.Run(args);
            if (args.Contains("--cli")) return Cli(args);
            SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { MessageBox.Show(Util.Error(e.Exception), "操作未完成"); };
            Application.Run(new MainForm(args.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray())); return 0;
        }
        static int Cli(string[] args) {
            string outDir = null, report = null, ffmpeg = Util.FindFFmpeg(); bool verify = true, recursive = true;
            var paths = new List<string>();
            try {
                for (int i = 0; i < args.Length; i++) {
                    string a = args[i];
                    if (a == "--output") outDir = args[++i]; else if (a == "--report") report = args[++i];
                    else if (a == "--ffmpeg") ffmpeg = args[++i]; else if (a == "--no-verify") verify = false;
                    else if (a == "--no-recursive") recursive = false; else if (a != "--cli") paths.Add(a);
                }
                if (paths.Count == 0 || outDir == null) throw new IOException("用法：QQBatchDecoder.exe --cli 文件或文件夹 --output 输出目录 [--no-verify]");
                if (verify && !File.Exists(ffmpeg)) throw new IOException("未找到 FFmpeg，请指定 --ffmpeg 或使用 --no-verify。");
                var jobs = Util.Collect(paths, recursive, CancellationToken.None);
                if (jobs.Count == 0) throw new IOException("没有找到支持的文件。");
                var result = Batch.Run(jobs, new Options { OutputDirectory = Path.GetFullPath(outDir), FFmpeg = ffmpeg, Verify = verify },
                    (i, s, p) => Console.WriteLine((i + 1) + "/" + jobs.Count + " " + s + " " + p + "%"), CancellationToken.None);
                if (report != null) Util.WriteJson(Path.GetFullPath(report), result);
                return result.Failed > 0 || result.Cancelled ? 1 : 0;
            } catch (Exception ex) {
                if (report != null) try { Util.WriteJson(Path.GetFullPath(report), new { Error = Util.Error(ex) }); } catch { }
                return 2;
            }
        }
    }
}
