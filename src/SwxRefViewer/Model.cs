using System.Collections.Generic;

namespace SwxRefViewer
{
    // One reference as it is stored inside a SOLIDWORKS file.
    public class StoredReference
    {
        public string StoredPath;
        public string Source;          // "Document Manager", "Component", "Raw scan"
        public string Configuration;   // configuration of the referencing file (components only)
        public string ComponentName;
        public bool Suppressed;
        public bool Virtual;
        public string Detail;          // extra info, e.g. raw Document Manager status
    }

    public enum RefStatus
    {
        Ok,         // found at the stored path
        Moved,      // found, but not at the stored path
        Ambiguous,  // several different files with this name were found
        NameClash,  // another file with the same name is already loaded, SOLIDWORKS would use that one
        Missing,    // nothing found
        Virtual     // virtual component, stored inside the assembly
    }

    public class ResolvedReference
    {
        public StoredReference Stored;
        public string FileName;
        public string ResolvedPath;
        public string ResolvedBy;      // which search step found the file
        public RefStatus Status;
        public List<string> Candidates = new List<string>(); // all existing files with this name that were found
        public DocNode Child;
    }

    // One SOLIDWORKS file in the reference tree.
    public class DocNode
    {
        public string Path;
        public DocNode Parent;
        public string Reader;          // how the file was read
        public string Error;
        public bool IsRecursion;       // file already appears higher up in the same branch
        public List<ResolvedReference> References = new List<ResolvedReference>();
    }
}
