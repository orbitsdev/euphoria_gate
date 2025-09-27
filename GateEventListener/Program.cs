using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
// Event time
[StructLayout(LayoutKind.Sequential)]
public struct NET_DVR_TIME
{
    public uint dwYear;
    public uint dwMonth;
    public uint dwDay;
    public uint dwHour;
    public uint dwMinute;
    public uint dwSecond;
}

// ACS event info (QR, Card, etc.)
[StructLayout(LayoutKind.Sequential)]
public struct NET_DVR_ACS_EVENT_INFO
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] byCardNo; // card or QR string
    public byte byCardType;
    public byte byDoorNo;
    public byte byReaderNo;
    public byte byDeviceNo;
    public byte byVerifyMode;
    public byte byNetworkChannel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] byRes; // reserved
}

// ACS alarm info (wrapper for full event)
[StructLayout(LayoutKind.Sequential)]
public struct NET_DVR_ACS_ALARM_INFO
{
    public uint dwSize;
    public NET_DVR_TIME struTime;
    public uint dwMajor;
    public uint dwMinor;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] sNetUser;
    public NET_DVR_ACS_EVENT_INFO struAcsEventInfo;
}
public static class HikvisionEventHelper
{
    public static string GetEventDescription(uint dwMajor, uint dwMinor)
    {
        // Major event types
        if (dwMajor == 5) // Major type: Access Control
        {
            switch (dwMinor)
            {
                case 0: return "Unknown ACS Event";
                case 1: return "Access Granted";
                case 2: return "Access Denied";
                case 3: return "Door Opened Normally";
                case 4: return "Door Forced Open";
                case 5: return "Door Held Open Too Long";
                case 53: return "Invalid Card/QR";
                case 75: return "Valid QR Access";
                default: return $"ACS Event (Minor={dwMinor})";
            }
        }

        return $"Other Event (Major={dwMajor}, Minor={dwMinor})";
    }
}

class Program
{
    // ===========================
    // SDK Function Imports
    // ===========================
    [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Init();
    [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Cleanup();
    [DllImport("HCNetSDK.dll")] public static extern uint NET_DVR_GetLastError();

    [DllImport("HCNetSDK.dll")]
    public static extern int NET_DVR_Login_V30(
        string sDVRIP,
        int wDVRPort,
        string sUserName,
        string sPassword,
        ref NET_DVR_DEVICEINFO_V30 lpDeviceInfo);

    [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_Logout(int lUserID);

    [DllImport("HCNetSDK.dll")]
    public static extern int NET_DVR_StartListen_V30(
        string sLocalIP,
        ushort wLocalPort,
        MSGCallBack fMessageCallBack,
        IntPtr pUserData);

    [DllImport("HCNetSDK.dll")] public static extern bool NET_DVR_StopListen_V30(int lListenHandle);

    // ===========================
    // SDK Structures
    // ===========================
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DEVICEINFO_V30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] sSerialNumber;
        public byte byAlarmInPortNum;
        public byte byAlarmOutPortNum;
        public byte byDiskNum;
        public byte byDVRType;
        public byte byChanNum;
        public byte byStartChan;
        public byte byAudioChanNum;
        public byte byIPChanNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] byRes2;
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
    private static ManualResetEvent quitEvent = new ManualResetEvent(false);

    // ===========================
    // Callback Implementation
    // ===========================
    private static bool AlarmCallback(int lCommand, IntPtr pAlarmer, IntPtr pAlarmInfo, uint dwBufLen, IntPtr pUser)
    {
        try
        {
            if (lCommand == 0x5002) // COMM_ALARM_ACS
            {
                NET_DVR_ACS_ALARM_INFO alarmInfo = Marshal.PtrToStructure<NET_DVR_ACS_ALARM_INFO>(pAlarmInfo);

                string cardOrQR = System.Text.Encoding.UTF8.GetString(alarmInfo.struAcsEventInfo.byCardNo).TrimEnd('\0');
                string eventDesc = HikvisionEventHelper.GetEventDescription(alarmInfo.dwMajor, alarmInfo.dwMinor);

                Console.WriteLine(
                    $"[{DateTime.Now}] Event: {eventDesc}, " +
                    $"CardOrQR={cardOrQR}, Door={alarmInfo.struAcsEventInfo.byDoorNo}, Reader={alarmInfo.struAcsEventInfo.byReaderNo}");
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now}] Unknown event. Command={lCommand}, DataLength={dwBufLen}");
            }
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
        // Load configuration
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

        // Set SDK DLL path
        string sdkPath = Path.Combine(AppContext.BaseDirectory, "sdk");
        Environment.SetEnvironmentVariable("PATH",
            sdkPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

        Console.CancelKeyPress += (sender, eArgs) =>
        {
            eArgs.Cancel = true;
            quitEvent.Set();
        };

        // ===========================
        // Forever Loop
        // ===========================
        while (!quitEvent.WaitOne(0))
        {
            try
            {
                if (!NET_DVR_Init())
                {
                    uint err = NET_DVR_GetLastError();
                    Console.Error.WriteLine($"[{DateTime.Now}] SDK init failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
                    Thread.Sleep(5000);
                    continue;
                }

                Console.WriteLine($"[{DateTime.Now}] SDK initialized successfully.");

                NET_DVR_DEVICEINFO_V30 deviceInfo = new NET_DVR_DEVICEINFO_V30();
                userId = NET_DVR_Login_V30(deviceIp, devicePort, username, password, ref deviceInfo);

                if (userId < 0)
                {
                    uint err = NET_DVR_GetLastError();
                    Console.Error.WriteLine($"[{DateTime.Now}] Login failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
                    NET_DVR_Cleanup();
                    Thread.Sleep(5000);
                    continue;
                }

                Console.WriteLine($"[{DateTime.Now}] Login successful. UserID={userId}");

                listenHandle = NET_DVR_StartListen_V30(null, listenPort, AlarmCallback, IntPtr.Zero);
                if (listenHandle < 0)
                {
                    uint err = NET_DVR_GetLastError();
                    Console.Error.WriteLine($"[{DateTime.Now}] Listener start failed. Error={err} ({HikvisionErrorHelper.GetErrorMessage(err)})");
                    NET_DVR_Logout(userId);
                    NET_DVR_Cleanup();
                    Thread.Sleep(5000);
                    continue;
                }

                Console.WriteLine($"[{DateTime.Now}] Listening started on port {listenPort}.");
                Console.WriteLine("Press Ctrl+C to exit...");

                // Wait until exit requested
                quitEvent.WaitOne();

                // Stop listening
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
                Console.WriteLine($"[{DateTime.Now}] SDK cleaned up.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.Now}] FATAL ERROR: {ex.Message}");
            }

            if (!quitEvent.WaitOne(0))
            {
                Console.WriteLine($"[{DateTime.Now}] Will retry in 5 seconds...");
                Thread.Sleep(5000);
            }
        }

        Console.WriteLine($"[{DateTime.Now}] GateEventListener shutting down gracefully.");
    }
}
