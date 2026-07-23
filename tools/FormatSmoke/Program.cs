using RoslynMcp.ServiceLayer;

var sol = @"D:\Experiments\Personal Cursor Folder\Financial\software\open\cdp-core\Cdp.Core.csproj";
var file = @"D:\Experiments\Personal Cursor Folder\Financial\software\open\cdp-core\Wave1AffordanceSeed.cs";
var before = await File.ReadAllTextAsync(file);
Console.WriteLine(await FormatDocument.FormatAsync(sol, file, apply: true, aggressive: true));
var after = await File.ReadAllTextAsync(file);
Console.WriteLine(before == after ? "UNCHANGED" : "CHANGED");
