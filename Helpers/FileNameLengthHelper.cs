using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SyncDroidWinUI.Helpers
{
    public class OverlongFileItem
    {
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Utf8ByteCount { get; set; }
        public string DisplayText => $"[{Utf8ByteCount} B] {RelativePath}";
    }

    public class RenameResultItem
    {
        public string OldFullPath { get; set; } = "";
        public string NewFullPath { get; set; } = "";
        public string OldRelativePath { get; set; } = "";
        public string NewRelativePath { get; set; } = "";
        public string OldFileName { get; set; } = "";
        public string NewFileName { get; set; } = "";
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static class FileNameLengthHelper
    {
        public const int AndroidMaxFileNameBytes = 255;
        public const int TruncateTargetMaxBytes = 254; // Strictly < 255 bytes

        public static int GetFileNameUtf8ByteCount(string filePathOrName)
        {
            if (string.IsNullOrEmpty(filePathOrName)) return 0;
            string name = Path.GetFileName(filePathOrName);
            return Encoding.UTF8.GetByteCount(name);
        }

        public static bool IsFileNameOver255Bytes(string filePathOrName)
        {
            return GetFileNameUtf8ByteCount(filePathOrName) > AndroidMaxFileNameBytes;
        }

        public static List<OverlongFileItem> ScanDirectoryForOverlongFileNames(string rootDir)
        {
            var results = new List<OverlongFileItem>();
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            {
                return results;
            }

            string normalizedRoot = Path.GetFullPath(rootDir.Trim());
            var stack = new Stack<string>();
            stack.Push(normalizedRoot);

            while (stack.Count > 0)
            {
                var currentDir = stack.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(currentDir))
                    {
                        var fileName = Path.GetFileName(file);
                        if (fileName.StartsWith(".syncdroid_", StringComparison.OrdinalIgnoreCase) &&
                            fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int byteCount = Encoding.UTF8.GetByteCount(fileName);
                        if (byteCount > AndroidMaxFileNameBytes)
                        {
                            results.Add(new OverlongFileItem
                            {
                                FullPath = file,
                                RelativePath = Path.GetRelativePath(normalizedRoot, file).Replace('\\', '/'),
                                FileName = fileName,
                                Utf8ByteCount = byteCount
                            });
                        }
                    }
                }
                catch { }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        stack.Push(subDir);
                    }
                }
                catch { }
            }

            return results.OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<OverlongFileItem> ScanPathsForOverlongFileNames(IEnumerable<string> paths)
        {
            var results = new List<OverlongFileItem>();
            foreach (var rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                var fullPath = Path.GetFullPath(rawPath.Trim());
                if (File.Exists(fullPath))
                {
                    var fileName = Path.GetFileName(fullPath);
                    int byteCount = Encoding.UTF8.GetByteCount(fileName);
                    if (byteCount > AndroidMaxFileNameBytes)
                    {
                        results.Add(new OverlongFileItem
                        {
                            FullPath = fullPath,
                            RelativePath = fileName,
                            FileName = fileName,
                            Utf8ByteCount = byteCount
                        });
                    }
                }
                else if (Directory.Exists(fullPath))
                {
                    var subItems = ScanDirectoryForOverlongFileNames(fullPath);
                    string rootFolderName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
                    foreach (var item in subItems)
                    {
                        item.RelativePath = $"{rootFolderName}/{item.RelativePath}";
                        results.Add(item);
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Truncates a filename from the end of its main stem (preserving extension) using complete Unicode
        /// grapheme clusters until the total UTF-8 byte count is strictly under 255 bytes (<= 254 bytes).
        /// Resolves collisions in the same directory by appending _1, _2, etc.
        /// </summary>
        public static string TruncateFileNameStrictlyUnder255Bytes(
            string originalFileName,
            Func<string, bool>? isNameTakenInDir = null,
            int maxBytes = TruncateTargetMaxBytes)
        {
            string ext = Path.GetExtension(originalFileName);
            string stem = Path.GetFileNameWithoutExtension(originalFileName);
            var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();

            stem = new string(stem.Where(c => !invalidChars.Contains(c)).ToArray());
            ext = new string(ext.Where(c => !invalidChars.Contains(c)).ToArray());

            int extBytes = Encoding.UTF8.GetByteCount(ext);
            if (extBytes >= maxBytes - 8 || string.IsNullOrEmpty(stem))
            {
                // Fallback if the file has no stem or the extension itself is abnormally long
                stem = new string(originalFileName.Where(c => !invalidChars.Contains(c)).ToArray());
                ext = "";
                extBytes = 0;
            }

            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(stem);
            while (enumerator.MoveNext())
            {
                elements.Add(enumerator.GetTextElement());
            }

            while (elements.Count > 0)
            {
                string currentStem = string.Concat(elements).TrimEnd(' ', '.');
                string candidate = currentStem + ext;

                if (!string.IsNullOrEmpty(currentStem) &&
                    Encoding.UTF8.GetByteCount(candidate) <= maxBytes)
                {
                    if (isNameTakenInDir == null || !isNameTakenInDir(candidate))
                    {
                        return candidate;
                    }

                    int suffixIndex = 1;
                    while (true)
                    {
                        string suffix = $"_{suffixIndex}";
                        int maxStemBytes = maxBytes - extBytes - Encoding.UTF8.GetByteCount(suffix);
                        while (elements.Count > 0 &&
                               Encoding.UTF8.GetByteCount(string.Concat(elements).TrimEnd(' ', '.')) > maxStemBytes)
                        {
                            elements.RemoveAt(elements.Count - 1);
                        }

                        string baseStem = string.Concat(elements).TrimEnd(' ', '.');
                        if (string.IsNullOrEmpty(baseStem))
                        {
                            baseStem = "file";
                        }

                        string numberedCandidate = baseStem + suffix + ext;
                        if (!isNameTakenInDir(numberedCandidate))
                        {
                            return numberedCandidate;
                        }
                        suffixIndex++;
                    }
                }

                elements.RemoveAt(elements.Count - 1);
            }

            string fallbackBase = "renamed_file";
            string fallbackCandidate = fallbackBase + ext;
            if (isNameTakenInDir != null && isNameTakenInDir(fallbackCandidate))
            {
                int idx = 1;
                while (isNameTakenInDir($"{fallbackBase}_{idx}{ext}"))
                {
                    idx++;
                }
                return $"{fallbackBase}_{idx}{ext}";
            }
            return fallbackCandidate;
        }

        public static List<RenameResultItem> RenameOverlongFiles(string rootDir, IEnumerable<OverlongFileItem> items)
        {
            var results = new List<RenameResultItem>();
            string normalizedRoot = string.IsNullOrWhiteSpace(rootDir) ? "" : Path.GetFullPath(rootDir.Trim());

            foreach (var item in items)
            {
                var result = new RenameResultItem
                {
                    OldFullPath = item.FullPath,
                    OldRelativePath = item.RelativePath,
                    OldFileName = item.FileName
                };

                try
                {
                    if (!File.Exists(item.FullPath))
                    {
                        result.Success = false;
                        result.ErrorMessage = "File no longer exists.";
                        results.Add(result);
                        continue;
                    }

                    string dir = Path.GetDirectoryName(item.FullPath) ?? normalizedRoot;
                    string newFileName = TruncateFileNameStrictlyUnder255Bytes(
                        item.FileName,
                        candidate =>
                        {
                            string candidatePath = Path.Combine(dir, candidate);
                            return !string.Equals(candidatePath, item.FullPath, StringComparison.OrdinalIgnoreCase) &&
                                   File.Exists(candidatePath);
                        },
                        TruncateTargetMaxBytes);

                    string newFullPath = Path.Combine(dir, newFileName);
                    File.Move(item.FullPath, newFullPath);

                    result.NewFileName = newFileName;
                    result.NewFullPath = newFullPath;
                    result.NewRelativePath = !string.IsNullOrEmpty(normalizedRoot) && newFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        ? Path.GetRelativePath(normalizedRoot, newFullPath).Replace('\\', '/')
                        : newFileName;
                    result.Success = true;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                }

                results.Add(result);
            }

            return results;
        }
    }
}
