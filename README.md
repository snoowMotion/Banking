# Budget tenable

Application de budget du foyer, auto-hébergée : suivi de la période de budget (début le 28 de chaque mois), enveloppes, pots de provisions, boîte « À trier », règles de classement, synchronisation bancaire (Enable Banking), import de relevés CSV/OFX. Interface en français, installable sur téléphone (PWA).

La spec complète est dans [`docs/SPEC-budget-sync.md`](docs/SPEC-budget-sync.md) ; le prototype d'origine, qui fait foi pour les calculs, dans [`docs/reference/`](docs/reference/budget-tenable-reference.html).

## État d'avancement

| Phase | Contenu | État |
|---|---|---|
| 1. Socle sans banque | API + SQLite, frontend du prototype branché sur l'API, saisie manuelle, import CSV/OFX, règles, période paramétrable, PWA, Docker, authentification PIN | Faite (voir « Limites ») |
| 2. Synchronisation bancaire | Client Enable Banking, retour de la banque, synchro planifiée et manuelle, reconnexion du consentement | Codée et testée contre une fausse API ; à valider avec ta banque (voir « Limites ») |
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
| `BUDGET_EB_APP_ID` | identifiant de l'application Enable Banking ; active la synchro bancaire | absent (synchro inactive) |
| `BUDGET_EB_KEY_FILE` / `BUDGET_EB_KEY` | clé privée RSA de l'application (fichier PEM recommandé, ou contenu PEM) | requis si `BUDGET_EB_APP_ID` |
| `BUDGET_EB_REDIRECT_URL` | URL de retour déclarée chez Enable Banking : `https://ton-domaine/api/bank/callback` | requis si `BUDGET_EB_APP_ID` |
| `BUDGET_EB_COUNTRY` | pays des banques proposées | `FR` |
| `BUDGET_SYNC_HOURS` | heures entre deux synchros automatiques ; `0` les désactive | `6` |

## Synchronisation bancaire (Enable Banking)

Les opérations arrivent toutes seules par [Enable Banking](https://enablebanking.com), gratuit pour ses propres comptes (mode « restreint » : seuls les comptes liés à ton application sont lisibles). Tes identifiants bancaires ne passent jamais par l'application : tu t'authentifies sur le site de ta banque. Sans cette configuration, tout le reste fonctionne (collage, import, saisie).

### Mise en place

1. Crée un compte sur le [portail Enable Banking](https://enablebanking.com/sign-in/) (lien de connexion par e-mail).
2. Enregistre une application en environnement **Production**, avec comme URL de retour `https://ton-domaine/api/bank/callback` (HTTPS obligatoire). Deux façons d'obtenir la clé : la laisser générer par le navigateur (le portail te fait télécharger la clé privée), ou créer la tienne et envoyer le certificat :
   ```bash
   openssl genrsa -out private.key 4096
   ```
   ```bash
   openssl req -new -x509 -days 365 -key private.key -out public.crt -subj "/C=FR/CN=budget"
   ```
   Le formulaire demande aussi une description, un e-mail et des URL de confidentialité et de conditions.
3. L'application apparaît inactive : active-la avec « Activate by linking accounts » en liant **tes comptes** (perso, commun). Sans cette étape, la banque ne renvoie aucun compte.
4. Donne l'identifiant et la clé à l'application, puis redémarre :
   ```bash
   cp private.key secrets/enablebanking.key && chmod 600 secrets/enablebanking.key
   ```
   et dans `docker-compose.yml`, décommente le secret `eb_key` et renseigne `BUDGET_EB_APP_ID` et `BUDGET_EB_REDIRECT_URL` (exemple dans le fichier). Une configuration incomplète arrête le démarrage avec un message qui dit ce qui manque.
5. Dans l'application : **Règles et import → Connexion bancaire → Connecter une banque**. Choisis ta banque, authentifie-toi chez elle, puis indique quel compte de l'appli chaque compte bancaire alimente (« Perso », « Commun », un nouveau compte, ou « Ne pas synchroniser » pour un livret). Lance la première synchro : elle remonte 90 jours.

La clé privée est un secret : jamais dans le dépôt ni dans une image. Elle ne sert qu'à signer les requêtes vers Enable Banking (jeton d'une heure) et n'est jamais journalisée.

### Fonctionnement

- **Synchro automatique toutes les 6 heures**, et non 4 : beaucoup de banques n'acceptent que quatre lectures par jour quand l'utilisateur n'est pas présent, et Enable Banking recommande d'attendre 6 heures après un refus. Le compteur survit aux redémarrages. Si la banque refuse quand même, le message le dit et la synchro suivante réessaie.
- **« Synchroniser maintenant »** signale à la banque que tu es présent (ton navigateur, et ton adresse IP si elle est publique), ce qui ne compte pas dans cette limite. Pour que l'adresse IP soit connue derrière un reverse proxy, il faut `BUDGET_TRUST_PROXY=true`. Si la banque exige une information indisponible, la synchro part comme une synchro automatique.
- **Durée du consentement** : c'est la banque qui la fixe (180 jours le plus souvent, parfois 90). L'application demande le maximum annoncé par la banque. Un bandeau prévient 14 jours avant la fin, sur tous les onglets ; « Reconnecter » refait l'autorisation et garde les rattachements de comptes et l'historique. Une session peut aussi expirer avant terme : le bandeau apparaît alors à la synchro suivante.
- **Compte commun** : si la banque ne te le montre pas, ta partenaire peut connecter la banque de son côté (seconde connexion). Un compte vu par deux connexions n'est lu qu'une fois par passage.
- **Déconnecter** ferme le consentement chez Enable Banking et garde les opérations déjà récupérées.
- Rien d'autre que ces appels ne sort du serveur : ni les soldes (non utilisés) ni les opérations ne vont ailleurs.

## Utilisation

- **Suivi du mois** : dépensé, reste, reste par jour, projection, enveloppes, rythme, journal filtrable, pots, bilan, six dernières périodes, export CSV.
- **Budget** : revenus, charges fixes, provisions, pourcentages par poste. Tout se recalcule en direct et s'enregistre sur le serveur.
- **Coller des opérations** (dans « Suivi du mois ») : copie la liste des opérations sur le site de ta banque et colle-la. Libellé, date, montant, statut (« En cours » = en attente) et catégorie bancaire sont reconnus ; l'année se déduit du jour de la semaine. Un aperçu te laisse tout corriger avant de valider. Pour chaque catégorie bancaire inconnue, tu choisis une catégorie existante, d'en créer une (elle démarre à 0 % de l'enveloppe, à régler dans l'onglet Budget) ou de ne pas l'utiliser. La correspondance est retenue pour les collages suivants (liste et oubli dans « Règles et import »). Ordre de priorité : tes règles, puis la catégorie déjà posée sur l'opération rapprochée, puis la correspondance bancaire.
- **Charges fixes** (dans « Suivi du mois ») : chaque charge est « à payer » ou « payée ». Elle est payée dès qu'une opération est classée dedans, que ce soit par collage, import ou saisie. Un clic sur « Marquer payée » crée la saisie au montant prévu, sur le compte de prélèvement de la charge (à régler une fois dans l'onglet Budget) ; ton prochain collage de la ligne bancaire s'y rapproche au lieu de la dupliquer, et la charge passe « via la banque ». « Annuler » ne retire qu'un marquage manuel. Un montant différent du prévu s'affiche avec son écart. Une période passée sans paiement indique « Non payée ». Le crédit marqué soldé n'est plus attendu ; les provisions annuelles (taxe foncière, assurance auto) se suivent dans les pots.
- **Budget par période** (onglet Budget) : les montants, pourcentages, comptes de prélèvement et revenus sont datés. Une modification vaut « à partir de la période affichée » (la période en cours par défaut, donc elle compte ce mois-ci) jusqu'à un changement déjà prévu ; les périodes d'avant gardent leurs valeurs, y compris l'historique des six derniers mois et les soldes de pots. Les flèches du haut permettent de corriger une période passée ou de préparer une période à venir. Si des changements sont déjà prévus plus tard, un bandeau le dit et propose de les remplacer (« Les appliquer aussi aux périodes suivantes »).
- **Catégories et charges** (onglet Budget, en bas) : ajout d'une charge fixe, d'une catégorie variable, d'un pot, d'une provision annuelle ou d'une catégorie hors budget, avec un aperçu chiffré de l'effet sur l'enveloppe et l'épargne (une part d'enveloppe vient de l'épargne). Pour chaque catégorie : renommer ; **arrêter** à partir de la période affichée (elle sort du budget, l'historique la garde) puis réactiver ; **fusionner** dans une autre (opérations, règles et correspondances bancaires suivent, la source disparaît : c'est la réponse à un doublon comme « Télécoms » face à « Télécom (Free box + mobile) ») ; **supprimer** seulement si rien n'y est classé. Une dépense classée dans une catégorie arrêtée, ou pas encore créée à sa date, reste comptée (ligne « Catégories arrêtées ou pas encore créées »). Une catégorie créée au collage existe dès la période de sa plus ancienne opération.
- **À trier** : opérations non reconnues. Chaque tri propose de créer une règle (« toujours classer X en Y »).
- **Connexion bancaire** (dans « Règles et import ») : voir « Synchronisation bancaire ». Les opérations synchronisées passent par les mêmes règles de classement et arrivent dans « À trier » si aucune ne correspond.
- **Règles et import** : import CSV/OFX, gestion des règles. Les opérations déjà importées sont ignorées : on peut réimporter un relevé qui chevauche le précédent.

### Formats d'import

CSV (séparateur `;`, `,` ou tabulation, UTF-8 ou Windows-1252) avec une colonne date, un libellé et soit un montant signé, soit deux colonnes débit/crédit ; un préambule avant l'en-tête est toléré. OFX/QFX en SGML ou XML. Si les colonnes d'une banque ne sont pas reconnues, l'erreur liste les en-têtes lus : envoie-les pour que le format soit ajouté.

### Règles métier appliquées

- Doublons : clé unique (compte, identifiant fournisseur). Sans identifiant (CSV), empreinte date + montant + contrepartie + libellé, avec un rang pour deux opérations identiques le même jour. Pour un collage, l'empreinte inclut l'heure.
- Rapprochement (collage, CSV, OFX) : même compte et même montant exact. Une saisie manuelle se rapproche à ±5 jours ; une opération « en attente » à ±5 jours si l'enseigne ressemble ; une opération déjà débitée seulement le même jour et pour la même enseigne. Le statut passe alors à « débitée » et la saisie garde sa note et sa catégorie. Dans l'aperçu d'un collage, tu peux refuser un rapprochement ; à l'import CSV/OFX, sans aperçu, seuls les cas sans ambiguïté sont fusionnés.
- Synchro bancaire : l'identifiant d'une opération est la référence de la banque (`entry_reference`), stable d'une lecture à l'autre ; sans référence, une empreinte. Chaque synchro relit les 10 derniers jours : une opération « en attente » y devient débitée (date et référence mises à jour, catégorie gardée). Une opération en attente venue de la banque qui disparaît de la relecture est retirée : elle a été annulée, ou débitée pour un autre montant (pré-autorisation de carburant), et la garder compterait la dépense deux fois. Les opérations annulées, rejetées ou programmées ne sont pas importées.
- Synchro bancaire et opérations déjà présentes : une saisie manuelle au même montant à ±5 jours est rapprochée ; une opération collée ou importée d'un fichier au même montant, à ±5 jours et de la même enseigne, est reprise par la synchro au lieu d'être doublée (la date du site et la date comptable de l'API diffèrent souvent). Entre plusieurs candidats équivalents, la synchro en prend un : le nombre d'opérations reste juste.
- Virements internes : exclus du budget et de « À trier ». Un crédit rattaché à une catégorie y réduit la dépense (remboursement).
- Le budget est daté par période (voir « Budget par période »). Il n'y a plus de case « Crédit Arkea soldé » globale : « Arrêter » une charge la remplace pour n'importe quelle charge. Une ancienne case cochée est convertie en arrêt à partir de la période en cours au premier lancement.
- Une dépense imputée à un pot réduit le solde du pot, pas l'enveloppe variable. Une opération classée dans une charge fixe n'entame pas l'enveloppe variable.
- Dans l'aperçu d'un collage, une opération peut être proposée dans une charge fixe (pastille « charge fixe ? ») si son montant est exactement celui d'une seule charge encore à payer et que son libellé lui ressemble ou que c'est un prélèvement. C'est une piste à confirmer, jamais appliquée sans validation.
- Un classement manuel n'est jamais écrasé par une règle.
- Dates de période en Europe/Paris, montants en `decimal`.

## Sauvegarde et restauration

La base est le fichier `data/budget.db` (avec `-wal`/`-shm` quand l'application tourne). Sauvegarde cohérente, application en marche :

```bash
sqlite3 data/budget.db ".backup 'sauvegarde-$(date +%F).db'"
```

Restauration : arrête le conteneur, remplace `data/budget.db` par la sauvegarde (supprime les éventuels `budget.db-wal` et `budget.db-shm`), relance. La sauvegarde automatique chiffrée avec rétention de 30 jours est prévue en phase 3.

## Limites connues

Synchronisation bancaire (phase 2) :

- Elle n'a pas pu être essayée contre la vraie API : il faut ton application Enable Banking et ta banque. Le client suit la [documentation officielle](https://enablebanking.com/docs/api/reference/) (relue le 12 novembre 2026) et les tests tournent contre une fausse API de même forme. Le premier essai réel peut révéler des particularités de ta banque (dates, libellés, opérations en attente).
- Certaines banques ne donnent pas de référence d'opération, ou pas pour les opérations en attente. L'empreinte de remplacement ne distingue pas deux opérations strictement identiques le même jour d'une même opération renvoyée deux fois par la banque.
- Une opération en attente retirée (voir « Règles métier ») perd la catégorie que tu lui avais donnée si la banque la débite pour un autre montant : la ligne débitée repasse par les règles.
- Un compte de l'appli ne peut être alimenté que par un seul compte bancaire, et tous les montants sont supposés en euros.
- Les soldes des comptes ne sont pas lus : le « reste sur le compte » reste calculé par le budget.
- Pas encore de notification push avant l'expiration du consentement (phase 3) : le bandeau s'affiche à l'ouverture de l'application.

Phase 1 :


- Le rapprochement repose sur le montant exact et des dates proches : deux achats identiques à quelques jours d'écart chez la même enseigne peuvent être confondus avec une saisie manuelle. L'aperçu du collage permet de le voir et de le refuser.
- Le fichier `Dockerfile` et le `docker-compose.yml` n'ont pas été construits dans l'environnement de développement (pas de démon Docker) ; ils suivent les images officielles .NET 8.
- Deux imports de fichiers qui coupent une journée en deux peuvent, dans de rares cas d'opérations strictement identiques le même jour, se dédoubler ou se confondre. Les relevés par jour complet ne posent pas de problème.
- Le type d'une catégorie (fixe, variable, pot…) ne se change pas : crée la bonne catégorie puis fusionne l'ancienne dedans.
- Les calculs de l'onglet Budget existent en JavaScript (simulation instantanée) et en C# (référence testée) : à modifier ensemble.
- Polices : celles du système (pas de Google Fonts), pour qu'aucune requête ne parte vers un tiers.
