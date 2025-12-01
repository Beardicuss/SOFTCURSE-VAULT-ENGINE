using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BorderlandsStorageCleaner.WinStat.Models;

namespace BorderlandsStorageCleaner.WinStat.Services
{
    /// <summary>
    /// Scans the filesystem and builds a tree of FSNode objects.
    /// HIGHLY OPTIMIZED: Minimal I/O, deferred categorization, aggressive batching.
    /// </summary>
    public class TreeBuilder
    {
        private readonly List<CategoryDefinition> _categories;
        private long _scannedBytes = 0;
        private int _scannedFiles = 0;
        private int _scannedDirs = 0;

        public TreeBuilder()
        {
            _categories = CategoryDefinition.GetDefaultCategories();
        }

        /// <summary>
        /// Scans a directory and builds a complete tree.
        /// </summary>
        public async Task<FSNode> ScanAsync(
            string rootPath,
            IProgress<ScanProgress> progress = null,
            CancellationToken cancellationToken = default,
            ScanOptions options = null)
        {
            options = options ?? new ScanOptions();
            _scannedBytes = 0;
            _scannedFiles = 0;
            _scannedDirs = 0;

            var rootNode = new FSNode(rootPath, Directory.Exists(rootPath))
            {
                Depth = 0
            };

            await Task.Run(() =>
            {
                if (rootNode.IsDirectory)
                {
                    ScanDirectoryFast(rootNode, progress, cancellationToken, options);
                }
                else if (File.Exists(rootPath))
                {
                    ScanFileFast(rootNode);
                }
                
                // Final progress report
                progress?.Report(new ScanProgress
                {
                    CurrentPath = rootPath,
                    ScannedBytes = _scannedBytes,
                    ScannedFiles = _scannedFiles,
                    ScannedDirectories = _scannedDirs
                });
            }, cancellationToken);

            return rootNode;
        }

        private void ScanDirectoryFast(
            FSNode node,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken,
            ScanOptions options)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (options.MaxDepth.HasValue && node.Depth >= options.MaxDepth.Value)
                return;

            if (IsExcluded(node.Path, options.ExcludePaths))
                return;

            try
            {
                var dirInfo = new DirectoryInfo(node.Path);
                
                // Use EnumerateFileSystemInfos for better performance (single call)
                foreach (var item in dirInfo.EnumerateFileSystemInfos())
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    try
                    {
                        if (item is DirectoryInfo subDir)
                        {
                            var dirNode = new FSNode(subDir.FullName, true)
                            {
                                Depth = node.Depth + 1,
                                Created = subDir.CreationTime,
                                LastAccessed = subDir.LastAccessTime,
                                LastModified = subDir.LastWriteTime,
                                Category = DetermineCategory(new FSNode(subDir.FullName, true))
                            };

                            node.Children.Add(dirNode);
                            _scannedDirs++;

                            // Recursive scan
                            ScanDirectoryFast(dirNode, progress, cancellationToken, options);
                        }
                        else if (item is FileInfo file)
                        {
                            var fileNode = new FSNode(file.FullName, false)
                            {
                                Depth = node.Depth + 1,
                                Size = file.Length,
                                Created = file.CreationTime,
                                LastAccessed = file.LastAccessTime,
                                LastModified = file.LastWriteTime,
                                Extension = file.Extension.ToLowerInvariant(),
                                Category = DetermineCategoryFast(file.Extension.ToLowerInvariant(), false)
                            };

                            node.Children.Add(fileNode);
                            _scannedFiles++;
                            _scannedBytes += file.Length;
                        }

                        // HYPER-OPTIMIZED: Only report every 2000 items (4x more aggressive)
                        if ((_scannedFiles + _scannedDirs) % 2000 == 0)
                        {
                            progress?.Report(new ScanProgress
                            {
                                CurrentPath = item.FullName,
                                ScannedBytes = _scannedBytes,
                                ScannedFiles = _scannedFiles,
                                ScannedDirectories = _scannedDirs
                            });
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip inaccessible items
                    }
                    catch (PathTooLongException)
                    {
                        // Skip paths that are too long
                    }
                    catch (Exception)
                    {
                        // Skip other errors
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible directories
            }
            catch (Exception)
            {
                // Skip other toplevel errors
            }
        }

        private void ScanFileFast(FSNode node)
        {
            try
            {
                var fileInfo = new FileInfo(node.Path);
                node.Size = fileInfo.Length;
                node.Created = fileInfo.CreationTime;
                node.LastAccessed = fileInfo.LastAccessTime;
                node.LastModified = fileInfo.LastWriteTime;
                node.Extension = fileInfo.Extension.ToLowerInvariant();
            }
            catch
            {
                node.Size = 0;
            }
        }

        private FileCategory DetermineCategory(FSNode node)
        {
            foreach (var category in _categories)
            {
                if (category.Matches(node))
                {
                    return category.Category;
                }
            }

            return FileCategory.Other;
        }

        private FileCategory DetermineCategoryFast(string extension, bool isDirectory)
        {
            if (isDirectory)
                return FileCategory.Other;

            // Fast extension-based categorization
            switch (extension)
            {
                case ".mp3": case ".wav": case ".flac": case ".m4a": case ".aac": case ".wma": case ".ogg":
                    return FileCategory.Music;
                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv": case ".flv": case ".webm":
                    return FileCategory.Videos;
                case ".jpg": case ".jpeg": case ".png": case ".gif": case ".bmp": case ".svg": case ".webp": case ".ico":
                    return FileCategory.Images;
                case ".doc": case ".docx": case ".pdf": case ".txt": case ".xls": case ".xlsx": case ".ppt": case ".pptx":
                    return FileCategory.Documents;
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2": case ".xz":
                    return FileCategory.Archives;
                case ".exe": case ".dll": case ".msi": case ".sys":
                    return FileCategory.System;
                default:
                    return FileCategory.Other;
            }
        }

        private bool IsExcluded(string path, List<string> excludePaths)
        {
            if (excludePaths == null || excludePaths.Count == 0)
                return false;

            foreach (var exclude in excludePaths)
            {
                if (path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Options for controlling the scan behavior.
    /// </summary>
    public class ScanOptions
    {
        public List<string> ExcludePaths { get; set; }
        public int? MaxDepth { get; set; }
        public bool FollowSymlinks { get; set; }

        public ScanOptions()
        {
            ExcludePaths = new List<string>();
            FollowSymlinks = false;
        }

        public static ScanOptions GetDefaultOptions()
        {
            return new ScanOptions
            {
                ExcludePaths = new List<string>
                {
                    @"C:\Windows\WinSxS",
                    @"C:\$Recycle.Bin",
                    @"C:\System Volume Information"
                }
            };
        }
    }

    /// <summary>
    /// Progress information during scanning.
    /// </summary>
    public class ScanProgress
    {
        public string CurrentPath { get; set; }
        public long ScannedBytes { get; set; }
        public int ScannedFiles { get; set; }
        public int ScannedDirectories { get; set; }
    }
}
