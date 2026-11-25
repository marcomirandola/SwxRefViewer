using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace SwxRefViewer.Tests
{
    // These tests write fake SOLIDWORKS files that only contain path strings, and read them with the raw scan.
    // They use Windows paths and run on Windows (CI).
    public class AnalyzerTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "SwxRefViewerTests", Guid.NewGuid().ToString("N"));

        public AnalyzerTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        string FakeFile(string relativePath, params string[] storedPaths)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path))
            {
                stream.Write(new byte[] { 1, 2, 3, 0, 0 }, 0, 5);
                foreach (var stored in storedPaths)
                {
                    var bytes = Encoding.Unicode.GetBytes(stored);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Write(new byte[] { 0, 0, 7, 0 }, 0, 4);
                }
            }
            return path;
        }

        static DocNode Analyze(string file, params string[] searchFolders)
        {
            using (var reader = new ReferenceReader(null, true, _ => { }))
                return new Analyzer(reader, searchFolders, null).Analyze(file);
        }

        [Fact]
        public void RawScan_finds_utf16_and_ansi_paths()
        {
            var path = Path.Combine(root, "scan.sldasm");
            var content = new List<byte> { 0, 1, 2 };
            content.AddRange(Encoding.Unicode.GetBytes(@"C:\Work\Parts\Bolt.SLDPRT"));
            content.AddRange(new byte[] { 0, 0, 9 });
            content.AddRange(Encoding.ASCII.GetBytes(@"\\server\share\Assy\Frame.sldasm"));
            content.AddRange(new byte[] { 0, 5 });
            content.AddRange(Encoding.Unicode.GetBytes(path)); // own path is ignored
            File.WriteAllBytes(path, content.ToArray());

            using (var reader = new ReferenceReader(null, true, _ => { }))
            {
                var refs = reader.Read(path, out var readerName).Select(r => r.StoredPath).ToList();
                Assert.Equal("Raw scan", readerName);
                Assert.Contains(@"C:\Work\Parts\Bolt.SLDPRT", refs);
                Assert.Contains(@"\\server\share\Assy\Frame.sldasm", refs);
                Assert.Equal(2, refs.Count);
            }
        }

        [Fact]
        public void RawScan_finds_paths_in_compressed_sections()
        {
            var text = Encoding.Unicode.GetBytes(@"D:\Lib\Washer.sldprt");
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                    d.Write(text, 0, text.Length);
                compressed = ms.ToArray();
            }
            var name = Encoding.ASCII.GetBytes("Config-0-ResolvedFeatures");
            var file = new List<byte> { 9, 9, 9 };
            file.AddRange(new byte[] { 0x14, 0, 6, 0, 8, 0 });
            file.AddRange(BitConverter.GetBytes(compressed.Length));
            file.AddRange(BitConverter.GetBytes(text.Length));
            file.AddRange(BitConverter.GetBytes(name.Length));
            file.AddRange(name);
            file.AddRange(compressed);
            var path = Path.Combine(root, "packed.sldasm");
            File.WriteAllBytes(path, file.ToArray());

            using (var reader = new ReferenceReader(null, true, _ => { }))
            {
                var refs = reader.Read(path, out _);
                Assert.Equal(@"D:\Lib\Washer.sldprt", Assert.Single(refs).StoredPath);
            }
        }

        [Fact]
        public void Reference_at_stored_path_is_ok()
        {
            var part = FakeFile(@"A\part.sldprt");
            var top = FakeFile(@"A\top.sldasm", part);

            var node = Analyze(top);

            var r = Assert.Single(node.References);
            Assert.Equal(RefStatus.Ok, r.Status);
            Assert.Equal(part, r.ResolvedPath, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(r.Child);
        }

        [Fact]
        public void Missing_file_is_reported()
        {
            var top = FakeFile(@"A\top.sldasm", @"Z:\does\not\exist\gone.sldprt");

            var r = Assert.Single(Analyze(top).References);

            Assert.Equal(RefStatus.Missing, r.Status);
            Assert.Null(r.ResolvedPath);
            Assert.Null(r.Child);
        }

        [Fact]
        public void File_moved_next_to_referencing_file_is_found()
        {
            var part = FakeFile(@"A\part.sldprt");
            var top = FakeFile(@"A\top.sldasm", @"Z:\old\location\part.sldprt");

            var r = Assert.Single(Analyze(top).References);

            Assert.Equal(RefStatus.Moved, r.Status);
            Assert.Equal(part, r.ResolvedPath, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Same_name_loaded_earlier_wins_in_sub_assembly()
        {
            // top references x\part.sldprt first, then sub; sub references y\part.sldprt.
            // SOLIDWORKS keeps the first part.sldprt in memory, so sub gets the wrong one.
            var partX = FakeFile(@"A\x\part.sldprt");
            var partY = FakeFile(@"A\y\part.sldprt");
            var sub = FakeFile(@"A\sub\sub.sldasm", partY);
            var top = FakeFile(@"A\top.sldasm", partX, sub);

            var node = Analyze(top);

            var subRef = node.References.Single(r => r.FileName == "sub.sldasm");
            var inSub = Assert.Single(subRef.Child.References);
            Assert.Equal(RefStatus.NameClash, inSub.Status);
            Assert.Equal(partX, inSub.ResolvedPath, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(partY, inSub.Candidates, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Circular_reference_does_not_loop()
        {
            var a = Path.Combine(root, "a.sldasm");
            var b = Path.Combine(root, "b.sldasm");
            FakeFile("a.sldasm", b);
            FakeFile("b.sldasm", a);

            var node = Analyze(a);

            var child = Assert.Single(node.References).Child;
            Assert.True(Assert.Single(child.References).Child.IsRecursion);
        }
    }
}
