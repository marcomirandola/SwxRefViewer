using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwxRefViewer
{
    // Builds the reference tree of a file and decides for each reference where SOLIDWORKS would find it.
    // The search order is an approximation of the SOLIDWORKS search routine for referenced documents:
    //   1. a file with the same name that is already loaded (SOLIDWORKS can only load one file per name)
    //   2. the search folders from the options ("Referenced Documents" folders in SOLIDWORKS)
    //   3. the folder of the referencing file
    //   4. the path stored in the referencing file
    //   5. the stored sub folders, relative to the referencing file
    //   6. the folder of the top-level file
    public class Analyzer
    {
        readonly ReferenceReader reader;
        readonly List<string> searchFolders;
        readonly Action<string> progress;
        readonly Dictionary<string, string> loadedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (List<StoredReference> refs, string reader, string error)> cache =
            new Dictionary<string, (List<StoredReference>, string, string)>(StringComparer.OrdinalIgnoreCase);
        string rootFolder;

        public readonly List<DocNode> AllNodes = new List<DocNode>();

        public Analyzer(ReferenceReader reader, IEnumerable<string> searchFolders, Action<string> progress)
        {
            this.reader = reader;
            this.searchFolders = searchFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
            this.progress = progress;
        }

        public DocNode Analyze(string rootPath)
        {
            rootPath = Path.GetFullPath(rootPath);
            rootFolder = Path.GetDirectoryName(rootPath);
            loadedByName[Path.GetFileName(rootPath)] = rootPath;
            var root = new DocNode { Path = rootPath };
            Build(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return root;
        }

        void Build(DocNode node, HashSet<string> ancestors)
        {
            AllNodes.Add(node);
            progress?.Invoke(node.Path);

            if (!cache.TryGetValue(node.Path, out var entry))
            {
                try
                {
                    var refs = reader.Read(node.Path, out var readerName);
                    entry = (refs, readerName, null);
                }
                catch (Exception ex)
                {
                    entry = (new List<StoredReference>(), null, ex.Message);
                }
                cache[node.Path] = entry;
            }
            node.Reader = entry.reader;
            node.Error = entry.error;

            // The same stored path can come from several sources and configurations: show it once.
            var groups = entry.refs
                .GroupBy(r => r.StoredPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => new StoredReference
                {
                    StoredPath = g.First().StoredPath,
                    Source = string.Join(", ", g.Select(r => r.Source).Distinct()),
                    Configuration = string.Join(", ", g.Select(r => r.Configuration).Where(c => !string.IsNullOrEmpty(c)).Distinct()),
                    ComponentName = string.Join(", ", g.Select(r => r.ComponentName).Where(c => !string.IsNullOrEmpty(c)).Distinct().Take(5)),
                    Suppressed = g.Any(r => r.Source == "Component") && g.Where(r => r.Source == "Component").All(r => r.Suppressed),
                    Virtual = g.Any(r => r.Virtual),
                    Detail = string.Join("; ", g.Select(r => r.Detail).Where(d => !string.IsNullOrEmpty(d)).Distinct())
                })
                .ToList();

            foreach (var stored in groups)
                node.References.Add(Resolve(stored, node));

            ancestors.Add(node.Path);
            foreach (var r in node.References)
            {
                if (r.ResolvedPath == null || r.Status == RefStatus.Virtual) continue;
                var child = new DocNode { Path = r.ResolvedPath, Parent = node };
                r.Child = child;
                if (ancestors.Contains(child.Path))
                {
                    child.IsRecursion = true;
                    AllNodes.Add(child);
                    continue;
                }
                Build(child, ancestors);
            }
            ancestors.Remove(node.Path);
        }

        ResolvedReference Resolve(StoredReference stored, DocNode parent)
        {
            var result = new ResolvedReference { Stored = stored };
            result.FileName = SafeFileName(stored.StoredPath);

            if (stored.Virtual || result.FileName.Contains("^"))
            {
                result.Status = RefStatus.Virtual;
                result.ResolvedBy = "Virtual component, stored inside the assembly";
                return result;
            }

            var parentFolder = Path.GetDirectoryName(parent.Path);
            var steps = new List<(string label, string path)>();
            foreach (var folder in searchFolders)
                steps.Add(("Search folder " + folder, Combine(folder, result.FileName)));
            steps.Add(("Folder of the referencing file", Combine(parentFolder, result.FileName)));
            steps.Add(("Stored path", stored.StoredPath));
            var storedFolders = SafeFolderParts(stored.StoredPath);
            for (int take = 1; take <= storedFolders.Count; take++)
            {
                var relative = string.Join("\\", storedFolders.Skip(storedFolders.Count - take));
                steps.Add(("Relative to the referencing file", Combine(Combine(parentFolder, relative), result.FileName)));
            }
            steps.Add(("Folder of the top-level file", Combine(rootFolder, result.FileName)));

            string firstHit = null, firstHitBy = null;
            foreach (var step in steps)
            {
                if (step.path == null || !File.Exists(step.path)) continue;
                var full = Path.GetFullPath(step.path);
                if (!result.Candidates.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Candidates.Add(full);
                if (firstHit == null) { firstHit = full; firstHitBy = step.label; }
            }

            if (loadedByName.TryGetValue(result.FileName, out var loaded))
            {
                if (!result.Candidates.Contains(loaded, StringComparer.OrdinalIgnoreCase)) result.Candidates.Insert(0, loaded);
                result.ResolvedPath = loaded;
                result.ResolvedBy = "Already loaded (same file name)";
                if (SamePath(loaded, stored.StoredPath)) result.Status = RefStatus.Ok;
                else if (SamePath(loaded, firstHit)) { result.Status = RefStatus.Moved; result.ResolvedBy = firstHitBy; }
                else result.Status = RefStatus.NameClash;
            }
            else if (firstHit == null)
            {
                result.Status = RefStatus.Missing;
                result.ResolvedBy = "Not found";
            }
            else
            {
                result.ResolvedPath = firstHit;
                result.ResolvedBy = firstHitBy;
                result.Status = SamePath(firstHit, stored.StoredPath) ? RefStatus.Ok : RefStatus.Moved;
                loadedByName[result.FileName] = firstHit;
            }

            if (result.Candidates.Count > 1 && result.Status != RefStatus.NameClash)
                result.Status = RefStatus.Ambiguous;
            return result;
        }

        static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        static string Combine(string folder, string name)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) return null;
            try { return Path.Combine(folder, name); } catch { return null; }
        }

        public static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        // Folder names of a stored path, without drive or server share.
        static List<string> SafeFolderParts(string path)
        {
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0) return parts;
            parts.RemoveAt(parts.Count - 1); // file name
            if (path.StartsWith(@"\\") && parts.Count >= 2) parts.RemoveRange(0, 2); // \\server\share
            else if (parts.Count > 0 && parts[0].EndsWith(":")) parts.RemoveAt(0); // C:
            return parts;
        }

        public static string StatusText(RefStatus status)
        {
            switch (status)
            {
                case RefStatus.Ok: return "OK";
                case RefStatus.Moved: return "Found elsewhere";
                case RefStatus.Ambiguous: return "Several files with this name";
                case RefStatus.NameClash: return "Other file with same name already loaded";
                case RefStatus.Missing: return "Not found";
                case RefStatus.Virtual: return "Virtual";
                default: return status.ToString();
            }
        }
    }
}
