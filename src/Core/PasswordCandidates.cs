// Rerar 核心：PasswordCandidates 密码候选阶梯与同目录线索采集（规格 §6.5，配合不变式 I5）。
//
// 它解决的问题：加密包被判为「需密码」之后，工具必须**按固定顺序**把候选密码逐个交给 7-Zip 去试。
// 真正的验证（`7z t`，绝不用 `x`）在 Task 10 的 Extractor 里；本类只产出**有序候选清单**，
// 绝不调用 7-Zip。顺序不是装饰：用户能看着清单往前走，而把用户刚输入的密码排在字典后面，
// 等于让他盯着「密码错误」却明明自己输对了——规格把这类消息称为最伤信任的一类。
//
// 规格 §6.5 的阶梯：手动输入 → 本次运行已验证成功的密码 → 导入字典 → 内置字典 → 同目录线索。
// Build 只有四个入参，映射关系是：导入字典与内置字典由调用方按序拼进同一个 dictLines
//（两者本来就是同一层里的先后关系），其余三层一一对应。
//
// 本类是**纯逻辑**：字符串进、字符串出。不读文件系统（CluesFromTextFile 收的是文件**内容**而不是
// 路径，正是为了守住这一点）、不起进程、没有可变静态状态。
//
// 三条核心规则：
//   1. 每个原始候选都展开成**变体**：原文 / 去首尾空白 / 全角转半角 + 去零宽与 NBSP / 剥标签前缀。
//      变体是**叠加**的，绝不替换原文——真实密码可能真的带空格或全角字符，丢掉原文就是丢掉机会。
//   2. 输出**去重且保序**：重复出现的候选只保留第一次的位置，于是「先出现的优先级更高」不会被
//      后出现的候选重排。
//   3. 空 / 纯空白 / 只剩零宽字符的候选**丢弃**，绝不产出空密码：空密码在某些格式下是一次"合法"
//      的尝试，让它混进阶梯毫无意义，还可能把「没设密码」误判成「密码是空字符串」。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Text;

namespace Rerar.Core
{
    public static class PasswordCandidates
    {
        // 线索标签词表。规格 §6.5 给的是 `密码|password|pwd|mima|解压码`；这里补上资源站里同样
        // 常见的写法（解压密码 / 压缩密码 / pw / pass / passwd）。匹配一律取**最长命中**：
        // 否则 `解压密码：xxx` 会先命中 `密码`，把值读成 `：xxx`。
        private static readonly string[] Labels = new string[]
        {
            "解压密码", "压缩密码", "password", "passwd", "解压码", "密码", "mima", "pass", "pwd", "pw"
        };

        // URL 前缀（协议头）。主机名采集只认这些开头，绝不把整条 URL 当候选。
        // 裸 `www.` 不带协议时，`www.` 自己算主机名的一部分，见 TryHarvestUrl。
        private static readonly string[] UrlSchemes = new string[]
        {
            "http://", "https://", "ftp://", "ftps://"
        };

        // 值尾部可以安全剥掉的**句读**。只在「整段读完之后」剥一次，且**原文与剥后的形式都保留**，
        // 所以不会因为剥错而丢掉任何候选（`abc.` 与 `abc` 都在清单里）。
        private static readonly char[] TrailingJunk = new char[]
        {
            '。', '、', '，', ',', '；', ';', '！', '？', '…', '·', '：', ':', '.'
        };

        // 候选阶梯：manual → succeeded → dictLines → clues，每项展开变体、按序去重。
        // 返回 List 而不是惰性的 yield：调用方（Extractor）会把清单走一遍也可能要走第二遍，
        // 急切求值既避免了「同一个惰性序列被枚举两次」的意外，也让顺序与去重在此刻就定死。
        public static IEnumerable<string> Build(string manual, IEnumerable<string> succeeded, IEnumerable<string> dictLines, IEnumerable<string> clues)
        {
            List<string> ladder = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            AddVariants(ladder, seen, manual);      // 1. 手动输入的密码
            AppendAll(ladder, seen, succeeded);     // 2. 本次运行已验证成功的密码
            AppendAll(ladder, seen, dictLines);     // 3./4. 导入字典 + 内置字典（调用方按序拼好）
            AppendAll(ladder, seen, clues);         // 5. 同目录线索

            return ladder;
        }

        // 一个原始候选 → 变体清单（原文、去首尾空白、全角转半角+去零宽/NBSP+去首尾空白、剥标签前缀）。
        // 空 / 纯空白 / null 一律产出空清单。
        public static IEnumerable<string> Variants(string raw)
        {
            List<string> variants = new List<string>();
            if (raw == null) { return variants; }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            Add(variants, seen, raw);                       // 原文
            string trimmed = raw.Trim();                    // 去首尾空白
            Add(variants, seen, trimmed);

            string normalised = Normalise(raw);             // 全角转半角 + 去零宽/NBSP + 去首尾空白
            Add(variants, seen, normalised);

            // 前缀剥离对「未归一化」与「已归一化」两种写法都试：`密码：x` 的冒号是全角，
            // `password=x` 的等号是半角，两种都要能剥掉。
            Add(variants, seen, StripLabel(trimmed));
            Add(variants, seen, StripLabel(normalised));

            return variants;
        }

        // 文件名 / 文件夹名里的线索（含 `【解压密码：xxx】` 形式，也含名字里出现的 URL 主机名）。
        // 调用方可以把目录名与文件名一起传进来：本方法只看字符串，路径里的父目录同样会被扫到。
        public static IEnumerable<string> CluesFromFileNames(IEnumerable<string> names)
        {
            List<string> clues = new List<string>();
            if (names == null) { return clues; }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (name == null) { continue; }
                HarvestLine(name, clues, seen);
            }
            return clues;
        }

        // 小文本文件的**内容**（不是路径）→ 线索。逐行扫描，行内按出现先后采集，故线索也保序。
        //
        // 返回的每一条都已展开为变体（原文也在内）：`解压密码： pw=abc123` 这种真实写法里，
        // 值本身还带一层 `pw=` 标签，展开后既有 `pw=abc123` 也有 `abc123`。
        // Build 拿到这些线索后会再展开一次——幂等，且这样两个入口（直接采线索 / 走完整阶梯）行为一致。
        public static IEnumerable<string> CluesFromTextFile(string content)
        {
            List<string> clues = new List<string>();
            if (content == null) { return clues; }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in content.Split('\n'))
            {
                HarvestLine(line, clues, seen);
            }
            return clues;
        }

        // ------------------------------------------------------------------
        // 阶梯装配
        // ------------------------------------------------------------------

        private static void AppendAll(List<string> ladder, HashSet<string> seen, IEnumerable<string> rawCandidates)
        {
            if (rawCandidates == null) { return; }

            foreach (string raw in rawCandidates) { AddVariants(ladder, seen, raw); }
        }

        // 展开一个原始候选并追加未出现过的变体。seen 负责保序去重（Add 返回 false 即已存在）。
        private static void AddVariants(List<string> target, HashSet<string> seen, string raw)
        {
            foreach (string variant in Variants(raw))
            {
                if (seen.Add(variant)) { target.Add(variant); }
            }
        }

        private static void Add(List<string> target, HashSet<string> seen, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) { return; }
            // 纯空白（含 \u00a0、\u3000）或「剥掉零宽字符后什么都不剩」的候选一律丢弃。
            // 不能只用 Trim()：char.IsWhiteSpace('\u200b') 是 false，于是 `" \u200b"` 会以一个不可见
            // 字符的身份混进阶梯——它不可能是用户能输入的密码，只会换来一次必然失败的尝试，
            // 还会在界面上显示成一行空白。用 Normalise 判定，才与「去零宽」这条规则真正一致。
            if (Normalise(candidate).Length == 0) { return; }
            if (seen.Add(candidate)) { target.Add(candidate); }
        }

        // ------------------------------------------------------------------
        // 文本归一化
        // ------------------------------------------------------------------

        // 去零宽（\u200b\u200c\u200d\ufeff）与 NBSP（\u00a0）+ 全角转半角 + 去首尾空白。
        // 全部**叠加**成一档：分开给会被「全角尾巴」这类组合坑到（`１２３４５６ ` 只转全角不去尾空白，
        // 得到的 `123456 ` 依旧不是用户要的那个密码）。
        private static string Normalise(string raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '\u200b' || c == '\u200c' || c == '\u200d' || c == '\ufeff' || c == '\u00a0') { continue; }
                sb.Append(ToHalfWidth(c));
            }
            return sb.ToString().Trim();
        }

        private static char ToHalfWidth(char c)
        {
            if (c == '\u3000') { return ' '; }                                  // 全角空格
            if (c >= '\uff01' && c <= '\uff5e') { return (char)(c - 0xfee0); }   // 全角 ASCII 区（含全角冒号/等号/数字/字母）
            return c;
        }

        // ------------------------------------------------------------------
        // 标签前缀剥离
        // ------------------------------------------------------------------

        // 反复剥掉开头的「标签 + 分隔符」，直到剥不动为止（`密码：password=abc` → `abc`）。
        // 一个字符都不剥时原样返回，所以调用方可以无条件 Add。
        private static string StripLabel(string value)
        {
            string current = value;
            while (true)
            {
                string peeled = PeelOneLabel(current);
                if (peeled == null) { return current; }
                current = peeled;
            }
        }

        // 剥掉一个「标签 + 至少一个分隔符」前缀；剥不掉返回 null。
        // 「至少一个分隔符」是刻意的：没有它，`mypassword123` 会被拆出一个 `123`，
        // 噪声远多于线索；有了它，`密码：x`、`password=x`、`pwd: x`、`密码是x` 一个不漏。
        private static string PeelOneLabel(string value)
        {
            string s = value.TrimStart();
            if (s.Length == 0) { return null; }

            int labelLength = LongestLabelAt(s, 0);
            if (labelLength == 0) { return null; }

            int at = labelLength;
            if (at >= s.Length || !IsSeparator(s[at])) { return null; }

            while (at < s.Length && IsSeparator(s[at])) { at++; }
            return s.Substring(at);
        }

        // 标签与值之间的分隔符：冒号（半角/全角）、等号（半角/全角）、「是/为」、空白。
        private static bool IsSeparator(char c)
        {
            return c == ':' || c == '：' || c == '=' || c == '＝' || c == '是' || c == '为' || char.IsWhiteSpace(c);
        }

        // 扫描时的分隔符放宽一档：值本身常被括号包着（`密码：【abc】`），开括号也算分隔符。
        private static bool IsLabelValueSeparator(char c)
        {
            return IsSeparator(c)
                || c == '【' || c == '[' || c == '「' || c == '『' || c == '（' || c == '(';
        }

        // 在 text[index] 处能命中的最长标签长度；没有命中返回 0。
        private static int LongestLabelAt(string text, int index)
        {
            int best = 0;
            for (int i = 0; i < Labels.Length; i++)
            {
                string label = Labels[i];
                if (label.Length <= best) { continue; }
                if (index + label.Length > text.Length) { continue; }
                if (string.Compare(text, index, label, 0, label.Length, StringComparison.OrdinalIgnoreCase) != 0) { continue; }
                best = label.Length;
            }
            return best;
        }

        // ------------------------------------------------------------------
        // 线索扫描
        // ------------------------------------------------------------------

        // 逐字符扫描一行：先看 URL 主机名，再看带标签的值；命中就跳过已消费的那一段。
        // 左到右的单遍扫描让线索天然按出现顺序排列（去重保序后就是阶梯里的顺序）。
        private static void HarvestLine(string line, List<string> clues, HashSet<string> seen)
        {
            int i = 0;
            while (i < line.Length)
            {
                int consumed = TryHarvestUrl(line, i, clues, seen);
                if (consumed == 0) { consumed = TryHarvestLabelledValue(line, i, clues, seen); }
                i += consumed > 0 ? consumed : 1;
            }
        }

        // 规格 §6.5：只取 URL 的**主机名**。读到 `:`（端口）、`/`（路径）、`?`/`#`（查询与片段）
        // 或任何非主机名字符就停 —— 于是永远不会把整条 URL 当成候选密码。
        private static int TryHarvestUrl(string line, int index, List<string> clues, HashSet<string> seen)
        {
            int hostStart = -1;
            for (int i = 0; i < UrlSchemes.Length; i++)
            {
                string scheme = UrlSchemes[i];
                if (index + scheme.Length > line.Length) { continue; }
                if (string.Compare(line, index, scheme, 0, scheme.Length, StringComparison.OrdinalIgnoreCase) != 0) { continue; }
                hostStart = index + scheme.Length;
                break;
            }

            if (hostStart < 0)
            {
                // 裸 `www.` 开头（没有协议）：`www.` 自己就是主机名的一部分。
                if (index + 4 <= line.Length &&
                    string.Compare(line, index, "www.", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    hostStart = index;
                }
                else
                {
                    return 0;
                }
            }

            int end = hostStart;
            while (end < line.Length && IsHostChar(line[end])) { end++; }

            // 只有协议头、没有主机名（`http://` 后面直接是中文或空白）：跳过协议头，不产出候选。
            if (end == hostStart) { return hostStart - index; }

            AddVariants(clues, seen, line.Substring(hostStart, end - hostStart));
            return end - index;
        }

        private static bool IsHostChar(char c)
        {
            // 刻意只认 ASCII 主机名字符：char.IsLetterOrDigit 对中文为真，那样 `www.example.com解压密码`
            // 会把中文一起吞进主机名。
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '-' || c == '.' || c == '_';
        }

        // 命中「标签 + 分隔符」后读出值：读到空白 / 括号 / 引号 / 中英文句读为止。
        // 返回消费掉的字符数（0 = 此处没有线索）。
        private static int TryHarvestLabelledValue(string line, int index, List<string> clues, HashSet<string> seen)
        {
            int labelLength = LongestLabelAt(line, index);
            if (labelLength == 0) { return 0; }

            int at = index + labelLength;
            if (at >= line.Length || !IsLabelValueSeparator(line[at])) { return 0; }
            while (at < line.Length && IsLabelValueSeparator(line[at])) { at++; }

            int start = at;
            while (at < line.Length && !IsValueTerminator(line[at])) { at++; }

            int consumed = at - index;
            if (consumed <= 0) { consumed = 1; }

            string value = line.Substring(start, at - start).Trim();
            if (value.Length > 0)
            {
                AddVariants(clues, seen, value);

                // 句读收尾（`解压密码：abc.`）时补一条剥掉尾句读的候选。原文也留着，
                // 所以这是一次**加法**：两种写法都在清单里，猜错一次也只是多花一次 `7z t`。
                string tailTrimmed = value.TrimEnd(TrailingJunk);
                if (tailTrimmed.Length > 0 && tailTrimmed != value) { AddVariants(clues, seen, tailTrimmed); }
            }

            return consumed;
        }

        // 值的终止符：空白、括号、引号、中英文句读。刻意**不**包含半角 `!`、`?`、`*`、`#`、`@`、
        // `-`、`_`、`+`、`.`、`=`、`:` —— 这些是真实密码里常见的内嵌字符，拿它们截断会切掉密码。
        private static bool IsValueTerminator(char c)
        {
            if (char.IsWhiteSpace(c)) { return true; }
            return c == '】' || c == '］' || c == '」' || c == '』' || c == '【'
                || c == '"' || c == '\''
                || c == '，' || c == ',' || c == '。' || c == '、' || c == '；' || c == ';'
                || c == '！' || c == '？' || c == '…' || c == '·'
                || c == '（' || c == '）' || c == '(' || c == ')' || c == '[' || c == ']'
                || c == '{' || c == '}' || c == '<' || c == '>' || c == '《' || c == '》'
                || c == '|';
        }
    }
}
