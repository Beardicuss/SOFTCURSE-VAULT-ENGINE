using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.IO;
using System.Threading.Tasks;
using BorderlandsStorageCleaner.WinStat.Models;

namespace BorderlandsStorageCleaner.WinStat.Services
{
    /// <summary>
    /// Analyzes scanned filesystem data to produce insights and rankings.
    /// </summary>
    public class Aggregator
    {
        /// <summary>
        /// Performs complete analysis on a scanned tree.
        /// </summary>
        public ScanResult Analyze(FSNode rootNode, int topN = 100)
        {
            var result = new ScanResult
            {
                RootNode = rootNode,
                ScanEndTime = DateTime.Now
            };

            result.CalculateSummary();

            // Get all files and directories
            var allFiles = rootNode.GetAllFiles().ToList();
            var allDirs = rootNode.GetAllDirectories().ToList();

            // Top files by size
            result.TopFiles = allFiles
                .OrderByDescending(f => f.Size)
                .Take(topN)
                .ToList();

            // Top folders by total size
            result.TopFolders = allDirs
                .Where(d => d != rootNode)
                .OrderByDescending(d => d.TotalSize)
                .Take(topN)
                .ToList();

            // Category breakdown
            result.CategoryBreakdown = AnalyzeCategories(allFiles, result.TotalSize);

            // Extension breakdown
            result.ExtensionBreakdown = AnalyzeExtensions(allFiles, result.TotalSize);

            // Find old files (not accessed in 180 days)
            var oldThreshold = DateTime.Now.AddDays(-180);
            result.OldFiles = allFiles
                .Where(f => f.LastAccessed < oldThreshold)
                .OrderByDescending(f => f.Size)
                .Take(topN)
                .ToList();

            // Generate smart recommendations
            result.Recommendations = GenerateRecommendations(result, allFiles);

            return result;
        }

        /// <summary>
        /// Detects duplicate files by content hash.
        /// </summary>
        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(List<FSNode> files)
        {
            var hashGroups = new Dictionary<string, List<FSNode>>();

            // Group files by size first (quick filter)
            var sizeGroups = files
                .Where(f => f.Size > 0)  // Skip empty files
                .GroupBy(f => f.Size)
                .Where(g => g.Count() > 1)  // Only sizes with multiple files
                .ToList();

            foreach (var sizeGroup in sizeGroups)
            {
                foreach (var file in sizeGroup)
                {
                    try
                    {
                        var hash = await ComputeFileHashAsync(file.Path);
                        file.FileHash = hash;

                        if (!hashGroups.ContainsKey(hash))
                            hashGroups[hash] = new List<FSNode>();

                        hashGroups[hash].Add(file);
                    }
                    catch
                    {
                        // Skip files we can't hash
                    }
                }
            }

            // Create duplicate groups (only where count > 1)
            var duplicates = new List<DuplicateGroup>();
            foreach (var group in hashGroups.Where(kvp => kvp.Value.Count > 1))
            {
                var dupGroup = new DuplicateGroup
                {
                    Hash = group.Key,
                    Files = group.Value,
                    FileSize = group.Value.First().Size
                };

                // Mark all files as duplicates
                foreach (var file in group.Value)
                {
                    file.IsDuplicate = true;
                    file.DuplicateNodes = group.Value.Where(f => f != file).ToList();
                }

                duplicates.Add(dupGroup);
            }

            return duplicates.OrderByDescending(d => d.WastedSpace).ToList();
        }

        private Dictionary<FileCategory, CategoryStats> AnalyzeCategories(List<FSNode> files, long totalSize)
        {
            var categories = new Dictionary<FileCategory, CategoryStats>();

            var grouped = files.GroupBy(f => f.Category);
            foreach (var group in grouped)
            {
                var totalCategorySize = group.Sum(f => f.Size);
                var stats = new CategoryStats
                {
                    Category = group.Key,
                    Name = group.Key.ToString(),
                    TotalSize = totalCategorySize,
                    FileCount = group.Count(),
                    AverageFileSize = group.Average(f => f.Size),
                    PercentOfTotal = totalSize > 0 ? (double)totalCategorySize / totalSize * 100 : 0
                };

                // Get color from CategoryDefinition
                var catDef = CategoryDefinition.GetDefaultCategories()
                    .FirstOrDefault(c => c.Category == group.Key);
                stats.Color = catDef?.Color ?? "#95A5A6";

                categories[group.Key] = stats;
            }

            return categories;
        }

        private Dictionary<string, ExtensionStats> AnalyzeExtensions(List<FSNode> files, long totalSize)
        {
            var extensions = new Dictionary<string, ExtensionStats>();

            var grouped = files
                .Where(f => !string.IsNullOrEmpty(f.Extension))
                .GroupBy(f => f.Extension);

            foreach (var group in grouped)
            {
                var totalExtSize = group.Sum(f => f.Size);
                var stats = new ExtensionStats
                {
                    Extension = group.Key,
                    TotalSize = totalExtSize,
                    FileCount = group.Count(),
                    AverageFileSize = group.Average(f => f.Size),
                    PercentOfTotal = totalSize > 0 ? (double)totalExtSize / totalSize * 100 : 0,
                    LargestFile = group.OrderByDescending(f => f.Size).FirstOrDefault()
                };

                extensions[group.Key] = stats;
            }

            return extensions.OrderByDescending(kvp => kvp.Value.TotalSize)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private List<Recommendation> GenerateRecommendations(ScanResult result, List<FSNode> allFiles)
        {
            var recommendations = new List<Recommendation>();

            // Recommendation 1: Large log files
            var largeLogFiles = allFiles
                .Where(f => f.Category == FileCategory.Logs && f.Size > 50 * 1024 * 1024)  // >50MB
                .ToList();

            if (largeLogFiles.Any())
            {
                recommendations.Add(new Recommendation
                {
                    Type = RecommendationType.LargeLogs,
                    Title = "Large Log Files Detected",
                    Description = $"{largeLogFiles.Count} log files larger than 50MB found.",
                    PotentialSavings = largeLogFiles.Sum(f => f.Size),
                    AffectedFiles = largeLogFiles,
                    ActionText = "Review and delete old logs"
                });
            }

            // Recommendation 2: Old files
            if (result.OldFiles.Count > 0)
            {
                var oldFilesSize = result.OldFiles.Sum(f => f.Size);
                recommendations.Add(new Recommendation
                {
                    Type = RecommendationType.OldFiles,
                    Title = "Old Files Not Accessed",
                    Description = $"{result.OldFiles.Count} files not accessed in 6+ months.",
                    PotentialSavings = oldFilesSize,
                    AffectedFiles = result.OldFiles,
                    ActionText = "Review and archive or delete"
                });
            }

            // Recommendation 3: Development artifacts
            var devArtifacts = allFiles
                .Where(f => f.Path.Contains("node_modules") || 
                           f.Path.Contains("\\target\\") ||
                           f.Path.Contains("\\bin\\") ||
                           f.Path.Contains("\\obj\\"))
                .ToList();

            if (devArtifacts.Any())
            {
                recommendations.Add(new Recommendation
                {
                    Type = RecommendationType.DevelopmentArtifacts,
                    Title = "Development Build Artifacts",
                    Description = $"{devArtifacts.Count} build artifacts found (node_modules, bin, obj).",
                    PotentialSavings = devArtifacts.Sum(f => f.Size),
                    AffectedFiles = devArtifacts,
                    ActionText = "Clean and rebuild when needed"
                });
            }

            return recommendations.OrderByDescending(r => r.PotentialSavings).ToList();
        }

        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = await Task.Run(() => sha256.ComputeHash(stream));
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }
    }
}
