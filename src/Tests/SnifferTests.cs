// Task 2 单元测试：Sniffer（真实格式识别 / 伪装后缀 / 网页陷阱 / 下载未完成 / 头部损坏）。
// 前 8 条用例的名字与期望值逐字来自 task-2-brief.md Step 1，仅做一处必要改写：
//   C# 没有 \3 \7 \1 \5 \6 这类八进制转义（csc 报 CS1009），故 brief 的 B("PK\3\4")
//   一律写成 B("PK\x03\x04")，字节值经 csc 实测逐一核对（见 task-2-report.md）。
// 其余用例覆盖 brief Produces 里 Step 1 未触碰的签名表成员与 HasEocdInTail。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System.IO;
using Rerar.Core;

internal sealed class SnifferTests : TestBase
{
    public static void Run()
    {
        // ---- brief Step 1 的 8 条（用例名与期望值逐字照抄）----
        H.Run("Sniffer.Zip", delegate { AssertEq(Sniffer.Classify(TestEnv.B("PK\x03\x04"), 10, "a.zip"), SniffKind.Zip); });
        H.Run("Sniffer.Rar5", delegate { AssertEq(Sniffer.Classify(TestEnv.B("Rar!\x1a\x07\x01\x00"), 10, "a.rar"), SniffKind.Rar5); });
        H.Run("Sniffer.SevenZip", delegate { AssertEq(Sniffer.Classify(TestEnv.B("7z\xbc\xaf\x27\x1c"), 10, "a.7z"), SniffKind.SevenZip); });
        H.Run("Sniffer.CloakedJpgIsZip", delegate { AssertEq(Sniffer.Classify(TestEnv.B("PK\x03\x04"), 10, "a.jpg"), SniffKind.Zip); });
        H.Run("Sniffer.HtmlIsNotArchive", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("<!DOCTYPE html>"), 20, "a.zip"), SniffKind.Html); });
        H.Run("Sniffer.InProgressSuffix", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("PK\x03\x04"), 10, "a.zip.crdownload"), SniffKind.InProgressDownload); });
        H.Run("Sniffer.ZeroByte", delegate { AssertEq(Sniffer.Classify(new byte[0], 0, "a.zip"), SniffKind.Empty); });
        H.Run("Sniffer.DamagedHeaderFromEocd", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\0\0\0\0"), 100, "a.zip", TestEnv.B("garbagegarbagePK\x05\x06")), SniffKind.DamagedHeader); });

        // ---- 签名表其余成员：brief Step 3「按签名表匹配头部」+ 规格 §6.3 第 1 档 ----
        H.Run("Sniffer.Rar4", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("Rar!\x1a\x07\x00"), 10, "a.rar"), SniffKind.Rar); });
        H.Run("Sniffer.Gzip", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\x1f\x8b\x08"), 10, "a.gz"), SniffKind.Gzip); });
        H.Run("Sniffer.Bzip2", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("BZh9"), 10, "a.bz2"), SniffKind.Bzip2); });
        H.Run("Sniffer.Xz", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\u00fd7zXZ\0"), 10, "a.xz"), SniffKind.Xz); });
        H.Run("Sniffer.Tar", delegate {
            byte[] head = new byte[512];
            byte[] magic = TestEnv.B("ustar");                 // tar 魔数在偏移 257（规格 §6.3）
            for (int i = 0; i < magic.Length; i++) { head[257 + i] = magic[i]; }
            AssertEq(Sniffer.Classify(head, 10240, "a.tar"), SniffKind.Tar); });
        H.Run("Sniffer.TarMagicMustSitAtOffset257", delegate {
            byte[] head = new byte[512];                       // 只差一个字节就必须判不出 tar
            byte[] magic = TestEnv.B("ustar");
            for (int i = 0; i < magic.Length; i++) { head[256 + i] = magic[i]; }
            AssertEq(Sniffer.Classify(head, 10240, "a.tar"), SniffKind.Unknown); });
        H.Run("Sniffer.UnknownForUnrecognizedHead", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("hello world"), 11, "a.zip"), SniffKind.Unknown);
            AssertEq(Sniffer.Classify(null, 11, "a.zip"), SniffKind.Unknown); });

        // ---- Html 档的边界：大小写不敏感 + 跳过前导空白（brief Step 3）----
        H.Run("Sniffer.HtmlWithLeadingWhitespaceAndMixedCase", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B(" \t\r\n<HTML lang=\"en\">"), 40, "a.zip"), SniffKind.Html); });
        H.Run("Sniffer.XmlDeclarationIsHtmlTrap", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("<?xml version=\"1.0\"?>"), 40, "a.7z"), SniffKind.Html); });

        // ---- 下载中后缀全集（brief Step 3 的后缀集）----
        H.Run("Sniffer.AllInProgressSuffixes", delegate {
            string[] suffixes = TestEnv.F(
                ".crdownload", ".part", ".partial", ".!ut", ".td", ".xltd", ".baiduyun.p.downloading");
            string mismatch = "";                              // 记下不匹配的后缀，失败信息可直接定位到是哪一个
            foreach (string suffix in suffixes) {
                SniffKind kind = Sniffer.Classify(TestEnv.B("PK\x03\x04"), 10, "a.zip" + suffix);
                if (kind != SniffKind.InProgressDownload) { mismatch = suffix + " -> " + kind; }
            }
            AssertEq(mismatch, ""); });

        // ---- DamagedHeader 档：规格 §6.3 第 2 档的两种 zip 尾部标记 ----
        H.Run("Sniffer.DamagedHeaderFromCentralDirectory", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\0\0\0\0"), 100, "a.zip", TestEnv.B("junkPK\x01\x02junk")), SniffKind.DamagedHeader); });
        H.Run("Sniffer.JunkTailStaysUnknown", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\0\0\0\0"), 100, "a.zip", TestEnv.B("no zip here")), SniffKind.Unknown); });

        // ---- HasEocdInTail：brief Produces 里的公共成员，Step 1 未给用例 ----
        H.Run("HasEocdInTail.FindsEocdMarker", delegate {
            AssertTrue(Sniffer.HasEocdInTail(TestEnv.B("xxPK\x05\x06"))); });
        H.Run("HasEocdInTail.FindsCentralDirectoryMarker", delegate {
            AssertTrue(Sniffer.HasEocdInTail(TestEnv.B("PK\x01\x02"))); });
        H.Run("HasEocdInTail.RejectsNullEmptyAndPartialMagic", delegate {
            AssertFalse(Sniffer.HasEocdInTail(null));
            AssertFalse(Sniffer.HasEocdInTail(new byte[0]));
            AssertFalse(Sniffer.HasEocdInTail(TestEnv.B("PK")));
            AssertFalse(Sniffer.HasEocdInTail(TestEnv.B("PK\x05")));
            AssertFalse(Sniffer.HasEocdInTail(TestEnv.B("plain text"))); });

        // ---- OLE2/CFB 复合文档（.doc/.xls/.ppt/.msi/.msg/.vsd 一族）：数据丢失事故回归 ----
        //
        // 为什么这一档必须落在 **Sniffer**（头部签名）而不是 ArchiveGater（内容身份）：
        // gater 只能看 ArchiveIndex 的条目名，而 CFB 容器的条目全是文档内部流的名字
        //（Data / 1Table / WordDocument / [5]SummaryInformation…），里面**一个 zip 身份标记都没有**
        //（没有 [Content_Types].xml、没有 _rels 段、没有 mimetype），所以 gater 对整族容器是**看不见**的
        // —— 真事故里它就是这么一路 Allow 的。头部 8 字节是确定性的，不需要清单，列表读不出来时
        //（7-Zip 不在 / 头部被写坏）这一档照样成立。
        H.Run("Sniffer.Ole2CompoundDocument", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"), 9728, "legacy.doc"), SniffKind.Ole2);
            // 伪装后缀不改变结论（与 CloakedJpgIsZip 同一条规矩：判定只看字节，不看磁盘上的名字）。
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"), 9728, "legacy.xls"), SniffKind.Ole2);
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"), 9728, "installer.msi.zip"), SniffKind.Ole2);
        });

        // 真夹具（手搓但被 7-Zip 自己认成 Compound 的那份字节）走一遍产品自己的 Classify 形状。
        H.Run("Sniffer.Ole2FixtureIsRecognizedByItsRealBytes", delegate {
            string doc = TestEnv.Ole2Doc;
            byte[] bytes = File.ReadAllBytes(doc);
            AssertEq(Sniffer.Classify(bytes, bytes.Length, Path.GetFileName(doc), bytes), SniffKind.Ole2);
        });

        // 新签名绝不能遮蔽真归档 —— 这是「加一条签名」这类改动唯一真正危险的方向。
        //
        // 逐项检查（写下来是为了让复核者能照着复算，而不是只看一句「测过了」）：
        //   * OLE2 魔数是**偏移 0 的 8 个字节全等**才成立，不做前缀猜测：只差 1 个字节的头部、
        //     以及长度不足 8 字节的短头部（ReadPrefix 在读不动时会给出空/短数组）都必须仍判 Unknown；
        //   * 现有签名表里**没有任何一个签名以 OLE2 的头几个字节开头**（PK\x03\x04 / Rar!\x1a\x07… /
        //     7z\xbc\xaf\x27\x1c / \x1f\x8b / BZh / \xfd7zXZ / ustar@257 首字节分别是 0x50、0x52、
        //     0x37、0x1f、0x42、0xfd、0x75，而 OLE2 首字节是 0xD0）—— 所以两个集合不可能重叠，
        //     也就不存在「谁先判」的顺序问题；下面用真归档头部逐个复核这条推理；
        //   * 反向也成立：OLE2 魔数不可能是一个真归档的头部 —— 没有任何归档族以 D0 CF 11 E0 开头。
        //（「一个文件既是合法 zip 又带 OLE2 头」在结构上不存在：zip 的偏移 0 必须是 PK\x03\x04 /
        //  PK\x05\x06 / PK\x06\x06 / PK\x07\x08，与 D0 CF 11 E0 互斥。所以这条不可能构造出正例，
        //  能钉的只有「真归档头部照样各自判成自己那一档」+「只差一个字节不判 OLE2」。）
        H.Run("Sniffer.Ole2SignatureCannotShadowRealArchives", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("PK\x03\x04"), 100, "a.zip"), SniffKind.Zip);
            AssertEq(Sniffer.Classify(TestEnv.B("Rar!\x1a\x07\x01\x00"), 100, "a.rar"), SniffKind.Rar5);
            AssertEq(Sniffer.Classify(TestEnv.B("Rar!\x1a\x07\x00"), 100, "a.rar"), SniffKind.Rar);
            AssertEq(Sniffer.Classify(TestEnv.B("7z\xbc\xaf\x27\x1c"), 100, "a.7z"), SniffKind.SevenZip);
            AssertEq(Sniffer.Classify(TestEnv.B("\x1f\x8b\x08"), 100, "a.gz"), SniffKind.Gzip);
            AssertEq(Sniffer.Classify(TestEnv.B("BZh9"), 100, "a.bz2"), SniffKind.Bzip2);
            AssertEq(Sniffer.Classify(TestEnv.B("\u00fd7zXZ\0"), 100, "a.xz"), SniffKind.Xz);

            // 只差最后一个字节 / 只差中间一个字节 ⇒ 不是 OLE2（8 字节全等才算）。
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a"), 100, "a.doc"), SniffKind.Unknown);
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe2"), 100, "a.doc"), SniffKind.Unknown);
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\x00"), 100, "a.doc"), SniffKind.Unknown);
            // 长度不足 8 字节的短头部：不得越界读，也不得判成 OLE2。
            AssertEq(Sniffer.Classify(new byte[] { 0xD0, 0xCF }, 2, "a.doc"), SniffKind.Unknown);
            AssertEq(Sniffer.Classify(new byte[] { 0xD0 }, 1, "a.doc"), SniffKind.Unknown);
        });

        // 0 字节 / 下载中后缀 / 尾部有 EOCD 这几档的**优先级**不能被新签名打乱：
        // Classify 先看长度与后缀、后看签名，所以一个叫 x.doc.crdownload 的 OLE2 仍是「下载未完成」。
        H.Run("Sniffer.Ole2DoesNotDisturbEarlierGates", delegate {
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"), 9728, "x.doc.crdownload"),
                SniffKind.InProgressDownload);
            AssertEq(Sniffer.Classify(TestEnv.B("\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1"), 0, "x.doc"), SniffKind.Empty);
        });
    }
}
