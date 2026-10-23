using System.Text;

namespace NegiThemes;

public static class Program
{
    private const string DefaultSource = "src/NegiCraftLauncher.Theme/Controls.xaml";
    private const string DefaultOutput = "src/NegiCraftLauncher.Theme.Avalonia/Generated/Controls.g.axaml";

    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var check = args.Contains("--check");
        var sourceOption = ReadOption(args, "--source") ?? DefaultSource;
        var outputOption = ReadOption(args, "--output") ?? DefaultOutput;
        var root = FindRepositoryRoot();

        var source = Path.GetFullPath(sourceOption, root);
        var output = Path.GetFullPath(outputOption, root);

        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"Source file not found: {source}");
            return 2;
        }

        var compiler = new ThemeCompiler();
        var generated = compiler.Compile(source, root);

        if (check)
        {
            var current = File.Exists(output) ? File.ReadAllText(output) : string.Empty;
            if (Normalize(current) != Normalize(generated))
            {
                Console.Error.WriteLine(
                    $"Theme drift: {Path.GetRelativePath(root, output)} is not generated from the current source.");
                return 1;
            }

            Console.WriteLine($"Theme is up to date: {Path.GetRelativePath(root, output)}");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"Generated {Path.GetRelativePath(root, output)}");
        Console.WriteLine($"     from {Path.GetRelativePath(root, source)}");
        return 0;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd() + "\n";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NegiCraftLauncher.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root (NegiCraftLauncher.slnx).");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet run --project tools/NegiThemes -- [options]

              --source <path>   WPF XAML source (default: src/NegiCraftLauncher.Theme/Controls.xaml)
              --output <path>   Generated Avalonia XAML (default: src/NegiCraftLauncher.Theme.Avalonia/Generated/Controls.g.axaml)
              --check           Verify the generated file is current; exit 1 on drift
              --help            Show this help
            """);
    }
}
