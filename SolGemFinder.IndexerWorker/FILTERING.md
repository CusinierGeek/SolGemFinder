# ?? Système de Filtrage IndexerWorker

## Architecture du Filtrage

```
Blockchain Solana (50 tx/scan)
         ?
   ?? NIVEAU 1: FILTRES ULTRA-RAPIDES (micro-latence)
         ?
   ? 70-80% rejeté ici
         ?
   ?? Événements propres ? DetectionWorker
```

## ?? NIVEAU 1 - Filtres Ultra-Rapides

**Objectif** : Tuer le bruit immédiatement (70-80% de rejet)

**Latence** : < 50ms par transaction

### Filtres Appliqués

#### 1?? Statut de Confirmation ?
```csharp
signatureInfo.ConfirmationStatus != null
```
**But** : Ignorer les transactions non confirmées
**Rejet** : ~5%

#### 2?? Nombre de Comptes ?
```csharp
accountKeys.Count >= 8
```
**But** : Une création de token Pump.fun nécessite minimum 8 comptes :
- System Program
- Token Program  
- Rent Sysvar
- Mint Account
- Metadata Account
- Bonding Curve
- Associated Token Account
- Creator

**Rejet** : ~30%

#### 3?? SPL Token Program ?
```csharp
accountKeys.Contains("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA")
```
**But** : Toute création de token nécessite le Token Program
**Rejet** : ~10%

#### 4?? System Program ?
```csharp
accountKeys.Contains("11111111111111111111111111111111")
```
**But** : CreateAccount instruction nécessaire pour créer le mint
**Rejet** : ~5%

#### 5?? Instructions de Création ?
```csharp
hasCreateAccount && hasInitializeMint
```
**But** : Vérifier que c'est bien une CRÉATION et pas un trade

**Détails** :
- `CreateAccount` (System Program) : Créer le compte mint
- `InitializeMint` (Token Program) : Initialiser le mint avec supply, decimals, etc.

**Rejet** : ~20-30% (le plus gros filtre !)

#### 6?? Déduplication ?
```csharp
_seenTokens.Contains(tokenAddress)
```
**But** : Ne pas publier le même token plusieurs fois
**Rejet** : ~5%

---

## ?? Résultats Attendus

### Taux de Rejet Global : **70-80%**

Sur 50 transactions scannées :
- **35-40 transactions** rejetées (trades, autres interactions)
- **10-15 tokens** passent les filtres
- **10-15 événements** publiés vers DetectionWorker

### Exemple de Logs

```
?? Scanning slots 388770200 ? 388770300
?? Found 50 Pump.fun transactions to scan
? NEW TOKEN: FZm9KgwU... | Slot: 388770257
? NEW TOKEN: 4NtAf5BzU... | Slot: 388770259
? NEW TOKEN: EwxJrTXF... | Slot: 388770266
?? Processed: 50 | Kept: 3 | Filtered: 47 (94%)
? Radar found 3 NEW token creation(s)
?? Published: FZm9KgwU... ? DetectionWorker
?? Published: 4NtAf5BzU... ? DetectionWorker
?? Published: EwxJrTXF... ? DetectionWorker
```

---

## ? Performance

### Latence Moyenne par Transaction
- **GetTransaction RPC** : 100-200ms
- **Filtres niveau 1** : < 5ms
- **Total par transaction** : ~105-205ms

### Throughput
- **50 transactions** : ~5-10 secondes
- **Rate** : ~5-10 tx/seconde

### Optimisations Appliquées

1. ? **Déduplication en mémoire** : HashSet pour éviter les duplicates
2. ? **Filtres en cascade** : Les filtres les plus discriminants en premier
3. ? **Early return** : Dès qu'un filtre échoue, on passe à la suivante
4. ? **Pas de RPC supplémentaire** : Tout se fait avec GetTransaction

---

## ?? Ce Que l'Indexer NE FAIT PAS

L'IndexerWorker est un **RADAR**, pas un **ANALYSTE**.

### ? PAS de Calculs Lourds
- Pas de scoring
- Pas d'indicateurs techniques
- Pas de prédictions

### ? PAS de RPC Supplémentaires
- Pas de `getAccountInfo` pour la liquidité
- Pas de `getTokenAccountsByOwner` pour les holders
- Pas de `getSignaturesForAddress` pour l'historique du token

### ? PAS d'Analyse Approfondie
- Pas de vérification de la liquidité exacte
- Pas de comptage des buys/wallets
- Pas de détection de rug pull patterns

**Pourquoi ?**
- Ces analyses sont **lentes** (plusieurs secondes)
- Elles créent **beaucoup de charge RPC**
- Elles sont faites par le **DetectionWorker** ensuite

---

## ?? Prochaine Étape : DetectionWorker

Une fois qu'un token passe les filtres niveau 1, il est envoyé au **DetectionWorker** qui va :

### ?? Niveau 2 - Analyses Rapides (< 1s)
- ? Vérifier si bonding curve active
- ? Récupérer la liquidité actuelle
- ? Compter les holders uniques
- ? Compter les buys récents
- ? Vérifier le statut (pre/post bonding)

### ?? Niveau 3 - Analyses Profondes (1-5s)
- Historique des trades
- Pattern d'accumulation
- Vélocité des transactions
- Distribution des holders

### ?? Niveau 4 - Scoring (GemFinderWorker)
- Calcul du score de qualité
- Détection de gems potentielles
- Alertes

---

## ??? Configuration du Filtrage

### Variables d'Environnement

```bash
# Polling interval (5s = temps de traiter ~50 tx)
POLLING_INTERVAL_MS=5000

# Batch size (nombre de tx par scan)
# Plus = moins de scans, mais plus de latence
# Moins = plus de scans, mais moins de latence
# Optimal : 50
```

### Ajuster le Filtrage

Dans `SolanaRadar.cs`, ligne 147 :

```csharp
// Ajuster le nombre minimum de comptes
if (accountKeys.Count < 8)  // ? Augmenter pour filtrer plus agressivement
{
    return null;
}
```

### Monitoring

Les logs fournissent des métriques en temps réel :

```
?? Processed: 50 | Kept: 3 | Filtered: 47 (94%)
```

**Métriques à surveiller** :
- **Taux de rejet** : Doit être > 70%
- **Tokens publiés** : Doit être < 30% des transactions scannées
- **Latence RPC** : Doit être < 300ms en moyenne

---

## ?? Troubleshooting

### Trop de Tokens Publiés (> 30%)

**Cause** : Filtres pas assez stricts

**Solution** :
1. Augmenter `accountKeys.Count` minimum (ex: 10 au lieu de 8)
2. Ajouter plus de vérifications d'instructions
3. Vérifier les logs pour voir ce qui passe

### Pas Assez de Tokens (< 5%)

**Cause** : Filtres trop stricts

**Solution** :
1. Diminuer `accountKeys.Count` minimum (ex: 6 au lieu de 8)
2. Vérifier que les instructions sont bien détectées
3. Ajouter des logs debug pour voir ce qui est filtré

### Beaucoup de Duplicates

**Cause** : Le même token apparaît dans plusieurs transactions

**Solution** : Déjà géré par `_seenTokens` HashSet ?

---

## ?? Références

- **Pump.fun Program** : `6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P`
- **SPL Token Program** : `TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA`
- **System Program** : `11111111111111111111111111111111`

---

Made with ? for ultra-fast token discovery
