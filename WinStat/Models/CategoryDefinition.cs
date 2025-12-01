using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BorderlandsStorageCleaner.WinStat.Models
{
    /// <summary>
    /// Defines a file category with patterns for smart classification.
    /// </summary>
    public class CategoryDefinition
    {
        public FileCategory Category { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }  // Hex color for treemap
        public List<string> Extensions { get; set; }
        public List<string> PathPatterns { get; set; }
        public Func<FSNode, bool> CustomMatcher { get; set; }

        public CategoryDefinition()
        {
            Extensions = new List<string>();
            PathPatterns = new List<string>();
        }

        /// <summary>
        /// Checks if a node matches this category.
        /// </summary>
        public bool Matches(FSNode node)
        {
            // Check custom matcher first
            if (CustomMatcher != null && CustomMatcher(node))
                return true;

            // Check extensions
            if (!string.IsNullOrEmpty(node.Extension) && Extensions.Contains(node.Extension))
                return true;

            // Check path patterns
            foreach (var pattern in PathPatterns)
            {
                if (MatchesPattern(node.Path, pattern))
                    return true;
            }

            return false;
        }

        private bool MatchesPattern(string path, string pattern)
        {
            // Simple wildcard pattern matching
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*\\*", ".*")     // ** matches any path
                .Replace("\\*", "[^\\\\]*")  // * matches filename chars
                + "$";

            return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
        }

        public static List<CategoryDefinition> GetDefaultCategories()
        {
            return new List<CategoryDefinition>
            {
                new CategoryDefinition
                {
                    Category = FileCategory.Games,
                    Name = "Games",
                    Color = "#9B59B6",
                    Extensions = new List<string> { ".pak", ".wad", ".vpk", ".unity3d" },
                    PathPatterns = new List<string>
                    {
                        "*\\Steam\\steamapps\\**",
                        "*\\Epic Games\\**",
                        "*\\GOG Galaxy\\Games\\**",
                        "*\\Program Files*\\Steam\\**"
                    }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Development,
                    Name = "Development",
                    Color = "#3498DB",
                    Extensions = new List<string> { ".cs", ".py", ".js", ".ts", ".cpp", ".h", ".java", ".go", ".rs" },
                    PathPatterns = new List<string>
                    {
                        "*\\node_modules\\**",
                        "*\\.git\\**",
                        "*\\target\\**",
                        "*\\bin\\**",
                        "*\\obj\\**",
                        "*\\__pycache__\\**",
                        "*\\venv\\**",
                        "*\\.venv\\**"
                    }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Videos,
                    Name = "Videos",
                    Color = "#E74C3C",
                    Extensions = new List<string> { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Music,
                    Name = "Music",
                    Color = "#1ABC9C",
                    Extensions = new List<string> { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma" }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Images,
                    Name = "Images",
                    Color = "#F39C12",
                    Extensions = new List<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".psd", ".raw", ".cr2", ".nef" }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Documents,
                    Name = "Documents",
                    Color = "#2ECC71",
                    Extensions = new List<string> { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt" }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Archives,
                    Name = "Archives",
                    Color = "#95A5A6",
                    Extensions = new List<string> { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso" }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.System,
                    Name = "System",
                    Color = "#34495E",
                    PathPatterns = new List<string>
                    {
                        "C:\\Windows\\**",
                        "C:\\Program Files\\**",
                        "C:\\Program Files (x86)\\**",
                        "*\\AppData\\Local\\Microsoft\\Windows\\**"
                    }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.CloudSync,
                    Name = "Cloud Sync",
                    Color = "#5DADE2",
                    PathPatterns = new List<string>
                    {
                        "*\\OneDrive\\**",
                        "*\\Dropbox\\**",
                        "*\\Google Drive\\**",
                        "*\\iCloud\\**"
                    }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Temporary,
                    Name = "Temporary",
                    Color = "#E67E22",
                    Extensions = new List<string> { ".tmp", ".temp", ".cache" },
                    PathPatterns = new List<string>
                    {
                        "*\\Temp\\**",
                        "*\\AppData\\Local\\Temp\\**",
                        "C:\\Windows\\Temp\\**"
                    }
                },
                new CategoryDefinition
                {
                    Category = FileCategory.Logs,
                    Name = "Logs",
                    Color = "#C0392B",
                    Extensions = new List<string> { ".log", ".txt", ".out" },
                    PathPatterns = new List<string>
                    {
                        "*\\logs\\**",
                        "*\\Log Files\\**"
                    }
                }
            };
        }
    }
}
