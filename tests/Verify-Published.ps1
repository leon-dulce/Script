param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PublishedManifest {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
}
'@
$module = [PublishedManifest]::LoadLibraryEx((Resolve-Path -LiteralPath $Executable).Path, [IntPtr]::Zero, 2)
if ($module -eq [IntPtr]::Zero) { throw 'Cannot read executable resources.' }
try {
    $resource = [PublishedManifest]::FindResource($module, [IntPtr]1, [IntPtr]24)
    if ($resource -eq [IntPtr]::Zero) { throw 'Executable manifest missing.' }
    $size = [PublishedManifest]::SizeofResource($module, $resource)
    $pointer = [PublishedManifest]::LockResource([PublishedManifest]::LoadResource($module, $resource))
    $bytes = New-Object byte[] $size
    [Runtime.InteropServices.Marshal]::Copy($pointer, $bytes, 0, $size)
    [xml]$manifest = [Text.Encoding]::UTF8.GetString($bytes).Trim([char]0, [char]0xFEFF)
    $level = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($level.level -ne 'requireAdministrator' -or $level.uiAccess -ne 'false') {
        throw 'Published executable does not request administrator privileges correctly.'
    }
    Write-Output 'PASS published executable contains requireAdministrator and uiAccess=false'
}
finally { [void][PublishedManifest]::FreeLibrary($module) }
