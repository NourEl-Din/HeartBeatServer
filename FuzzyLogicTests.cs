using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

[TestClass]
public class FuzzyLogicTests
{
    [TestMethod]
    public void TestFuzzyStatus()
    {
        var form = new HeartBeatServer.Form1();
        var now = DateTime.UtcNow;
        Assert.AreEqual(1.0, form.GetFuzzyStatus(now));
        Assert.AreEqual(0.7, form.GetFuzzyStatus(now.AddSeconds(-3)));
        Assert.AreEqual(0.4, form.GetFuzzyStatus(now.AddSeconds(-7)));
        Assert.AreEqual(0.1, form.GetFuzzyStatus(now.AddSeconds(-10)));
        Assert.AreEqual(0.0, form.GetFuzzyStatus(now.AddSeconds(-20)));
    }
}