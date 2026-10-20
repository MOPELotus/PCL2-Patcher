param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$ReferenceDirectory
)
# Run with Windows PowerShell 5.1 / .NET Framework 4.8, not PowerShell Core.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use powershell.exe (.NET Framework), not pwsh.' }
$script:PclReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $requested = [Reflection.AssemblyName]::new($eventArgs.Name)
    $candidate = [IO.Path]::Combine($script:PclReferenceDirectory, $requested.Name + '.dll')
    if ([IO.File]::Exists($candidate)) { return [Reflection.Assembly]::LoadFrom($candidate) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($Executable))
    $flags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance,DeclaredOnly'
    $targets = @(
        @('PCL.PageDownloadInstall', 'LoaderInit', 0),
        @('PCL.ModModpack', 'ModpackInstall', 0),
        @('PCL.ModModpack', 'ModpackInstall', 3),
        @('PCL.PageDownloadResourceDetail', 'InitializeStackResourceVersions', 1),
        @('PCL.PageDownloadModify', 'LoadInstanceFinished', 1)
    )
    $prepared = @()
    foreach ($target in $targets) {
        $type = $assembly.GetType($target[0], $true)
        $methods = @($type.GetMethods($flags) | Where-Object { $_.Name -eq $target[1] -and $_.GetParameters().Count -eq $target[2] })
        if ($methods.Count -ne 1) { throw "Missing/ambiguous CLR method: $target" }
        [Runtime.CompilerServices.RuntimeHelpers]::PrepareMethod($methods[0].MethodHandle)
        $prepared += $methods[0].ToString()
    }
    @{ assembly = $assembly.FullName; preparedMethods = $prepared; count = $prepared.Count; scope = 'CLR load and JIT preparation only; no UI or download operations' } | ConvertTo-Json
}
finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
