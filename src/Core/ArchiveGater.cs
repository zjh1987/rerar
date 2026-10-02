// Rerar 递归门控 ArchiveGater（规格 §6.4、不变式 I4；审计 C1）。
//
// 为什么需要它：`.docx/.xlsx/.pptx/.jar/.apk/.epub/.odt/.whl/.nupkg` 本质都是 zip。
// 只按魔数（PK\x03\x04）放行递归，就会把用户的 Word 文档拆成上千个散文件；若同时又开了
//「解压成功后删除原包」，就是**用户文档被销毁**，而每一步都返回 exit 0 —— 本项目最危险的
// 静默错误结果路径。所以门控必须按**内容身份**（包里的条目名）判定，而不是魔数或磁盘后缀。
//
// 判定顺序（顺序本身是行为的一部分，不可重排）：
//   1. 清单不可用（ListingFailed，或 0 个文件条目）⇒ **拒绝递归**。
//      读不出清单就谈不上身份；绝不能把「不知道」当成「安全」。
//      （Task 4 记录过这一档最危险的方向：截断的包 `l` 会失败却给出**非空**的半截清单，
//        所以 ListingFailed 必须压过条目表，而不是只看「有没有条目」。）
//   2. OOXML（[Content_Types].xml 或 _rels 路径段）⇒ 拒绝
//   3. APK（AndroidManifest.xml + classes.dex）⇒ 拒绝
//   4. JAR（META-INF/MANIFEST.MF + 任一 .class）⇒ 拒绝
//   5. EPUB/ODF（mimetype 标记条目）⇒ 拒绝
//   6. 其余 ⇒ 放行（视为普通归档，允许递归）
//
// 三处刻意的判定取舍，方向一律是「宁可少递归一层，也绝不拆散用户文档」：
//   * **按路径段匹配，而不是只匹配根级**：规格的标记名是真实容器里的根级条目名，但一个 zip
//     也可能在更深一层装着「解开过的 docx/APK」。只认根级会让那种包被递归拆散；多认一层只会
//     多拒绝（用户仍然拿得到文件，只是少递归一层）。
//   * **大小写不敏感**：zip 条目名是大小写敏感的字节串，各家打包工具给的大小写五花八门，而
//     标记名的大小写不携带任何语义。
//   * **mimetype 只认「标记条目存在」，不比对内容**：ArchiveIndex 里只有 Path/Size/目录/重解析点，
//     **没有条目内容**，本任务也没有任何读内容的入口，所以无法比对 `application/epub+zip` 这个
//     字符串。OCF 容器规范要求包里根级有一个成员叫 mimetype，因此「根级存在 mimetype 条目」
//     本身就是容器身份特征；代价是「恰好带一个根级 mimetype 文件的普通包」也会被拒 —— 那是
//     安全的一侧（不递归、绝不删原文件），而漏判的一侧是文档被拆散。
//
// 「强制递归容器文档」的高级开关属于调用方（界面，规格 §6.4 默认关）：本类只给判定与理由，
// 不读配置、不碰文件系统、不起进程。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;

namespace Rerar.Core
{
    // 门控结论。ContainerDocument = 按内容身份认出这是「容器文档」：默认拒绝递归、绝不删原文件。
    public enum GateVerdict { Allow, ContainerDocument }

    public static class ArchiveGater
    {
        // 身份标记（规格 §6.4 表，名字逐字）。
        private const string ContentTypesMarker = "[Content_Types].xml";
        private const string RelsSegment = "_rels";
        private const string AndroidManifestMarker = "AndroidManifest.xml";
        private const string DexMarker = "classes.dex";
        private const string ManifestDirSegment = "META-INF";
        private const string ManifestName = "MANIFEST.MF";
        private const string ClassSuffix = ".class";
        private const string MimeTypeMarker = "mimetype";

        // 判定一个归档能否递归。reason 永远会被赋值（拒绝时说明身份或「无法判定」，放行时也说明
        // 依据），界面据此如实告诉用户为什么没有往下拆。
        public static GateVerdict Judge(ArchiveIndex index, out string reason)
        {
            reason = "";
            if (index == null) { throw new ArgumentNullException("index"); }

            // 1) 没有可用的清单 ⇒ 无法判定身份 ⇒ 拒绝递归。
            //    这里刻意不看「条目表里有没有东西」就返回：截断的包会给出「失败 + 非空半截清单」，
            //    只有 ListingFailed 优先才能挡住它（Task 4 记录的形状）。
            if (index.ListingFailed)
            {
                reason = "该归档的清单读取失败，无法判定内容身份：为安全起见按容器文档处理（不递归、绝不删除原文件）。";
                return GateVerdict.ContainerDocument;
            }
            if (index.Entries == null || index.FileCount == 0)
            {
                reason = "该归档的清单里没有文件条目（单流格式、空包或只有目录条目），无法判定内容身份：为安全起见按容器文档处理（不递归、绝不删除原文件）。";
                return GateVerdict.ContainerDocument;
            }

            // 2) OOXML：docx / xlsx / pptx。
            if (LooksLikeOoxml(index.Entries))
            {
                reason = "内容身份为 OOXML 文档（含 " + ContentTypesMarker + " 或 " + RelsSegment +
                    "/）：默认拒绝递归，绝不删除原文件。";
                return GateVerdict.ContainerDocument;
            }

            // 3) APK：AndroidManifest.xml **与** classes.dex 同时存在才算（规格用 "+" 表示成对特征）。
            if (HasEntryNamed(index.Entries, AndroidManifestMarker) && HasEntryNamed(index.Entries, DexMarker))
            {
                reason = "内容身份为 Android 应用包 APK（含 " + AndroidManifestMarker + " 与 " + DexMarker +
                    "）：默认拒绝递归，绝不删除原文件。";
                return GateVerdict.ContainerDocument;
            }

            // 4) JAR：META-INF/MANIFEST.MF **与** 任一 .class 同时存在才算。
            if (HasManifestEntry(index.Entries) && HasClassEntry(index.Entries))
            {
                reason = "内容身份为 Java 归档 JAR（含 " + ManifestDirSegment + "/" + ManifestName +
                    " 与 .class）：默认拒绝递归，绝不删除原文件。";
                return GateVerdict.ContainerDocument;
            }

            // 5) EPUB / ODF：OCF 容器要求的 mimetype 标记条目（内容无法比对，见文件头）。
            if (HasMimetypeMarker(index.Entries))
            {
                reason = "内容身份为 EPUB/ODF 容器（含 " + MimeTypeMarker +
                    " 标记条目）：默认拒绝递归，绝不删除原文件。";
                return GateVerdict.ContainerDocument;
            }

            reason = "未发现容器文档的内容身份特征，视为普通归档：允许递归。";
            return GateVerdict.Allow;
        }

        // ---------------- 身份特征 ----------------

        // OOXML：任一位置出现 [Content_Types].xml 条目，或任一位置出现 _rels 路径段。
        // 真实 docx 两者都有：根级 `[Content_Types].xml` 与 `_rels/.rels`，关系部件还会更深
        // （`word/_rels/document.xml.rels`）；只认根级的话，一个「解开过一层」的 docx 目录
        // 被打包后就会被递归拆散。
        private static bool LooksLikeOoxml(List<IndexEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                IndexEntry entry = entries[i];
                if (entry == null) { continue; }

                string[] segments = Segments(entry.Path);
                for (int s = 0; s < segments.Length; s++)
                {
                    if (EqualsIgnoreCase(segments[s], RelsSegment)) { return true; }
                }
                if (LastSegmentEquals(entry.Path, ContentTypesMarker)) { return true; }
            }
            return false;
        }

        // 有没有一个**文件**条目的末段叫这个名字（任意深度）。
        private static bool HasEntryNamed(List<IndexEntry> entries, string name)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                IndexEntry entry = entries[i];
                if (entry == null || entry.IsDirectory) { continue; }
                if (LastSegmentEquals(entry.Path, name)) { return true; }
            }
            return false;
        }

        // 有没有一条 <…>/META-INF/<…>/MANIFEST.MF 的路径（META-INF 必须是 MANIFEST.MF 之上的
        // 某个路径段，这样 `META-INF/MANIFEST.MF` 与 `app/META-INF/MANIFEST.MF` 都算，
        // 而 `fooMANIFEST.MF` 或 `META-INF/other.txt` 不算）。
        private static bool HasManifestEntry(List<IndexEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                IndexEntry entry = entries[i];
                if (entry == null || entry.IsDirectory) { continue; }

                string[] segments = Segments(entry.Path);
                if (segments.Length < 2) { continue; }
                if (!EqualsIgnoreCase(segments[segments.Length - 1], ManifestName)) { continue; }

                for (int s = 0; s < segments.Length - 1; s++)
                {
                    if (EqualsIgnoreCase(segments[s], ManifestDirSegment)) { return true; }
                }
            }
            return false;
        }

        // 任一 .class 文件条目（后缀匹配，不是 Contains：`a.classx` 不算）。
        private static bool HasClassEntry(List<IndexEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                IndexEntry entry = entries[i];
                if (entry == null || entry.IsDirectory) { continue; }

                string last = LastSegment(entry.Path);
                if (last.Length > ClassSuffix.Length &&
                    last.EndsWith(ClassSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // 根级、或任意深度下的一个名叫 mimetype 的**文件**条目。
        // 要求不是目录：Python 等项目里 `mimetype/` 这样的包目录很常见，目录不该触发这条。
        private static bool HasMimetypeMarker(List<IndexEntry> entries)
        {
            return HasEntryNamed(entries, MimeTypeMarker);
        }

        // ---------------- 路径工具 ----------------

        // zip 里的路径分隔符在 7-Zip 的 `l -slt` 上可能是 '\'（Windows）也可能是 '/'（跨平台包），
        // 目录条目还可能带尾部分隔符，所以一律按两种分隔符切、丢掉空段。
        private static string[] Segments(string path)
        {
            if (string.IsNullOrEmpty(path)) { return new string[0]; }
            return path.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string LastSegment(string path)
        {
            string[] segments = Segments(path);
            if (segments.Length == 0) { return ""; }
            return segments[segments.Length - 1];
        }

        private static bool LastSegmentEquals(string path, string name)
        {
            return EqualsIgnoreCase(LastSegment(path), name);
        }

        private static bool EqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
