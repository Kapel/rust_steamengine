# Steam Engine power source -- Design & Implementation

## Requirements (from original brief)

| Requirement | Implemented |
|---|---|
| New item using existing model | Furnace item with configurable workshop skin ID |
| Turn on/off via button (like electric furnace) | Press E on furnace toggles engine, visual fire via Flags.On |
| Mount storage adaptor for fuel input | Furnace natively supports industrial storage adaptors; CanAcceptItem permits wood/charcoal |
| Input charcoal or wood | Allowed fuel set: `wood`, `charcoal`; accepted in any inventory slot |
| Wood generates less power than charcoal | Config: `WoodPowerBonus=0W`, `CharcoalPowerBonus=15W` |
| Input water | Hose into hidden barrel (pump/catcher) AND/OR a jug/bottle/bucket with fresh water in the water slot; barrel drains first |
| Car parts determine power (quality matters) | 5 part types, 3 tiers each; ALL 5 required to run; `TierMultipliers` per tier; wear down over time |
| Output electrical power | ElectricGenerator child (`generator.small.prefab`); IOEntity output wire to grid |
| Power curve: cheapest=generator, best<windmill | 40W with all tier1 parts, 140W with all tier3 (cap), additive by default |

## Architecture

```
  SteamEngine.cs (single-file Oxide plugin)

  Entity tree:
  │
  ├─ BaseOven "furnace"          ← skinned, deployable item, player interacts with this
  │   ├─ skinID = config.SkinId
  │   ├─ panelName = "generic_resizable" (replaces typed furnace UI)
  │   ├─ inventory (7 slots):
  │   │    slot 0   = fuel (wood/charcoal)
  │   │    slot 1-5 = car parts (5 types, tiers 1-3)
  │   │    slot 6   = water container item (jug/bottle/bucket/botabag)
  │   └─ Flags.On = fire visuals
  │
  ├─ ElectricGenerator (child)   ← hidden test generator, spawns at GeneratorOffset
  │   ├─ enableSaving = false
  │   ├─ electricAmount set via cached FieldInfo
  │   └─ MarkDirtyForceUpdateOutputs() propagates to grid
  │
  └─ LiquidContainer (child)     ← hidden water barrel, spawns at WaterBarrelOffset
      ├─ enableSaving = false
      ├─ inventory[0] = water item (amount=ml)
      └─ fluid IO for hose connection

  Slot typing is enforced server-side in the CanAcceptItem hook (returns
  true/false explicitly, overriding the vanilla furnace slot filter that
  rejected car parts). Items shift-clicked in (targetPos = -1) are allowed
  by category and may land in any free slot; the engine logic scans all
  slots, so placement does not affect behavior.

  Water sourcing: GetWaterAmount() = barrel water + slot-container water
  (fresh water only). ConsumeWater() drains the barrel first, then the
  slot container.

  Custom item: when the CustomItemDefinitions library (0xF,
  https://lone.design/product/custom-item-definitions/) is loaded, a
  real "steamengine" ItemDefinition is registered (parent: furnace,
  skin from config) and used by /steamengine.give; otherwise a renamed
  skinned furnace item is given.
```

## Hook map

| Hook | What it does |
|---|---|
| `Init()` | Build car part hashset, cache FieldInfo for generator output |
| `OnServerInitialized()` | Find all steam engines by skinID, auto-resume those with fuel+water+parts |
| `OnEntitySpawned(BaseNetworkable)` | Detect newly placed steam engines |
| `OnEntityKill(BaseNetworkable)` | Clean up children, remove from tracking |
| `OnOvenToggle(BaseOven, BasePlayer)` | Block native toggle, start/stop our engine tick |
| `OnOvenStart(BaseOven)` | Block native cooking start (defense-in-depth) |
| `OnFuelConsume(BaseOven, Item, ItemModBurnable)` | Block native fuel consumption |
| `OnOvenCook(BaseOven, Item, BasePlayer)` | Block native cooking |
| `CanAcceptItem(ItemContainer, Item, int)` | Slot typing: fuel→0, parts→1-5, water container→6; overrides vanilla furnace filter |
| `CanMoveItem(Item, PlayerInventory, uint, int, int)` | Only fuel/parts/water containers may move within the engine |
| `CanLootEntity(BasePlayer, StorageContainer)` | Allow looting (return null = default behavior) |
| `Unload()` | Destroy all child entities and timers |
| `steamengine.reload` | Stops all, clears state, re-scans entities, re-inits |

## Engine tick loop (every TickInterval seconds)

```
EngineTick:
  ├─ Guard: !Running || Oven destroyed → cleanup & return
  ├─ Check fuel > 0 → stop if empty
  ├─ Check water > 0 → stop if empty
  ├─ ConsumeFuel()     → remove FuelPerTick units from first fuel slot found
  ├─ ConsumeWater()    → drain WaterPerSecond * TickInterval ml (barrel first, then slot container)
  ├─ DegradeParts()    → subtract PartWearPerTick from each car part's condition
  ├─ Check HasAllParts → stop if any part type completely broken
  ├─ CalculatePower()  → (BasePower + FuelBonus) * Σ (1.0 + tier_bonus)
  └─ SetGeneratorOutput() → electricAmount = power watts
```

## Power formula

```
power = clamp((BasePower + FuelBonus) * PartBoost, 0, MaxPower)

FuelBonus = CharcoalPowerBonus or WoodPowerBonus (whichever is in the furnace)
PartBoost = 1.0 + sum of (TierMultiplier[tier] - 1.0) for each unique part type
            (highest tier per part type wins; duplicates of same type are ignored)
```

Defaults: 25W base + 15W charcoal = 40W. Tier3 adds 0.5 each → 5 × 0.5 = 2.5 bonus → 40 × 3.5 = 140W.

### Power scaling by parts

| Parts | Power (charcoal) | Power (wood) |
|---|---|---|
| All T1 | 40W | 25W |
| All T2 | 80W | 50W |
| All T3 | 140W (cap) | 87.5W |
| 2T3 + 3T2 | 104W | 65W |
| 1T3 + 4T1 | 60W | 37.5W |
| 3T3 + 1T2 + 1T1 | 108W | 67.5W |

## Startup requirements

Engine will NOT start unless ALL three conditions are met:

1. **All 5 part types present** (any quality tier) -- `HasAllParts()` check
2. **Fuel present** (wood or charcoal) -- `GetFuelAmount() > 0`
3. **Water present** -- hose-fed barrel and/or a container item (jug/bottle/bucket/botabag) holding fresh water (`water`, not salt water) -- `GetWaterAmount() > 0`

During runtime, if ANY of these drops to zero (fuel exhausted, water drained, a part type's last copy breaks), the engine stops immediately on that tick.

## Car part wear

Each tick while running, `item.condition` decreases by `PartWearPerTick` (default 0.05/tick). At 100 condition → ~2000 ticks = ~33 minutes runtime. Broken parts auto-remove via `TryRemoveItem`. Set `PartWearPerTick` to 0 to disable wear.

## Persistence

- Child entities (`enableSaving = false`) are NOT saved
- On server restart, `OnServerInitialized` iterates `BaseNetworkable.serverEntities`, finds by skinID, respawns children
- Auto-resume: engines with all 5 parts + fuel + water restart automatically
- `_engines` dictionary tracks by `entity.net.ID` (NetworkableId)
- `steamengine.reload` console command: stops all, clears state, re-scans, re-inits

## Prefabs used

| Entity | Prefab path | Status |
|---|---|---|
| Generator | `assets/prefabs/deployable/playerioents/generators/generator.small.prefab` | NEEDS VERIFICATION |
| Water barrel | `assets/prefabs/deployable/liquidbarrel/waterbarrel.prefab` | NEEDS VERIFICATION |

## Live testing checklist

| # | Test | Status |
|---|---|---|
| 1 | `/steamengine.give` gives a furnace item named "Steam Engine" | ⬜ |
| 2 | Placing furnace spawns generator + barrel children; inventory shows 7-slot generic panel | ⬜ |
| 3 | Wood/charcoal accepted (slot 0 or shift-click), other items rejected | ⬜ |
| 4 | Car parts accepted in slots 1-5, non-parts rejected | ⬜ |
| 5 | E to toggle: needs all 5 parts + fuel + water container to start | ⬜ |
| 6 | Jug/bottle/bucket with fresh water accepted in slot 6; salt water does not run engine | ⬜ |
| 6b | Hose from pump into barrel input works; barrel drains before slot container | ⬜ |
| 6c | CustomItemDefinitions loaded → /steamengine.give gives `steamengine` custom item | ⬜ |
| 7 | Wire from generator output to root combiner/battery works | ⬜ |
| 8 | Power output matches formula: 40W (T1) → 140W (T3) | ⬜ |
| 9 | Charcoal gives +15W vs wood | ⬜ |
| 10 | Parts degrade over time, break at 0 condition | ⬜ |
| 11 | Any part type fully broken → engine stops | ⬜ |
| 12 | Fuel exhausted → engine stops next tick | ⬜ |
| 13 | Water container drained → engine stops next tick | ⬜ |
| 14 | Kill furnace → children cleaned up, no orphans | ⬜ |
| 15 | Server restart → engines re-initialized, auto-started if all conditions met | ⬜ |
| 16 | Storage adaptor feeds wood/charcoal via industrial conveyor | ⬜ |
| 17 | `steamengine.reload` restarts all engines with new config | ⬜ |
| 18 | Part replacement after break → engine can restart | ⬜ |
| 19 | Swapping in a full water container after drain → engine can restart | ⬜ |


## Build & test

```bash
# One-time build:
docker build -f Dockerfile.build -t steamengine-plugin .

# Run tests:
docker run --rm -v $(pwd):/src -w /src --entrypoint bash steamengine-plugin \
  -c 'mcs SteamEngine.Tests.cs -out:t.exe && mono t.exe'

# Extract compiled DLL:
docker create --name se steamengine-plugin && docker cp se:/output/SteamEngine.dll . && docker rm se
```

Test suite: 137 tests, 0 failures. Covers start conditions, power for all tier combos, fuel/water exhaustion, part degradation/breakage, all 7 runtime stop permutations, MaxPower clamp, config validation, TryStart gates, DegradeParts, combined runtime scenarios, slot typing filter, and water-container sourcing.

## File listing

| File | Purpose |
|---|---|
| `SteamEngine.cs` | Main plugin |
| `SteamEngine.Tests.cs` | Logic test suite (137 tests) |
| `Dockerfile.build` | Reproducible build with SteamCMD + Oxide |
| `design.md` | This design doc |
| `README.md` | User-facing documentation |
