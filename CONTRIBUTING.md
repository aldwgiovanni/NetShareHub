# Contribuer à NetShare Hub

Merci de votre intérêt ! Les corrections, tests sur du matériel varié et améliorations de la documentation sont les bienvenus.

## Démarrer

```bash
git clone https://github.com/aldwgiovanni/NetShareHub.git
cd NetShareHub
dotnet build NetShareHub.slnx
dotnet test Src/NetShareHub/Tests/NetShareHub.Tests.csproj
```

Prérequis : SDK .NET 10. Aucun test ne modifie votre réseau et aucun n'exige les droits administrateur.

## Où se trouve quoi

| Dossier | Contenu |
|---|---|
| `Src/NetShareHub/Domain` | Entités, DTOs, énumérations, exceptions |
| `Src/NetShareHub/Application` | Contrats et logique pure (planificateur de partage, répartiteur de liaisons) |
| `Src/NetShareHub/Infrastructure` | Moteurs de partage Windows/Linux, DHCP, proxy Dispatch, inventaire réseau |
| `Src/NetShareHub/Desktop` | Interface Avalonia et composition |
| `Src/NetShareHub/Tests` | Tests unitaires, d'intégration et d'interface |
| `packaging/` | Scripts d'installeurs (Windows) et de paquets (Linux) |

Le fichier [CLAUDE.md](CLAUDE.md) décrit l'architecture, les conventions et les pièges déjà rencontrés (encodage, PowerShell,
chemins longs…) : à lire avant de toucher aux moteurs de partage.

## Règles

- **Chaque modification du système est précédée de l'enregistrement de son annulation** dans l'`UndoJournal`. Un moteur ne doit
  jamais pouvoir laisser une machine dans un état réseau modifié après un échec ou un plantage.
- Les commandes système passent par `ICommandRunner` (arguments séparés, jamais de chaîne passée à un shell), pour rester testables.
- Ajoutez un test avec toute correction ou fonctionnalité. Les moteurs se testent en vérifiant les commandes émises.
- Les chaînes affichées sont en français ; le code, les identifiants et les messages de commit peuvent être en français ou en anglais.
- Encodage : `.ps1` et `.iss` en UTF-8 **avec BOM** ; scripts `.sh` en UTF-8 sans BOM, fins de ligne LF (voir `.gitattributes`).

## Proposer une modification

1. Créez une branche, faites vos changements, lancez `dotnet test`.
2. Ouvrez une *pull request* en décrivant le problème résolu et ce que vous avez testé (système, matériel).
3. La CI (Windows + Linux) doit être au vert.

## Publier une version (mainteneur)

```bash
git tag v1.2.0 && git push origin v1.2.0
```

Le workflow `Release` compile, teste, empaquette (installeur Windows, zip portable, `.deb` et `.tar.gz` Linux x64/arm64),
calcule les empreintes SHA-256 et crée la release GitHub.
