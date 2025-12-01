using System;
using System.Collections.Generic;
using System.Linq;

namespace BorderlandsStorageCleaner.WinStat.Models
{
    /// <summary>
    /// Represents a file system node (file or directory) with enhanced metadata for analysis.
    /// </summary>
    public class FSNode
    {
        // Basic Properties
        public string Path { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public List<FSNode> Children { get; set; }

        // Metadata
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime LastModified { get; set; }
        public int Depth { get; set; }
        public string Extension { get; set; }

        // Analysis Properties
        public FileCategory Category { get; set; }
        public string FileHash { get; set; }
        public bool IsDuplicate { get; set; }
        public List<FSNode> DuplicateNodes { get; set; }

        // Computed Properties
        public long TotalSize
        {
            get
            {
                if (!IsDirectory)
                    return Size;

                return Size + (Children?.Sum(c => c.TotalSize) ?? 0);
            }
        }

        public int FileCount
        {
            get
            {
                if (!IsDirectory)
                    return 1;

                return Children?.Sum(c => c.FileCount) ?? 0;
            }
        }

        public int DirectoryCount
        {
            get
            {
                if (!IsDirectory)
                    return 0;

                return 1 + (Children?.Sum(c => c.DirectoryCount) ?? 0);
            }
        }

        public TimeSpan Age => DateTime.Now - LastAccessed;

        public double PercentOfParent { get; set; }

        public FSNode()
        {
            Children = new List<FSNode>();
            DuplicateNodes = new List<FSNode>();
            Category = FileCategory.Other;
        }

        public FSNode(string path, bool isDirectory) : this()
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            IsDirectory = isDirectory;
            Extension = isDirectory ? "" : System.IO.Path.GetExtension(path).ToLowerInvariant();
        }

        /// <summary>
        /// Gets all file nodes in the tree (recursive).
        /// </summary>
        public IEnumerable<FSNode> GetAllFiles()
        {
            if (!IsDirectory)
            {
                yield return this;
            }
            else
            {
                foreach (var child in Children)
                {
                    foreach (var file in child.GetAllFiles())
                    {
                        yield return file;
                    }
                }
            }
        }

        /// <summary>
        /// Gets all directory nodes in the tree (recursive).
        /// </summary>
        public IEnumerable<FSNode> GetAllDirectories()
        {
            if (IsDirectory)
            {
                yield return this;

                foreach (var child in Children)
                {
                    foreach (var dir in child.GetAllDirectories())
                    {
                        yield return dir;
                    }
                }
            }
        }

        public override string ToString()
        {
            return $"{Name} ({(IsDirectory ? "DIR" : "FILE")}, {TotalSize:N0} bytes)";
        }
    }

    /// <summary>
    /// Smart categorization of files beyond just extensions.
    /// </summary>
    public enum FileCategory
    {
        Games,
        Development,
        Videos,
        Music,
        Images,
        Documents,
        Archives,
        System,
        CloudSync,
        Temporary,
        Logs,
        Other
    }
}
