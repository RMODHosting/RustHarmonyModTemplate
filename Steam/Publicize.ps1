Set-Location .\Rust\RustDedicated_Data\Managed
Get-ChildItem ".\" | Foreach {
    Write-Output $_.fullname
    ..\..\..\AssemblyPublicizer.exe $_.fullname
}
Start-Sleep -Seconds 5