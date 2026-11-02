# Budget tenable : notes pour Claude

Application de budget auto-hébergée (voir `docs/SPEC-budget-sync.md`, la spec, et `docs/reference/budget-tenable-reference.html`, le prototype qui fait foi pour les calculs).

- Stack : .NET 8 (minimal API, EF Core + SQLite), frontend JS vanilla dans `web/` (PWA). Interface et libellés en français.
- `src/BudgetTenable.Core` : calculs purs (périodes, enveloppe, pots, règles, parseurs CSV/OFX). `src/BudgetTenable.Api` : API, auth PIN, base. `tests/` : xUnit.
- Commandes : `dotnet test` ; `node --check web/app.js` ; `node tests/reference/prototype-oracle.mjs` (valeurs de référence du prototype).
- Argent : `decimal`, jamais de flottants. Dates de période en `DateOnly` (Europe/Paris), jamais en UTC.
- Les calculs de l'onglet Budget existent deux fois : `compute()` dans `web/app.js` (simulation instantanée) et `BudgetCalculator` en C# (source de vérité). Les modifier ensemble.
- Ne jamais journaliser d'IBAN ni de montants au niveau INFO. Secrets hors dépôt.
- Migrations : `dotnet-ef migrations add <Nom> --project src/BudgetTenable.Api --output-dir Data/Migrations`.
- Collage d'opérations : `PastedStatementParser` (Core) + `PasteEndpoints` (aperçu puis validation) ; rapprochement partagé avec l'import CSV/OFX dans `Reconciler`. Les correspondances catégorie bancaire -> catégorie sont dans `BankCategoryMapping`.
- État : phase 1 terminée (voir README). Phase 2 = synchronisation Enable Banking, à faire après vérification de leur doc officielle.
