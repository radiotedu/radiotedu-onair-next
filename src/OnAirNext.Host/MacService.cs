using System.Security.Cryptography;
using System.Text.Json;
using OnAirNext.Engine;

namespace OnAirNext.Host;

// macOS has its own installed service boundary. Developer configs stay loopback-only.
internal static class MacService
{
    public const string Pipe = "onair-next-dev-macos";
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RadioTEDU","OnAirNext-Dev","mac-service");
    public static string ConfigPath => Path.Combine(Root,"host.json");
    public static string Data => Path.Combine(Root,"data");
    public static string Resources => "/Applications/RadioTEDU OnAir Next.app/Contents/Resources";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead|UnixFileMode.UserWrite;
    public static void RequireInstalled(string configPath)
    {
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("The macOS service requires macOS.");
        if(Path.GetFullPath(configPath)!=ConfigPath || Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)!=Path.Combine(Resources,"engine"))
            throw new UnauthorizedAccessException("Start the installed macOS service from its application package.");
        RequirePrivate(Root,true); RequirePrivate(Data,true); RequirePrivate(ConfigPath,false);
    }
    public static void Initialize()
    {
        if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("macOS installation only.");
        if(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)!=Path.Combine(Resources,"engine"))throw new UnauthorizedAccessException("Install the application in Applications first.");
        Directory.CreateDirectory(Root,PrivateDirectory);Directory.CreateDirectory(Data,PrivateDirectory);
        RequirePrivate(Root,true);RequirePrivate(Data,true);
        if(File.Exists(ConfigPath)){RequireInstalled(ConfigPath);return;}
        var stations=new[]{"1","2","4","5","8","9","10","11"}.Select(id=>new StationSettings(id,[],[],AutoStart:false,CrossfadeSeconds:id=="1"?0:3)).ToList();
        WritePrivate(ConfigPath,JsonSerializer.SerializeToUtf8Bytes(new HostSettings(Path.Combine(Resources,"audio-runtime","bin","ffmpeg"),Data,Pipe,stations)));
    }
    private static void RequirePrivate(string path,bool directory)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(!Path.GetFullPath(path).StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&path!=Root)throw new UnauthorizedAccessException("Unexpected service data path.");
        for(var item=path;!string.IsNullOrEmpty(item);item=Path.GetDirectoryName(item))
            if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("Service data cannot traverse links.");
        var mode=File.GetUnixFileMode(path);
        var foreign=UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute;
        if((mode&foreign)!=0)throw new UnauthorizedAccessException("Service data must be accessible only to its operator account.");
        if(directory&&!Directory.Exists(path))throw new DirectoryNotFoundException("Service data is missing.");
    }
    private static void WritePrivate(string destination,byte[] content)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(File.Exists(destination))RequirePrivate(destination,false);
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using(var file=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=PrivateFile}))
            {file.Write(content);file.Flush(true);}
            File.Move(temporary,destination,true);RequirePrivate(destination,false);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static ProductionOutputConfiguration? Load(IEnumerable<string> stations,JsonSerializerOptions json)
    {
        var file=Path.Combine(Data,"outputs.aes");if(!File.Exists(file))return null;RequirePrivate(file,false);
        var keyPath=Path.Combine(Data,"output-key");RequirePrivate(keyPath,false);
        var key=File.ReadAllBytes(keyPath);var encrypted=File.ReadAllBytes(file);
        if(key.Length!=32||encrypted.Length is <28 or >131072)throw new InvalidDataException("Invalid protected output settings.");
        var plain=new byte[encrypted.Length-28];
        try
        {
            using var aes=new AesGcm(key,16);aes.Decrypt(encrypted.AsSpan(0,12),encrypted.AsSpan(28),encrypted.AsSpan(12,16),plain);
            return (JsonSerializer.Deserialize<ProductionOutputConfiguration>(plain,json)??throw new InvalidDataException("Missing output settings.")).Validate(stations);
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    public static void Save(ProductionOutputConfiguration profile,IEnumerable<string> stations,JsonSerializerOptions json)
    {
        RequirePrivate(Data,true);profile.Validate(stations);var file=Path.Combine(Data,"outputs.aes");var keyPath=Path.Combine(Data,"output-key");
        if(!File.Exists(keyPath))WritePrivate(keyPath,RandomNumberGenerator.GetBytes(32));RequirePrivate(keyPath,false);
        var key=File.ReadAllBytes(keyPath);var plain=JsonSerializer.SerializeToUtf8Bytes(profile,json);
        if(plain.Length>65536)throw new ArgumentException("Output settings exceed their limit.");
        try
        {
            var encrypted=new byte[plain.Length+28];RandomNumberGenerator.Fill(encrypted.AsSpan(0,12));
            using var aes=new AesGcm(key,16);aes.Encrypt(encrypted.AsSpan(0,12),plain,encrypted.AsSpan(28),encrypted.AsSpan(12,16));
            if(File.Exists(file)){_=Load(stations,json);WritePrivate(Path.Combine(Data,"outputs.previous.aes"),File.ReadAllBytes(file));}
            WritePrivate(file,encrypted);
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
}
