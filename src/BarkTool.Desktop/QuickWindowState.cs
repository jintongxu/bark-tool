using System.Text.Json;
namespace BarkTool.Desktop;
internal sealed record QuickWindowState(double? Left=null,double? Top=null,double Width=430,double Height=520);
internal static class QuickWindowStore
{
 private static readonly string PathName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BarkTool","quick-window.json");
 internal static QuickWindowState Load(){try{return File.Exists(PathName)?JsonSerializer.Deserialize<QuickWindowState>(File.ReadAllText(PathName))??new():new();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){return new();}}
 internal static void Save(QuickWindowState state){try{Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);File.WriteAllText(PathName,JsonSerializer.Serialize(state));}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }}
}
