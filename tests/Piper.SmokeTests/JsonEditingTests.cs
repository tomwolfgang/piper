using System.Text.Json.Nodes;
using Piper.Core.Http;

internal static class JsonEditingTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("JSON values keep their type when edited", () =>
        {
            const string source = """{"id":7,"name":"widget","active":true,"ratio":1.5,"note":null}""";
            runner.IsTrue(JsonEditing.TryParse(source, out var root, out var error), $"parses ({error})");
            var obj = (JsonObject)root!;

            // A string stays a string: typing 123 into a field that was "123" must not retype it.
            runner.AreEqual("\"456\"", JsonEditing.ValueFrom(obj["name"], "456")!.ToJsonString(),
                "a string property stays a string");
            runner.AreEqual("99", JsonEditing.ValueFrom(obj["id"], "99")!.ToJsonString(), "a number stays a number");
            runner.AreEqual("false", JsonEditing.ValueFrom(obj["active"], "false")!.ToJsonString(), "a boolean stays boolean");
            runner.AreEqual("2.75", JsonEditing.ValueFrom(obj["ratio"], "2.75")!.ToJsonString(), "a fractional number survives");
            runner.AreEqual<JsonNode?>(null, JsonEditing.ValueFrom(obj["note"], "null"), "null stays null");
            runner.AreEqual("\"text\"", JsonEditing.ValueFrom(obj["id"], "text")!.ToJsonString(),
                "a number given non-numeric text becomes a string rather than failing");

            // Applying an edit writes through to the document.
            runner.IsTrue(JsonEditing.TryReplace(obj["id"]!, JsonEditing.ValueFrom(obj["id"], "42"), out error), $"replace ({error})");
            runner.AreEqual("42", obj["id"]!.ToJsonString(), "the document now holds the new value");

            var array = (JsonArray)JsonNode.Parse("""["a","b","c"]""")!;
            runner.IsTrue(JsonEditing.TryReplace(array[1]!, JsonValue.Create("B"), out error), $"array replace ({error})");
            runner.AreEqual("""["a","B","c"]""", array.ToJsonString(), "an array element is replaced in place");

            runner.IsTrue(!JsonEditing.TryReplace(JsonNode.Parse("1")!, JsonValue.Create(2), out var rootError),
                "replacing the root is refused");
            runner.IsTrue(rootError.Length > 0, "with a reason");

            return Task.CompletedTask;
        });

        await runner.RunAsync("renaming a JSON property keeps the document's order", () =>
        {
            JsonEditing.TryParse("""{"first":1,"second":2,"third":3}""", out var root, out _);
            var obj = (JsonObject)root!;

            runner.IsTrue(JsonEditing.TryRename(obj["second"]!, "middle", out var error), $"rename ({error})");
            runner.AreEqual("""{"first":1,"middle":2,"third":3}""", obj.ToJsonString(),
                "the renamed property stays where it was instead of moving to the end");
            runner.AreEqual(2, obj["middle"]!.GetValue<int>(), "and keeps its value");

            runner.IsTrue(!JsonEditing.TryRename(obj["middle"]!, "third", out var clash), "a clashing name is refused");
            runner.IsTrue(clash.Contains("third"), "and names the conflict");
            runner.IsTrue(!JsonEditing.TryRename(obj["middle"]!, "", out _), "an empty name is refused");
            runner.IsTrue(JsonEditing.TryRename(obj["middle"]!, "middle", out _), "renaming to the same name is a no-op");

            var array = (JsonArray)JsonNode.Parse("[1,2]")!;
            runner.IsTrue(!JsonEditing.TryRename(array[0]!, "nope", out _), "an array element has no name to rename");

            return Task.CompletedTask;
        });

        await runner.RunAsync("JSON tree text and round-tripping", () =>
        {
            JsonEditing.TryParse("""{"a":{"b":[1,2,3]},"s":"hi","n":null}""", out var root, out _);
            var obj = (JsonObject)root!;

            runner.AreEqual("a: {1 property}", JsonEditing.Describe("a", obj["a"]), "objects show a property count");
            runner.AreEqual("b: [3 items]", JsonEditing.Describe("b", obj["a"]!["b"]), "arrays show an item count");
            runner.AreEqual("s: \"hi\"", JsonEditing.Describe("s", obj["s"]), "strings are shown quoted");
            runner.AreEqual("n: null", JsonEditing.Describe("n", obj["n"]), "null is shown as null");
            runner.AreEqual("[1 item]", JsonEditing.Describe(null, JsonNode.Parse("[0]")), "a nameless node has no prefix");

            runner.AreEqual("hi", JsonEditing.EditableText(obj["s"]), "a string edits without its quotes");
            runner.AreEqual("null", JsonEditing.EditableText(obj["n"]), "null edits as the word null");
            runner.IsTrue(JsonEditing.IsLeaf(obj["s"]) && JsonEditing.IsLeaf(obj["n"]), "values and null are leaves");
            runner.IsTrue(!JsonEditing.IsLeaf(obj["a"]), "objects are not");

            // Serialised output must parse back, and stay readable while it does.
            var text = JsonEditing.Serialize(root);
            runner.IsTrue(text.Contains('\n'), "output is indented for editing");
            runner.IsTrue(JsonEditing.TryParse(text, out var again, out var error), $"and parses back ({error})");
            runner.AreEqual(obj.ToJsonString(), again!.ToJsonString(), "with the same content");

            runner.IsTrue(!JsonEditing.TryParse("{ nope", out _, out var bad), "malformed JSON is reported");
            runner.IsTrue(bad.Length > 0, "with a message");
            runner.IsTrue(!JsonEditing.TryParse("   ", out _, out _), "an empty body is not JSON");

            return Task.CompletedTask;
        });
    }
}
