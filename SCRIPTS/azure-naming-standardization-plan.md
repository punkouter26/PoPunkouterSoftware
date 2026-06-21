# Azure Naming Standardization Plan

Generated: 2026-06-17T15:14:49.1870252-04:00

Scope: apply recommendations 1, 2, 3, 4, 5, 6, 8, and 9 from the report review.

Baseline:

- Resource groups: `rg-<app>-<env>-<region>`
- Web apps/APIs: `app-<app>-<role>-<env>-<region>-001`
- App Service Plans: `asp-<app-or-shared>-<os>-<sku>-<env>-<region>-001`
- Storage Accounts: `st<app><env><region>001`
- Shared platform group: `rg-platform-shared-<env>-eus2`
- Default workload location: westus2 / wus2

Important: Azure resource renames are generally recreate-and-migrate operations. Do not delete old resources until apps are redeployed, data is copied, DNS/configuration is cut over, and validation passes.

## pocouplequiz

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpocouplequizapp | microsoft.storage/storageaccounts | eastus2 | rg-pocouplequizapp-prod-wus2 | stpocouplequizappprodwus | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| pocouplequiz-app | microsoft.web/sites | westus2 | rg-pocouplequiz-prod-wus2 | app-pocouplequiz-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## poface

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| pofacedevsa | microsoft.storage/storageaccounts | westus2 | rg-pofacedevsa-prod-wus2 | stpofacedevsaprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| poface-dev-web | microsoft.web/sites | westus2 | rg-poface-prod-wus2 | app-poface-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pofunquiz

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| storagezizogpdhpyiwo | microsoft.storage/storageaccounts | eastus | rg-pofunquiz-prod-wus2 | stpofunquizprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-pofunquiz-zizogpdhpyiwo | microsoft.web/sites | westus2 | rg-pofunquiz-prod-wus2 | app-pofunquiz-api-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pohappytrump

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpohappytrump2jr7zl | microsoft.storage/storageaccounts | westus2 | rg-pohappytrump2jr7zl-prod-wus2 | stpohappytrump2jr7zlprod | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-pohappytrump-2jr7zldry26xa | microsoft.web/sites | westus2 | rg-pohappytrump-prod-wus2 | app-pohappytrump-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## poissues

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| poissuestbl6824 | microsoft.storage/storageaccounts | eastus2 | rg-poissuestbl6824-prod-wus2 | stpoissuestbl6824prodwus | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| po-issues | microsoft.web/sites | westus2 | rg-poissues-prod-wus2 | app-poissues-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pojoker

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpojoker26 | microsoft.storage/storageaccounts | westus2 | rg-pojoker26-prod-wus2 | stpojoker26prodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-pojoker | microsoft.web/sites | westus2 | rg-pojoker-prod-wus2 | app-pojoker-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## polinks

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpolinksdev01 | microsoft.storage/storageaccounts | eastus | rg-polinksdev01-prod-wus2 | stpolinksdev01prodwus200 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-polinks | microsoft.web/sites | westus2 | rg-polinks-prod-wus2 | app-polinks-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## polocalcompare

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| polocalcomparedevsa | microsoft.storage/storageaccounts | westus2 | rg-polocalcomparedevsa-prod-wus2 | stpolocalcomparedevsapro | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| PoLocalCompare-AppService-dev | microsoft.web/sites | westus2 | rg-polocalcompare-prod-wus2 | app-polocalcompare-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pomarriedlife

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| pomarriedlifestorage | microsoft.storage/storageaccounts | eastus2 | rg-pomarriedlifestorage-prod-wus2 | stpomarriedlifestoragepr | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| pomarriedlife-api | microsoft.web/sites | westus2 | rg-pomarriedlife-prod-wus2 | app-pomarriedlife-api-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pomemevideo

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpomemevideo | microsoft.storage/storageaccounts | westus2 | rg-pomemevideo-prod-wus2 | stpomemevideoprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-pomemevideo | microsoft.web/sites | westus2 | rg-pomemevideo-prod-wus2 | app-pomemevideo-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## pominigames

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| st5ln5hfdrvof5u | microsoft.storage/storageaccounts | westus2 | rg-pominigames-prod-wus2 | stpominigamesprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-5ln5hfdrvof5u | microsoft.web/sites | westus2 | rg-pominigames-prod-wus2 | app-pominigames-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## ponovaweight

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stponovaweight | microsoft.storage/storageaccounts | westus2 | rg-ponovaweight-prod-wus2 | stponovaweightprodwus200 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| ponovaweight-app | microsoft.web/sites | westus2 | rg-ponovaweight-prod-wus2 | app-ponovaweight-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## popunkoutersoftware

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stpopunkoutersoftware | microsoft.storage/storageaccounts | westus2 | rg-popunkoutersoftware-prod-wus2 | stpopunkoutersoftwarepro | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-popunkoutersoftware | microsoft.web/sites | westus2 | rg-popunkoutersoftware-prod-wus2 | app-popunkoutersoftware-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## poredoimage

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stporedoimage26 | microsoft.storage/storageaccounts | eastus | rg-poredoimage26-prod-wus2 | stporedoimage26prodwus20 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| poredoimage-web | microsoft.web/sites | westus2 | rg-poredoimage-prod-wus2 | app-poredoimage-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## porepolinetracker

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stporepolinetracker | microsoft.storage/storageaccounts | eastus2 | rg-porepolinetracker-prod-wus2 | stporepolinetrackerprodw | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-porepolinetracker | microsoft.web/sites | westus2 | rg-porepolinetracker-prod-wus2 | app-porepolinetracker-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## porobotstocks

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stlzwajfaczgake | microsoft.storage/storageaccounts | westus2 | rg-porobotstocks-prod-wus2 | stporobotstocksprodwus20 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| PoRobotStocks-PoRobotStocks-web | microsoft.web/sites | westus2 | rg-porobotstocks-prod-wus2 | app-porobotstocks-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## poseereview

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| stposeereview | microsoft.storage/storageaccounts | eastus2 | rg-poseereview-prod-wus2 | stposeereviewprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-poseereview | microsoft.web/sites | westus2 | rg-poseereview-prod-wus2 | app-poseereview-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## poshared

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| po-aiservices-shared | microsoft.cognitiveservices/accounts | eastus2 | rg-platform-shared-prod-eus2 | ais-platform-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| po-aiservices-shared/po-aiservices-shared-project | microsoft.cognitiveservices/accounts/projects | eastus2 | rg-platform-shared-prod-eus2 | proj-platform-ai-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| poappideinsights8f9c9a4e | microsoft.insights/components | eastus2 | rg-platform-shared-prod-eus2 | appi-platform-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| avail-app-porepolinetracker-health | microsoft.insights/webtests | eastus2 | rg-platform-shared-prod-eus2 | avail-platform-health-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| kv-poshared | microsoft.keyvault/vaults | eastus | rg-platform-shared-prod-eus2 | kv-platform-prod-eus2-00 | eastus2 | Create new Key Vault, migrate secrets/keys/certs metadata safely, update references, validate, then retire old vault. |
| mi-poshared-containerapps | microsoft.managedidentity/userassignedidentities | eastus | rg-platform-shared-prod-eus2 | id-platform-workload-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| PoShared-LogAnalytics | microsoft.operationalinsights/workspaces | eastus2 | rg-platform-shared-prod-eus2 | log-platform-ops-prod-eus2-001 | eastus2 | Recreate or redeploy with standardized name/location; use tags immediately on current resource while migrating. |
| asp-pofunquiz-f1 | microsoft.web/serverfarms | westus2 | rg-platform-shared-prod-eus2 | asp-platform-linux-f1-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |
| asp-pofunquiz-f2 | microsoft.web/serverfarms | westus2 | rg-platform-shared-prod-eus2 | asp-platform-linux-f1-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |
| asp-pofunquiz-f3 | microsoft.web/serverfarms | westus2 | rg-platform-shared-prod-eus2 | asp-platform-linux-f1-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |
| asp-poissues-b1-wus2 | microsoft.web/serverfarms | westus2 | rg-platform-shared-prod-eus2 | asp-platform-linux-b1-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |
| asp-poissues-f1-westus3 | microsoft.web/serverfarms | westus3 | rg-platform-shared-prod-eus2 | asp-platform-linux-f1-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |
| asp-poshared-linux | microsoft.web/serverfarms | westus2 | rg-platform-shared-prod-eus2 | asp-platform-linux-b2-prod-wus2-001 | westus2 | Create new App Service Plan, move apps by redeploying or changing serverFarmId where compatible, validate, then remove old plan. |

## potraffic

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| potrafficstorage | microsoft.storage/storageaccounts | eastus | rg-potrafficstorage-prod-wus2 | stpotrafficstorageprodwu | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| potraffic-api | microsoft.web/sites | westus2 | rg-potraffic-prod-wus2 | app-potraffic-api-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

## powatch

| Current | Type | Current location | Target RG | Target name | Target location | Action |
|---|---|---:|---|---|---:|---|
| powatchsa | microsoft.storage/storageaccounts | westus2 | rg-powatchsa-prod-wus2 | stpowatchsaprodwus2001 | westus2 | Create new Storage Account, copy data/configuration, update app settings and connection references, validate, then retire old account. |
| app-powatch | microsoft.web/sites | westus2 | rg-powatch-prod-wus2 | app-powatch-web-prod-wus2-001 | westus2 | Create new App Service, deploy app, copy configuration/custom domains, validate, then swap DNS/traffic and retire old app. |

