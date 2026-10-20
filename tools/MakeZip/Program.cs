// Builds the Linux package as a .zip that keeps Unix permissions (the executable bit of the program and install.sh).
// Usage:  dotnet run --project tools/MakeZip -- <folder with the files> <output.zip>
var src = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
UnixZip.Create(src, output, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
Console.WriteLine($"{output} ({new FileInfo(output).Length / 1048576.0:0.0} MB)");
