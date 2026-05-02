using Anthropic.Models.Messages;
using SecondBrain.Llm;

namespace SecondBrain.Llm.Tests;

public sealed class ToolDefinitionsTests
{
    [Fact]
    public void Build_ReturnsTwoTools()
    {
        var tools = ToolDefinitions.Build();
        tools.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ContainsSearchTool()
    {
        var tools = ToolDefinitions.Build();
        var names = GetToolNames(tools);
        names.Should().Contain("search");
    }

    [Fact]
    public void Build_ContainsReadFileTool()
    {
        var tools = ToolDefinitions.Build();
        var names = GetToolNames(tools);
        names.Should().Contain("read_file");
    }

    [Fact]
    public void SearchTool_HasRequiredParameters()
    {
        var tools = ToolDefinitions.Build();
        var searchTool = GetTool(tools, "search");

        searchTool.InputSchema.Properties.Should().ContainKey("query");
        searchTool.InputSchema.Properties.Should().ContainKey("date_start");
        searchTool.InputSchema.Properties.Should().ContainKey("people");
        searchTool.InputSchema.Properties.Should().ContainKey("source_type");
    }

    [Fact]
    public void ReadFileTool_RequiresPath()
    {
        var tools = ToolDefinitions.Build();
        var readTool = GetTool(tools, "read_file");
        readTool.InputSchema.Required.Should().Contain("path");
    }

    private static List<string> GetToolNames(IReadOnlyList<ToolUnion> tools)
    {
        var names = new List<string>();
        foreach (var t in tools)
        {
            if (t.TryPickTool(out var tool))
                names.Add(tool.Name);
        }
        return names;
    }

    private static Tool GetTool(IReadOnlyList<ToolUnion> tools, string name)
    {
        foreach (var t in tools)
        {
            if (t.TryPickTool(out var tool) && tool.Name == name)
                return tool;
        }
        throw new InvalidOperationException($"Tool '{name}' not found");
    }
}
