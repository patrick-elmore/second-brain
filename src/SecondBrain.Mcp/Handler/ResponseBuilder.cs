using System.Text.Json.Nodes;

namespace SecondBrain.Mcp.Handler;

internal static class ResponseBuilder
{
    public static JsonNode Success(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    public static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    public static JsonNode ToolResult(string content, bool isError = false) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = content,
        }),
        ["isError"] = isError,
    };
}
