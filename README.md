# Budget tenable

Application de budget du foyer, auto-hébergée : suivi de la période de budget (début le 28 de chaque mois), enveloppes, pots de provisions, boîte « À trier », règles de classement, import de relevés CSV/OFX. Interface en français, installable sur téléphone (PWA).

La spec complète est dans [`docs/SPEC-budget-sync.md`](docs/SPEC-budget-sync.md) ; le prototype d'origine, qui fait foi pour les calculs, dans [`docs/reference/`](docs/reference/budget-tenable-reference.html).

## État d'avancement

| Phase | Contenu | État |
|---|---|---|
| 1. Socle sans banque | API + SQLite, frontend du prototype branché sur l'API, saisie manuelle, import CSV/OFX, règles, période paramétrable, PWA, Docker, authentification PIN | Faite (voir « Limites ») |
| 2. Synchronisation bancaire | Client Enable Banking, callback, synchro planifiée, reconnexion du consentement | À faire |
| 3. Confort | Récurrents, alertes 80 %/100 %, sauvegarde chiffrée automatique, charges fixes attendu/réel | À faire |

## Démarrage rapide (développement)

Prérequis : SDK .NET 8.

```bash
export BUDGET_PIN=123456 BUDGET_INSECURE_COOKIES=true
dotnet run --project src/BudgetTenable.Api --urls http://localhost:5080
```

Puis ouvre http://localhost:5080. Les données vont dans `./data/budget.db` (ignoré par git). `BUDGET_INSECURE_COOKIES=true` ne sert qu'en HTTP local : en production le cookie est toujours `Secure`.

Tests : `dotnet test`. Les valeurs attendues viennent du prototype lui-même (`node tests/reference/prototype-oracle.mjs`).

## Déploiement Docker

```bash
mkdir -p secrets data
printf '%s' 'TON-PIN' > secrets/pin.txt && chmod 600 secrets/pin.txt
docker compose up -d --build
```

L'application écoute sur `127.0.0.1:8080`. Place devant elle un reverse proxy en HTTPS ; exemple Caddy :

```
budget.example.fr {
    reverse_proxy 127.0.0.1:8080
}
```

- **PIN** : au moins 4 caractères (6 chiffres ou plus recommandés). Il se donne par `BUDGET_PIN_FILE` (Docker secret, recommandé) ou `BUDGET_PIN`. Sans PIN, l'application refuse de démarrer.
- **Authentification par le proxy** (Authelia, Authentik, basic auth) : mets `BUDGET_AUTH_MODE=none`. Dans ce cas rien d'autre ne protège l'application : ne l'expose jamais directement.
- **`BUDGET_TRUST_PROXY=true`** fait confiance à `X-Forwarded-For` pour la limitation de débit de la connexion (5 essais par minute et par adresse). À n'activer que si le proxy est la seule porte d'entrée, sinon l'en-tête est falsifiable. Sans cette option, derrière un proxy, tous les essais partagent l'adresse du proxy : la limite est alors globale, ce qui protège aussi.
- Les sessions durent 30 jours (`Auth__SessionDays`). La clé de signature est créée dans `data/cookie.key` au premier lancement, ou fournie par `BUDGET_COOKIE_KEY` (base64, 32 octets minimum). Changer la clé déconnecte tout le monde.

### Variables d'environnement

| Variable | Rôle | Défaut |
|---|---|---|
| `BUDGET_PIN` / `BUDGET_PIN_FILE` | PIN, ou fichier qui le contient | requis |
| `BUDGET_AUTH_MODE` | `pin` ou `none` | `pin` |
| `BUDGET_COOKIE_KEY` | clé de signature des cookies (base64) | générée dans `data/` |
| `BUDGET_DATA_DIR` | dossier de la base | `./data` (`/data` en Docker) |
| `BUDGET_TRUST_PROXY` | confiance aux en-têtes `X-Forwarded-*` | `false` |
| `BUDGET_INSECURE_COOKIES` | cookies sans `Secure` (dev local seulement) | `false` |

## Utilisation

- **Suivi du mois** : dépensé, reste, reste par jour, projection, enveloppes, rythme, journal filtrable, pots, bilan, six dernières périodes, export CSV.
- **Budget** : revenus, charges fixes, provisions, pourcentages par poste. Tout se recalcule en direct et s'enregistre sur le serveur.
- **Coller des opérations** (dans « Suivi du mois ») : copie la liste des opérations sur le site de ta banque et colle-la. Libellé, date, montant, statut (« En cours » = en attente) et catégorie bancaire sont reconnus ; l'année se déduit du jour de la semaine. Un aperçu te laisse tout corriger avant de valider. Pour chaque catégorie bancaire inconnue, tu choisis une catégorie existante, d'en créer une (elle démarre à 0 % de l'enveloppe, à régler dans l'onglet Budget) ou de ne pas l'utiliser. La correspondance est retenue pour les collages suivants (liste et oubli dans « Règles et import »). Ordre de priorité : tes règles, puis la catégorie déjà posée sur l'opération rapprochée, puis la correspondance bancaire.
- **À trier** : opérations non reconnues. Chaque tri propose de créer une règle (« toujours classer X en Y »).
- **Règles et import** : import CSV/OFX, gestion des règles. Les opérations déjà importées sont ignorées : on peut réimporter un relevé qui chevauche le précédent.

### Formats d'import

CSV (séparateur `;`, `,` ou tabulation, UTF-8 ou Windows-1252) avec une colonne date, un libellé et soit un montant signé, soit deux colonnes débit/crédit ; un préambule avant l'en-tête est toléré. OFX/QFX en SGML ou XML. Si les colonnes d'une banque ne sont pas reconnues, l'erreur liste les en-têtes lus : envoie-les pour que le format soit ajouté.

### Règles métier appliquées

- Doublons : clé unique (compte, identifiant fournisseur). Sans identifiant (CSV), empreinte date + montant + contrepartie + libellé, avec un rang pour deux opérations identiques le même jour. Pour un collage, l'empreinte inclut l'heure.
- Rapprochement (collage, CSV, OFX) : même compte et même montant exact. Une saisie manuelle se rapproche à ±5 jours ; une opération « en attente » à ±5 jours si l'enseigne ressemble ; une opération déjà débitée seulement le même jour et pour la même enseigne. Le statut passe alors à « débitée » et la saisie garde sa note et sa catégorie. Dans l'aperçu d'un collage, tu peux refuser un rapprochement ; à l'import CSV/OFX, sans aperçu, seuls les cas sans ambiguïté sont fusionnés.
- Virements internes : exclus du budget et de « À trier ». Un crédit rattaché à une catégorie y réduit la dépense (remboursement).
- Une dépense imputée à un pot réduit le solde du pot, pas l'enveloppe variable.
- Un classement manuel n'est jamais écrasé par une règle.
- Dates de période en Europe/Paris, montants en `decimal`.

## Sauvegarde et restauration

La base est le fichier `data/budget.db` (avec `-wal`/`-shm` quand l'application tourne). Sauvegarde cohérente, application en marche :

```bash
sqlite3 data/budget.db ".backup 'sauvegarde-$(date +%F).db'"
```

Restauration : arrête le conteneur, remplace `data/budget.db` par la sauvegarde (supprime les éventuels `budget.db-wal` et `budget.db-shm`), relance. La sauvegarde automatique chiffrée avec rétention de 30 jours est prévue en phase 3.

## Limites connues de la phase 1

- Le rapprochement repose sur le montant exact et des dates proches : deux achats identiques à quelques jours d'écart chez la même enseigne peuvent être confondus avec une saisie manuelle. L'aperçu du collage permet de le voir et de le refuser.
- Le fichier `Dockerfile` et le `docker-compose.yml` n'ont pas été construits dans l'environnement de développement (pas de démon Docker) ; ils suivent les images officielles .NET 8.
- Deux imports de fichiers qui coupent une journée en deux peuvent, dans de rares cas d'opérations strictement identiques le même jour, se dédoubler ou se confondre. Les relevés par jour complet ne posent pas de problème.
- Les calculs de l'onglet Budget existent en JavaScript (simulation instantanée) et en C# (référence testée) : à modifier ensemble.
- Polices : celles du système (pas de Google Fonts), pour qu'aucune requête ne parte vers un tiers.
