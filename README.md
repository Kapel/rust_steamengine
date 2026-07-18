# SteamEngine -- Oxide plugin for Rust

A steam engine power generator. Deploy as a skinned furnace (or as the `steamengine` custom item if [CustomItemDefinitions](https://lone.design/product/custom-item-definitions/) is installed), fuel with wood/charcoal, feed it water via hose or container, install car parts to boost output -- generates electrical power for your grid.

## How it works

1. Get the item (use `/steamengine.give` as admin)
2. **Place it** -- the plugin assembles the machine: a storage box in front, a water barrel on the left, a generator on the right
3. **Open the box** (press E on the box) and fill the slots:
   - **Slot 1 (fuel)**: wood or charcoal
   - **Slots 2-6 (parts)**: ALL 5 car part types (carburetor, crankshaft, piston, sparkplug, valve) -- any quality tier works
   - **Slot 7 (water)**: a water jug, bottle, bucket, or bota bag filled with **fresh water** (salt water won't work)
4. **Water** (either source works, both drain barrel-first):
   - **Hose**: use a hose tool to connect a water pump/catcher to the engine's barrel input, or
   - **Container**: keep a filled water container in slot 7
5. **Wire the output** -- use the wire tool on the generator socket, connect to root combiners, splitters, batteries, etc.
6. **Toggle** -- press E on the FURNACE to start/stop the engine (the furnace no longer opens an inventory; the box is the inventory)

The engine drains water while running (50ml/s by default) -- pipe it in for hands-off operation or swap containers manually.

The engine outputs standard IOEntity power. Connect it to any electrical component: root combiner, splitter, branch, battery, turret, etc.
See [Rustrician power distribution](https://www.rustrician.io/wiki/distribution.html#power-distribution).

## Running it on a server

Everything is server-side -- players need NO client mods, downloads, or skins installed.

### Required
1. A **Rust dedicated server** with [Oxide/uMod](https://umod.org) installed
2. **`SteamEngine.cs`** copied into `oxide/plugins/` (raw source -- Oxide compiles it on load):
   ```bash
   curl -fsSL -o oxide/plugins/SteamEngine.cs \
     https://raw.githubusercontent.com/Kapel/rust_steamengine/main/SteamEngine.cs
   ```
3. That's it. The config auto-generates at `oxide/config/SteamEngine.json` on first load.

### Optional
- **[CustomItemDefinitions](https://lone.design/product/custom-item-definitions/)** (free library by 0xF) in `oxide/plugins/` -- with it, `/steamengine.give` hands out a real `steamengine` custom item (own name/description/itemid); without it, a renamed skinned furnace item that works identically. Load order does not matter (registration retries when the library appears).
- **Workshop skin ID** -- `SkinId` in the config identifies engine furnaces AND skins the item. The default (`2838812890`) works out of the box; change it if you publish your own skin.

### Permissions
| Who | What |
|---|---|
| Server admins (`IsAdmin`) | `/steamengine.give`, `/steamengine.status` (chat) |
| Auth level 2 / RCON | `steamengine.list`, `steamengine.reload` (console) |
| Everyone | Using placed engines (box, hose, wire, toggle) |

## Configuration

All values below are configurable in `oxide/config/SteamEngine.json`. Defaults are tuned so:
- **Cheapest parts** (tier 1) = **40W** (matches small generator)
- **Best parts** (tier 3) = **140W** (just under wind turbine max of 150W)

```json
{
  "Skin ID used to identify Steam Engine furnaces": 2838812890,
  "Power output bonus with charcoal fuel (watts)": 15,
  "Power output bonus with wood fuel (watts)": 0,
  "Minimum power output when running (watts)": 25,
  "Maximum power output cap (watts)": 140,
  "Water consumed per second (ml)": 50,
  "Fuel units consumed per tick": 1.0,
  "Engine tick interval in seconds": 1.0,
  "Car part wear per tick (condition points decreased each tick)": 0.05,
  "Multiply part bonuses (false = additive)": false,
  "Water container item shortnames accepted in the water slot": [
    "waterjug", "smallwaterbottle", "bucket.water", "botabag"
  ],
  "Tier power multipliers (tier number -> bonus added per part)": {
    "1": 1.0,
    "2": 1.2,
    "3": 1.5
  },
  "Car part base shortnames (tier 1,2,3 appended automatically)": [
    "carburetor", "crankshaft", "piston", "sparkplug", "valve"
  ],
  "Child generator offset from furnace center": {"x": 0.9, "y": 0.0, "z": -0.3},
  "Child water barrel offset from furnace center": {"x": -0.9, "y": 0.0, "z": 0.3},
  "Child storage box offset from furnace center": {"x": 0.0, "y": 0.0, "z": 0.85}
}
```

### Power formula

```
power = (BasePower + FuelBonus) * PartBoost
         clamped to [0, MaxPower]

FuelBonus = CharcoalPowerBonus or WoodPowerBonus (whichever is in the furnace)
PartBoost = 1.0 + sum of (TierMultiplier[tier] - 1.0) for each unique part type
            (highest tier per part type wins; duplicates of same type are ignored)
```

### Power scaling by parts

| Parts | Charcoal | Wood |
|---|---|---|
| All T1 | 40W | 25W |
| All T2 | 80W | 50W |
| All T3 | 140W (cap) | 87.5W |
| 2T3 + 3T2 | 104W | 65W |
| 1T3 + 4T1 | 60W | 37.5W |

To change these values, adjust `TierMultipliers` and `MaxPower` in the config.

### Startup requirements

The engine requires **all three** to run:
1. **All 5 car part types** (carburetor, crankshaft, piston, sparkplug, valve -- any quality tier)
2. **Fuel** (wood or charcoal)
3. **Water** (hose into the barrel input, or a jug/bottle/bucket/bota bag with fresh water in the water slot)

Missing any one → engine won't start. During runtime, if any condition drops to zero (fuel exhausted, water drained, a part type fully breaks), the engine stops immediately.

### Car part wear

Car parts have durability (`item.condition`). Each tick while running, condition drops by `PartWearPerTick` (default 0.05/tick, ~33 min lifetime). Broken parts auto-remove. If the last copy of any part type breaks, the engine stops. Replace worn parts to maintain power.

## Wiring & electrical connectivity

The steam engine outputs power through a standard **ElectricGenerator** (test generator) IOEntity socket. Use the Rust **Wire Tool** to:

1. Click the generator output socket (the small generator to the right of the furnace)
2. Click the input of any electrical device: **root combiner**, **splitter**, **branch**, **battery**, **switch**, **turret**, etc.

The generator position offset is configurable. If the socket is hard to reach, adjust `GeneratorOffset` in the config.

## Commands

| Command | Permission | Description |
|---|---|---|
| `/steamengine.give` | admin | Gives the Steam Engine item (custom item when CID is loaded) |
| `/steamengine.status` | admin | Shows active engine count and config summary |
| `steamengine.list` (console) | authlevel 2 / RCON | Per-engine diagnostics: skin, running, fuel/water/parts, child health |
| `steamengine.reload` (console) | authlevel 2 / RCON | Reloads config, re-scans and re-inits all engines |

## Development

- **Smoke compile against real game DLLs** (SteamCMD downloads the server, Oxide refs pulled from the latest release):
  ```bash
  docker build -f Dockerfile.build -t steamengine-build .
  ```
- **Logic test suite** (150 tests, no game DLLs needed):
  ```bash
  docker run --rm -v "$PWD:/src:ro" mono:latest \
    bash -c 'mcs /src/SteamEngine.Tests.cs -out:/tmp/t.exe && mono /tmp/t.exe'
  ```
- The server itself compiles the plugin from source -- deployment is just copying the `.cs` file; no DLL shipping.

### Verify (checklist)
- [ ] `/steamengine.give` gives a furnace item named "Steam Engine"
- [ ] Placing the engine spawns box (front), barrel (left), generator (right) next to the furnace
- [ ] Open the BOX, add wood/charcoal -- lands in slot 1; rejected for other items
- [ ] Add all 5 car part types (any tiers) -- accepted; non-parts rejected
- [ ] Add a water jug/bottle/bucket with fresh water -- accepted; salt water won't run the engine
- [ ] Hose a water pump into the barrel input -- engine runs without a slot container
- [ ] With both sources, barrel drains first, then the slot container
- [ ] With CustomItemDefinitions loaded: `/steamengine.give` gives the `steamengine` custom item
- [ ] Press E on the FURNACE to toggle -- needs all 5 parts + fuel + water to start
- [ ] Missing any part, fuel, or water → doesn't start (chat message shown)
- [ ] Wire from generator output to a root combiner or battery -- power flows
- [ ] Charcoal fuel: 40W (T1) → 140W (T3); wood: 25W → 87.5W
- [ ] Parts degrade over time, break at 0 condition, engine stops if any type lost
- [ ] Fuel exhausted → engine stops next tick
- [ ] Water container drained → engine stops next tick
- [ ] Replace broken part / swap in full water container → engine can restart
- [ ] Kill the furnace → children die with it, box contents drop
- [ ] Server restart → engines re-initialized, auto-started if all conditions met
- [ ] `steamengine.reload` stops all, re-scans, re-inits with new config

## Architecture

```
              ┌───────────────────────────┐
              │  Furnace (BaseOven)       │  ← skinned shell, fire visuals,
              │  E = engine on/off toggle │    inventory unused (auto-migrated)
              └────────────┬──────────────┘
        ┌──────────────────┼──────────────────────┐
  ┌─────┴──────┐    ┌──────┴───────┐    ┌─────────┴───────┐
  │ Water      │    │ Storage box  │    │ Test Generator  │
  │ Barrel     │    │ (7 slots)    │    │ (wire output)   │
  │ hose input │    │ 0: fuel      │    │ 25-140W         │
  │ drains 1st │    │ 1-5: parts   │    └─────────────────┘
  └────────────┘    │ 6: water     │
                    └──────────────┘
```

All three children are REAL vanilla entities parented to the furnace — the box
gives the native generic loot UI (no furnace slot-typing to fight), the barrel
gives a native hose socket, the generator a native wire socket. Children
persist in the save (wiring, hoses, stored items and water survive restarts)
and die with the furnace (box contents drop). Slot typing is enforced
server-side; shift-clicked items are relocated to their dedicated slot or
bounced out. If [CustomItemDefinitions](https://lone.design/product/custom-item-definitions/)
is loaded, a real `steamengine` item is registered and given out by
`/steamengine.give`; otherwise a renamed skinned furnace item is used.

