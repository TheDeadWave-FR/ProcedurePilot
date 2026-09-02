# Procedure Pilot

Application Windows portable inspirée du projet `Procedure` existant.

## Téléchargement

Le package Windows x64 non chiffré est disponible dans les Assets de la dernière Release : [ProcedurePilot-windows-x64-v1.0.0.zip](https://github.com/TheDeadWave-FR/ProcedurePilot/releases/download/v1.0.0/ProcedurePilot-windows-x64-v1.0.0.zip).

Il contient directement `ProcedurePilot.exe` et ce fichier `README.md`. Aucun mot de passe n'est nécessaire.

## Utilisation

Placez `ProcedurePilot.exe` à la racine du dossier `Procedure`, à côté de :

- `Documents` : documents modifiables
- `PDFs` : PDF générés
- `Archive` : documents archivés
- `Data` : données internes de l’application

L’application permet de consulter la bibliothèque, rechercher une procédure, ajouter un document bureautique, numéroter les nouveaux fichiers, convertir les documents en PDF, actualiser l’index XML, archiver une procédure et créer des listes ordonnées à partir des procédures existantes.

Le bouton **Listes de procédures** permet de composer un parcours (par exemple « Configuration d’un PC »), de choisir uniquement des procédures présentes dans la bibliothèque et de définir leur ordre. Une procédure peut être ouverte directement depuis une liste. Les listes sont enregistrées dans `Data\Lists.xml`. Si le libellé d’une procédure numérotée change, son code `<PRÉFIXE>-00001` permet de conserver automatiquement son association avec la liste.

Après l’ajout d’une ou plusieurs procédures modifiables, la mise à jour complète démarre automatiquement : numérotation, conversion PDF, régénération de l’index XML et actualisation de la bibliothèque.

Tant que l’application est ouverte, elle surveille également `Documents`. Un fichier compatible ajouté ou modifié directement dans ce dossier est pris en compte automatiquement. Si un document source est supprimé manuellement, sa ligne disparaît de la bibliothèque, son PDF correspondant est supprimé et l’index XML est régénéré. Le dossier `Archive` est lui aussi surveillé afin que son index reflète les ajouts, suppressions et déplacements manuels. Les changements rapprochés sont regroupés afin d’attendre la fin d’une copie et d’éviter plusieurs mises à jour successives.

Le bouton **Paramètres** permet de choisir les dossiers des documents modifiables, des PDF, des archives et des données de l’application. Il permet aussi de sélectionner la langue de l’interface entre **Français** et **English**. Le changement est appliqué dès l’enregistrement et mémorisé séparément pour chaque utilisateur Windows dans `%LOCALAPPDATA%\ProcedurePilot\UserSettings.xml` : un utilisateur peut donc travailler en anglais sans modifier la langue des autres postes.

Cette fenêtre permet également de remplacer le préfixe `FR` par un préfixe personnalisé partagé, par exemple `PROC`, avec un aperçu `PROC-00001`. Le préfixe doit commencer par une lettre et contenir de 1 à 16 lettres non accentuées, chiffres ou tirets internes. Il est enregistré dans `Settings.xml` et automatiquement rechargé par les autres postes. Il s’applique aux nouveaux documents lors de la **Mise à jour des tags** ; les documents déjà numérotés conservent leur ancien code. Toute forme valide `<PRÉFIXE>-00001` placée au début d’un nom est donc considérée comme un tag existant.

Les chemins partagés et le préfixe restent enregistrés dans `Settings.xml`. Le dossier principal de l’application n’est pas modifiable depuis cette fenêtre. Par défaut, le dossier des données est `Data`.

Le bouton **TOUT METTRE À JOUR** lance la synchronisation complète. Sa flèche ouvre les actions secondaires **Mise à jour des tags**, **Convertir en PDF** et **Actualiser les index XML**.

Tous les événements du journal sont également conservés dans `Logs.txt`, dans le dossier des données de l’application (`Data\Logs.txt` par défaut). Chaque ligne contient la date complète, sa provenance (`SYSTEM` ou `USER`) et le message. Lorsqu’une opération est déclenchée depuis l’interface, les lignes correspondantes indiquent le nom de la session Windows sous la forme `DOMAINE\Utilisateur`. Les changements détectés automatiquement par la surveillance des dossiers restent marqués `SYSTEM`, car Windows ne communique pas à l’application l’identité de la personne ayant modifié directement un fichier.

- `Index.xml` recense les documents modifiables et leurs PDF, avec pour chaque fichier sa date d’ajout et sa date de dernière modification ;
- `Settings.xml` conserve les chemins et le préfixe de tag partagés ;
- `Archive.xml` recense récursivement tous les documents modifiables présents dans `Archive` ;
- `Lists.xml` conserve les listes ordonnées et les références aux procédures existantes.

## Utilisation multi-utilisateur sur un NAS

`ProcedurePilot.exe` et les quatre dossiers de travail peuvent être placés sur un partage NAS. Dans **Paramètres**, utilisez de préférence des chemins UNC identiques sur tous les postes, par exemple `\\NAS\Procedure\Documents`, plutôt que des lettres de lecteur qui peuvent différer d’un PC à l’autre. Chaque utilisateur doit disposer des droits de création, modification, renommage et suppression dans les dossiers partagés.

Les écritures sensibles sont sérialisées par un verrou partagé dans le dossier des données : numérotation, conversion, index XML, archivage, paramètres et listes ne peuvent pas être modifiés simultanément par deux postes. Une opération manuelle attend que le poste précédent ait terminé ; une synchronisation automatique occupée est reportée. Le verrou SMB est libéré automatiquement si l’application ou un PC s’arrête.

Les autres instances sont prévenues par un signal partagé et surveillent également les dossiers. Une vérification périodique toutes les deux secondes complète les notifications Windows, qui peuvent être perdues sur certains NAS. La bibliothèque, les index, les paramètres et les listes sont ainsi actualisés en quasi temps réel, généralement en une à deux secondes après la fin de l’écriture. Dans une fenêtre de listes déjà ouverte, la vérification a lieu toutes les 1,5 seconde. Si deux personnes tentent de modifier la même liste, la seconde reçoit un avertissement de conflit et la version la plus récente est rechargée au lieu d’être écrasée.

Les fichiers `.procedurepilot-write.lock`, `.procedurepilot-change` et `.procedurepilot-log.lock` présents dans le dossier des données sont internes au fonctionnement multi-utilisateur. Il ne faut ni les modifier ni les supprimer pendant l’utilisation. `Logs.txt` est lui aussi protégé contre les écritures simultanées.

Le NAS doit prendre en charge les verrous de fichiers SMB standards. Pour remplacer l’exécutable par une nouvelle version, fermez d’abord Procedure Pilot sur tous les postes.

Tous les postes utilisant le même espace partagé doivent exécuter la même version de Procedure Pilot, notamment pour conserver le préfixe personnalisé lors de l’enregistrement des paramètres.

Les dates sont enregistrées au format ISO 8601 UTC. Les anciennes installations utilisant `Procedures_Modifiables`, `Procedures_PDF`, `_ARCHIVE`, `Settings`, `Index_Procedures.xml` et `ProcedurePilot.settings.xml` sont migrées automatiquement lorsque ces chemins correspondent aux anciens emplacements par défaut. Les chemins personnalisés restent inchangés.

La mise à jour complète synchronise `PDFs` avec `Documents`. Si un document source a été supprimé, le PDF correspondant est également supprimé. Si la date de modification du document source est plus récente que celle du PDF, le PDF existant est remplacé par une nouvelle version. Les PDF déjà à jour ne sont pas reconvertis.

L’index XML et la bibliothèque reflètent le même état. Un PDF plus ancien que son document source apparaît comme `pdf-a-actualiser` dans l’index et « PDF à actualiser » dans l’application jusqu’à sa reconversion.

Les formats reconnus couvrent Microsoft Word (`.doc`, `.docx`, `.docm` et modèles), LibreOffice/OpenOffice (`.odt`, `.ott`, `.fodt`, `.sxw`, `.stw`) ainsi que `.rtf`. Les documents créés avec ONLYOFFICE sont pris en charge lorsqu’ils utilisent l’un de ces formats standards.

La conversion des fichiers DOCX utilise en priorité le moteur PDF local intégré : Procedure Pilot lit directement le document et demande au moteur de rendu Windows (Microsoft Edge, Google Chrome ou Chromium) de créer le PDF hors ligne. Word et ONLYOFFICE ne sont donc pas nécessaires. Les moteurs bureautiques Word, LibreOffice et Apache OpenOffice restent disponibles en secours, notamment pour les anciens formats tels que DOC, ODT ou RTF. L’index XML est généré directement par l’application et ne nécessite pas Excel. Aucun service Internet ni installateur n’est requis.

## Compilation

Exécutez `build.ps1`. L’exécutable est produit dans `dist\ProcedurePilot.exe`.
