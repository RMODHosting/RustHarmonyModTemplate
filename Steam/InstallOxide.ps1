Invoke-WebRequest -Uri 'https://umod.org/games/rust/download?tag=public' -OutFile '.\Oxide.zip'
    Expand-Archive -Path Oxide.zip -DestinationPath .\Rust -Force
