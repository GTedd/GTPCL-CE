using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;

namespace PCL.Core.Test.App;

[TestClass]
public sealed class DependencyGroupsTest
{
    [TestMethod]
    public void RpcPropertyGroupExistsWithoutAnyRpcProperty()
    {
        // 代码里一个 [RegisterRpc] 属性都没有时，注入也应成功（空列表），
        // 而不是在每次启动时提示 "Dependency injection failed: rpc-property@128"
        var received = -1;
        Action<ImmutableList<(PropertyAccessor<string> prop, string name)>> inject = items => received = items.Count;

        Assert.IsTrue(DependencyGroups.InvokeInjection(inject, "rpc-property", AttributeTargets.Property));
        Assert.AreEqual(0, received);
    }

    [TestMethod]
    public void RpcFunctionGroupStillCollectsFunctions()
    {
        string[] names = [];
        Action<ImmutableList<(RpcFunction func, string name)>> inject = items => names = items.Select(x => x.name).ToArray();

        Assert.IsTrue(DependencyGroups.InvokeInjection(inject, "rpc-function", AttributeTargets.Method));
        CollectionAssert.Contains(names, "cli");
    }

    [TestMethod]
    public void UnknownIdentifierStillFails()
    {
        Action<ImmutableList<(RpcFunction func, string name)>> inject = _ => { };

        Assert.IsFalse(DependencyGroups.InvokeInjection(inject, "no-such-collector", AttributeTargets.Method));
    }
}
