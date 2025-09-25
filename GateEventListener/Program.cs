using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Configuration;

class Program
{
    // ===========================
    // SDK Function Imports
    // ===========================
    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Init();

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Cleanup();

    [DllImport("HCNetSDK.dll")]
    public static extern uint NET_DVR_GetLastError();

    [DllImport("HCNetSDK.dll")]
    public static extern int NET_DVR_Login_V30(
        string sDVRIP,
        int wDVRPort,
        string sUserName,
        string sPassword,
        ref NET_DVR_DEVICEINFO_V30 lpDeviceInfo);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Logout(int lUserID);

    [DllImport("HCNetSDK.dll")]
    public static extern int NET_DVR_StartListen_V30(
        string sLocalIP,
        ushort wLocalPort,
        MSGCallBack fMessageCallBack,
        IntPtr pUserData);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_StopListen_V30(int lListenHandle);

    // ===========================
    // SDK Structures
    // ===========================
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DEVICEINFO_V30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] sSerialNumber;
        public byte byAlarmInPortNum;
        public byte byAlarmOutPortNum;
        public byte byDiskNum;
        public byte byDVRType;
        public byte byChanNum;
        public byte byStartChan;
        public byte byAudioChanNum;
        public byte byIPChanNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] byRes2;
    }

    // ===========================
    // Callback Delegate
    // ===========================
    public delegate bool MSGCallBack(
        int lCommand,
        IntPtr pAlarmer,
        IntPtr pAlarmInfo,
        uint dwBufLen,
        IntPtr pUser);

    private static int listenHandle = -1;
    private static int userId = -1;

    // ===========================
    // Callback Implementation
    // ===========================
    private static bool AlarmCallback(
        int lCommand,
        IntPtr pAlarmer,
        IntPtr pAlarmInfo,
        uint dwBufLen,
        IntPtr pUser)
    {
        try
        {
            Console.WriteLine($"[{DateTime.Now}] Event received. Command={lCommand}, DataLength={dwBufLen}");
            // TODO: parse ACS alarm info and detect QR code events
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now}] ERROR in callback: {ex.Message}");
        }
        return true;
    }

    // ===========================
    // Main Entry
    // ===========================
    static void Main(string[] args)
    {
        // 1. Load configuration
        IConfiguration config = new ConfigurationBuilder()
           .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string deviceIp = config["Hikvision:Ip"];
        int devicePort = int.Parse(config["Hikvision:Port"]);
        string username = config["Hikvision:Username"];
        string password = config["Hikvision:Password"];
        ushort listenPort = ushort.Parse(config["Hikvision:ListenPort"]);

        Console.WriteLine($"[{DateTime.Now}] Starting GateEventListener...");
        Console.WriteLine($"Device IP: {deviceIp}, Port: {devicePort}, User: {username}, ListenPort: {listenPort}");

        // 2. Setup SDK DLL path
        string sdkPath = Path.Combine(AppContext.BaseDirectory, "sdk");
        Environment.SetEnvironmentVariable("PATH",
            sdkPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

        // 3. Init SDK
        if (!NET_DVR_Init())
        {
            uint err = NET_DVR_GetLastError();
            Console.Error.WriteLine($"[{DateTime.Now}] SDK init failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
            return;
        }

        Console.WriteLine($"[{DateTime.Now}] SDK initialized successfully.");

        // 4. Login to device
        NET_DVR_DEVICEINFO_V30 deviceInfo = new NET_DVR_DEVICEINFO_V30();
        userId = NET_DVR_Login_V30(deviceIp, devicePort, username, password, ref deviceInfo);
        if (userId < 0)
        {
            uint err = NET_DVR_GetLastError();
            Console.Error.WriteLine($"Login failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
            NET_DVR_Cleanup();
            return;
        }
        Console.WriteLine($"[{DateTime.Now}] Login successful. UserID={userId}");

        // 5. Start listening
        listenHandle = NET_DVR_StartListen_V30(null, listenPort, AlarmCallback, IntPtr.Zero);
        if (listenHandle < 0)
        {
            uint err = NET_DVR_GetLastError();
            Console.Error.WriteLine($"[{DateTime.Now}] Listener start failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
            NET_DVR_Logout(userId);
            NET_DVR_Cleanup();
            return;
        }

        Console.WriteLine($"[{DateTime.Now}] Listening started on port {listenPort}.");

        // 6. Keep running until Ctrl+C
        Console.WriteLine("Press Ctrl+C to exit...");
        ManualResetEvent quitEvent = new ManualResetEvent(false);
        Console.CancelKeyPress += (sender, eArgs) =>
        {
            eArgs.Cancel = true;
            quitEvent.Set();
        };
        quitEvent.WaitOne();

        // 7. Shutdown
        if (listenHandle >= 0)
        {
            NET_DVR_StopListen_V30(listenHandle);
            Console.WriteLine($"[{DateTime.Now}] Listener stopped.");
        }

        if (userId >= 0)
        {
            NET_DVR_Logout(userId);
            Console.WriteLine($"[{DateTime.Now}] Logged out of device.");
        }

        NET_DVR_Cleanup();
        Console.WriteLine($"[{DateTime.Now}] SDK cleaned up. Exiting...");
    }
}
