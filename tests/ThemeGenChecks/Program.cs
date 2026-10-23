var directory = new DirectoryInfo(AppContext.BaseDirectory);
while (directory is not null &&
       !File.Exists(Path.Combine(directory.FullName, "NegiCraftLauncher.slnx")))
{
    directory = directory.Parent;
}

if (directory is null)
{
    Console.Error.WriteLine("Could not find the repository root.");
    return 2;
}

Environment.CurrentDirectory = directory.FullName;
return NegiThemes.Program.Main(["--check"]);
