// Task 5 单元测试：ArchiveGater（递归门控，规格 §6.4 / 不变式 I4；审计 C1）。
//
// 前 4 条用例的名字与期望值**逐字**来自 task-5-brief.md Step 1（含 brief 里 `string why;` 的写法）；
// 其后每条都用**真 zip / 真清单**钉住一个「判错就会静默放行、进而拆散用户文档」的分支：
//   * 清单不可用（读不出来 / 0 个文件条目）时绝不当成「安全可递归」；
//   * OOXML 里还嵌着别的 zip 时照样拒绝（「包里有包」不是放行理由）；
//   * 标记名大小写不敏感；
//   * «含嵌套 zip 的普通包»照样放行 —— 否则本工具的主用例（递归）整体失效。
//
// 尾部另有一组用例属于「规格 §6.4 特征表被裁定为欠包含」之后补上的规则（不变式 I4 是权威）：
//   * Apple iWork（Index/Document.iwa、Metadata/DocumentIdentifier）；
//   * JAR / APK 的两个标记**各自单独**就够（原表写成合取，会让真容器整包放行）；
//   * Python wheel（*.dist-info/ 路径段）、VSIX（extension.vsixmanifest）。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using Rerar.Core;

internal sealed class ArchiveGaterTests : TestBase
{
    public static void Run()
    {
        // ---- brief Step 1 的 4 条（用例名与期望值逐字照抄）----
        H.Run("Gater.RefusesDocx", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexDocx, out why), GateVerdict.ContainerDocument); });
        H.Run("Gater.RefusesApk", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexApk, out why), GateVerdict.ContainerDocument); });
        H.Run("Gater.RefusesJar", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexJar, out why), GateVerdict.ContainerDocument); });
        H.Run("Gater.AllowsPlainZip", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexPlain, out why), GateVerdict.Allow); });

        // ---- 拒绝必须带得出理由（Produces 的 out string reason；界面要如实说明为什么没递归）----
        H.Run("Gater.RefusalExplainsWhy", delegate {
            string docxWhy; AssertEq(ArchiveGater.Judge(TestEnv.IndexDocx, out docxWhy), GateVerdict.ContainerDocument);
            string jarWhy; AssertEq(ArchiveGater.Judge(TestEnv.IndexJar, out jarWhy), GateVerdict.ContainerDocument);
            AssertTrue(docxWhy.Length > 0);
            AssertTrue(jarWhy.Length > 0); });

        // ---- 容器身份压过「包里有包」：OOXML 里嵌入一个**真** zip 也照样拒绝 ----
        H.Run("Gater.RefusesDocxWithNestedZip", delegate {
            AssertTrue(TestEnv.IndexDocxWithNestedZip.FileCount > TestEnv.IndexDocx.FileCount);   // 夹具真多了一个成员
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexDocxWithNestedZip, out why), GateVerdict.ContainerDocument); });

        // ---- zip 条目名是大小写敏感的字节串：标记匹配必须大小写不敏感 ----
        H.Run("Gater.RefusesUpperCasedOoxmlMarkers", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexDocxUpperCase, out why), GateVerdict.ContainerDocument); });

        // ---- 规格 §6.4 第 4/5 行（EPUB / ODF 的 mimetype 标记条目）----
        H.Run("Gater.RefusesEpubMimetypeMarker", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexEpub, out why), GateVerdict.ContainerDocument); });

        // ---- 规格 §6.4 表被裁定为「欠包含」之后补的 6 条（不变式 I4 是约束权威，不是表里那一行）----
        // 共同形状：单凭一个标记就足以拒绝。漏判的一侧是「用户的文档被拆散 + 原文件被删」，
        // 多判的一侧只是「少递归一层，用户可在界面上强制按压缩包尝试」，两侧代价完全不对称。

        // Apple iWork（Pages/Numbers/Keynote）：zip 包，主条目是 Index/Document.iwa。
        // 它一个现有标记都不带，只靠「其余 ⇒ 放行」就会走进拆散用户文档的那条路。
        H.Run("Gater.RefusesIWorkIndexDocument", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexIWork, out why), GateVerdict.ContainerDocument);
            AssertTrue(why.IndexOf("iwa", StringComparison.Ordinal) >= 0); });      // 拒绝的理由必须指名是哪种身份

        H.Run("Gater.RefusesIWorkMetadataIdentifier", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexIWorkIdentifier, out why), GateVerdict.ContainerDocument);
            AssertTrue(why.IndexOf("DocumentIdentifier", StringComparison.Ordinal) >= 0); });

        // JAR 的两个半边**各自单独**都够（Ant <zip> 打的 jar 没有 MANIFEST.MF；资源 jar 没有 .class）。
        // 规格的 `+` 描述的是常见形状，不是「必须两半齐全」的许可：一旦要求配对，
        // classes-only / manifest-only 的真容器就整包放行、被递归拆散。
        H.Run("Gater.RefusesJarWithClassesOnly", delegate {
            AssertEq(TestEnv.IndexJarClassesOnly.FileCount, 2);
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexJarClassesOnly, out why), GateVerdict.ContainerDocument); });

        H.Run("Gater.RefusesJarWithManifestOnly", delegate {
            AssertEq(TestEnv.IndexJarManifestOnly.FileCount, 1);
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexJarManifestOnly, out why), GateVerdict.ContainerDocument); });

        // APK：只有 AndroidManifest.xml、没有 classes.dex（无 dex 的 split APK / 资源包）。
        H.Run("Gater.RefusesApkWithoutDex", delegate {
            AssertEq(TestEnv.IndexApkManifestOnly.FileCount, 1);
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexApkManifestOnly, out why), GateVerdict.ContainerDocument); });

        // Python wheel：任一 *.dist-info/ 路径段。
        H.Run("Gater.RefusesPythonWheel", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexWheel, out why), GateVerdict.ContainerDocument);
            AssertTrue(why.IndexOf("dist-info", StringComparison.Ordinal) >= 0); });

        // VSIX：extension.vsixmanifest。
        H.Run("Gater.RefusesVsix", delegate {
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexVsix, out why), GateVerdict.ContainerDocument);
            AssertTrue(why.IndexOf("vsixmanifest", StringComparison.Ordinal) >= 0); });

        // ---- 清单读不出来（损坏包，真 7-Zip 退出码 2）：不知道身份就绝不递归 ----
        H.Run("Gater.RefusesFailedListing", delegate {
            ArchiveIndex corrupt = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.CorruptZip, null);
            AssertTrue(corrupt.ListingFailed);                 // 夹具必须真的列不出来，否则这条测的是别的
            string why; AssertEq(ArchiveGater.Judge(corrupt, out why), GateVerdict.ContainerDocument); });

        // ---- 清单读得出来但 0 个文件条目（单流 bz2：l 成功却无名）：同样没有身份可判 ----
        H.Run("Gater.RefusesEmptyListing", delegate {
            ArchiveIndex singleStream = SevenZipIndex.Read(TestEnv.SevenZip, TestEnv.SingleStreamBz2, null);
            AssertFalse(singleStream.ListingFailed);           // 这一档是「成功但无基线」，不是「读失败」
            AssertEq(singleStream.FileCount, 0);
            string why; AssertEq(ArchiveGater.Judge(singleStream, out why), GateVerdict.ContainerDocument); });

        // ---- 「清单失败但条目非空」这一档（Task 4 记录的截断包形状）：ListingFailed 必须压过条目表 ----
        H.Run("Gater.RefusesPartialListingDespiteEntries", delegate {
            ArchiveIndex truncated = new ArchiveIndex();
            truncated.ListingFailed = true;
            truncated.FileCount = 1;                           // 截断的 tar：l 失败，却给了半截清单
            truncated.Entries.Add(new IndexEntry { Path = "a.txt", Size = 3 });
            string why; AssertEq(ArchiveGater.Judge(truncated, out why), GateVerdict.ContainerDocument); });

        // ---- 普通包 + 真嵌套 zip 必须放行：递归功能本身依赖这条 ----
        H.Run("Gater.AllowsZipWithNestedZip", delegate {
            AssertTrue(TestEnv.IndexPlainWithNestedZip.FileCount > TestEnv.IndexPlain.FileCount);
            string why; AssertEq(ArchiveGater.Judge(TestEnv.IndexPlainWithNestedZip, out why), GateVerdict.Allow); });

        // ---- 传 null 索引是调用方 bug（不是归档状态）：大声抛出来，绝不静默放行 ----
        H.Run("Gater.RejectsNullIndex", delegate {
            bool threw = false;
            try { string why; ArchiveGater.Judge(null, out why); }
            catch (ArgumentNullException) { threw = true; }
            AssertTrue(threw); });
    }
}
