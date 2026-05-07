using System.Text.Json.Nodes;
using SecondBrain.Mcp.Handler;

namespace SecondBrain.Mcp.Tests;

public sealed class ResponseBuilderTests
{
    [Fact]
    public void Success_ContainsJsonRpc20AndId()
    {
        var id = JsonValue.Create(42);
        var result = new JsonObject { ["ok"] = true };

        var response = ResponseBuilder.Success(id, result);

        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        response["id"]!.GetValue<int>().Should().Be(42);
        response["result"]!["ok"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Success_WithNullId_IdNodeIsNull()
    {
        var response = ResponseBuilder.Success(null, new JsonObject());

        response["id"].Should().BeNull();
    }

    [Fact]
    public void Success_IdIsDeepCloned_MutatingOriginalDoesNotAffectResponse()
    {
        var id = new JsonObject { ["x"] = 1 };
        var response = ResponseBuilder.Success(id, new JsonObject());

        id["x"] = 99; // mutate the original

        response["id"]!["x"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Error_ContainsCodeAndMessage()
    {
        var id = JsonValue.Create("req-1");

        var response = ResponseBuilder.Error(id, -32601, "Method not found");

        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        response["id"]!.GetValue<string>().Should().Be("req-1");
        response["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        response["error"]!["message"]!.GetValue<string>().Should().Be("Method not found");
        response["result"].Should().BeNull();
    }

    [Fact]
    public void ToolResult_WrapsContentAsTextBlock()
    {
        var result = ResponseBuilder.ToolResult("hello world");

        var content = result["content"]!.AsArray();
        content.Should().HaveCount(1);
        content[0]!["type"]!.GetValue<string>().Should().Be("text");
        content[0]!["text"]!.GetValue<string>().Should().Be("hello world");
        result["isError"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void ToolResult_WithIsErrorTrue_FlagIsSet()
    {
        var result = ResponseBuilder.ToolResult("boom", isError: true);

        result["isError"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ToolResult_WithJsonNode_WrapsAsTextBlock()
    {
        var node = new JsonObject { ["status"] = "ok" };
        var result = ResponseBuilder.ToolResult(node.ToJsonString());

        var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        text.Should().Contain("status");
        text.Should().Contain("ok");
    }
}
