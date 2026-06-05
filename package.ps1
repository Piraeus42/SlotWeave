$ErrorActionPreference = "Stop"

dotnet build -c Release
cargo build --release

if (Test-Path ./local/SlotWeave) {
  Remove-Item ./local/SlotWeave -Recurse
}
Copy-Item -Path ./SlotWeave/bin/Release/net8.0 -Destination ./local/SlotWeave/SlotWeave/core -Recurse
Copy-Item -Path ./target/release/loader.dll -Destination ./local/SlotWeave/winmm.dll

New-Item -Path ./local/SlotWeave/SlotWeave/mods -ItemType Directory
Write-Output "Copy your mods into this directory (make sure they're in separate folders)." > ./local/SlotWeave/SlotWeave/mods/README.txt

if (Test-Path ./local/SlotWeave.zip) {
  Remove-Item ./local/SlotWeave.zip
}

# Thunderstore
if (Test-Path ./thunderstore/SlotWeave) {
  Remove-Item ./thunderstore/SlotWeave -Recurse
}
Copy-Item -Path ./local/SlotWeave/SlotWeave -Destination ./thunderstore/SlotWeave -Recurse

if (Test-Path ./thunderstore/winmm.dll) {
  Remove-Item ./thunderstore/winmm.dll
}
Copy-Item -Path ./local/SlotWeave/winmm.dll -Destination ./thunderstore/winmm.dll

Copy-Item -Path ./README.md -Destination ./thunderstore/README.md

# thunderstore doesn't need the mods directory
Remove-Item ./thunderstore/SlotWeave/mods -Recurse