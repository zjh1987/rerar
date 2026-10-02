// Task 1 单元测试：PathSanitizer（输出目录名消毒、越界判定、重名避让）。
// 前 9 条用例的名字与期望值逐字来自 task-1-brief.md Step 1；
// 末尾 4 条 Uniquify 用例对应 brief Interfaces 里 Produces 的行为（brief 未给用例，见报告）。

using System.IO;
using Rerar.Core;

internal sealed class PathSanitizerTests : TestBase
{
    public static void Run()
    {
        H.Run("Sanitize.ReplacesIllegalChars", delegate {
            AssertEq(PathSanitizer.Sanitize("a:b*c?.zip"), "a_b_c_"); });
        H.Run("Sanitize.AvoidsReservedDeviceNames", delegate {
            AssertEq(PathSanitizer.Sanitize("CON.rar"), "_CON"); });
        H.Run("Sanitize.StripsTrailingDotAndSpace", delegate {
            AssertEq(PathSanitizer.Sanitize("name .zip"), "name"); });
        H.Run("Sanitize.CapsLengthAt120", delegate {
            AssertTrue(PathSanitizer.Sanitize(new string('x', 300) + ".zip").Length <= 120); });
        H.Run("Sanitize.EmptyNameGetsFallback", delegate {
            AssertTrue(PathSanitizer.Sanitize(".zip").Length > 0); });          // Review Focus #3
        H.Run("Sanitize.OverlongEntryNameDoesNotThrow", delegate {
            AssertTrue(PathSanitizer.Sanitize(new string('y', 5000) + ".zip").Length > 0); }); // Review Focus #3
        H.Run("IsStrictChild.RejectsSibling", delegate {
            AssertFalse(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out2\f")); });
        H.Run("IsStrictChild.RejectsTraversal", delegate {
            AssertFalse(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out\..\evil")); });
        H.Run("IsStrictChild.AcceptsNested", delegate {
            AssertTrue(PathSanitizer.IsStrictChild(@"C:\out", @"C:\out\a\b.txt")); });

        H.Run("Uniquify.AppendsCounterWhenTargetNonEmpty", delegate {
            TestEnv.MakeFile(@"occupied\keep.txt", "x");
            string dir = Path.Combine(TestEnv.Tmp, "occupied");
            AssertEq(PathSanitizer.Uniquify(dir), dir + " (2)"); });
        H.Run("Uniquify.CountsUpPastOccupiedCandidates", delegate {
            TestEnv.MakeFile(@"taken\a.txt", "x");
            TestEnv.MakeFile(@"taken (2)\b.txt", "x");
            string dir = Path.Combine(TestEnv.Tmp, "taken");
            AssertEq(PathSanitizer.Uniquify(dir), dir + " (3)"); });
        H.Run("Uniquify.ReusesEmptyExistingDirectory", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "empty");
            Directory.CreateDirectory(dir);
            AssertEq(PathSanitizer.Uniquify(dir), dir); });
        H.Run("Uniquify.KeepsFreeName", delegate {
            string dir = Path.Combine(TestEnv.Tmp, "free");
            AssertEq(PathSanitizer.Uniquify(dir), dir); });
    }
}
