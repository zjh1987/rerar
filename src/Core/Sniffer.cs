// Rerar 归档格式嗅探（规格 §6.3；审计 C3/C4）。
// 纯函数、不碰文件系统、永不抛异常：调用方读好 head / tail 字节后交给这里判定，
// 结论一律是三档判定里的一个 —— 绝不静默丢弃（规格 §6.3 的硬要求）。
//
// 判定顺序（顺序本身是行为的一部分，缺一不可）：
//   1. 文件长度 0 → Empty（规格 §6.3「0 字节成员视为缺卷」）
//   2. 文件名后缀是「下载中」→ InProgressDownload：必须早于签名表，
//      因为没下完的文件常常已经以 PK 开头，但它此刻还不是可用的压缩包
//      （brief 用例 Sniffer.InProgressSuffix 正是钉这一点）
//   3. 偏移 0 的签名表（tar 的魔数在偏移 257）
//   4. 网页陷阱（跳过前导空白、大小写不敏感）
//   5. 尾部有 zip 结尾标记 → DamagedHeader（头部被清零/改写的「防和谐」包，审计 C4）
//   6. 其余 → Unknown
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;

namespace Rerar.Core
{
    // 嗅探结论。除 Unknown 外每种结论都对应一句要如实告诉用户的话（规格 §6.3）。
    public enum SniffKind { Zip, Rar, Rar5, SevenZip, Gzip, Bzip2, Xz, Tar, Html, InProgressDownload, Empty, DamagedHeader, Unknown }

    public static class Sniffer
    {
        // 「下载未完成」后缀集（brief Step 3；规格 §6.3）。含前导点，避免误伤 .part1.rar 这类分卷名。
        private static readonly string[] InProgressSuffixes = new string[]
        {
            ".crdownload", ".part", ".partial", ".!ut", ".td", ".xltd", ".baiduyun.p.downloading"
        };

        // tar 魔数在 512 字节块头的偏移 257（"ustar" 之后的版本号有 "\0" 与 "  " 两种写法，
        // 故只比对这 5 个字节）。
        private const int TarMagicOffset = 257;

        // 判定一个文件到底是什么。head = 文件开头若干字节（tar 需 ≥ 262 字节），
        // fileLength = 完整文件长度，fileName = 磁盘上的名字（可能是伪装后缀），
        // tail = 文件结尾若干字节（规格 §6.3 用尾部 64KB；仅供 DamagedHeader 使用）。
        public static SniffKind Classify(byte[] head, long fileLength, string fileName, byte[] tail = null)
        {
            // 只用 == 0：负数表示「调用方没拿到长度」，不能当成空文件，
            // 否则一个头部损坏的包会被误报成「0 字节、缺卷」。
            if (fileLength == 0) { return SniffKind.Empty; }

            if (HasInProgressSuffix(fileName)) { return SniffKind.InProgressDownload; }

            if (head == null) { head = new byte[0]; }

            SniffKind bySignature = MatchHeaderSignature(head);
            if (bySignature != SniffKind.Unknown) { return bySignature; }

            if (LooksLikeHtml(head)) { return SniffKind.Html; }

            if (HasEocdInTail(tail)) { return SniffKind.DamagedHeader; }

            return SniffKind.Unknown;
        }

        // 尾部是否含 zip 的结尾标记。名字照 brief 的 HasEocdInTail，
        // 但按规格 §6.3 第 2 档同时认 EOCD（PK\x05\x06）与中央目录头（PK\x01\x02）：
        // 尾部出现中央目录头同样证明「这是个 zip，只是头部坏了」。
        public static bool HasEocdInTail(byte[] tail)
        {
            if (tail == null) { return false; }

            for (int i = 0; i + 4 <= tail.Length; i++)
            {
                if (tail[i] != (byte)'P' || tail[i + 1] != (byte)'K') { continue; }

                bool eocd = tail[i + 2] == 0x05 && tail[i + 3] == 0x06;
                bool centralDirectory = tail[i + 2] == 0x01 && tail[i + 3] == 0x02;
                if (eocd || centralDirectory) { return true; }
            }
            return false;
        }

        // 偏移 0（tar 为偏移 257）的签名表。Rar5 必须先于 Rar 判定：
        // 两者的前 7 字节完全相同，只有第 8 字节区分（\x01\x00 vs \x00）。
        private static SniffKind MatchHeaderSignature(byte[] head)
        {
            if (StartsWith(head, "PK\x03\x04")) { return SniffKind.Zip; }
            if (StartsWith(head, "Rar!\x1a\x07\x01\x00")) { return SniffKind.Rar5; }
            if (StartsWith(head, "Rar!\x1a\x07\x00")) { return SniffKind.Rar; }
            if (StartsWith(head, "7z\xbc\xaf\x27\x1c")) { return SniffKind.SevenZip; }
            if (StartsWith(head, "\x1f\x8b")) { return SniffKind.Gzip; }
            if (StartsWith(head, "BZh")) { return SniffKind.Bzip2; }
            if (StartsWith(head, "\u00fd7zXZ\0")) { return SniffKind.Xz; }   // \xfd 之后必须用 \u：\xfd7 会被当成一个字符
            if (MatchesAt(head, TarMagicOffset, "ustar")) { return SniffKind.Tar; }
            return SniffKind.Unknown;
        }

        // 网页陷阱（规格 §6.3）：跳过前导空白后，大小写不敏感地匹配
        // <!DOCTYPE html / <html / <?xml。
        private static bool LooksLikeHtml(byte[] head)
        {
            int i = 0;
            while (i < head.Length && IsAsciiWhitespace(head[i])) { i++; }

            return MatchesAtIgnoreCase(head, i, "<!doctype html")
                || MatchesAtIgnoreCase(head, i, "<html")
                || MatchesAtIgnoreCase(head, i, "<?xml");
        }

        // 后缀匹配用序数忽略大小写（Windows 文件名语义，且不受当前区域影响）。
        private static bool HasInProgressSuffix(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) { return false; }

            foreach (string suffix in InProgressSuffixes)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static bool StartsWith(byte[] head, string signature)
        {
            return MatchesAt(head, 0, signature);
        }

        // 逐字节比对；签名串的每个字符都 < 256，直接按字节取用。
        private static bool MatchesAt(byte[] head, int offset, string signature)
        {
            if (head == null) { return false; }
            if (head.Length - offset < signature.Length) { return false; }

            for (int i = 0; i < signature.Length; i++)
            {
                if (head[offset + i] != (byte)signature[i]) { return false; }
            }
            return true;
        }

        // 同上，但比对前把 head 的字节折成小写 ASCII（签名串本身已是小写）。
        private static bool MatchesAtIgnoreCase(byte[] head, int offset, string lowerCaseSignature)
        {
            if (head.Length - offset < lowerCaseSignature.Length) { return false; }

            for (int i = 0; i < lowerCaseSignature.Length; i++)
            {
                if (ToLowerAscii(head[offset + i]) != (byte)lowerCaseSignature[i]) { return false; }
            }
            return true;
        }

        private static byte ToLowerAscii(byte b)
        {
            return (b >= (byte)'A' && b <= (byte)'Z') ? (byte)(b + 32) : b;
        }

        private static bool IsAsciiWhitespace(byte b)
        {
            return b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D || b == 0x0C || b == 0x0B;
        }
    }
}
