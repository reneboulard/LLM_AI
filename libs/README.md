# libs/ — Emby reference assemblies

Ce dossier contient les **assemblies de référence Emby** utilisées à la compilation :
`MediaBrowser.Model.dll`, `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`.

This folder contains the **Emby reference assemblies** used at compile time:
`MediaBrowser.Model.dll`, `MediaBrowser.Common.dll`, `MediaBrowser.Controller.dll`.

## Pourquoi elles sont là / Why they're here

Emby ne publie pas de paquets NuGet publics. Pour que le build soit **reproductible
partout** (notamment sur le runner GitHub Actions, où Emby n'est pas installé), ces DLL
sont vendorisées ici et référencées via des `HintPath` relatifs dans `LLM_AI.csproj`.

Elles sont marquées `<Private>false</Private>` : elles **ne sont pas copiées** dans la
sortie du build ni dans l'archive de release. Le plugin chargé dans Emby utilise les
assemblies fournies par l'hôte Emby au runtime.

Emby does not publish public NuGet packages. To make the build **reproducible anywhere**
(including the GitHub Actions runner, where Emby is not installed), these DLLs are
vendored here and referenced via relative `HintPath` in `LLM_AI.csproj`.

They are marked `<Private>false</Private>`: they are **not copied** into the build output
or the release archive. The plugin, once loaded in Emby, uses the assemblies provided by
the Emby host at runtime.

## Licence / License

Ces DLL sont la propriété d'Emby (code fermé). Elles sont incluses ici **uniquement**
comme assemblies de référence pour la compilation. Si tu redistribues ce dépôt, vérifie
les conditions d'utilisation d'Emby applicables.

These DLLs are property of Emby (closed source). They are included here **only** as
reference assemblies for compilation. If you redistribute this repository, check the
applicable Emby terms of use.

## Mettre à jour / Update

Pour aligner sur une autre version d'Emby installée localement :

```bash
cp /opt/emby-server/system/MediaBrowser.Model.dll      libs/
cp /opt/emby-server/system/MediaBrowser.Common.dll     libs/
cp /opt/emby-server/system/MediaBrowser.Controller.dll libs/
```

(Adapte le chemin `/opt/emby-server/system` selon ton installation Emby.)