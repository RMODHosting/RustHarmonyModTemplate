Write-Output "Downloading Rust Server Files From Steam"
Start-Process -FilePath $(".\steamcmd\steamcmd.exe" ) -ArgumentList "+login anonymous +force_install_dir $("C:\Users\Anthony\source\repos\HarmonyMods\Steam\Rust") +app_update 258550 validate +quit" -Wait -NoNewWindow
