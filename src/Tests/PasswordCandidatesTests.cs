// Task 7 单元测试：PasswordCandidates（密码候选阶梯、变体展开、同目录线索采集）。
//
// 前 6 条用例的名称、输入与期望值逐字来自 task-7-brief.md Step 1。唯一的改写是把裸 F(...) 写成
// TestEnv.F(...)：F 是 TestEnv 的静态方法，而 C# 5 没有 using static，裸 F 是 error CS0103
//（F 的语义由 task-1-report.md 裁定：原样打包成数组，返回裸文件名而不是路径）。
//
// 末尾 7 条用例覆盖 brief Step 3 与规格 §6.5 写明、但 brief 未给用例的行为：保序去重、
// 空/纯空白候选被丢弃、原文变体、各前缀形式（含全角冒号）、URL 只取主机名、字典先于线索。

using System.Collections.Generic;
using Rerar.Core;

internal sealed class PasswordCandidatesTests : TestBase
{
    public static void Run()
    {
        H.Run("Pwd.VariantsIncludeTrimmedAndFullWidthNormalised", delegate {
            List<string> v = new List<string>(PasswordCandidates.Variants("１２３４５６ "));
            AssertTrue(v.Contains("１２３４５６")); AssertTrue(v.Contains("123456")); });
        H.Run("Pwd.VariantsStripZeroWidthAndNbsp", delegate {
            List<string> v = new List<string>(PasswordCandidates.Variants("ab\u200bcd\u00a0"));
            AssertTrue(v.Contains("abcd")); });
        H.Run("Pwd.ClueFromFileName", delegate {
            List<string> c = new List<string>(PasswordCandidates.CluesFromFileNames(TestEnv.F("【解压密码：hello123】movie.rar")));
            AssertTrue(c.Contains("hello123")); });
        H.Run("Pwd.ClueFromTextFile", delegate {
            List<string> c = new List<string>(PasswordCandidates.CluesFromTextFile("下载说明\n解压密码： pw=abc123 \n"));
            AssertTrue(c.Contains("abc123")); });
        H.Run("Pwd.ClueFromUrlHost", delegate {
            List<string> c = new List<string>(PasswordCandidates.CluesFromTextFile("http://www.example.com/x"));
            AssertTrue(c.Contains("www.example.com") || c.Contains("example.com")); });
        H.Run("Pwd.OrderManualFirstThenSucceeded", delegate {
            List<string> l = new List<string>(PasswordCandidates.Build("M", TestEnv.F("S1", "S2"), TestEnv.F("D1"), TestEnv.F("C1")));
            AssertEq(l[0], "M"); AssertEq(l[1], "S1"); AssertEq(l[2], "S2"); });

        H.Run("Pwd.DedupesKeepingFirstOccurrence", delegate {
            List<string> l = new List<string>(PasswordCandidates.Build("M", TestEnv.F("M", "S1"), TestEnv.F("S1", "S1"), TestEnv.F("M")));
            AssertEq(l.Count, 2); AssertEq(l[0], "M"); AssertEq(l[1], "S1"); });
        H.Run("Pwd.DropsBlankAndNullCandidates", delegate {
            AssertEq(new List<string>(PasswordCandidates.Variants(null)).Count, 0);
            AssertEq(new List<string>(PasswordCandidates.Variants("   \u00a0\u200b")).Count, 0);
            AssertEq(new List<string>(PasswordCandidates.Build(null, null, null, null)).Count, 0);
            AssertEq(new List<string>(PasswordCandidates.Build("  ", TestEnv.F(""), TestEnv.F("\t"), TestEnv.F(" \u3000"))).Count, 0); });
        H.Run("Pwd.VariantsKeepOriginalUntrimmed", delegate {
            List<string> v = new List<string>(PasswordCandidates.Variants(" abc "));
            AssertTrue(v.Contains(" abc ")); AssertTrue(v.Contains("abc")); });
        H.Run("Pwd.StripsChineseLabelPrefixes", delegate {
            string[] forms = new string[] { "密码：abc", "密码:abc", "解压密码：abc" };
            foreach (string form in forms)
            {
                AssertTrue(new List<string>(PasswordCandidates.Variants(form)).Contains("abc"));
            } });
        H.Run("Pwd.StripsAsciiLabelPrefixes", delegate {
            string[] forms = new string[] { "password=abc", "password: abc", "pwd:abc", "Password=abc" };
            foreach (string form in forms)
            {
                AssertTrue(new List<string>(PasswordCandidates.Variants(form)).Contains("abc"));
            } });
        H.Run("Pwd.UrlClueIsHostNotWholeUrl", delegate {
            List<string> c = new List<string>(PasswordCandidates.CluesFromTextFile("下载地址：http://www.example.com/a/b.html 提取码 0000"));
            AssertTrue(c.Contains("www.example.com"));
            AssertFalse(c.Contains("http://www.example.com/a/b.html")); });
        H.Run("Pwd.LadderPutsDictionaryBeforeClues", delegate {
            List<string> l = new List<string>(PasswordCandidates.Build("M", TestEnv.F("S1"), TestEnv.F("D1"), TestEnv.F("C1")));
            AssertTrue(l.IndexOf("D1") > l.IndexOf("S1"));
            AssertTrue(l.IndexOf("C1") > l.IndexOf("D1")); });
    }
}
