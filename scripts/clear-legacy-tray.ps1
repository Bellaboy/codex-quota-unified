# 升级清理：只移除旧额度插件且回调窗口已销毁的托盘记录；不改其他应用，不重启 Explorer。
$ErrorActionPreference='Stop'
Add-Type @"
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public static class LegacyQuotaTray {
 delegate bool E(IntPtr h,IntPtr p);
 [DllImport("user32.dll")]static extern bool EnumWindows(E e,IntPtr p);
 [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,E e,IntPtr p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")]static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("kernel32.dll")]static extern IntPtr OpenProcess(uint a,bool b,uint p);
 [DllImport("kernel32.dll")]static extern IntPtr VirtualAllocEx(IntPtr p,IntPtr a,UIntPtr n,uint t,uint r);
 [DllImport("kernel32.dll")]static extern bool VirtualFreeEx(IntPtr p,IntPtr a,UIntPtr n,uint t);
 [DllImport("kernel32.dll")]static extern bool ReadProcessMemory(IntPtr p,IntPtr a,byte[] b,int n,out UIntPtr read);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr p);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern bool Shell_NotifyIconW(uint op,ref N d);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct N {
 public uint size;public IntPtr hwnd;public uint uid,flags,message;public IntPtr icon;
 [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string tip;public uint state,mask;
 [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string info;public uint version;
 [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string title;public uint infoFlags;public Guid guid;public IntPtr balloon; }
 public static object Clean(){
 var bars=new HashSet<IntPtr>();
 E find=(h,p)=>{var s=new StringBuilder(256);GetClassName(h,s,256);if(s.ToString()=="ToolbarWindow32")bars.Add(h);return true;};
 EnumWindows((h,p)=>{var s=new StringBuilder(256);GetClassName(h,s,256);if(s.ToString()=="Shell_TrayWnd"||s.ToString()=="NotifyIconOverflowWindow")EnumChildWindows(h,find,p);return true;},IntPtr.Zero);
 int matching=0,dead=0,removed=0;
 foreach(var bar in bars){
 GetWindowThreadProcessId(bar,out uint pid);var p=OpenProcess(0x438,false,pid);if(p==IntPtr.Zero)continue;
 var remote=VirtualAllocEx(p,IntPtr.Zero,(UIntPtr)4096,0x3000,4);if(remote==IntPtr.Zero){CloseHandle(p);continue;}
 try{int count=SendMessage(bar,0x418,IntPtr.Zero,IntPtr.Zero).ToInt32();
 for(int i=count-1;i>=0;i--){if(SendMessage(bar,0x417,(IntPtr)i,remote)==IntPtr.Zero)continue;
 var b=new byte[32];if(!ReadProcessMemory(p,remote,b,32,out _))continue;
 var data=(IntPtr)BitConverter.ToInt64(b,16);var text=(IntPtr)BitConverter.ToInt64(b,24);if(text.ToInt64()<=65535)continue;
 var label=new byte[1024];if(!ReadProcessMemory(p,text,label,label.Length,out _))continue;
 var tip=Encoding.Unicode.GetString(label).Split('\0')[0];if(tip!="Codex 额度与恢复 · 正在连接"&&!tip.StartsWith("Codex · ",StringComparison.Ordinal))continue;
 matching++;var d=new byte[32];if(!ReadProcessMemory(p,data,d,32,out _))continue;
 var hwnd=(IntPtr)BitConverter.ToInt64(d,0);if(IsWindow(hwnd))continue;dead++;
 var n=new N{size=(uint)Marshal.SizeOf<N>(),hwnd=hwnd,uid=BitConverter.ToUInt32(d,8),tip="",info="",title=""};
 if(Shell_NotifyIconW(2,ref n))removed++;
 }}finally{VirtualFreeEx(p,remote,UIntPtr.Zero,0x8000);CloseHandle(p);}}
 return new{legacyMatchingIcons=matching,orphanedIcons=dead,removedIcons=removed,remainingIcons=matching-removed};
 }
}
"@
[LegacyQuotaTray]::Clean() | ConvertTo-Json
