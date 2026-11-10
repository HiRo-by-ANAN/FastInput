using System.Runtime.InteropServices;
using System.Text;

namespace ProbeLab;
public static class Native
{
    public const uint Marker = 0x50524F42;
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; public POINT(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] public struct RECT {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {public uint cbSize,flags;public nint hwndActive,hwndFocus,hwndCapture,hwndMenuOwner,hwndMoveSize,hwndCaret; public RECT rcCaret;}
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {public uint type; public InputUnion U;}
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion {[FieldOffset(0)]public KEYBDINPUT ki;[FieldOffset(0)]public MOUSEINPUT mi;}
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {public ushort wVk,wScan;public uint dwFlags,time;public nuint dwExtraInfo;}
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {public int dx,dy;public uint mouseData,dwFlags,time;public nuint dwExtraInfo;}
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT {public uint vkCode,scanCode,flags,time;public nuint dwExtraInfo;}
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT {public POINT pt;public uint mouseData,flags,time;public nuint dwExtraInfo;}
    public delegate nint HookProc(int code,nint wParam,nint lParam);
    public delegate bool EnumWindowsProc(nint hwnd,nint param);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h,out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback,nint param);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(nint h,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetWindowTextW")] public static extern int GetWindowText(nint h,StringBuilder text,int capacity);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h,uint flags);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h,out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread,ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint count,INPUT[] inputs,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="SendMessageTimeoutW")]
    public static extern nint SendMessageTimeout(nint h,uint msg,nint wp,nint lp,uint flags,uint timeout,out nuint result);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="SendMessageTimeoutW")]
    public static extern nint SendMessageTimeoutText(nint h,uint msg,nint wp,nint lp,uint flags,uint timeout,out nuint result);
    [DllImport("user32.dll",SetLastError=true)] public static extern nint SetWindowsHookEx(int id,HookProc proc,nint module,uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook,int code,nint wp,nint lp);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(nint h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint h,int id);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT point,uint flags);
    public static INPUT[] Key(ushort vk,bool up=false) => new[]{new INPUT{type=1,U=new InputUnion{ki=new KEYBDINPUT{wVk=vk,dwFlags=up?2u:0u,dwExtraInfo=Marker}}}};
    public static INPUT[] Unicode(string s) => s.SelectMany(c=> new[]{new INPUT{type=1,U=new InputUnion{ki=new KEYBDINPUT{wScan=c,dwFlags=4,dwExtraInfo=Marker}}},new INPUT{type=1,U=new InputUnion{ki=new KEYBDINPUT{wScan=c,dwFlags=6,dwExtraInfo=Marker}}}}).ToArray();
    public static INPUT[] MouseClick(int x,int y)
    {
        int l=GetSystemMetrics(76),t=GetSystemMetrics(77),w=GetSystemMetrics(78),h=GetSystemMetrics(79);
        return new[]{new INPUT{type=0,U=new InputUnion{mi=new MOUSEINPUT{dx=(int)((long)(x-l)*65535/Math.Max(1,w-1)),dy=(int)((long)(y-t)*65535/Math.Max(1,h-1)),dwFlags=0xC001,dwExtraInfo=Marker}}},new INPUT{type=0,U=new InputUnion{mi=new MOUSEINPUT{dwFlags=2,dwExtraInfo=Marker}}},new INPUT{type=0,U=new InputUnion{mi=new MOUSEINPUT{dwFlags=4,dwExtraInfo=Marker}}}};
    }
}
