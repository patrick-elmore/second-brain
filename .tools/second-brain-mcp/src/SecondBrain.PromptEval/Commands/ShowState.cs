namespace SecondBrain.PromptEval.Commands;

public static class ShowState
{
    public static int Run(HarnessEnvironment env, string[] args)
    {
        Console.WriteLine($"State directory: {env.StateDir}");
        if (!Directory.Exists(env.StateDir))
        {
            Console.WriteLine("(empty)");
            return 0;
        }

        var files = Directory.GetFiles(env.StateDir, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Console.WriteLine("(empty)");
            return 0;
        }

        foreach (var f in files.OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(env.StateDir, f);
            var size = new FileInfo(f).Length;
            Console.WriteLine($"  {rel}  ({size:N0} bytes)");
        }
        return 0;
    }
}
