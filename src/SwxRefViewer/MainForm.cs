using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SwxRefViewer
{
    public class MainForm : Form
    {
        class NodeTag
        {
            public DocNode Doc;
            public ResolvedReference Ref;
        }

        readonly Settings settings = Settings.Load();
        readonly ToolStrip toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        readonly TreeView tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
        readonly ListView refList = NewList("Status", 170, "File name", 180, "Stored path", 330, "Resolved path", 330,
                                            "Found by", 170, "Configurations", 120, "Read from", 150, "Notes", 200);
        readonly ListView conflictList = NewList("File name", 180, "Stored path", 330, "Resolved path", 330,
                                                 "Referenced by", 330, "Status", 170);
        readonly TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f) };
        readonly TextBox logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        readonly Label header = new Label { Dock = DockStyle.Top, AutoEllipsis = true, Height = 40, Padding = new Padding(6, 4, 6, 4) };
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly TabPage conflictTab = new TabPage("Same file name, different paths");
        readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripButton problemsOnly = new ToolStripButton("Problems only") { CheckOnClick = true };
        readonly List<ToolStripItem> busyDisabled = new List<ToolStripItem>();

        string currentFile;
        DocNode currentRoot;
        List<DocNode> currentNodes = new List<DocNode>();

        public MainForm(string fileToOpen)
        {
            Text = "SwxRefViewer";
            Size = new Size(1300, 800);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AllowDrop = true;

            AddButton("Open...", (s, e) => OpenFile());
            AddButton("Reload", (s, e) => { if (currentFile != null) Analyze(currentFile); });
            toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("Expand all", (s, e) => tree.ExpandAll());
            AddButton("Collapse all", (s, e) => { tree.CollapseAll(); if (tree.Nodes.Count > 0) tree.Nodes[0].Expand(); });
            problemsOnly.ToolTipText = "Show only branches that contain references that are not OK";
            problemsOnly.CheckedChanged += (s, e) => FillTree();
            toolStrip.Items.Add(problemsOnly);
            toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("Export CSV...", (s, e) => ExportCsv());
            AddButton("Options...", (s, e) =>
            {
                using (var dlg = new OptionsForm(settings))
                    if (dlg.ShowDialog(this) == DialogResult.OK && currentFile != null) Analyze(currentFile);
            });

            var refSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            refSplit.Panel1.Controls.Add(refList);
            refSplit.Panel2.Controls.Add(details);
            var refTab = new TabPage("References of the selected file");
            refTab.Controls.Add(refSplit);
            conflictTab.Controls.Add(conflictList);
            var logTab = new TabPage("Log");
            logTab.Controls.Add(logBox);
            tabs.TabPages.AddRange(new[] { refTab, conflictTab, logTab });

            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(tabs);
            rightPanel.Controls.Add(header);

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill };
            mainSplit.Panel1.Controls.Add(tree);
            mainSplit.Panel2.Controls.Add(rightPanel);

            var statusStrip = new StatusStrip();
            statusStrip.Items.Add(statusLabel);

            Controls.Add(mainSplit);
            Controls.Add(toolStrip);
            Controls.Add(statusStrip);

            Load += (s, e) =>
            {
                mainSplit.SplitterDistance = 380;
                refSplit.SplitterDistance = (int)(refSplit.Height * 0.65);
                header.Text = "Open a SOLIDWORKS assembly (.sldasm), part (.sldprt) or drawing (.slddrw), or drop it here.";
                if (!string.IsNullOrEmpty(fileToOpen)) Analyze(fileToOpen);
            };

            tree.AfterSelect += (s, e) => ShowNode(e.Node?.Tag as NodeTag);
            refList.SelectedIndexChanged += (s, e) =>
            {
                if (refList.SelectedItems.Count == 1) details.Text = Describe((ResolvedReference)refList.SelectedItems[0].Tag);
            };
            refList.DoubleClick += (s, e) => SelectChildInTree();
            conflictList.DoubleClick += (s, e) =>
            {
                if (conflictList.SelectedItems.Count != 1) return;
                var r = (ResolvedReference)conflictList.SelectedItems[0].Tag;
                var node = FindTreeNode(tree.Nodes, t => t.Ref == r);
                if (node != null) { tree.SelectedNode = node; tabs.SelectedIndex = 0; }
            };
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) Analyze(files[0]);
            };
        }

        static ListView NewList(params object[] columns)
        {
            var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, GridLines = true };
            for (int i = 0; i < columns.Length; i += 2)
                list.Columns.Add((string)columns[i], (int)columns[i + 1]);
            return list;
        }

        void AddButton(string text, EventHandler onClick)
        {
            var button = new ToolStripButton(text);
            button.Click += onClick;
            toolStrip.Items.Add(button);
            busyDisabled.Add(button);
        }

        void OpenFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "SOLIDWORKS files (*.sldasm;*.sldprt;*.slddrw)|*.sldasm;*.sldprt;*.slddrw|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(settings.LastFolder) ? settings.LastFolder : ""
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                settings.LastFolder = Path.GetDirectoryName(dlg.FileName);
                try { settings.Save(); } catch { }
                Analyze(dlg.FileName);
            }
        }

        void Log(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n");
        }

        void SetBusy(bool busy)
        {
            foreach (var item in busyDisabled) item.Enabled = !busy;
            problemsOnly.Enabled = !busy;
            UseWaitCursor = busy;
        }

        void Analyze(string file)
        {
            if (!File.Exists(file))
            {
                MessageBox.Show(this, "File not found:\r\n" + file, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            currentFile = Path.GetFullPath(file);
            Text = "SwxRefViewer - " + Path.GetFileName(currentFile);
            tree.Nodes.Clear();
            refList.Items.Clear();
            conflictList.Items.Clear();
            details.Clear();
            SetBusy(true);
            Log("Reading " + currentFile);

            var licenseKey = settings.EffectiveLicenseKey;
            var forceRaw = settings.ForceRawScan;
            var folders = settings.SearchFolders.ToList();
            var file2 = currentFile;
            int count = 0;

            // The Document Manager is a COM server: use an STA thread and keep the UI responsive.
            var thread = new Thread(() =>
            {
                DocNode root = null;
                List<DocNode> nodes = null;
                string error = null;
                try
                {
                    using (var reader = new ReferenceReader(licenseKey, forceRaw, Log))
                    {
                        var analyzer = new Analyzer(reader, folders, p =>
                        {
                            count++;
                            BeginInvoke(new Action(() => statusLabel.Text = "Reading (" + count + "): " + p));
                        });
                        root = analyzer.Analyze(file2);
                        nodes = analyzer.AllNodes;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                BeginInvoke(new Action(() => Finished(root, nodes, error)));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        void Finished(DocNode root, List<DocNode> nodes, string error)
        {
            SetBusy(false);
            if (error != null)
            {
                Log("Error: " + error);
                statusLabel.Text = "Error: " + error;
                return;
            }
            currentRoot = root;
            currentNodes = nodes;
            FillTree();
            FillConflicts();

            var refs = nodes.SelectMany(n => n.References).ToList();
            var summary = string.Format("{0} files read. References: {1} not found, {2} other file with same name loaded, {3} several files with this name, {4} found elsewhere.",
                nodes.Select(n => n.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                refs.Count(r => r.Status == RefStatus.Missing),
                refs.Count(r => r.Status == RefStatus.NameClash),
                refs.Count(r => r.Status == RefStatus.Ambiguous),
                refs.Count(r => r.Status == RefStatus.Moved));
            statusLabel.Text = summary;
            Log(summary);
            foreach (var n in nodes.Where(n => n.Error != null).GroupBy(n => n.Path).Select(g => g.First()))
                Log("Cannot read " + n.Path + ": " + n.Error);
        }

        void FillTree()
        {
            if (currentRoot == null) return;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            var rootNode = MakeTreeNode(currentRoot, null, 0);
            tree.Nodes.Add(rootNode);
            rootNode.Expand();
            if (problemsOnly.Checked) tree.ExpandAll();
            tree.EndUpdate();
            tree.SelectedNode = rootNode;
        }

        TreeNode MakeTreeNode(DocNode doc, ResolvedReference incoming, int depth)
        {
            var name = incoming != null ? incoming.FileName : Path.GetFileName(doc.Path);
            var node = new TreeNode(name) { Tag = new NodeTag { Doc = doc, Ref = incoming } };
            node.ToolTipText = incoming != null ? Describe(incoming) : doc.Path;

            if (incoming != null)
            {
                node.ForeColor = StatusColor(incoming.Status);
                if (incoming.Status != RefStatus.Ok) node.Text += "  [" + Analyzer.StatusText(incoming.Status) + "]";
                if (incoming.Stored.Suppressed) node.Text += "  (suppressed)";
            }
            if (doc != null && doc.Error != null) { node.Text += "  [cannot read]"; node.ForeColor = Color.Firebrick; }
            if (doc != null && doc.IsRecursion) node.Text += "  [circular reference]";

            if (doc != null && !doc.IsRecursion && depth < 64)
            {
                foreach (var r in doc.References)
                {
                    if (problemsOnly.Checked && !HasProblem(r)) continue;
                    node.Nodes.Add(MakeTreeNode(r.Child, r, depth + 1));
                }
            }
            if (HasProblemBelow(doc)) node.NodeFont = new Font(tree.Font, FontStyle.Bold);
            return node;
        }

        static bool IsProblem(ResolvedReference r) => r.Status != RefStatus.Ok && r.Status != RefStatus.Virtual;

        static bool HasProblem(ResolvedReference r) =>
            IsProblem(r) || (r.Child != null && (r.Child.Error != null || HasProblemBelow(r.Child)));

        static bool HasProblemBelow(DocNode doc) =>
            doc != null && !doc.IsRecursion && doc.References.Any(HasProblem);

        static Color StatusColor(RefStatus status)
        {
            switch (status)
            {
                case RefStatus.Missing: return Color.Firebrick;
                case RefStatus.NameClash: return Color.OrangeRed;
                case RefStatus.Ambiguous: return Color.DarkOrange;
                case RefStatus.Moved: return Color.DarkGoldenrod;
                case RefStatus.Virtual: return Color.Gray;
                default: return SystemColors.WindowText;
            }
        }

        void ShowNode(NodeTag tag)
        {
            refList.BeginUpdate();
            refList.Items.Clear();
            details.Clear();
            if (tag == null) { refList.EndUpdate(); return; }

            var doc = tag.Doc;
            if (doc == null)
            {
                header.Text = tag.Ref.Stored.StoredPath + "\r\n" + Analyzer.StatusText(tag.Ref.Status) + " (no file to read)";
                details.Text = Describe(tag.Ref);
                refList.EndUpdate();
                return;
            }

            header.Text = doc.Path + "\r\n" +
                          (doc.Error != null ? "Cannot read: " + doc.Error
                           : "Read with: " + doc.Reader + ", " + doc.References.Count + " stored references");
            foreach (var r in doc.References)
            {
                var notes = new List<string>();
                if (r.Stored.Suppressed) notes.Add("suppressed");
                if (r.Candidates.Count > 1) notes.Add(r.Candidates.Count + " files with this name found");
                if (!string.IsNullOrEmpty(r.Stored.Detail)) notes.Add(r.Stored.Detail);
                var item = new ListViewItem(new[]
                {
                    Analyzer.StatusText(r.Status), r.FileName, r.Stored.StoredPath, r.ResolvedPath ?? "", r.ResolvedBy ?? "",
                    r.Stored.Configuration ?? "", r.Stored.Source ?? "", string.Join("; ", notes)
                }) { Tag = r, ForeColor = StatusColor(r.Status) };
                refList.Items.Add(item);
            }
            refList.EndUpdate();
            if (tag.Ref != null) details.Text = Describe(tag.Ref);
        }

        void SelectChildInTree()
        {
            if (refList.SelectedItems.Count != 1 || tree.SelectedNode == null) return;
            var r = (ResolvedReference)refList.SelectedItems[0].Tag;
            foreach (TreeNode child in tree.SelectedNode.Nodes)
                if (((NodeTag)child.Tag).Ref == r) { tree.SelectedNode = child; return; }
        }

        static TreeNode FindTreeNode(TreeNodeCollection nodes, Func<NodeTag, bool> match)
        {
            foreach (TreeNode n in nodes)
            {
                if (match((NodeTag)n.Tag)) return n;
                var found = FindTreeNode(n.Nodes, match);
                if (found != null) return found;
            }
            return null;
        }

        static string Describe(ResolvedReference r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("File name:      " + r.FileName);
            sb.AppendLine("Status:         " + Analyzer.StatusText(r.Status));
            sb.AppendLine("Stored path:    " + r.Stored.StoredPath);
            sb.AppendLine("Resolved path:  " + (r.ResolvedPath ?? "(none)"));
            sb.AppendLine("Found by:       " + r.ResolvedBy);
            if (!string.IsNullOrEmpty(r.Stored.Configuration)) sb.AppendLine("Configurations: " + r.Stored.Configuration);
            if (!string.IsNullOrEmpty(r.Stored.ComponentName)) sb.AppendLine("Components:     " + r.Stored.ComponentName);
            sb.AppendLine("Read from:      " + r.Stored.Source);
            if (r.Stored.Suppressed) sb.AppendLine("Suppressed in all configurations");
            if (!string.IsNullOrEmpty(r.Stored.Detail)) sb.AppendLine("Details:        " + r.Stored.Detail);
            if (r.Candidates.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Files with this name found on disk:");
                foreach (var c in r.Candidates) sb.AppendLine("  " + c);
            }
            sb.AppendLine();
            switch (r.Status)
            {
                case RefStatus.Missing:
                    sb.AppendLine("SOLIDWORKS will not find this file. Check the stored path, or add the folder to the search folders in the options."); break;
                case RefStatus.NameClash:
                    sb.AppendLine("Another file with the same name was loaded earlier in this assembly. SOLIDWORKS can only load one file per name,");
                    sb.AppendLine("so it will use that file instead of the one at the stored path. This often explains why a sub-assembly");
                    sb.AppendLine("opens fine on its own but shows unresolved references inside the top-level assembly."); break;
                case RefStatus.Ambiguous:
                    sb.AppendLine("Several different files with this name exist in the searched folders. SOLIDWORKS takes the first one it finds,");
                    sb.AppendLine("which may not be the one you expect."); break;
                case RefStatus.Moved:
                    sb.AppendLine("The file is not at the stored path, but SOLIDWORKS can find it in another location."); break;
            }
            return sb.ToString();
        }

        void FillConflicts()
        {
            conflictList.BeginUpdate();
            conflictList.Items.Clear();
            conflictList.Groups.Clear();
            var rows = currentNodes
                .SelectMany(n => n.References.Where(r => r.Status != RefStatus.Virtual).Select(r => (parent: n, r)))
                .GroupBy(x => x.r.FileName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(x => (x.r.Stored.StoredPath ?? "").ToLowerInvariant()).Distinct().Count() > 1
                         || g.Select(x => (x.r.ResolvedPath ?? "").ToLowerInvariant()).Distinct().Count() > 1)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in rows)
            {
                var group = new ListViewGroup(g.Key);
                conflictList.Groups.Add(group);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in g)
                {
                    if (!seen.Add(x.r.Stored.StoredPath + "|" + x.r.ResolvedPath + "|" + x.parent.Path)) continue;
                    conflictList.Items.Add(new ListViewItem(new[]
                    {
                        x.r.FileName, x.r.Stored.StoredPath, x.r.ResolvedPath ?? "(not found)", x.parent.Path, Analyzer.StatusText(x.r.Status)
                    }, group) { Tag = x.r, ForeColor = StatusColor(x.r.Status) });
                }
            }
            conflictList.EndUpdate();
            conflictTab.Text = "Same file name, different paths (" + rows.Count + ")";
        }

        void ExportCsv()
        {
            if (currentRoot == null) return;
            using (var dlg = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = Path.GetFileNameWithoutExtension(currentFile) + "_references.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("Referencing file;File name;Status;Stored path;Resolved path;Found by;Configurations;Read from;Suppressed;Virtual;Files with this name");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in currentNodes)
                    foreach (var r in n.References)
                    {
                        if (!seen.Add(n.Path + "|" + r.Stored.StoredPath + "|" + r.ResolvedPath)) continue;
                        sb.AppendLine(string.Join(";", new[]
                        {
                            n.Path, r.FileName, Analyzer.StatusText(r.Status), r.Stored.StoredPath, r.ResolvedPath, r.ResolvedBy,
                            r.Stored.Configuration, r.Stored.Source, r.Stored.Suppressed ? "yes" : "no", r.Stored.Virtual ? "yes" : "no",
                            string.Join(" | ", r.Candidates)
                        }.Select(Csv)));
                    }
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Log("Exported " + dlg.FileName);
            }
        }

        static string Csv(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
