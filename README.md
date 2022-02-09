# Harmony Mod Template

Couple scripts:

`Steam/Update.ps1` installs a vanilla server. This is where you'll reference assemblies from.
`Steam/Publicize.ps1` runs the publicizer.
`Steam/InstallOxide.ps1` downloads Oxide, if you want to test alongside Oxide plugins.

Build the project, copy the output DLL into Rust/HarmonyMods, and it will load.
