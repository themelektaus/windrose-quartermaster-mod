<#
    Windrose Stack Size Mod -- Configuration (example)

    Copy this file to `config.psd1` and adjust the paths to your system.
    `config.psd1` is listed in .gitignore so your user-specific paths don't
    get accidentally committed.

    The config is loaded by `lib\Common.ps1::Get-WindroseConfig`. Each script
    uses it as the default for its parameters; explicitly passed parameters
    take precedence.
#>
@{
    # Build paths.
    #
    # This section is OPTIONAL. If a key is omitted or empty, the default
    # relative to the modding root (= directory of this file) is used:
    #     Sources -> .\Sources
    #     Vanilla -> .\Sources\Vanilla
    #     Builds  -> .\Builds
    #
    # Entries may be relative ('Sources', '..\foo') or absolute. Relative
    # paths are resolved against the modding root. You only need this section
    # if you want to keep e.g. Builds/ or Vanilla/ outside the repo.
    Paths = @{
        # Sources = 'Sources'
        # Vanilla = 'Sources\Vanilla'
        # Builds  = 'Builds'
    }

    # Pak format (Windrose default, normally do NOT change).
    #
    # MountPoint
    #     Path prefix prepended to the pak-internal paths at mount time.
    #     '../../../' is the Windrose default and means "three levels up
    #     from the default mount point". With it, a pak entry
    #         R5\Plugins\R5BusinessRules\Content\InventoryItems\Foo.json
    #     ends up in the game at
    #         <Game-Root>\R5\Plugins\R5BusinessRules\Content\InventoryItems\Foo.json
    #     Only change this if your mod uses a different internal directory layout.
    #
    # Version
    #     Pak format version. V8B matches the current Windrose build (UE5.6).
    Pak = @{
        MountPoint = '../../../'
        Version    = 'V8B'
    }
}
