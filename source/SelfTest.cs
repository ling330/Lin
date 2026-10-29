using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace QQBatchDecoder {
    public static class SelfTest {
        public class Vector { public string KeyB64, PlainB64, EncryptedB64; public long Offset; }
        public class Derivation { public string EKey, KeyB64; }
        public class Fixture { public List<Vector> Vectors; public List<Derivation> Derivations; }
        static void Check(bool condition, string name) { if (!condition) throw new Exception("测试失败：" + name); }
        public static int Run(string[] args) {
            string reportPath = args[Array.IndexOf(args, "--report") + 1];
            var passed = new List<string>();
            try {
                string fixturePath = args[Array.IndexOf(args, "--vectors") + 1];
                string dir = args[Array.IndexOf(args, "--test-dir") + 1]; Directory.CreateDirectory(dir);
                var fixture = Util.Json().Deserialize<Fixture>(File.ReadAllText(fixturePath));
                foreach (var v in fixture.Vectors) {
                    byte[] key = Convert.FromBase64String(v.KeyB64), expected = Convert.FromBase64String(v.PlainB64);
                    byte[] data = Convert.FromBase64String(v.EncryptedB64); var cipher = new Cipher(key);
                    cipher.Decrypt(data, data.Length, v.Offset); Check(data.SequenceEqual(expected), "cipher offset " + v.Offset);
                    var input = Convert.FromBase64String(v.EncryptedB64); var streamed = new List<byte>();
                    for (int off = 0; off < input.Length; off += 257) {
                        byte[] piece = input.Skip(off).Take(257).ToArray(); cipher.Decrypt(piece, piece.Length, v.Offset + off); streamed.AddRange(piece);
                    }
                    Check(streamed.SequenceEqual(expected), "streamed offset " + v.Offset);
                } passed.Add("RC4 / Map reference vectors + non-aligned streaming: " + fixture.Vectors.Count);
                foreach (var d in fixture.Derivations) Check(Cipher.Derive(d.EKey).SequenceEqual(Convert.FromBase64String(d.KeyB64)), "key derivation");
                passed.Add("Raw / EncV1 / EncV2 derivation: " + fixture.Derivations.Count);
                string path = Path.Combine(dir, "尾部测试.mflac"); var payload = Enumerable.Range(0, 12345).Select(i => (byte)(i & 255)).ToArray(); var footer = new byte[192];
                Buffer.BlockCopy(Encoding.Unicode.GetBytes("003elgGQ3c1uQN"), 0, footer, 12, 28);
                byte[] name = Encoding.Unicode.GetBytes("AIM0003YXrHG1jTKRP.mflac"); Buffer.BlockCopy(name, 0, footer, 72, name.Length);
                Buffer.BlockCopy(BitConverter.GetBytes((uint)192), 0, footer, 176, 4); Buffer.BlockCopy(BitConverter.GetBytes((uint)1), 0, footer, 180, 4);
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("musicex\0"), 0, footer, 184, 8);
                File.WriteAllBytes(path, payload.Concat(footer).ToArray()); var meta = MusicEx.Parse(path);
                Check(meta.AudioSize == payload.Length && meta.Mid == "003elgGQ3c1uQN" && meta.FileName == "AIM0003YXrHG1jTKRP.mflac", "footer size includes descriptor");
                passed.Add("192-byte musicex footer preserves final 16 audio bytes");
                string token = "abcdefghijklmnopqrstuvwxyz0123456789";
                foreach (string text in new[] { "{\"authst\":\"" + token + "\"}", "{\\\"authst\\\":\\\"" + token + "\\\"}", "authst=" + token + "&a=1", "qqmusic_key=" + token + ";" })
                    Check(Session.ExtractTokens(text).Contains(token), "session format");
                passed.Add("Four scoped QQ Music session formats");
                Check(Util.Quote("C:\\歌曲 文件\\test.flac") == "\"C:\\歌曲 文件\\test.flac\"", "path quoting");
                Check(Util.Quote("C:\\folder\\") == "\"C:\\folder\\\\\"", "trailing slash quoting");
                passed.Add("Windows Unicode / space / trailing-slash paths");
                string nested = Path.Combine(dir, "嵌套目录"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "坏文件.mgg"), "bad");
                var collected = Util.Collect(new[] { dir, path }, true, CancellationToken.None);
                Check(collected.Count == 2 && collected.Single(j => j.Source.EndsWith(".mgg")).RelativeDirectory == "嵌套目录", "recursive folder collection");
                passed.Add("Recursive collection, preserved subfolders and deduplication");
                string invalid = Path.Combine(dir, "损坏.mflac"); File.WriteAllText(invalid, "not an encrypted song");
                var jobs = Util.Collect(new[] { invalid, Path.Combine(nested, "坏文件.mgg") }, true, CancellationToken.None);
                string outDir = Path.Combine(dir, "invalid-results");
                var report = Batch.Run(jobs, new Options { OutputDirectory = outDir, Verify = false }, (i, s, p) => { }, CancellationToken.None);
                Check(report.Failed == 2 && report.Files.All(j => j.Output == "") && !Directory.GetFiles(outDir, "*.part", SearchOption.AllDirectories).Any(), "failed files continue safely");
                passed.Add("Invalid files fail independently without partial output");
                var stop = new CancellationTokenSource(); stop.Cancel();
                report = Batch.Run(jobs, new Options { OutputDirectory = outDir, Verify = false }, (i, s, p) => { }, stop.Token);
                Check(report.Cancelled, "cancellation"); passed.Add("Cancellation before processing");
                Util.WriteJson(reportPath, new { Passed = true, Checks = passed }); return 0;
            } catch (Exception e) { Util.WriteJson(reportPath, new { Passed = false, Checks = passed, Error = e.ToString() }); return 1; }
        }
    }
}
