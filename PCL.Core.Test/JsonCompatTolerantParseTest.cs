using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils;

namespace PCL.Core.Test
{
    [TestClass]
    public class JsonCompatTolerantParseTest
    {
        [TestMethod]
        public void ParsesStringWithRawNewline()
        {
            // 真实数据：entity_model_features 的 fabric.mod.json 在 description 中直接写了裸换行
            const string json = "{\"schemaVersion\":1,\"description\":\"first line\nsecond line\"}";
            Assert.ThrowsExactly<JsonException>(() => JsonNode.Parse(json));

            var node = JsonCompat.ParseNode(json);
            Assert.AreEqual("first line\nsecond line", node["description"]!.GetValue<string>());
        }

        [TestMethod]
        public void ParsesStringWithRawTabAndCarriageReturn()
        {
            const string json = "{\"a\":\"x\ty\rz\"}";
            var node = JsonCompat.ParseNode(json);
            Assert.AreEqual("x\ty\rz", node["a"]!.GetValue<string>());
        }

        [TestMethod]
        public void KeepsStructureAndEscapesIntact()
        {
            // 结构内的换行与已有转义序列不应被改动
            const string json = "{\n  \"a\": \"q\\\\\"w\",\n  \"b\": [1, 2]\n}";
            var node = JsonCompat.ParseNode(json);
            Assert.AreEqual("q\"w", node["a"]!.GetValue<string>());
            Assert.AreEqual(2, node["b"]![1]!.GetValue<int>());
        }

        [TestMethod]
        public void StillThrowsForTrulyBrokenJson()
        {
            Assert.ThrowsExactly<JsonException>(() => JsonCompat.ParseNode("{ not json"));
        }
    }
}
