# Brief de développement : budget tenable avec synchronisation bancaire

Document destiné à un agent de développement (Claude Code). Propriétaire : Guillaume (développeur C#/.NET, administrateur Linux/Docker/Proxmox). Langue de l'interface et des libellés : français.

## 1. Objectif

Transformer un prototype de tableau de bord de budget (fichier `budget-tenable-reference.html`, joint) en application auto-hébergée qui :
1. récupère automatiquement les transactions bancaires de l'utilisateur (open banking, PSD2) ;
2. les catégorise selon des règles, avec une boîte de tri pour ce qui n'est pas reconnu ;
3. calcule le suivi de la période de budget (début le 28 de chaque mois) exactement comme le prototype ;
4. s'utilise depuis le téléphone (PWA installable). Pas d'application native dans la première version.

Utilisateur unique (un foyer, deux personnes au maximum). Pas de multi-tenant.

## 2. Ce que fait déjà le prototype (à reprendre tel quel)

Lire d'abord `budget-tenable-reference.html` : c'est la référence fonctionnelle et visuelle. La logique de calcul est dans la fonction `compute()` et le suivi dans `renderSuivi()`.

Principes du modèle :
- **Revenus** du budget : net de base, part du/de la partenaire, CAF. Les remboursements santé et les extras ne comptent pas comme revenus.
- **Charges fixes prélevées chaque mois** : liste de montants (voir seed en section 8).
- **Provisions obligatoires annuelles** (taxe foncière 1 432 €, assurance auto 396 €) : payées une fois par an, mais budgétées à 1/12 par mois et virées chaque mois vers un compte d'épargne (LDDS).
- **Enveloppe** = revenus − charges fixes mensuelles − provisions obligatoires mensuelles.
- Chaque poste de dépense reçoit un **pourcentage de l'enveloppe** : postes variables (courses, restauration, carburant, shopping, jeux, divers) et pots de provisions (voiture, bricolage, vacances). **Épargne = 100 % − somme des pourcentages.**
- **Pots** : l'argent non dépensé reste dans le pot. Solde d'un pot à la fin d'une période = solde de départ + (nombre de périodes depuis la première période de virement, incluse) × virement mensuel − dépenses imputées au pot depuis cette première période.
- **Période de budget** : commence le jour `D` (défaut 28), se termine la veille du même jour le mois suivant (ex. 28 oct. → 27 nov.). Une dépense appartient à la période qui contient sa date. La période est nommée d'après son mois de début.
- **Suivi** : dépensé, reste, reste par jour, projection de fin de période (rythme actuel), bilan (revenus − fixes − virement LDDS − variable dépensé), journal filtrable, historique sur six périodes, export CSV.

## 3. Architecture cible

```
Navigateur / PWA  ──HTTPS──>  Reverse proxy (Traefik ou Caddy)  ──>  API .NET 8 (minimal API)
                                                                      ├─ SQLite (EF Core)
                                                                      ├─ Service de synchronisation (BackgroundService)
                                                                      └─ Client Enable Banking
```

- Backend : C# / .NET 8, ASP.NET Core minimal API, EF Core + SQLite, `BackgroundService` pour la synchro. Docker + docker-compose.
- Frontend : reprendre le HTML/CSS/JS du prototype (vanilla JS), en remplaçant le stockage `localStorage` / base d'artefact par des appels `fetch` vers l'API. Ajouter un `manifest.webmanifest` et un service worker minimal (PWA).
- Authentification : application personnelle, donc une seule porte d'entrée. Au choix de l'utilisateur : authentification du reverse proxy (Authelia, Authentik, basic auth) ou code PIN + cookie de session signé. Aucun endpoint public hors le callback bancaire.
- Hébergement : chez l'utilisateur (Proxmox), nom de domaine en HTTPS obligatoire (exigé par l'agrégateur pour l'URL de retour).

## 4. Accès bancaire : Enable Banking, mode « Restricted Production »

Choix retenu : [Enable Banking](https://enablebanking.com), mode gratuit « restricted production », qui donne accès aux **propres comptes** de l'utilisateur avec de vraies données.

Contraintes connues (sources : documentation Firefly III et Actual Budget sur Enable Banking, OpenBankingTracker) :
- créer une application en environnement **Production** avec une URL de retour HTTPS sur le domaine de l'utilisateur ;
- **lier les comptes dans le portail Enable Banking** avant tout ; sans cela, aucun compte n'est renvoyé ;
- en mode restreint, les URL de politique de confidentialité et de conditions ne sont pas validées (une URL quelconque convient) ;
- le consentement d'accès aux comptes est limité dans le temps (180 jours depuis l'amendement des RTS PSD2). Prévoir une **procédure de reconnexion** et une alerte avant l'expiration.

Flux technique (API Enable Banking). **À vérifier dans la documentation officielle avant d'implémenter** : https://enablebanking.com/docs/api/reference/ . Ces noms viennent de ma connaissance générale de l'API et n'ont pas été revérifiés dans cette session.
1. Authentification de l'application : JWT signé RS256 avec la clé privée de l'application, `kid` = identifiant de l'application, passé en `Authorization: Bearer`.
2. Lister les banques d'un pays : `GET /aspsps?country=FR`.
3. Démarrer l'autorisation : `POST /auth` (banque, durée de validité, `redirect_url`, `state`). L'utilisateur s'authentifie sur le site de sa banque.
4. Callback : l'API reçoit `code` + `state` ; échanger via `POST /sessions` pour obtenir la session et les comptes (`uid` des comptes).
5. Transactions : `GET /accounts/{uid}/transactions` avec `date_from`, pagination par `continuation_key`.
6. Soldes : `GET /accounts/{uid}/balances`.

**Vérification du 12 novembre 2026** (référence de l'API et FAQ officielles), qui corrige ce brief sur quatre points :
- La synchro planifiée passe de 4 à **6 heures** : beaucoup de banques limitent à quatre lectures par jour les appels faits sans l'utilisateur (sans en-têtes `Psu-*`), et la FAQ recommande d'attendre 6 heures après un `ASPSP_RATE_LIMIT_EXCEEDED`. La synchro manuelle envoie les en-têtes `Psu-*` (tous ceux qu'exige la banque, ou aucun).
- L'identifiant stable d'une opération est `entry_reference`, pas `transaction_id` (qui peut changer d'une lecture à l'autre). Il manque souvent pour les opérations en attente.
- La durée du consentement n'est pas toujours 180 jours : elle se lit par banque dans `maximum_consent_validity` (`GET /aspsps`). Une session peut expirer avant terme (`EXPIRED_SESSION`).
- Pagination : continuer tant qu'une `continuation_key` est renvoyée, même après une page vide, sans changer les autres paramètres. Stratégie `longest` pour la première lecture, `default` ensuite.

Le reste du flux ci-dessus est confirmé : JWT RS256 (`kid` = identifiant de l'application, `iss` = enablebanking.com, `aud` = api.enablebanking.com, 24 h au plus), `GET /aspsps`, `POST /auth`, `POST /sessions`, `GET /accounts/{uid}/transactions`. L'`uid` d'un compte ne vaut que pour sa session ; `identification_hash` le retrouve d'une session à l'autre.

Si la banque de l'utilisateur n'est pas couverte, la solution de repli est l'**import CSV/OFX** (section 7, phase 1), qui reste de toute façon utile.

## 5. Modèle de données (SQLite)

- `Settings` : jour de début de période (défaut 28), revenus (net, partenaire, CAF), primes annuelles, première période de virement, soldes de départ des pots, indicateurs de virement LDDS par période, pourcentages par poste, montants des charges fixes.
- `Category` : id, libellé, groupe (`variable` | `pot` | `fixe` | `ignore`), pourcentage, ordre.
- `Account` : id interne, id fournisseur, libellé, type (`perso` | `commun`), devise. **Ne jamais journaliser ni exposer l'IBAN en clair** (le stocker chiffré ou tronqué).
- `Transaction` : id, accountId, `providerTransactionId`, `bookingDate`, `valueDate`, `amount` (décimal, négatif = débit), `currency`, `status` (`booked` | `pending`), `counterparty`, `remittanceInfo`, `categoryId` (nullable), `categorySource` (`rule` | `manual` | `none`), `isInternalTransfer` (bool), `note`, `createdAt`.
- `Rule` : motif (contient / regex) sur contrepartie et libellé, catégorie cible, priorité, sens (débit/crédit), plage de montants optionnelle.
- `BankSession` : sessionId, banque, `validUntil`, état, dernier succès de synchro. Les secrets (clé privée, jetons) restent hors base : variables d'environnement ou Docker secrets.
- `Expense` manuelle : une `Transaction` avec `providerTransactionId` nul (pour les dépenses en espèces ou à saisir à la main, comme dans le prototype).

## 6. Règles métier à respecter

- **Doublons** : clé d'unicité sur (`accountId`, `providerTransactionId`). Si la banque ne fournit pas d'identifiant stable, utiliser une empreinte (date, montant, contrepartie, libellé). Une transaction `pending` est remplacée quand la version `booked` arrive.
- **Virements internes** : ignorer pour le budget les mouvements entre le compte perso, le compte commun et le LDDS (`isInternalTransfer`). Règles dédiées, vérifiables dans l'interface.
- **Revenus** : le versement de la partenaire sur le compte commun et la CAF sont des revenus du budget, pas des « dépenses négatives ».
- **Remboursements** : un crédit rattaché à une catégorie réduit la dépense de cette catégorie (comme la case « Remboursement » du prototype). Les remboursements de santé n'entrent pas dans les revenus.
- **Charges fixes** : détecter par règles les prélèvements récurrents (crédit, télécom, assurances, abonnements) et les marquer `fixe` pour qu'ils ne comptent pas dans l'enveloppe variable. Afficher les écarts entre montant attendu et montant réel.
- **Pots** : une dépense imputée à un pot est payée depuis le LDDS dans la pratique ; elle réduit le solde du pot, pas l'enveloppe variable.
- **Catégorisation** : appliquer les règles par priorité décroissante. Le reste va dans une boîte « À trier » ; chaque tri manuel propose de créer une règle (« toujours classer X en Y »).
- **Fuseau et dates** : Europe/Paris. Calculs de période en dates locales, jamais en UTC.
- **Argent** : `decimal` en C#, jamais de flottants pour les montants.

## 7. Plan de réalisation

**Phase 1 : socle sans banque.** API + SQLite, reprise du frontend du prototype branché sur l'API, saisie manuelle, import CSV/OFX, règles de catégorisation, période paramétrable, PWA, Docker, authentification. Critère : toutes les fonctions du prototype marchent, les données survivent à un redémarrage, les calculs donnent les mêmes résultats que `compute()` sur le jeu de données de la section 8.

**Phase 2 : synchronisation bancaire.** Client Enable Banking, écran de connexion de la banque, callback, synchronisation planifiée (toutes les 4 heures) et bouton « Synchroniser maintenant », gestion des erreurs et de l'expiration du consentement (bandeau + alerte). Critère : après connexion, les transactions des 90 derniers jours apparaissent sans doublon, y compris après plusieurs synchronisations.

**Phase 3 : confort.** Dépenses récurrentes pré-remplies, alertes de dépassement (80 % et 100 % d'une enveloppe) par notification web push, sauvegarde automatique de la base, export CSV, comparaison attendu/réel des charges fixes.

**Hors périmètre pour l'instant** : application native, multi-utilisateur, investissements, objectifs d'épargne chiffrés.

## 8. Jeu de données de départ (seed)

Montants mensuels en euros, valeurs actuelles du prototype.

Revenus : net de base 2 050 ; part partenaire 750 ; CAF 198 (total 2 998). Primes et intéressement : 3 000 par an, versés en épargne.

Charges fixes prélevées chaque mois (total 1 396) : crédit immobilier 959 ; crédit Arkea 70 (case « soldé » possible) ; télécom 92 ; eau et électricité 62 ; assurances (habitation, juridique, Homeserve, prévoyance) 75 ; abonnements Karapass et Roole-Identicar 23 ; frais bancaires 22 ; Anthropic 23 ; Ring 10 ; PayPal récurrents 60.

Provisions obligatoires : taxe foncière 1 432 par an (payée en septembre) ; assurance auto 396 par an (payée en octobre). Soit 152 par mois mis de côté.

Enveloppe = 2 998 − 1 396 − 152 = 1 450. Pourcentages de l'enveloppe :

| Poste | Groupe | % | Montant mensuel indicatif |
|---|---|---|---|
| Courses et maison | variable | 47 | 681 |
| Restauration et sorties | variable | 7 | 101 |
| Carburant et péages | variable | 5 | 73 |
| Shopping | variable | 10 | 145 |
| Jeux et loisirs | variable | 4 | 58 |
| Divers | variable | 3 | 43 |
| Pot voiture | pot | 6 | 87 |
| Pot bricolage et déco | pot | 3 | 43 |
| Pot vacances et sorties famille | pot | 3 | 43 |
| Épargne courante | reste | 12 | 174 |

Dépenses actuelles moyennes (« aujourd'hui », pour comparer) : courses 810 ; restauration 160 ; carburant 70 ; shopping 272 ; jeux 102 ; divers 58 ; voiture 125 ; bricolage 116 ; vacances 40.

Virement automatique mensuel vers le LDDS : 326 € (152 de provisions obligatoires + 174 de pots).

Paramètres : début de période le 28 ; première période de virement : période commençant en novembre 2026.

## 9. Sécurité et conformité

- Les identifiants bancaires de l'utilisateur ne transitent jamais par l'application : seule la page de la banque les voit.
- Clé privée de l'application Enable Banking et secrets : variables d'environnement ou Docker secrets, jamais dans le dépôt.
- Journaux : pas de numéros de compte, pas de montants détaillés au niveau INFO.
- Sauvegarde chiffrée de la base SQLite (quotidienne, rétention 30 jours).
- HTTPS partout, cookies `Secure` + `HttpOnly` + `SameSite=Lax`, en-têtes de sécurité (CSP stricte), limitation du débit sur les endpoints d'authentification.
- Les données bancaires sont des données personnelles sensibles : accès réservé à l'utilisateur, aucune donnée envoyée à un service tiers autre que l'agrégateur.

## 10. Informations à demander à l'utilisateur avant de commencer

1. Nom exact de la banque du compte perso et du compte commun (vérifier la couverture avec `GET /aspsps?country=FR`).
2. Nom de domaine et reverse proxy déjà en place chez lui.
3. Choix de l'authentification (SSO existant ou code PIN).
4. Souhaite-t-il aussi synchroniser le compte commun ? La partenaire doit alors autoriser la connexion de son côté.
5. Fréquence de synchronisation souhaitée (4 heures par défaut) et besoin de notifications.

## 11. Définition de « terminé »

- Les calculs de période, d'enveloppe, de pots et de bilan donnent les mêmes résultats que le prototype sur le seed de la section 8 (tests unitaires à l'appui, notamment les périodes qui chevauchent deux mois, les années bissextiles et le changement d'heure).
- Aucune transaction en double après dix synchronisations consécutives.
- L'application reste utilisable hors connexion bancaire (saisie manuelle et import CSV).
- Un `README` explique le déploiement Docker, la création de l'application Enable Banking, la reconnexion du consentement et la restauration d'une sauvegarde.
