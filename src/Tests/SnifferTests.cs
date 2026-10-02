// Task 2 单元测试：Sniffer（真实格式识别 / 伪装后缀 / 网页陷阱 / 下载未完成 / 头部损坏）。
// 前 8 条用例的名字与期望值逐字来自 task-2-brief.md Step 1，仅做一处必要改写：
//   C# 没有 \3 \7 \1 \5 \6 这类八进制转义（csc 报 CS1009），故 brief 的 B("PK\3\4")
//   一律写成 B("PK\x03\x04")，字节值经 csc 实测逐一核对（见 task-2-report.md）。
// 其余用例覆盖 brief Produces 里 Step 1 未触碰的签名表成员与 HasEocdInTail。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

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
    }
}
