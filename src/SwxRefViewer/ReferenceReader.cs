using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwxRefViewer
{
    // Reads the references stored in a SOLIDWORKS file without SOLIDWORKS.
    // Uses the SOLIDWORKS Document Manager (installed with SOLIDWORKS, needs a license key) when available,
    // otherwise a raw scan of the file for stored path strings.
    // The Document Manager is called late bound (dynamic), so no SOLIDWORKS assemblies are needed to build this tool.
    public class ReferenceReader : IDisposable
    {
        const int DocTypePart = 1, DocTypeAssembly = 2, DocTypeDrawing = 3;

        // SwDmSearchFilters
        const int SearchExternalReference = 1, SearchRootAssemblyFolder = 2, SearchInContextReference = 8,
                  SearchForPart = 16, SearchForAssembly = 32, SearchForDrawing = 64;

        static readonly string[] OpenErrors =
        {
            "None", "Failed", "Not a SOLIDWORKS file", "File not found", "File is read-only",
            "No license (check the Document Manager license key)", "File was saved with a newer SOLIDWORKS version"
        };

        dynamic dmApp;
        readonly Action<string> log;

        public bool UsesDocumentManager => dmApp != null;
        public bool ForceRawScan;

        public ReferenceReader(string licenseKey, bool forceRawScan, Action<string> log)
        {
            this.log = log;
            ForceRawScan = forceRawScan;
            if (forceRawScan)
            {
                log("Reader: raw file scan (chosen in the options).");
                return;
            }
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                log("Reader: raw file scan. Enter a Document Manager license key for exact results.");
                return;
            }

            try
            {
                var factoryType = Type.GetTypeFromProgID("SwDocumentMgr.SwDMClassFactory");
                if (factoryType == null)
                {
                    log("SOLIDWORKS Document Manager is not installed (ProgID SwDocumentMgr.SwDMClassFactory not registered). Using raw file scan.");
                    return;
                }
                dynamic factory = Activator.CreateInstance(factoryType);
                dmApp = factory.GetApplication(licenseKey.Trim());
                if (dmApp == null)
                {
                    log("Document Manager did not accept the license key. Using raw file scan.");
                    return;
                }
                log("Reader: SOLIDWORKS Document Manager.");
            }
            catch (Exception ex)
            {
                dmApp = null;
                log("Document Manager could not be started (" + ex.Message + "). Using raw file scan.");
            }
        }

        public List<StoredReference> Read(string path, out string readerName)
        {
            if (dmApp != null)
            {
                try
                {
                    readerName = "Document Manager";
                    return ReadWithDocumentManager(path);
                }
                catch (Exception ex)
                {
                    log(Path.GetFileName(path) + ": Document Manager failed (" + ex.Message + "), falling back to raw scan.");
                }
            }
            readerName = "Raw scan";
            return ReadWithRawScan(path);
        }

        List<StoredReference> ReadWithDocumentManager(string path)
        {
            var result = new List<StoredReference>();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            int docType = ext == ".sldasm" ? DocTypeAssembly : ext == ".slddrw" ? DocTypeDrawing : DocTypePart;

            int openError;
            dynamic doc = dmApp.GetDocument(path, docType, true, out openError);
            if (doc == null)
            {
                var reason = openError >= 0 && openError < OpenErrors.Length ? OpenErrors[openError] : "error " + openError;
                throw new InvalidOperationException("cannot open file: " + reason);
            }

            try
            {
                // 1. All external references of the file (what SOLIDWORKS loads when opening it).
                try
                {
                    dynamic searchOpt = dmApp.GetSearchOptionObject();
                    searchOpt.SearchFilters = SearchExternalReference + SearchRootAssemblyFolder + SearchInContextReference
                                              + SearchForPart + SearchForAssembly + SearchForDrawing;
                    object broken, isVirtual, timeStamps;
                    object paths = doc.GetAllExternalReferences4(searchOpt, out broken, out isVirtual, out timeStamps);
                    var pathList = ToList(paths);
                    var brokenList = ToList(broken);
                    var virtualList = ToList(isVirtual);
                    for (int i = 0; i < pathList.Count; i++)
                    {
                        var p = Convert.ToString(pathList[i]);
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        result.Add(new StoredReference
                        {
                            StoredPath = p,
                            Source = "External reference",
                            Virtual = i < virtualList.Count && Convert.ToBoolean(virtualList[i]),
                            Detail = i < brokenList.Count ? "Document Manager status " + Convert.ToString(brokenList[i]) : null
                        });
                    }
                }
                catch (Exception ex)
                {
                    log(Path.GetFileName(path) + ": GetAllExternalReferences4 failed (" + ex.Message + ").");
                }

                // 2. Components per configuration (assemblies only), with the path stored for each component.
                if (docType == DocTypeAssembly)
                {
                    try
                    {
                        dynamic cfgMgr = doc.ConfigurationManager;
                        foreach (var cfgNameObj in ToList(cfgMgr.GetConfigurationNames()))
                        {
                            var cfgName = Convert.ToString(cfgNameObj);
                            dynamic cfg = cfgMgr.GetConfigurationByName(cfgName);
                            if (cfg == null) continue;
                            foreach (dynamic comp in ToList(cfg.GetComponents()))
                            {
                                var r = new StoredReference { Source = "Component", Configuration = cfgName };
                                try { r.StoredPath = comp.PathName; } catch { }
                                if (string.IsNullOrWhiteSpace(r.StoredPath)) continue;
                                try { r.ComponentName = comp.Name2; } catch { }
                                try { r.Suppressed = comp.IsSuppressed(); } catch { }
                                try { r.Virtual = comp.IsVirtual; } catch { }
                                try
                                {
                                    string refCfg = comp.ConfigurationName;
                                    if (!string.IsNullOrEmpty(refCfg)) r.Detail = "uses configuration '" + refCfg + "'";
                                }
                                catch { }
                                result.Add(r);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log(Path.GetFileName(path) + ": reading components failed (" + ex.Message + ").");
                    }
                }
            }
            finally
            {
                try { doc.CloseDoc(); } catch { }
                try { Marshal.FinalReleaseComObject(doc); } catch { }
            }
            return result;
        }

        static List<object> ToList(object value)
        {
            var list = new List<object>();
            if (value == null || value is string) return list;
            if (value is IEnumerable items)
                foreach (var item in items) list.Add(item);
            return list;
        }

        // Absolute (C:\...) or UNC (\\server\...) paths to SOLIDWORKS files.
        static readonly Regex PathPattern = new Regex(
            @"(?:[A-Za-z]:\\|\\\\[^\\/:*?""<>|\x00-\x1f]+\\)[^:*?""<>|\x00-\x1f]*?\.(?:sldprt|sldasm|slddrw)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Finds path strings stored in the file (as UTF-16 or ANSI text). This needs no license, but it is a heuristic:
        // it cannot tell configurations or suppressed components apart, it may list old paths that are still stored
        // in the file, and paths in compressed sections are not found.
        List<StoredReference> ReadWithRawScan(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var found = new List<string>();

            var utf16 = new StringBuilder();
            for (int offset = 0; offset < 2; offset++)
            {
                utf16.Clear();
                for (int i = offset; i + 1 < bytes.Length; i += 2)
                {
                    char c = (char)(bytes[i] | (bytes[i + 1] << 8));
                    if (c >= 0x20 && c != 0xFFFF && !char.IsSurrogate(c)) utf16.Append(c);
                    else { Collect(utf16, found); utf16.Clear(); }
                }
                Collect(utf16, found);
            }

            var ansi = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b >= 0x20 && b < 0x7F || b >= 0xA0) ansi.Append((char)b);
                else { Collect(ansi, found); ansi.Clear(); }
            }
            Collect(ansi, found);

            var self = Path.GetFullPath(path);
            return found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
                .Select(p => new StoredReference { StoredPath = p, Source = "Raw scan" })
                .ToList();
        }

        static void Collect(StringBuilder text, List<string> found)
        {
            if (text.Length < 8) return;
            foreach (Match m in PathPattern.Matches(text.ToString()))
                found.Add(m.Value);
        }

        public void Dispose()
        {
            if (dmApp != null)
            {
                try { Marshal.FinalReleaseComObject(dmApp); } catch { }
                dmApp = null;
            }
        }
    }
}
