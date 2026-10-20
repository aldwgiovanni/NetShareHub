# CLAUDE.md — NetShare Hub

Application de bureau .NET 10 / Avalonia 12 (Windows + Linux) : partage de connexion (hotspot, routeur) et agrégation de
liaisons (proxy « Dispatch »). Voir `README.md` pour le produit.

## Commandes

```powershell
dotnet build NetShareHub.slnx
dotnet test Src/NetShareHub/Tests/NetShareHub.Tests.csproj        # 221 tests
./packaging/build.ps1 [-Targets win-x64,linux-x64,linux-arm64]    # → dist/
```

- **Compiler/tester en `-c Release` si l'application tourne** : un `NetShareHub.exe` en cours d'exécution (surtout élevé,
  impossible à tuer depuis un shell normal) verrouille `bin/Debug`.
- Les tests d'interface (Avalonia.Headless) écrivent des captures dans `$NSH_SCREENSHOTS` (défaut : `bin/…/screenshots`).
- Aucun test ne modifie le réseau ni n'exige d'être administrateur ; les tests `RealSystemTests` n'exécutent que des
  scripts de **lecture**. Les chemins de modification réelle (ICS, Point d'accès mobile, WinNAT, NetworkManager, iptables)
  sont couverts par des tests sur les commandes émises, pas par des essais sur une vraie machine.

## Architecture (couches de `codex.md`, adaptées à une application de bureau)

`Domain` → `Application` → `Infrastructure` → `Desktop` (hôte + composition). `Tests` référence tout.
`Application` ne référence jamais `Infrastructure` ni `Desktop`. La composition (DI) vit uniquement dans
`Desktop/Extensions/ServiceCollectionExtensions.cs` (`AddLoggingConfig`, `AddMyInfrastructureGroup`, `AddMyApplicationGroup`,
`AddMyViewModelGroup`).

## Écarts avec `codex.md` (conventions personnelles du mainteneur, non publiées)

`codex.md` décrit des API web avec base de données. Ce projet n'a ni base, ni HTTP, ni authentification utilisateur.
Valeurs par défaut retenues :

| `codex.md` | Ici | Pourquoi |
|---|---|---|
| `Api` (controllers, `ProtectedBaseController`) | `Desktop` (Avalonia) | Pas d'hôte web. |
| EF Core, `BaseEntity`, soft delete, migrations | Aucun | Pas de modèle relationnel ; préférences en JSON. |
| Repository + Unit of Work | `IAppSettingsRepository` / `JsonAppSettingsRepository`, **sans UoW** | Pas de transaction : une écriture atomique (fichier temporaire + remplacement) suffit. L'équivalent « transactionnel » est l'`UndoJournal` des moteurs de partage. |
| AutoMapper (`MappingProfile`) | Méthodes d'extension manuelles (`Application/Mapping`) | Mappings triviaux ; AutoMapper 15 est sous licence commerciale. |
| DTOs `_InDto` / `_OutDto` / `_OutDto_Bff` | `_InDto` / `_OutDto` respectés, `_OutDto_Bff` non utilisé | Pas de front distinct. |
| `[Authorize]` | Droits système (administrateur / root) via `IPrivilegeService` | C'est la seule « autorisation » du produit. |
| Dossiers `Domain/Dto/Incoming` | `Domain/Dtos/Incoming` et `Outgoing` (pluriel) | Choix initial du projet ; ne pas renommer. |

Conventions de `codex.md` conservées : couches, suffixes de DTO, DI par extensions `Add*`, `async` partout, nullable activé,
code livré complet et compilé, aucune logique métier dans la couche de présentation.

## Pièges rencontrés (à ne pas redécouvrir)

- **Encodage** : Windows PowerShell 5.1 lit par défaut en ANSI. Réécrire un fichier source avec `Get-Content`/`Set-Content`
  sans `-Encoding UTF8` corrompt les accents (« Ã© »). Préférer les outils d'édition, ou `[IO.File]::ReadAllText/WriteAllText`.
  Les `.ps1` et `.iss` du dépôt sont en UTF-8 **avec BOM** (nécessaire à PowerShell 5.1 et Inno Setup) ; les scripts `.sh` en
  UTF-8 **sans BOM**, fins de ligne **LF**.
- **`InvariantGlobalization`** est activé (pas de dépendance à ICU sous Linux) : `CultureInfo("fr-FR")` ne donne alors pas la
  virgule décimale. Les formats français passent par `Formatting` (`NumberFormatInfo` explicite).
- **PowerShell** : les erreurs d'un script sortent en CLIXML sur stderr. `CommandSpec.PowerShell` ajoute un `trap` qui écrit le
  message seul. Un script qui finit par une commande dont l'erreur est masquée (`-ErrorAction SilentlyContinue`) sort avec le
  code 1 : terminer les scripts d'annulation par `'OK'`.
- **Espace de noms `Application`** : `NetShareHub.Application` masque `Avalonia.Application` ; écrire `Avalonia.Application`.
- **Avalonia.Headless.XUnit 12** exige xunit v3 ; on utilise `Avalonia.Headless` + `UiSession` (xunit v2).
- **NuGet** : le dépôt a son `nuget.config` (nuget.org seul) car la configuration globale de la machine contient un flux privé
  Azure DevOps qui répond 401 et fait échouer toute restauration de paquet non mis en cache.
- **Chemins longs** : `dotnet`/MSBuild échouent si le répertoire courant dépasse MAX_PATH ; travailler via une jonction courte.

## Règles de sécurité (l'application tourne en administrateur / root)

- **Journal d'annulation** (`UndoJournal`) : rejoué en administrateur au lancement suivant. Il ne contient que des
  opérations typées (`UndoStep` : enum + paramètres revalidés par `ToCommand`), jamais un exécutable ou un script libre.
  Dossier protégé par `JournalSecurity` (Windows : SYSTEM + Administrateurs, sans héritage de `C:\ProgramData` ;
  Linux : root 0700) ; un journal dont le propriétaire n'est pas l'administration, ou qui est un lien, est refusé.
  Toute nouvelle annulation = un nouveau `UndoKind`, pas un `CommandSpec`.
- **PowerShell** : toute donnée insérée dans un script passe par `WindowsScripts.Quote`, qui double aussi les apostrophes
  typographiques U+2018–U+201B (PowerShell les traite comme `'`).
- **Profil utilisateur sous Linux en root** : `~/.config` appartient à l'utilisateur. Écrire uniquement via
  `AppPaths.WriteUserFileAsync` / `OpenUserFileForAppend` / `OpenUserFileForRead` (liens refusés, création exclusive,
  `fchown`/`lchown`, contrôle du propriétaire par `fstat`), jamais `File.WriteAllText`/`AppendAllText` directement.
- **Instance unique** : `SingleInstanceLock` (`Global\NetShareHub` / `flock /run/netsharehub.lock`) pris dans `Program.Main`.
- **Dispatch** : un client venu du réseau ne peut joindre ni la machine (boucle locale, ses adresses, 0.0.0.0/8, 169.254/16,
  224/3) ni un nom vide (`DispatchProxyService.IsHostDestination`).
