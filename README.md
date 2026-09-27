# Thoth.Connectors.Siecle

Connecteur **métier** d'import des **élèves** depuis le fichier `ExportXML_ElevesSansAdresses.zip`
d'un export **SIECLE-BEE**.

> **Un connecteur parse ; il ne nettoie pas.** Il lit le XML SIECLE, reconstruit des
> `ThothRawIdentity` aussi bruts que possible, et les émet en NDJSON sur la sortie standard. Tout le
> nettoyage (trim, vide→null, validation de format) est fait côté moteur.

## SIECLE-BEE en deux mots

SIECLE (Système d'Information pour les Élèves de Collèges et de Lycées et pour les Établissements)
est l'application de gestion des élèves de l'Éducation Nationale. Son module **BEE** (Base Élèves
Établissement) permet d'exporter, depuis l'école, un lot d'archives décrivant les élèves
inscrits et leur scolarité de l'année en cours.

Ce connecteur importe **uniquement les élèves** (rôle `Student`). Il ne touche ni aux enseignants ni
aux personnels (autres sources, autres connecteurs).

## Format d'entrée attendu

- L'opérateur dépose les fichiers ZIP de son export SIECLE-BEE. Un seul sert :
  `ExportXML_ElevesSansAdresses.zip` ; les autres sont reconnus et ignorés.
- **Les fichiers déposés se lisent ensemble** : le connecteur retrouve le bon ZIP par son contenu
  parmi tous les fichiers du lot (`SiecleParser.FindElevesZip`), pas par son nom. L'opérateur peut
  donc déposer l'export complet sans le trier, et le manifeste le déclare —
  `"SourcesAreComplementary": true` — pour que le moteur remette tous les fichiers en un seul appel,
  à l'analyse comme à l'import.
- Encodage source : **ISO-8859-15** (Latin-9).
- Le parsing est **résistant aux attaques XXE** (DTD interdites) et lit le XML en deux passes sans le
  charger en entier — l'index des classes et groupes de l'année, puis les élèves un par un — pour
  tenir les grosses écoles.

Un élève est recomposé en joignant, sur `ELEVE_ID`, le bloc `<ELEVES>` (identité civile) et le bloc
`<STRUCTURES>` (scolarité de l'année). **Seuls les inscrits courants sont émis** : un élève sans
division courante (sortant) est ignoré.

## Vocabulaire métier (pour un lecteur externe)

| Terme | Signification |
|-------|---------------|
| **RNE / UAJ** | Identifiant national de l'**établissement** (ex. `0123456A`). Utilisé comme `Scope` (l'école). |
| **INE** | Identifiant National Élève (`ID_NATIONAL`) — clé stable de réconciliation d'un élève entre imports. |
| **Division** (`TYPE_STRUCTURE="D"`) | La **classe** de l'élève (une seule attendue). Obligatoire pour émettre un `Student`. |
| **Groupe pédagogique** (`TYPE_STRUCTURE="G"`) | Un ou plusieurs groupes (options, langues, ateliers…) auxquels l'élève est rattaché. |

## Fichiers

| Fichier | Rôle |
|---------|------|
| `Program.cs` | Point d'entrée — délègue à `ConnectorRunner.RunAsync()`. |
| `ConnectorRunner.cs` | Boucle protocolaire : lit la requête sur stdin, route inspect/ingest, émet le NDJSON. |
| `SiecleParser.cs` | Parsing pur des XML SIECLE (ISO-8859-15, XXE-safe, RNE/INE/divisions/groupes). |
| `ConnectorJsonContext.cs` | Contexte JSON source-generated (sérialisation AOT-safe, sans réflexion). |
| `manifest.json` | Le manifeste lu par le scanner du moteur (libellés en français). |
| `Properties/PublishProfiles/FolderProfile.pubxml` | Profil de publication (sortie dans `publish/siecle/`). |

## Protocole (NDJSON sur stdin/stdout)

Le moteur envoie une unique ligne JSON de requête sur stdin
(`{ "Mode": "inspect" | "ingest", ... }`). Le connecteur répond en JSON délimité par des sauts de
ligne, chaque ligne portant un préfixe :

| Préfixe | Signification |
|---------|---------------|
| `idy:` | Un `ThothRawIdentity` (émis par élève pendant l'ingestion). |
| `log:` | Une ligne de log (`{ "Level", "Message" }`) remontée par le moteur. |
| `res:` | Le résultat terminal (`InspectionResult` ou `IngestionResult`). |

Le processus se termine avec le code `0` une fois la requête traitée, refus compris, et avec le
code `1` quand la requête elle-même ne peut pas l'être (requête vide ou illisible, mode inconnu,
crash) — après avoir écrit une ligne de résultat d'échec dans tous les cas.

Les records du protocole (`ConnectorRequest`, `ConnectorLogPayload`, `ConnectorLinePrefix`) sont fournis
par `Thoth.Integration.Abstractions` — la seule dépendance du connecteur. Aucune duplication, aucune
référence au moteur.

## Build / publication

La publication produit un binaire **natif AOT auto-contenu**, accompagné des fichiers que le moteur
attend à côté :

```
dotnet publish -c Release -p:PublishProfile=FolderProfile
```

La sortie arrive dans `publish/siecle/` à la racine du dépôt : `Thoth.Connectors.Siecle.exe`,
`manifest.json` et `config.example.json`. C'est ce dossier qui se dépose dans `connectors-ext/siecle/`
du moteur (voir « Installer ce connecteur »).

## Import autonome

L'import autonome se décrit dans un `config.json` posé à côté de l'exécutable. Pour ce connecteur, il
suffit d'y déclarer l'emplacement du fichier `ExportXML_ElevesSansAdresses.zip` : voir
`config.example.json`.

## Installer ce connecteur

Chaque version est publiée dans une [release](https://github.com/BaguetteByte-Innovations/thoth-connector-siecle/releases/latest) :
une archive zip du dossier du connecteur, à extraire dans `connectors-ext/siecle/`, avec son
`SHA256SUMS.txt` et une attestation de provenance (`gh attestation verify <zip> --repo BaguetteByte-Innovations/thoth-connector-siecle`).

Voir la **[documentation de Thoth](https://thoth-iam.com/docs/connectors/#installation)**.

## Références

- **[Documentation développeur](https://thoth-iam.com/docs/develop/)** — le fonctionnement d'un connecteur Thoth : l'échange avec le moteur, `manifest.json`, `config.json`, les identités qu'il émet.
- **[`Thoth.Integration.Abstractions`](https://www.nuget.org/packages/Thoth.Integration.Abstractions)** — le contrat partagé (package NuGet) que cet exécutable référence.
