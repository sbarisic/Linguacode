using System.Text.Json.Nodes;
using Linguacode.Core;
namespace Linguacode.Tests;
internal static class RuntimeTests
{
    public static JsonObject Call(string id, string name, string arguments) => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["namespace"] = "agentsmith", ["arguments"] = arguments };
    public static ModelEvent Done(params JsonObject[] items) => new("completed", Output: items);
    public static JsonObject Message(string text = "Finished") => new() { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
}
