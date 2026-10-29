using System;
using System.Collections.Generic;

namespace Hibira.Tests
{
    static class JsonTests
    {
        [Test]
        static void ParsesObjectsArraysAndNumbers()
        {
            var v = (Dictionary<string, object>)Json.Parse("{\"a\": 1, \"b\": -2.5, \"c\": [true, false, null], \"d\": {\"e\": \"f\"}, \"g\": 1e3, \"h\": 12345678901234}");
            Assert.Equal(1L, (long)v["a"]);
            Assert.Equal(-2.5, (double)v["b"]);
            var c = (List<object>)v["c"];
            Assert.Equal(3, c.Count);
            Assert.Equal(true, (bool)c[0]);
            Assert.True(c[2] == null);
            Assert.Equal("f", (string)((Dictionary<string, object>)v["d"])["e"]);
            Assert.Equal(1000.0, (double)v["g"]);
            Assert.Equal(12345678901234L, (long)v["h"]);
        }

        [Test]
        static void ParsesEscapes()
        {
            var s = (string)Json.Parse("\"a\\\"b\\\\c\\n\\u00e9\\u3042\\/\"");
            Assert.Equal("a\"b\\c\néあ/", s);
        }

        [Test]
        static void RejectsBadJson()
        {
            foreach (var bad in new[] { "", "{", "[1,]", "{\"a\" 1}", "tru", "\"abc", "1 2", "{'a':1}", "-", "1." })
                Assert.True(!Json.TryParse(bad, out _), $"should reject {bad}");
            Assert.Throws<FormatException>(() => Json.Parse("[1,"));
        }

        [Test]
        static void SerializesRoundTrip()
        {
            var value = new Dictionary<string, object>
            {
                ["text"] = "quote \" backslash \\ newline \n tab \t \u0001 日本語",
                ["n"] = 3,
                ["f"] = 0.1,
                ["big"] = long.MaxValue,
                ["list"] = new List<object> { 1, "two", null, false },
                ["nested"] = new Dictionary<string, object> { ["x"] = 1.5f },
                ["nan"] = double.NaN,
            };
            var json = Json.Serialize(value);
            var back = (Dictionary<string, object>)Json.Parse(json);
            Assert.Equal((string)value["text"], (string)back["text"]);
            Assert.Equal(3L, (long)back["n"]);
            Assert.Equal(0.1, (double)back["f"]);
            Assert.Equal(long.MaxValue, (long)back["big"]);
            Assert.Equal(4, ((List<object>)back["list"]).Count);
            Assert.Equal(1.5, (double)((Dictionary<string, object>)back["nested"])["x"]);
            Assert.True(back["nan"] == null);
        }

        [Test]
        static void SerializesNumbersWithoutCulture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("[1.5,2]", Json.Serialize(new object[] { 1.5, 2 }));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        public class Move
        {
            public int x;
            public string who;
        }

        [Test]
        static void SerializesPlainObjectsByPublicFields()
        {
            Assert.Equal("{\"x\":3,\"who\":\"a\"}", Json.Serialize(new Move { x = 3, who = "a" }));
        }
    }
}
