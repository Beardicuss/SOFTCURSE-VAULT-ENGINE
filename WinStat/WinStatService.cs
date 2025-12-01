using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BorderlandsStorageCleaner.WinStat.Models;
using BorderlandsStorageCleaner.WinStat.Services;

namespace BorderlandsStorageCleaner.WinStat
{
    /// <summary>
    /// Main service coordinating disk analysis operations.
    /// </summary>
    public class WinStatService
    {
        private readonly TreeBuilder _treeBuilder;
        private readonly Aggregator _aggregator;

        public WinStatService()
        {
            _treeBuilder = new TreeBuilder();
            _aggregator = new Aggregator();
        }

        /// <summary>
        /// Performs a complete disk analysis.
        /// </summary>
        public async Task<ScanResult> AnalyzeDiskAsync(
            string path,
            IProgress<ScanProgress> progress = null,
            CancellationToken cancellationToken = default,
            bool findDuplicates = false)
        {
            // Step 1: Scan filesystem
            var scanStartTime = DateTime.Now;
            
            var rootNode = await _treeBuilder.ScanAsync(
                path,
                progress,
                cancellationToken,
                ScanOptions.GetDefaultOptions());

            if (cancellationToken.IsCancellationRequested)
                return null;

            // Step 2: Analyze results
            var result = _aggregator.Analyze(rootNode);
            result.ScanStartTime = scanStartTime;

            // Step 3: Find duplicates (optional, can be slow)
            if (findDuplicates)
            {
                var allFiles = rootNode.GetAllFiles().ToList();
                result.Duplicates = await _aggregator.FindDuplicatesAsync(allFiles);
            }

            return result;
        }

        /// <summary>
        /// Quick scan without duplicate detection.
        /// </summary>
        public async Task<ScanResult> QuickScanAsync(
            string path,
            IProgress<ScanProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return await AnalyzeDiskAsync(path, progress, cancellationToken, findDuplicates: false);
        }

        /// <summary>
        /// Deep scan with duplicate detection.
        /// </summary>
        public async Task<ScanResult> DeepScanAsync(
            string path,
            IProgress<ScanProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            return await AnalyzeDiskAsync(path, progress, cancellationToken, findDuplicates: true);
        }
    }
}
