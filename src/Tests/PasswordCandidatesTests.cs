// Task 7 单元测试：PasswordCandidates（密码候选阶梯、变体展开、同目录线索采集）。
//
// 前 6 条用例的名称、输入与期望值逐字来自 task-7-brief.md Step 1。唯一的改写是把裸 F(...) 写成
// TestEnv.F(...)：F 是 TestEnv 的静态方法，而 C# 5 没有 using static，裸 F 是 error CS0103
//（F 的语义由 task-1-report.md 裁定：原样打包成数组，返回裸文件名而不是路径）。
//
// 末尾 7 条用例覆盖 brief Step 3 与规格 §6.5 写明、但 brief 未给用例的行为：保序去重、
// 空/纯空白候选被丢弃、原文变体、各前缀形式（含全角冒号）、URL 只取主机名、字典先于线索。
//
// 最后 3 条是控制方裁定后补齐的两个缺口（各一条用例 + 名字主干次序）：含空格的值必须整段成候选、
// 紧贴形式（`解压密码123456`）必须与分隔形式一样被认出、名字主干必须排在标签值/URL 之后。
//
// 末尾 2 条是评审 I1 的回归：标签值里的半角 `,` / `;` 是**密码里的普通字符**，不能把值截断
//（`解压密码：ab,cd` 必须产出 `ab,cd`），同时逗号后面的说明文字也不能把真值本身挤掉
//（`解压密码：abc123, 请勿传播` 仍必须产出 `abc123`）。

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

        // 缺口 1：含空格的密码。`密码：my pass` 必须产出整段 `my pass`（空格是密码的一部分），
        // 同时保留旧的 token 行为 —— `解压密码：abc123 请勿传播` 里有用的是 `abc123`。
        H.Run("Pwd.ClueKeepsSpacedValueAlongsideToken", delegate {
            List<string> spaced = new List<string>(PasswordCandidates.CluesFromTextFile("密码：my pass"));
            AssertTrue(spaced.Contains("my pass"));
            AssertTrue(spaced.Contains("my"));
            List<string> prose = new List<string>(PasswordCandidates.CluesFromTextFile("解压密码：abc123 请勿传播"));
            AssertTrue(prose.Contains("abc123")); });

        // 缺口 2：无分隔符的标签（网盘名 `xxx密码123456.rar` / 文件夹 `解压密码123456`）。
        // 紧贴形式取的是「标签之后的值」；文件名里的扩展名是名字的尾巴，不属于密码，故一并剥掉。
        // 第三条是**回归**：分隔形式（含 `【解压密码：…】`）必须照旧。
        H.Run("Pwd.ClueAcceptsLabelWithoutSeparator", delegate {
            AssertTrue(new List<string>(PasswordCandidates.CluesFromFileNames(TestEnv.F("解压密码123456"))).Contains("123456"));
            AssertTrue(new List<string>(PasswordCandidates.CluesFromFileNames(TestEnv.F("xxx密码123456.rar"))).Contains("123456"));
            AssertTrue(new List<string>(PasswordCandidates.CluesFromFileNames(TestEnv.F("【解压密码：hello123】movie.rar"))).Contains("hello123")); });

        // 规格 §6.5 把「文件名与文件夹名」也算线索来源：名字主干（末段去扩展名）在最末，
        // 排在所有标签值之后 —— 于是 `movie.rar` 的 `movie` 不会插到 `hello123` 前面。
        H.Run("Pwd.NameStemIsLastPriorityClue", delegate {
            List<string> c = new List<string>(PasswordCandidates.CluesFromFileNames(TestEnv.F("movie.rar", "【解压密码：hello123】show.rar")));
            AssertTrue(c.Contains("hello123"));
            AssertTrue(c.Contains("movie"));
            AssertTrue(c.IndexOf("movie") > c.IndexOf("hello123")); });

        // 评审 I1：标签值里的半角 `,` / `;` 是密码里合法的字符，不是值的终点。旧行为把值截断成
        // `my` / `ab`，真正的 `my, pass` / `ab,cd` 一条都不在清单里 —— 用户看着屏幕上的密码收到
        // 「密码错误」，正是规格点名的最伤信任的一类消息。三种形状（含空格、紧贴、分号）都要整段在。
        H.Run("Pwd.ClueKeepsValueWithHalfWidthCommaOrSemicolon", delegate {
            List<string> spaced = new List<string>(PasswordCandidates.CluesFromTextFile("密码：my, pass"));
            AssertTrue(spaced.Contains("my"));
            AssertTrue(spaced.Contains("my, pass"));

            List<string> comma = new List<string>(PasswordCandidates.CluesFromTextFile("解压密码：ab,cd"));
            AssertTrue(comma.Contains("ab,cd"));

            List<string> semicolon = new List<string>(PasswordCandidates.CluesFromTextFile("解压密码：ab;cd"));
            AssertTrue(semicolon.Contains("ab;cd")); });

        // 同一处改动的另一半（回归）：逗号后面的说明文字绝不能把 token 本身挤掉。
        // 第二条特意**不留空格** —— 中文说明里半角逗号紧跟汉字很常见，而这一形状正是「把 `,`
        // 从终止符集合里删掉」会弄丢 `abc123` 的地方（整段会取代它），所以修复必须走加法。
        H.Run("Pwd.ClueKeepsTokenBeforeTrailingProse", delegate {
            List<string> spacedProse = new List<string>(PasswordCandidates.CluesFromTextFile("解压密码：abc123, 请勿传播"));
            AssertTrue(spacedProse.Contains("abc123"));

            List<string> gluedProse = new List<string>(PasswordCandidates.CluesFromTextFile("密码:abc123,请勿外传"));
            AssertTrue(gluedProse.Contains("abc123")); });
    }
}
