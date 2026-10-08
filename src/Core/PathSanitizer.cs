// Rerar 输出路径消毒与越界判定（规格 §6.11；研究文档「归档内条目名为空或超长」）。
// 纯逻辑，不依赖 UI；只有 Uniquify 需要查文件系统判断目标名是否已被占用。
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.IO;
using System.Text;

namespace Rerar.Core
{
    public static class PathSanitizer
    {
        // 规格 §6.11：长度上限 120 字符；空名兜底 "archive"。
        private const int MaxLength = 120;
        private const string Fallback = "archive";

        // 规格 §6.11 的非法字符集（路径分隔符一并替换，保证输出里不含路径）。
        private static readonly char[] IllegalChars = new char[]
        {
            ':', '?', '*', '<', '>', '|', '"', '\\', '/'
        };

        // 规格 §6.11 的保留设备名（大小写不敏感比较）。
        private static readonly string[] ReservedDeviceNames = new string[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // 可剥离的压缩包扩展名（小写、含前导点）。
        // 只剥「真压缩包」后缀：伪装后缀（如 photo.jpg 实为 zip）保留原样，
        // 与规格 §6.11「归档名去掉压缩包扩展名」一致；容器文档（docx/apk/jar）按 §6.8 被拒绝，
        // 根本不会生成输出目录，故不在此表内。
        private static readonly string[] ArchiveExtensions = new string[]
        {
            ".zip", ".zipx", ".rar", ".7z", ".tar", ".gz", ".tgz",
            ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".zst",
            ".lzh", ".lha", ".cab", ".arj", ".ace", ".iso", ".z", ".lzma"
        };

        // 归档文件名 → 输出目录名（不含路径）。消毒顺序见 task-1-brief.md Step 3。
        public static string Sanitize(string archiveFileName)
        {
            if (archiveFileName == null) { return Fallback; }

            // 1) 去压缩包扩展名（可连续剥：a.tar.gz → a；b.7z.001 → b）
            string s = StripArchiveExtensions(archiveFileName);

            // 2) 非法字符 → '_'；3) 去掉控制字符
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (Array.IndexOf(IllegalChars, c) >= 0) { sb.Append('_'); }
                else if (!char.IsControl(c)) { sb.Append(c); }
            }
            s = sb.ToString();

            // 4) 去尾点与尾空格。放在设备名判定之前：Win32 会把 "con .zip" 仍当作 CON 设备，
            //    先修尾才能让下面这一步把它挡住。
            s = s.TrimEnd('.', ' ');

            // 5) 保留设备名 → 前置 '_'
            if (IsReservedDeviceName(s)) { s = "_" + s; }

            // 6) 截断至 120。截断可能重新引入尾点/尾空格，故再修一次尾。
            if (s.Length > MaxLength) { s = s.Substring(0, MaxLength).TrimEnd('.', ' '); }

            // 7) 空名兜底
            return s.Length == 0 ? Fallback : s;
        }

        // 候选路径必须是 root 的「严格子项」：归一化后根路径 + 分隔符为前缀，且候选严格更长。
        // 序数忽略大小写比较（Windows 语义）；任何异常一律判「不是子项」（安全默认）。
        public static bool IsStrictChild(string rootFullPath, string candidateFullPath)
        {
            if (rootFullPath == null || candidateFullPath == null) { return false; }

            string root;
            string candidate;
            try
            {
                root = TrimTrailingSeparators(Path.GetFullPath(rootFullPath));
                candidate = TrimTrailingSeparators(Path.GetFullPath(candidateFullPath));
            }
            catch (Exception)
            {
                return false;
            }

            if (candidate.Length <= root.Length) { return false; }
            if (string.Compare(candidate, 0, root, 0, root.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            char sep = candidate[root.Length];
            return sep == Path.DirectorySeparatorChar || sep == Path.AltDirectorySeparatorChar;
        }

        // 目标目录已存在且非空 → "名字 (2)"、"名字 (3)"…（规格 §6.11：绝不覆盖，对应 I2）。
        // 空目录可以复用；被同名「文件」占用的路径原样返回，由调用方按规格 §6.11 中止该归档
        //（**唯一的例外**：那个文件就是源归档自己 —— 伪装后缀的包消毒后目标名等于源归档路径，
        // Extractor.ResolveTarget 对它换名继续而不是中止，理由见那里）。
        public static string Uniquify(string desiredDir)
        {
            string candidate = desiredDir;
            int n = 2;
            while (IsOccupiedDirectory(candidate))
            {
                candidate = desiredDir + " (" + n + ")";
                n++;
            }
            return candidate;
        }

        // 去掉压缩包扩展名；可连续剥，分卷数字后缀（.001）也算压缩包后缀。
        private static string StripArchiveExtensions(string name)
        {
            while (true)
            {
                int dot = name.LastIndexOf('.');
                if (dot < 0) { return name; }
                if (dot == 0) { return string.Empty; }   // ".zip" 这类只有扩展名的名字：基名为空
                if (!IsArchiveExtension(name.Substring(dot))) { return name; }
                name = name.Substring(0, dot);
            }
        }

        private static bool IsArchiveExtension(string extensionWithDot)
        {
            string e = extensionWithDot.ToLowerInvariant();
            if (Array.IndexOf(ArchiveExtensions, e) >= 0) { return true; }

            // 分卷数字后缀：.001 .002 …（恰好三位数字）
            return e.Length == 4
                && e[1] >= '0' && e[1] <= '9'
                && e[2] >= '0' && e[2] <= '9'
                && e[3] >= '0' && e[3] <= '9';
        }

        // Win32 设备名判定：取第一个点之前的部分（"CON.txt" 同样是设备名），大小写不敏感。
        private static bool IsReservedDeviceName(string name)
        {
            int dot = name.IndexOf('.');
            string head = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ');
            return Array.IndexOf(ReservedDeviceNames, head.ToUpperInvariant()) >= 0;
        }

        private static string TrimTrailingSeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsOccupiedDirectory(string path)
        {
            try
            {
                return Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length > 0;
            }
            catch (Exception)
            {
                // 无法判定时保守视为被占用：宁可换名字，也不冒覆盖的风险。
                return true;
            }
        }
    }
}
