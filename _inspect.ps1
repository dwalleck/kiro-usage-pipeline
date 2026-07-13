$pkg = Get-ChildItem "$env:USERPROFILE\.nuget\packages\amazon.cdk.lib" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$dll = Get-ChildItem $pkg.FullName -Recurse -Filter Amazon.CDK.Lib.dll | Select-Object -First 1 -ExpandProperty FullName
Write-Output "DLL: $dll"
$asm = [Reflection.Assembly]::LoadFrom($dll)
Write-Output "--- Runtime DOTNET fields ---"
$asm.GetType('Amazon.CDK.AWS.Lambda.Runtime').GetFields([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static) | Where-Object { $_.Name -like '*DOTNET*' } | ForEach-Object { $_.Name }
Write-Output "--- ILocalBundling methods ---"
$asm.GetType('Amazon.CDK.ILocalBundling').GetMethods() | ForEach-Object { $_.ToString() }
Write-Output "--- Architecture fields ---"
$asm.GetType('Amazon.CDK.AWS.Lambda.Architecture').GetProperties([Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static) | ForEach-Object { $_.Name }
