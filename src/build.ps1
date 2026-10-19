# =====================================================================
#  RustDesk Helper - build script
#  1) Copy the ORIGINAL icon group out of rustdesk.exe's PE resources
#     (byte-for-byte, so Explorer renders exactly the official icon)
#  2) Compile RustDeskHelper.cs with the built-in csc.exe (no VS needed)
#  3) Output to the parent folder as "RustDesk <Chinese name>.exe"
#  Usage: powershell -ExecutionPolicy Bypass -File build.ps1
#  NOTE: keep this file ASCII-only so PowerShell 5.1 parses it reliably.
# =====================================================================
$ErrorActionPreference = 'Stop'

$srcDir  = $PSScriptRoot
$src     = Join-Path $srcDir 'RustDeskHelper.cs'
$outDir  = Split-Path $srcDir -Parent
$cnName  = 'RustDesk ' + [char]0x4E00 + [char]0x952E + [char]0x914D + [char]0x7F6E + [char]0x52A9 + [char]0x624B + '.exe'   # "RustDesk yi-jian-pei-zhi-zhu-shou.exe"
$exeOut  = Join-Path $outDir $cnName
$build   = Join-Path $env:TEMP 'rdhelper_build'
$ico     = Join-Path $build 'rustdesk.ico'
$csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force -Path $build | Out-Null

# csc.exe mangles non-ASCII command line paths (ANSI code page), so we compile
# inside an ASCII-only temp folder and copy the result to the Chinese target.
$workSrc = Join-Path $build 'src.cs'
$workOut = Join-Path $build 'out.exe'
Copy-Item -Force $src $workSrc

# ---------------- 1. locate rustdesk.exe and copy its real icon ----------------
$rd = ''
foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk')) {
    if (Test-Path $k) {
        $loc = ''
        try { $loc = (Get-ItemProperty $k).InstallLocation } catch { }
        if ($loc) { $c = Join-Path $loc 'rustdesk.exe'; if (Test-Path $c) { $rd = $c; break } }
    }
}
if (-not $rd) {
    foreach ($c in @('C:\Program Files\RustDesk\rustdesk.exe', (Join-Path $env:LOCALAPPDATA 'rustdesk\rustdesk.exe'))) {
        if (Test-Path $c) { $rd = $c; break }
    }
}

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class IcoMaker
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr d);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")] static extern bool EnumResourceNames(IntPtr h, IntPtr type, EnumResNameProc cb, IntPtr p);
    public delegate bool EnumResNameProc(IntPtr h, IntPtr type, IntPtr name, IntPtr p);

    const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
    static readonly IntPtr RT_ICON = new IntPtr(3);
    static readonly IntPtr RT_GROUP_ICON = new IntPtr(14);

    // read one RT_GROUP_ICON group and rebuild it as a real .ico file whose
    // payloads are the untouched RT_ICON bytes taken from the PE resource.
    static byte[] FromGroup(IntPtr mod, IntPtr groupName, out int frames)
    {
        frames = 0;
        IntPtr r = FindResource(mod, groupName, RT_GROUP_ICON);
        if (r == IntPtr.Zero) return null;
        IntPtr d = LockResource(LoadResource(mod, r));
        if (d == IntPtr.Zero) return null;
        int count = Marshal.ReadInt16(d, 4);
        if (count <= 0) return null;

        byte[][] imgs = new byte[count][];
        byte[][] ents = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int b = 6 + 14 * i;
            byte w = Marshal.ReadByte(d, b);
            byte h = Marshal.ReadByte(d, b + 1);
            byte cc = Marshal.ReadByte(d, b + 2);
            byte rs = Marshal.ReadByte(d, b + 3);
            short planes = Marshal.ReadInt16(d, b + 4);
            short bits = Marshal.ReadInt16(d, b + 6);
            short id = Marshal.ReadInt16(d, b + 12);

            IntPtr ir = FindResource(mod, new IntPtr(id), RT_ICON);
            if (ir == IntPtr.Zero) return null;
            uint sz = SizeofResource(mod, ir);
            IntPtr idata = LockResource(LoadResource(mod, ir));
            if (idata == IntPtr.Zero || sz == 0) return null;
            byte[] img = new byte[sz];
            Marshal.Copy(idata, img, 0, (int)sz);

            byte[] e = new byte[16];
            e[0] = w; e[1] = h; e[2] = cc; e[3] = rs;
            BitConverter.GetBytes(planes).CopyTo(e, 4);
            BitConverter.GetBytes(bits).CopyTo(e, 6);
            BitConverter.GetBytes((int)sz).CopyTo(e, 8);
            imgs[i] = img; ents[i] = e;
        }

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)count);
            int off = 6 + 16 * count;
            for (int i = 0; i < count; i++)
            {
                BitConverter.GetBytes(off).CopyTo(ents[i], 12);
                bw.Write(ents[i]);
                off += imgs[i].Length;
            }
            for (int i = 0; i < count; i++) bw.Write(imgs[i]);
            frames = count;
            return ms.ToArray();
        }
    }

    // 0 in an ICONDIRENTRY width byte means 256
    static int MaxDim(byte[] ico)
    {
        int n = BitConverter.ToInt16(ico, 4);
        int max = 0;
        for (int i = 0; i < n; i++)
        {
            int w = ico[6 + 16 * i];
            if (w == 0) w = 256;
            if (w > max) max = w;
        }
        return max;
    }

    // rustdesk.exe ships several icon groups; pick the one with the biggest
    // frame (the app icon, which has a 256x256 entry).
    public static int Build(string exe, string outPath)
    {
        IntPtr mod = LoadLibraryEx(exe, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (mod == IntPtr.Zero) return 0;
        try
        {
            List<IntPtr> groups = new List<IntPtr>();
            EnumResNameProc cb = delegate(IntPtr h, IntPtr t, IntPtr name, IntPtr p) { groups.Add(name); return true; };
            EnumResourceNames(mod, RT_GROUP_ICON, cb, IntPtr.Zero);

            byte[] best = null; int bestScore = -1; int bestFrames = 0;
            foreach (IntPtr g in groups)
            {
                int n;
                byte[] ico = FromGroup(mod, g, out n);
                if (ico == null) continue;
                int score = MaxDim(ico);
                if (score > bestScore) { bestScore = score; best = ico; bestFrames = n; }
            }
            GC.KeepAlive(cb);
            if (best == null) return 0;
            File.WriteAllBytes(outPath, best);
            return bestFrames;
        }
        finally { FreeLibrary(mod); }
    }
}
"@

$outArg  = '/out:' + $workOut
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', $outArg,
             '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
             '/reference:System.Management.dll', '/reference:System.ServiceProcess.dll')

if ($rd) {
    $icoFrames = [IcoMaker]::Build($rd, $ico)
    if ($icoFrames -gt 0) {
        Write-Host (' [icon] copied original icon from rustdesk.exe: ' + $rd + '  (frames=' + $icoFrames + ')')
        $cscArgs += ('/win32icon:' + $ico)
    } else { Write-Host ' [warn] icon extraction failed, using default icon' }
} else {
    Write-Host ' [warn] rustdesk.exe not found, using default icon'
}

Write-Host ' [build] compiling RustDeskHelper.cs ...'
$cscArgs += $workSrc
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw (' build failed, exit code ' + $LASTEXITCODE) }

Copy-Item -Force $workOut $exeOut
Write-Host (' [done] ' + $exeOut + '  (' + (Get-Item $exeOut).Length + ' bytes)')
