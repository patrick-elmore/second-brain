using Microsoft.Extensions.Logging;
using SecondBrain.PromptEval;
using SecondBrain.PromptEval.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
           .SetMinimumLevel(LogLevel.Information));

try
{
    var env = HarnessEnvironment.Resolve(loggerFactory);
    var subcommand = args[0];
    var rest = args.Skip(1).ToArray();

    return subcommand switch
    {
        "generate-test-cases" => await GenerateTestCases.RunAsync(env, rest),
        "score" => await Score.RunAsync(env, rest),
        "tune" => await Tune.RunAsync(env, rest),
        "show-state" => ShowState.Run(env, rest),
        _ => Unknown(subcommand),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}

static int Unknown(string subcommand)
{
    Console.Error.WriteLine($"Unknown subcommand: {subcommand}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        SecondBrain.PromptEval — synthetic eval & prompt tuning harness for the second-brain MCP.

        Usage:
          dotnet run -- generate-test-cases [--count 15] [--output test-cases-v1.json]
              Generate stratified synthetic test cases from the live index.

          dotnet run -- score --variant <surface=path> [--test-cases <file>]
              Score a single variant against the test set. Reports per-case and aggregate F2.

          dotnet run -- tune --surface <id> [--max-llm-calls 125] [--test-cases <file>] [--dry-run]
              Iteratively tune one surface (system_prompt | tool_descriptions | user_wrapper | query_gen_prompt).

          dotnet run -- show-state
              Print pinned-best values and recent run history.

        Environment:
          --config <path>       Override mcp_config.json location
                                (default: %LOCALAPPDATA%/SecondBrainMcpServer/mcp_config.json)

        Authentication: ANTHROPIC_API_KEY or CLAUDE_CODE_USE_VERTEX=1.
        """);
}
