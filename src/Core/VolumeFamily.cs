// Rerar 核心：VolumeFamily 分卷族解析（规格 §6.6；审计 D1/D4、Review Focus #4）。
//
// 它要回答的问题：一个目录里同时躺着十几个文件，用户正在看的那个候选文件，到底该把**哪一个**
// 交给 7-Zip？指错成员，7-Zip 就报 "Cannot open file as archive"，于是工具和用户双双把
//「下载文件是好的」误读成「压缩包损坏」。所以权威成员是**按族**决定的：
//
//   族             成员模式                      权威成员（交给 7-Zip 的那一个）
//   新式 RAR       a.part1.rar … a.partN.rar     a.part1.rar（第一个）
//   旧式 RAR       a.rar + a.r00/a.r01…          a.rar（第一个）
//   ZIP 分卷       a.z01/a.z02… + a.zip          a.zip（**最后一个**：中央目录在它里面）
//   7z 分卷        a.7z.001/a.7z.002…            a.7z.001（第一个）
//   7-Zip zip 分卷 a.zip.001/a.zip.002…          a.zip.001（第一个）
//
// 最后一行（7-Zip zip 分卷）与它上面的 ZIP 分卷是**两套互不兼容**的方案，绝不是同一个族：
// WinZip 方案把中央目录放在 **.zip**（故 .zip 是最后一卷），而 7-Zip 的 `a -tzip -v…` 把卷号
// 缀在 .zip 之后（`vol.zip.001 …`），**.001 才是第一卷**。两族权威成员相反，若并成一个族，
// 就会把 vol.zip.001 交给 7-Zip 之外的错误成员 —— 正是本任务要消弭的那条误报路径。
//
// 规格 §6.6 的其余三条规则：
//   * 缺卷必须报**具体缺哪一个**（"缺 movie.7z.002"），而不是笼统说"损坏" —— 见 Missing；
//   * 分组是**终止式 token** + 基名一致（序数忽略大小写）+ 位宽一致 + 无空洞；
//   * 若每个分片自身都是一份完整归档，则判为**独立文件**（Review Focus #4：同目录的 .zip 与 .z01
//     各自完整时，用户要的是两个包都被正常解压，而不是被并成一个"缺卷"的分卷集）。
//
// 返回语义（调用方必须照此使用）：
//   true  —— 候选属于一个**真正的分卷集**（至少两个成员，或确有一卷缺失）。此时调用方**必须**把
//            AuthoritativeMember 交给 7-Zip，并**先看 Missing**：非空即缺卷，报出 Missing 里的
//            具体名字，绝不要把成员当独立压缩包去试探。
//   false —— 候选按**独立文件**处理（调用方直接用候选自己）。set 置为 null。涵盖三种情形：
//            (1) 名字不是任何分卷族的成员（含单独一个 .7z 这样的非分卷命名）；
//            (2) 单独一个成员且不缺卷（集只有一个卷 ⇒ 它自己就是权威成员，没有"分卷"可言）；
//            (3) allMembersHaveFullSignature 为真。
//
// 位宽一致的判定方式（数字位宽只在**零填充**族里才携带语义）：由同基名序列里**卷序最小**的数字
// 成员定义整集的位宽 W，其余成员的数字串必须等于「数字值按 W 左补零」的结果。于是：
//   * a.7z.001（宽 3）与 a.7z.01（宽 2）**不会**并进同一集 —— 它们是两套命名；
//   * a.part1.rar 与 a.part10.rar 仍属同一集（partN 不补零，"10" 就是数字 10 的写法）；
//   * a.z01 … a.z99 与 a.z100 仍属同一集（Zip 到 100 卷自然多一位）；
//   * 与参考位宽冲突的候选**不**被解析（返回 false，按独立文件处理），既不误并也不凭空报缺卷。
//
// 注意上一条只管「并成一个集」，**不**管缺卷名怎么写。合成缺卷名（含缺的权威成员）用的是族的
// **最小**位宽 —— 新式 RAR 1 位、旧式 RAR / ZIP 分卷 2 位、7z 与 7-Zip zip 分卷 3 位 —— 数字超出
// 位宽时按 10/100/1000 自然增长。拿参考位宽去补零会凭空造出**根本不存在**的文件名：movie.part10.rar
// 的同伴会被叫成 movie.part01.rar（新式 RAR 从不补零），而卷 1–9 全缺、只有某个 ≥10 的卷在时，
// 用户真正要去找的是 movie.part1.rar；报 movie.part01.rar 等于让他白找一趟（规格 §6.6 要的是
// 报**具体**缺哪一个）。
//
// 数字之后的族 token 必须**终止**文件名（自带 ^$ 语义：x.part1.rar.bak、x.7z.001.tmp 都不是成员），
// 基名必须完全一致（CD1.rar / CD2.rar 是两个独立影片：数字在基名里，不是分卷 token），
// 扩展名一律大小写不敏感。
//
// 本类是**纯函数**：只做名字与字符串运算（Path.GetFileName/GetDirectoryName/Combine 都是纯字符串
// 运算），不读目录、不碰文件系统、不起进程。Members/AuthoritativeMember 里的路径原样沿用调用方给的
// 字符串；需要合成缺卷名时，目录取「候选在 filesInDir 里那一条」的目录（正常调用下就是候选自己的
// 目录，也与同集其它成员同目录），基名、族 token 与尾扩展名沿用**参考成员**的原始写法，
// 只有 .rar/.zip 这类 lead 成员自己缺失、列表里没有它的写法可参照时，才退回落款小写 token。
//
// 上界：一集超过 MaxVolumes 卷一律返回 false。畸形名字（如 movie.part999999.rar）不得让我们
// 合成上万个缺卷名把内存和界面打爆；真实分卷集（7-Zip 用 -v 切出来的上千卷也在内）远达不到这个量级。
//
// C# 5 语法；源码一律 UTF-8 带 BOM。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Rerar.Core
{
    // 分卷集解析结果。三个字段都用「调用方传入的字符串原样」表示（缺卷名由候选所在目录合成）。
    public sealed class VolumeSet
    {
        // 应交由 7-Zip 打开的那一个文件（族不同：part1.rar / a.rar / a.zip / .7z.001）。
        // 权威成员本身缺失时它并不存在 —— 调用方必须先看 Missing。
        public string AuthoritativeMember;

        // 集内**实际存在**的成员，按卷序排列（ZIP 的 .zip 排在最后）。
        public List<string> Members;

        // 缺失的成员名（含缺失的权威成员），按卷序排列；空列表表示这一集是完整的。
        public List<string> Missing;
    }

    public static class VolumeFamily
    {
        // 一集允许的最大卷数：畸形名字（如 movie.part999999.rar）不得让我们合成上万个缺卷名。
        // 超过即返回 false（按独立文件处理）。真实分卷集（含 7-Zip -v 切出来的上千卷）远达不到这个量级。
        private const int MaxVolumes = 8192;

        // None 只作为安全默认值：Item 一律经 Parse/NewItem 构造，实际不会停在 None。
        // ZipNumericSplit 是 7-Zip 自己切 zip 分卷的命名（<base>.zip.<N>），与 WinZip 的
        // ZipSplit（<base>.z<NN> + <base>.zip）是**两个** Kind：两者权威成员相反，见文件头。
        private enum Kind { None, ModernRar, LegacyRar, ZipSplit, SevenZipSplit, ZipNumericSplit }

        // 一个解析出来的族成员。
        private sealed class Item
        {
            public string Given;      // 调用方给的原始字符串（可能是全路径）
            public string Name;       // Path.GetFileName(Given)
            public Kind Kind;
            public string Base;       // 族 token 之前的基名（保留原始大小写）
            public string Token;      // 基名与数字之间的族 token，含大小写与点，如 ".part" / ".7Z." / ".z" / ".r"
            public string Extension;  // 数字之后的尾扩展名，如 ".RAR"；没有则为 ""
            public string Digits;     // token 里数字的原始写法，如 "001"
            public int Number;        // 数字值（旧式 RAR 的 .r00 → 0）
            public int Slot;          // 卷序，1 = 第一个卷
            public int Width;         // 数字位宽
            public bool HasDigits;    // lead 成员（.rar / .zip）为 false
        }

        public static bool TryResolve(IEnumerable<string> filesInDir, string candidate, out VolumeSet set,
            bool allMembersHaveFullSignature = false)
        {
            set = null;
            if (filesInDir == null || candidate == null) { return false; }

            // 独立性判别（规格 §6.6 / Review Focus #4）：调用方已确认每一片自身都是一份完整归档，
            // 那就不是分卷集 —— 报"缺卷"会把两个正常的包说成坏的。
            // 本类是纯函数（不读文件系统），所以这件事只能由调用方判定：产品侧的唯一调用点是
            // Extractor.ResolveVolumeMember，它用 Sniffer 逐片闻真实字节后把结论传进来
            //（每一片都认得出格式 = 都是完整归档）。这里不留"没人传"的空保护。
            if (allMembersHaveFullSignature) { return false; }

            string candidateName = Path.GetFileName(candidate);
            if (string.IsNullOrEmpty(candidateName)) { return false; }

            // 1) 解析目录里全部能解析成族成员的名字。同名（含仅大小写不同）只取第一个。
            List<Item> all = new List<Item>();
            Item cand = null;
            foreach (string raw in filesInDir)
            {
                if (raw == null) { continue; }
                Item item = Parse(raw);
                if (item == null) { continue; }
                if (ContainsName(all, item.Name)) { continue; }
                all.Add(item);
                if (cand == null && string.Equals(item.Name, candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    cand = item;
                }
            }
            if (cand == null) { return false; }   // 候选不在 filesInDir 里：没有可解析的集

            // 2) 同族 + 同基名的成员构成候选序列（终止式 token 已由 Parse 保证）。
            List<Item> series = new List<Item>();
            foreach (Item item in all)
            {
                if (item.Kind != cand.Kind) { continue; }
                if (!string.Equals(item.Base, cand.Base, StringComparison.OrdinalIgnoreCase)) { continue; }
                series.Add(item);
            }

            // 3) 位宽：由卷序最小的数字成员（并列取名字序数在前者）定义，其余数字成员必须与它一致。
            //    lead 成员（.rar / .zip）没有数字，不参与位宽判定。
            Item reference = NumberedReference(series);
            if (reference == null) { return false; }   // 只有 lead 成员 ⇒ 该集只有一个卷，本就不是分卷集
            int width = reference.Width;               // 「位宽一致」的判定基准（参考成员）
            int nameWidth = MinimumWidth(cand.Kind);   // 合成缺卷名用族的**最小**位宽（见文件头）

            List<Item> group = new List<Item>();
            foreach (Item item in series)
            {
                if (!item.HasDigits) { group.Add(item); continue; }
                if (item.Digits != Pad(item.Number, width)) { continue; }
                group.Add(item);
            }
            if (!ContainsItem(group, cand)) { return false; }   // 候选自身与整集位宽冲突 ⇒ 按独立文件处理

            // 4) 卷序：旧式 RAR 的 .rar 是第一卷（Parse 已定），ZIP 的 .zip 是**最后一卷**。
            int maxNumbered = 0;
            foreach (Item item in group)
            {
                if (item.HasDigits && item.Slot > maxNumbered) { maxNumbered = item.Slot; }
            }
            foreach (Item item in group)
            {
                if (item.Kind == Kind.ZipSplit && !item.HasDigits) { item.Slot = maxNumbered + 1; }
            }

            int lastSlot;
            if (cand.Kind == Kind.ZipSplit)
            {
                // ZIP 分卷的 .zip 是**最后一卷**，卷序恒为「最大数字卷 + 1」；它自己缺失时，
                // 这一卷就落进 Missing（缺的正是权威成员本身，见 ZipWithoutLeadIsMissingItself）。
                lastSlot = maxNumbered + 1;
            }
            else
            {
                lastSlot = 0;
                foreach (Item item in group) { if (item.Slot > lastSlot) { lastSlot = item.Slot; } }
            }
            if (lastSlot < 1 || lastSlot > MaxVolumes) { return false; }

            // 5) 按卷序铺开：存在的进 Members，缺的按参考成员的命名风格合成进 Missing。
            List<Item> ordered = new List<Item>(group);
            ordered.Sort(CompareBySlot);
            Dictionary<int, Item> bySlot = new Dictionary<int, Item>();
            foreach (Item item in ordered)
            {
                if (!bySlot.ContainsKey(item.Slot)) { bySlot.Add(item.Slot, item); }
            }

            int authoritativeSlot = cand.Kind == Kind.ZipSplit ? lastSlot : 1;
            string directory = Path.GetDirectoryName(cand.Given);   // 裸文件名时为 ""
            string authoritative = null;
            List<string> members = new List<string>();
            List<string> missing = new List<string>();
            for (int slot = 1; slot <= lastSlot; slot++)
            {
                Item present;
                if (bySlot.TryGetValue(slot, out present))
                {
                    members.Add(present.Given);
                    if (slot == authoritativeSlot) { authoritative = present.Given; }
                }
                else
                {
                    string name = SlotName(reference, cand.Kind, nameWidth, lastSlot, slot);
                    string synthesized = Path.Combine(directory, name);
                    missing.Add(synthesized);
                    if (slot == authoritativeSlot) { authoritative = synthesized; }
                }
            }

            // 单独一个成员且不缺卷 ⇒ 它自己就是"权威成员"，没有分卷可言。
            if (members.Count < 2 && missing.Count == 0) { return false; }

            VolumeSet result = new VolumeSet();
            result.AuthoritativeMember = authoritative;
            result.Members = members;
            result.Missing = missing;
            set = result;
            return true;
        }

        // 把一个文件名解析成族成员；不是任何族的成员（或形状不合族规则）时返回 null。
        private static Item Parse(string given)
        {
            string name = Path.GetFileName(given);
            if (string.IsNullOrEmpty(name)) { return null; }

            // 1) 新式 RAR：<base>.part<N>.rar（N 不补零）。必须先于旧式 RAR 判定：
            //    否则 m.part2.rar 会被读成基名 "m.part2" 的旧式 .rar 卷，权威成员指到第二个卷上，
            //    7-Zip 随即报「无法作为压缩包打开」—— 正是本任务要消弭的那条误报路径。
            if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name.Substring(0, name.Length - 4);
                int partAt = stem.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
                if (partAt > 0)
                {
                    string digits = stem.Substring(partAt + 5);
                    int number;
                    if (IsDigitRun(digits, 1, 9) &&
                        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
                        number >= 1)
                    {
                        Item modern = NewItem(given, name, Kind.ModernRar, stem.Substring(0, partAt));
                        modern.Token = stem.Substring(partAt, 5);               // ".part"（原样大小写）
                        modern.Extension = name.Substring(name.Length - 4);     // ".rar"（原样大小写）
                        modern.Digits = digits;
                        modern.Number = number;
                        modern.Width = digits.Length;
                        modern.HasDigits = true;
                        modern.Slot = number;
                        return modern;
                    }
                }

                // 2) 旧式 RAR 的第一个卷：<base>.rar（续卷 <base>.r00… 走下面的 3b）
                //    lead 成员不设 Token/Extension：它在集里一定存在，永远不会被当作命名参考。
                if (stem.Length > 0)
                {
                    Item legacy = NewItem(given, name, Kind.LegacyRar, stem);
                    legacy.Slot = 1;
                    return legacy;
                }
                return null;
            }

            // 2b) ZIP 分卷的 lead：<base>.zip。它是**最后一卷**（中央目录在它里面），
            //     具体卷序要等同集的数字成员都收齐了才能定（见 TryResolve 第 4 步）。
            //     与旧式 RAR 的 lead 一样不设 Token/Extension：它一定存在，不会当命名参考。
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string stem = name.Substring(0, name.Length - 4);
                if (stem.Length == 0) { return null; }

                Item lead = NewItem(given, name, Kind.ZipSplit, stem);
                lead.Slot = 0;                                      // 分组时定为「最大数字卷 + 1」
                return lead;
            }

            // 3) 末尾的数字串：.z01（ZIP 分卷）/ .r00（旧式 RAR 续卷）/ .7z.001（7z 分卷）
            int start = TrailingDigitStart(name, 2, 9);
            if (start <= 0) { return null; }

            string tail = name.Substring(start);
            int value;
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out value)) { return null; }

            if (name[start - 1] == '.')
            {
                // 3a) 容器名 + 数字后缀的分卷（数字前必须是一个**已知**的容器扩展名：
                //     裸 .001 本身不是任何分卷方案的证据）。
                string stem7 = name.Substring(0, start - 1);

                // 7z 分卷：<base>.7z.<N>，权威成员是 .001（第一个卷）。
                if (stem7.Length > 3 && stem7.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) && value >= 1)
                {
                    Item seven = NewItem(given, name, Kind.SevenZipSplit, stem7.Substring(0, stem7.Length - 3));
                    seven.Token = stem7.Substring(stem7.Length - 3) + ".";   // ".7z."（原样大小写）
                    seven.Digits = tail;
                    seven.Number = value;
                    seven.Width = tail.Length;
                    seven.HasDigits = true;
                    seven.Slot = value;
                    return seven;
                }

                // 7-Zip 自己的 zip 分卷：<base>.zip.<N>（`7z a -tzip -v1k vol.zip …` 实测产出
                // vol.zip.001…；`7z x vol.zip.001` 成功、`7z x vol.zip.002` 报「无法作为压缩包打开」，
                // 所以权威成员同样是**第一个卷 .001**）。基名必须非空，故要求比 ".zip" 更长。
                // 这是**独立**于 3b 的 WinZip 方案（<base>.z01 + <base>.zip）的族，理由见文件头。
                if (stem7.Length > 4 && stem7.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && value >= 1)
                {
                    Item zipNumeric = NewItem(given, name, Kind.ZipNumericSplit, stem7.Substring(0, stem7.Length - 4));
                    zipNumeric.Token = stem7.Substring(stem7.Length - 4) + ".";   // ".zip."（原样大小写）
                    zipNumeric.Digits = tail;
                    zipNumeric.Number = value;
                    zipNumeric.Width = tail.Length;
                    zipNumeric.HasDigits = true;
                    zipNumeric.Slot = value;
                    return zipNumeric;
                }
                return null;
            }

            // 3b) <base>.z<N>（ZIP 分卷）与 <base>.r<N>（旧式 RAR 第二卷起）
            if (start < 2 || name[start - 2] != '.') { return null; }
            string stemBase = name.Substring(0, start - 2);
            if (stemBase.Length == 0) { return null; }

            char letter = name[start - 1];
            if (letter == 'z' || letter == 'Z')
            {
                if (value < 1) { return null; }
                Item zip = NewItem(given, name, Kind.ZipSplit, stemBase);
                zip.Token = name.Substring(start - 2, 2);                    // ".z" / ".Z"
                zip.Digits = tail;
                zip.Number = value;
                zip.Width = tail.Length;
                zip.HasDigits = true;
                zip.Slot = value;
                return zip;
            }
            if (letter == 'r' || letter == 'R')
            {
                Item legacy = NewItem(given, name, Kind.LegacyRar, stemBase);
                legacy.Token = name.Substring(start - 2, 2);                 // ".r" / ".R"
                legacy.Digits = tail;
                legacy.Number = value;
                legacy.Width = tail.Length;
                legacy.HasDigits = true;
                legacy.Slot = value + 2;                                     // .r00 是第二个卷（第一个是 .rar）
                return legacy;
            }
            return null;
        }

        private static Item NewItem(string given, string name, Kind kind, string baseName)
        {
            Item item = new Item();
            item.Given = given;
            item.Name = name;
            item.Kind = kind;
            item.Base = baseName;
            item.Token = "";
            item.Extension = "";
            item.Digits = "";
            return item;
        }

        // 卷序最小的数字成员（并列取名字序数在前者）：由它定义整集的数字位宽。
        // 它自身的数字串必然等于「按自身位宽左补零」的结果，所以不会被下面的位宽过滤掉。
        private static Item NumberedReference(List<Item> series)
        {
            Item best = null;
            foreach (Item item in series)
            {
                if (!item.HasDigits) { continue; }
                if (best == null || item.Slot < best.Slot ||
                    (item.Slot == best.Slot && string.CompareOrdinal(item.Name, best.Name) < 0))
                {
                    best = item;
                }
            }
            return best;
        }

        // 族的**最小**位宽：合成缺卷名时用的补零宽度（数字更长时由 Pad 自然增长）。
        // 它取自族自身的命名约定，而**不是**参考成员的位宽：参考位宽只负责「位宽一致」的判定。
        // 用参考位宽补零会造出不存在的名字（movie.part10.rar 的同伴被叫成 movie.part01.rar）。
        private static int MinimumWidth(Kind kind)
        {
            switch (kind)
            {
                case Kind.ModernRar: return 1;        // partN 从不补零
                case Kind.LegacyRar: return 2;        // .r00
                case Kind.ZipSplit: return 2;         // .z01（>99 卷自然写 .z100）
                case Kind.SevenZipSplit: return 3;    // .7z.001
                case Kind.ZipNumericSplit: return 3;  // .zip.001
                default: return 1;
            }
        }

        // 缺卷名：沿用参考成员（同集里卷序最小的数字成员）的基名、族 token 与尾扩展名写法，
        // 数字按**族的最小位宽**左补零（见 MinimumWidth；不是参考成员的位宽）。
        // 只有 lead 成员（ZIP 的 .zip / 旧式 RAR 的 .rar）自己缺失时，
        // 才退回落款小写的 .zip / .rar 扩展名 —— 那种情况下列表里没有它的写法可参照。
        private static string SlotName(Item reference, Kind kind, int width, int lastSlot, int slot)
        {
            if (kind == Kind.ZipSplit && slot == lastSlot) { return reference.Base + ".zip"; }
            if (kind == Kind.LegacyRar && slot == 1) { return reference.Base + ".rar"; }

            int number = kind == Kind.LegacyRar ? slot - 2 : slot;   // 旧式 RAR 的 .r00 是第二个卷
            return reference.Base + reference.Token + Pad(number, width) + reference.Extension;
        }

        // 左补零到**至少** width 位；数字本身更长时原样返回（10/100/1000 各自然多一位）。
        private static string Pad(int number, int width)
        {
            string text = number.ToString(CultureInfo.InvariantCulture);
            return width > text.Length ? text.PadLeft(width, '0') : text;
        }

        private static int CompareBySlot(Item a, Item b)
        {
            if (a.Slot != b.Slot) { return a.Slot < b.Slot ? -1 : 1; }
            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static bool ContainsName(List<Item> items, string name)
        {
            foreach (Item item in items)
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static bool ContainsItem(List<Item> items, Item target)
        {
            foreach (Item item in items)
            {
                if (ReferenceEquals(item, target)) { return true; }
            }
            return false;
        }

        // 名字末尾的数字串起始下标；数字位数不在 [minDigits, maxDigits] 内（含完全没有数字）时返回 -1。
        // 上界 9 保证 int.TryParse 不会溢出。
        private static int TrailingDigitStart(string name, int minDigits, int maxDigits)
        {
            int start = name.Length;
            while (start > 0 && name[start - 1] >= '0' && name[start - 1] <= '9') { start--; }
            int width = name.Length - start;
            if (width < minDigits || width > maxDigits) { return -1; }
            return start;
        }

        private static bool IsDigitRun(string text, int minDigits, int maxDigits)
        {
            if (text.Length < minDigits || text.Length > maxDigits) { return false; }
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9') { return false; }
            }
            return true;
        }
    }
}
