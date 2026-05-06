<#
    Windrose-Modding Configuration (Beispiel)

    Kopiere diese Datei nach `config.psd1` und passe die Pfade an dein System an.
    `config.psd1` ist in .gitignore eingetragen, damit deine User-spezifischen
    Pfade nicht versehentlich eingecheckt werden.

    Geladen wird die Config von `_config.ps1`. Jedes Skript nutzt sie als
    Default fuer seine Parameter; explizit uebergebene Parameter haben Vorrang.
#>
@{
    # Build-Pfade.
    #
    # Diese Sektion ist OPTIONAL. Wenn ein Schluessel weggelassen oder leer ist,
    # wird der Default relativ zum Modding-Root (= Verzeichnis dieser Datei)
    # verwendet:
    #     Sources -> .\Sources
    #     Vanilla -> .\Sources\Vanilla
    #     Builds  -> .\Builds
    #     Dumps   -> .\ue4ss-mods\VanillaItemDumper\Dumps
    #
    # Eintraege duerfen relativ ('Sources', '..\foo') oder absolut sein.
    # Relative Pfade werden gegen den Modding-Root aufgeloest. Du brauchst die
    # Sektion nur, wenn du z.B. Builds/ oder Vanilla/ ausserhalb des Repos
    # halten willst.
    Paths = @{
        # Sources = 'Sources'
        # Vanilla = 'Sources\Vanilla'
        # Builds  = 'Builds'
        # Dumps   = 'ue4ss-mods\VanillaItemDumper\Dumps'
    }

    # Tool-Pfade (User-spezifisch).
    Tools = @{
        RepakExe = 'C:\Tools\repak\repak.exe'
    }

    # Pak-Format (Windrose-Standard, normalerweise NICHT aendern).
    #
    # MountPoint
    #     Pfad-Praefix, das beim Mounten an die Pak-internen Pfade angehaengt
    #     wird. '../../../' ist der Windrose-Standard und bedeutet "drei Ebenen
    #     hoch vom Default-Mount-Punkt". Damit landet ein Pak-Eintrag
    #         R5\Plugins\R5BusinessRules\Content\InventoryItems\Foo.json
    #     im Spiel unter
    #         <Spiel-Root>\R5\Plugins\R5BusinessRules\Content\InventoryItems\Foo.json
    #     Nur aendern, wenn die Mod eine andere interne Verzeichnisstruktur nutzt.
    #
    # Version
    #     Pak-Format-Version. V8B passt zum aktuellen Windrose-Build (UE5.6).
    Pak = @{
        MountPoint = '../../../'
        Version    = 'V8B'
    }

    # Referenz-Mods (fuer Build-WindroseMod.ps1 -Action Init -FromPak).
    References = @{
        StackModX4 = 'C:\Windrose\Mods\Stack_Size_Changes_x04_P.pak'
    }
}
