using System;
using System.Collections.Generic;
using System.Linq;

namespace BorderlandsStorageCleaner.WinStat.Models
{
    /// <summary>
    /// Contains the complete result of a disk scan with analysis data.
    /// </summary>
    public class ScanResult
    {
        public FSNode RootNode { get; set; }
        public DateTime ScanStartTime { get; set; }
        public DateTime ScanEndTime { get; set; }
        public TimeSpan ScanDuration => ScanEndTime - ScanStartTime;
        
        // Summary Statistics
        public long TotalSize { get; set; }
        public int TotalFiles { get; set; }
        public int TotalDirectories { get; set; }
        public double AverageFileSize { get; set; }
        
        // Top Items
        public List<FSNode> TopFiles { get; set; }
        public List<FSNode> TopFolders { get; set; }
        
        // Category Breakdown
        public Dictionary<FileCategory, CategoryStats> CategoryBreakdown { get; set; }
        
        // Extension Analysis
        public Dictionary<string, ExtensionStats> ExtensionBreakdown { get; set; }
        
        // Age Analysis
        public List<FSNode> OldFiles { get; set; }  // Not accessed in 6+ months
        
        // Duplicates
        public List<DuplicateGroup> Duplicates { get; set; }
        
        // Recommendations
        public List<Recommendation> Recommendations { get; set; }

        public ScanResult()
        {
            TopFiles = new List<FSNode>();
            TopFolders = new List<FSNode>();
            CategoryBreakdown = new Dictionary<FileCategory, CategoryStats>();
            ExtensionBreakdown = new Dictionary<string, ExtensionStats>();
            OldFiles = new List<FSNode>();
            Duplicates = new List<DuplicateGroup>();
            Recommendations = new List<Recommendation>();
        }

        public void CalculateSummary()
        {
            if (RootNode == null) return;

            TotalSize = RootNode.TotalSize;
            TotalFiles = RootNode.FileCount;
            TotalDirectories = RootNode.DirectoryCount;
            AverageFileSize = TotalFiles > 0 ? (double)TotalSize / TotalFiles : 0;
        }
    }

    /// <summary>
    /// Statistics for a file category.
    /// </summary>
    public class CategoryStats
    {
        public FileCategory Category { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public long TotalSize { get; set; }
        public int FileCount { get; set; }
        public double AverageFileSize { get; set; }
        public double PercentOfTotal { get; set; }
    }

    /// <summary>
    /// Statistics for a file extension.
    /// </summary>
    public class ExtensionStats
    {
        public string Extension { get; set; }
        public long TotalSize { get; set; }
        public int FileCount { get; set; }
        public double AverageFileSize { get; set; }
        public double PercentOfTotal { get; set; }
        public FSNode LargestFile { get; set; }
    }

    /// <summary>
    /// Group of duplicate files.
    /// </summary>
    public class DuplicateGroup
    {
        public string Hash { get; set; }
        public List<FSNode> Files { get; set; }
        public long FileSize { get; set; }
        public long WastedSpace => FileSize * (Files.Count - 1);

        public DuplicateGroup()
        {
            Files = new List<FSNode>();
        }
    }

    /// <summary>
    /// Smart recommendation for user action.
    /// </summary>
    public class Recommendation
    {
        public RecommendationType Type { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public long PotentialSavings { get; set; }
        public List<FSNode> AffectedFiles { get; set; }
        public string ActionText { get; set; }

        public Recommendation()
        {
            AffectedFiles = new List<FSNode>();
        }
    }

    public enum RecommendationType
    {
        OldFiles,
        LargeFiles,
        Duplicates,
        UnusedGames,
        DevelopmentArtifacts,
        LargeLogs,
        TemporaryFiles
    }
}
