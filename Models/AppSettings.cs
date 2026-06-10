namespace NaiwaProxy.Models;

public sealed class AppSettings
{
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public string CoreExecutable { get; set; } = "xray.exe";
    public List<VmessProfile> Profiles { get; set; } = [];
    public string? SelectedProfileId { get; set; }
}
