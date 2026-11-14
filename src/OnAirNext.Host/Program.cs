using OnAirNext.Host;
using OnAirNext.ServiceControl;

if (args.Length == 2 && args[0] == "--inspect-maintenance-journal") return MaintenanceJournalProbe.Run(args[1]);

if (args.Length == 2 && args[0] == "--probe-media-source") return await SourceAccessProbe.RunAsync(args[1]);

if(args is ["--macos-initialize"]){MacService.Initialize();return 0;}
if(args is ["--macos-paths"] && OperatingSystem.IsMacOS())
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {config=MacService.ConfigPath,descriptor=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","browser-console","access","console.json")}));return 0;
}
if(args is ["--macos-service","--config",var macConfig] && OperatingSystem.IsMacOS())
{
    MacService.RequireInstalled(macConfig);
    using var macStop=new CancellationTokenSource();
    using var termination=System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,context=>{context.Cancel=true;macStop.Cancel();});
    return await EngineHost.RunAsync(macConfig,false,macStop.Token,()=>{},()=>{},macService:true);
}
var serviceMode = args.Length == 3 && args[0] == "--windows-service" && args[1] == "--config";
if (!serviceMode && (args.Length != 2 || args[0] != "--config"))
{
    Console.Error.WriteLine("Use the guided installation, or the isolated developer host configuration.");
    return 2;
}
var configPath=args[serviceMode?2:1];
if(serviceMode)
{
    if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service mode requires Windows.");
    return WindowsServiceRunner.Run((token,ready,stopping)=>EngineHost.RunAsync(configPath,true,token,ready,stopping));
}
using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,eventArgs)=>{eventArgs.Cancel=true;stop.Cancel();};
return await EngineHost.RunAsync(configPath,false,stop.Token,()=>{},()=>{});
