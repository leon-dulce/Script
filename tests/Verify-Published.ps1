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
    public delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    public static byte[] ReadResourceBytes(IntPtr module, IntPtr name, int type) {
        var resource = FindResource(module, name, new IntPtr(type));
        if (resource == IntPtr.Zero) throw new Exception("Missing icon resource");
        var bytes = new byte[SizeofResource(module, resource)];
        Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
        return bytes;
    }
    public static byte[] FirstIconGroup(IntPtr module) {
        byte[] result = null;
        ResourceNameCallback callback = (m,t,n,p) => { result = ReadResourceBytes(m,n,14); return false; };
        EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero);
        return result ?? throw new Exception("Executable icon group missing");
    }
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
    $iconFile = Join-Path $PSScriptRoot '../assets/flowkey.ico'
    $icon = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $iconFile).Path)
    $group = [PublishedManifest]::FirstIconGroup($module)
    $count = [BitConverter]::ToUInt16($icon, 4)
    if ([BitConverter]::ToUInt16($group, 4) -ne $count) { throw 'Published icon resolution count differs.' }
    for ($i = 0; $i -lt $count; $i++) {
        $iconOffset = 6 + $i * 16
        $groupOffset = 6 + $i * 14
        $resourceId = [BitConverter]::ToUInt16($group, $groupOffset + 12)
        $actual = [PublishedManifest]::ReadResourceBytes($module, [IntPtr][int]$resourceId, 3)
        $length = [BitConverter]::ToUInt32($icon, $iconOffset + 8)
        $start = [BitConverter]::ToUInt32($icon, $iconOffset + 12)
        $expected = $icon[$start..($start + $length - 1)]
        if ([Convert]::ToBase64String($actual) -ne [Convert]::ToBase64String($expected)) { throw "Published icon image $i does not match the shared logo." }
    }
    Write-Output 'PASS published executable contains all nine matching FlowKey icon sizes'
}
finally { [void][PublishedManifest]::FreeLibrary($module) }
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Executable).Path)
if ($versionInfo.FileVersion -ne '1.0.1.0' -or $versionInfo.ProductVersion -ne '1.0.1 Stable' -or $versionInfo.ProductName -ne 'FlowKey') {
    throw "Published executable has unexpected version metadata: file=$($versionInfo.FileVersion), product=$($versionInfo.ProductVersion), name=$($versionInfo.ProductName)."
}
Write-Output 'PASS published executable identifies FlowKey 1.0.1 Stable'
