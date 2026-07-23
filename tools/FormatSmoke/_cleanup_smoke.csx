using RoslynMcp.ServiceLayer;
var r = await CleanupDocument.CleanupAsync(
  @"D:\Experiments\Personal Cursor Folder\Financial\software\open\roslyn-mcp\RoslynMcp.csproj",
  @"D:\Experiments\Personal Cursor Folder\Financial\software\open\roslyn-mcp\ToolCatalog.cs",
  apply: false,
  profile: "whitespace");
Console.WriteLine(r);
