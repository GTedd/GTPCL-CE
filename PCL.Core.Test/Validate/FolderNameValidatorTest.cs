using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Validate;

namespace PCL.Core.Test.Validate;

[TestClass]
public class FolderNameValidatorTest
{
    public TestContext TestContext { get; set; }
    
    [TestMethod]
    [DataRow(" 测试 ", false)]
    [DataRow("Folder.", false)]
    [DataRow("LPT4", false)]
    [DataRow("给我一首歌的~1", false)]
    [DataRow("1|1|4|5|1|4", false)]
    [DataRow("不懂爱恨情仇煎熬的我们", true)]
    [DataRow("关注洛天依LuoTianyi0712谢谢喵!!!", true)]
    [DataRow("关注初音未来初音ミクHatsuneMiku0831谢谢喵!!!", true)]
    public void TestFolderNameValidate(string folderPath, bool expected)
    {
        var validator = new FolderNameValidator();
        var result = validator.Validate(folderPath);
        if (!result.IsValid)
        {
            foreach (var error in result.Errors)
            {
                TestContext.WriteLine(error.ErrorMessage);
            }
        }
        Assert.AreEqual(expected, result.IsValid);
    }

    [TestMethod]
    public void ExceptNameAllowsKeepingOriginalName()
    {
        var parent = Path.Combine(Path.GetTempPath(), "pcl-fnv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(parent, "MyInstance"));
        try
        {
            // 不带 ExceptName 时，保持原名会被判为重复
            Assert.IsFalse(new FolderNameValidator(parent).Validate("MyInstance").IsValid);
            // 带 ExceptName 时，保持原名应当通过（重命名弹窗的默认值场景）
            Assert.IsTrue(new FolderNameValidator(parent, exceptName: "MyInstance").Validate("MyInstance").IsValid);
            // 其它已存在的名字仍然被判为重复
            Directory.CreateDirectory(Path.Combine(parent, "Other"));
            Assert.IsFalse(new FolderNameValidator(parent, exceptName: "MyInstance").Validate("Other").IsValid);
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}