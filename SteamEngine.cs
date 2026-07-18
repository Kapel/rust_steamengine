using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SteamEngine", "Kapel", "2.0.1")]
    [Description("Composite steam engine: furnace shell + parts/fuel box + hoseable barrel + wireable generator")]
    public class SteamEngine : RustPlugin
    {
        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty("Skin ID used to identify Steam Engine furnaces")]
            public ulong SkinId = 2838812890UL;

            [JsonProperty("Power output bonus with charcoal fuel (watts)")]
            public float CharcoalPowerBonus = 15f;

            [JsonProperty("Power output bonus with wood fuel (watts)")]
            public float WoodPowerBonus = 0f;

            [JsonProperty("Minimum power output when running (watts)")]
            public float BasePower = 25f;

            [JsonProperty("Maximum power output cap (watts)")]
            public float MaxPower = 140f;

            [JsonProperty("Water consumed per second (ml)")]
            public float WaterPerSecond = 50f;

            [JsonProperty("Fuel units consumed per tick")]
            public float FuelPerTick = 1f;

            [JsonProperty("Engine tick interval in seconds")]
            public float TickInterval = 1f;

            [JsonProperty("Car part wear per tick (condition points decreased each tick)")]
            public float PartWearPerTick = 0.05f;

            [JsonProperty("Multiply part bonuses (false = additive)")]
            public bool MultiplyPartBonuses = false;

            [JsonProperty("Tier power multipliers (tier number -> bonus added per part)")]
            public Dictionary<string, float> TierMultipliers = new Dictionary<string, float>
            {
                ["1"] = 1.0f,
                ["2"] = 1.2f,
                ["3"] = 1.5f,
            };

            [JsonProperty("Car part base shortnames (tier 1,2,3 appended automatically)")]
            public string[] CarPartBaseNames = new string[]
            {
                "carburetor",
                "crankshaft",
                "piston",
                "sparkplug",
                "valve",
            };

            [JsonProperty("Child generator offset from furnace center")]
            public Vector3Config GeneratorOffset = new Vector3Config(0.9f, 0f, -0.3f);

            [JsonProperty("Child water barrel offset from furnace center")]
            public Vector3Config WaterBarrelOffset = new Vector3Config(-0.9f, 0f, 0.3f);

            [JsonProperty("Child storage box offset from furnace center")]
            public Vector3Config StorageOffset = new Vector3Config(0f, 0f, 0.85f);

            [JsonProperty("Water container item shortnames accepted in the water slot")]
            public string[] WaterContainerShortnames = new string[]
            {
                "waterjug",
                "smallwaterbottle",
                "bucket.water",
                "botabag",
            };
        }

        private class Vector3Config
        {
            public float x, y, z;
            public Vector3Config() { x = 0; y = 0; z = 0; }
            public Vector3Config(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                    throw new Exception("Config deserialized to null");
                ValidateConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Configuration error: {ex.Message}");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        private void ValidateConfig()
        {
            if (_config.SkinId == 0)
                PrintWarning("SkinId is 0 -- Steam Engine will match ALL furnaces!");
            if (_config.TickInterval <= 0f)
                _config.TickInterval = 1f;
            if (_config.FuelPerTick <= 0f)
                _config.FuelPerTick = 1f;
            if (_config.WaterPerSecond < 0f)
                _config.WaterPerSecond = 50f;
            if (_config.BasePower < 0f)
                _config.BasePower = 0f;
            if (_config.MaxPower <= 0f)
                _config.MaxPower = 100f;
            if (_config.CharcoalPowerBonus < 0f)
                _config.CharcoalPowerBonus = 0f;
            if (_config.WoodPowerBonus < 0f)
                _config.WoodPowerBonus = 0f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Constants

        private const string GeneratorPrefab =
            "assets/prefabs/deployable/playerioents/generators/generator.small.prefab";

        private const string WaterBarrelPrefab =
            "assets/prefabs/deployable/liquidbarrel/waterbarrel.prefab";

        private const string StorageBoxPrefab =
            "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";

        private const int FuelSlot = 0;
        private const int FirstPartSlot = 1;
        private const int LastPartSlot = 5;
        private const int WaterSlot = 6;
        private const int EngineCapacity = 7;
        private const string FreshWaterShortname = "water";

        private static readonly HashSet<string> AllowedFuel = new HashSet<string>
        {
            "wood",
            "charcoal",
        };

        #endregion

        #region Runtime State

        private readonly Dictionary<NetworkableId, EngineInstance> _engines = new Dictionary<NetworkableId, EngineInstance>();
        private readonly Dictionary<NetworkableId, EngineInstance> _storageIndex = new Dictionary<NetworkableId, EngineInstance>();
        private readonly HashSet<ulong> _hintedPlayers = new HashSet<ulong>();
        private HashSet<string> _carPartShortnames;
        private HashSet<string> _waterContainerShortnames;
        private FieldInfo _genEnergyField;

        private class EngineInstance
        {
            public BaseOven Oven;
            public ElectricGenerator Generator;
            public LiquidContainer WaterContainer;
            public StorageContainer Storage;
            public Timer TickTimer;
            public bool Running;
        }

        private ItemContainer EngineInventory(EngineInstance instance)
        {
            var storage = instance?.Storage;
            if (storage == null || storage.IsDestroyed)
                return null;
            return storage.inventory;
        }

        // permanent = engine entity destroyed: children die with it, box contents
        // drop. Soft (plugin unload/reload) keeps all children alive so player
        // wiring, hoses, stored parts and water survive.
        private void TeardownEngine(EngineInstance instance, bool permanent)
        {
            instance.TickTimer?.Destroy();
            instance.TickTimer = null;
            instance.Running = false;

            SetGeneratorOutput(instance.Generator, 0f);

            if (instance.Storage?.net != null)
                _storageIndex.Remove(instance.Storage.net.ID);

            if (permanent)
            {
                if (instance.Storage != null && !instance.Storage.IsDestroyed)
                {
                    var inv = instance.Storage.inventory;
                    if (inv != null && inv.itemList != null && inv.itemList.Count > 0)
                        DropUtil.DropItems(inv, instance.Storage.transform.position + Vector3.up * 0.5f);
                    instance.Storage.Kill();
                }
                if (instance.WaterContainer != null && !instance.WaterContainer.IsDestroyed)
                    instance.WaterContainer.Kill();
                if (instance.Generator != null && !instance.Generator.IsDestroyed)
                    instance.Generator.Kill();
                instance.Storage = null;
                instance.WaterContainer = null;
                instance.Generator = null;
            }

            if (instance.Oven != null && !instance.Oven.IsDestroyed)
            {
                instance.Oven.SetFlagLocal(BaseEntity.Flags.On, false);
                instance.Oven.SendNetworkUpdate();
            }
        }

        #endregion

        #region Initialization

        private void Init()
        {
            _carPartShortnames = new HashSet<string>();
            foreach (var name in _config.CarPartBaseNames)
                for (int t = 1; t <= 3; t++)
                    _carPartShortnames.Add(name + t);

            _waterContainerShortnames = new HashSet<string>(
                _config.WaterContainerShortnames ?? new string[0]);

            _genEnergyField = typeof(ElectricGenerator).GetField("electricAmount",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? typeof(IOEntity).GetField("currentEnergy",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (_genEnergyField == null)
                PrintWarning("Could not find generator energy field -- power output will not work!");
        }

        private Timer _initTimer;
        private int _initAttempts;

        private void OnServerInitialized()
        {
            ScheduleInitScan(5f);
        }

        private void ScheduleInitScan(float delay)
        {
            _initTimer?.Destroy();
            _initTimer = timer.Once(delay, () =>
            {
                // Entity streaming can still be in flight right after server
                // init; an entity spawning mid-enumeration aborts the scan.
                // Retry with backoff instead of silently losing the boot scan.
                try
                {
                    TryRegisterCustomItem();

                    foreach (var entity in BaseNetworkable.serverEntities.ToList())
                    {
                        var oven = entity as BaseOven;
                        if (oven == null || oven.skinID != _config.SkinId)
                            continue;
                        InitEngine(oven);
                    }

                    var resumed = 0;
                    foreach (var instance in _engines.Values)
                    {
                        if (!instance.Running && TryStartEngine(instance))
                            resumed++;
                    }

                    Puts($"SteamEngine: {_engines.Count} engine(s) found, {resumed} auto-started.");
                }
                catch (Exception ex)
                {
                    _initAttempts++;
                    if (_initAttempts <= 5)
                    {
                        PrintWarning($"Init scan failed (attempt {_initAttempts}/5): {ex.Message} -- retrying in 10s.");
                        ScheduleInitScan(10f);
                    }
                    else
                    {
                        PrintError($"Init scan failed permanently: {ex}");
                    }
                }
            });
        }

        private void Unload()
        {
            _initTimer?.Destroy();
            foreach (var instance in _engines.Values.ToList())
                TeardownEngine(instance, permanent: false);
            _engines.Clear();
            _storageIndex.Clear();

            UnregisterCustomItem();
        }

        #endregion

        #region Custom Item (CustomItemDefinitions integration)

        [PluginReference]
        private Plugin CustomItemDefinitions;

        private ItemDefinition _customItemDef;
        private const string CustomItemShortname = "steamengine";

        private void TryRegisterCustomItem()
        {
            if (_customItemDef != null)
                return;

            if (CustomItemDefinitions == null || !CustomItemDefinitions.IsLoaded)
                return;

            var furnace = ItemManager.FindItemDefinition("furnace");
            if (furnace == null)
                return;

            try
            {
                var result = CustomItemDefinitions.Call("Register", new
                {
                    shortname = CustomItemShortname,
                    parentItemId = furnace.itemid,
                    defaultName = "Steam Engine",
                    defaultDescription = "A steam-powered generator. Feed it wood or charcoal, keep the water topped up, and fit all five car parts to spin the dynamo.",
                    defaultSkinId = _config.SkinId,
                    maxStackSize = 1,
                    category = ItemCategory.Electrical,
                    itemMods = furnace.itemMods,
                }, this);

                _customItemDef = result as ItemDefinition;
                if (_customItemDef != null)
                    Puts($"Registered custom item '{CustomItemShortname}' (itemid {_customItemDef.itemid}) via CustomItemDefinitions.");
                else
                    PrintWarning("CustomItemDefinitions.Register returned no definition -- falling back to skinned furnace item.");
            }
            catch (Exception ex)
            {
                PrintWarning($"Failed to register custom item via CustomItemDefinitions: {ex.Message} -- falling back to skinned furnace item.");
            }
        }

        private void UnregisterCustomItem()
        {
            if (_customItemDef == null)
                return;

            try
            {
                if (CustomItemDefinitions != null && CustomItemDefinitions.IsLoaded)
                    CustomItemDefinitions.Call("UnregisterAll", this);
            }
            catch (Exception ex)
            {
                PrintWarning($"Failed to unregister custom item: {ex.Message}");
            }
            _customItemDef = null;
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "CustomItemDefinitions")
                NextTick(TryRegisterCustomItem);
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == "CustomItemDefinitions")
                _customItemDef = null;
        }

        #endregion

        #region Entity Lifecycle Hooks

        private bool IsEngineOven(BaseOven oven)
        {
            if (oven == null || oven.net == null)
                return false;
            return oven.skinID == _config.SkinId || _engines.ContainsKey(oven.net.ID);
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            var oven = entity as BaseOven;
            if (oven == null || oven.skinID != _config.SkinId)
                return;

            InitEngine(oven);
        }

        private void OnItemDeployed(Deployer deployer, BaseEntity entity, BaseEntity slot)
        {
            var oven = entity as BaseOven;
            if (oven == null)
                return;

            Item sourceItem = null;
            try { sourceItem = deployer?.GetOwnerPlayer()?.GetActiveItem(); } catch { }

            var ours = oven.skinID == _config.SkinId;
            if (!ours && sourceItem != null &&
                (sourceItem.skin == _config.SkinId ||
                 string.Equals(sourceItem.info?.shortname, CustomItemShortname, StringComparison.OrdinalIgnoreCase)))
                ours = true;

            if (!ours)
                return;

            // Deployment does not always carry the skin from custom items --
            // normalize it so every other skinID-based check keeps working.
            if (oven.skinID != _config.SkinId)
            {
                oven.skinID = _config.SkinId;
                oven.SendNetworkUpdate();
            }

            InitEngine(oven);
            Puts($"Steam engine deployed at {oven.transform.position} (net {oven.net.ID}).");
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var ent = entity as BaseEntity;
            if (ent?.net == null)
                return;

            if (ent is BaseOven oven && _engines.TryGetValue(oven.net.ID, out var instance))
            {
                TeardownEngine(instance, permanent: true);
                _engines.Remove(oven.net.ID);
                return;
            }

            // Storage box destroyed on its own (raid damage): stop the engine,
            // it can no longer hold parts/fuel.
            if (_storageIndex.TryGetValue(ent.net.ID, out var boxOwner))
            {
                _storageIndex.Remove(ent.net.ID);
                boxOwner.Storage = null;
                StopEngine(boxOwner);
            }
        }

        // The organs are part of the machine -- no picking them up individually.
        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity == null)
                return null;
            var parentOven = entity.GetParentEntity() as BaseOven;
            if (parentOven != null && IsEngineOven(parentOven))
                return false;
            return null;
        }

        #endregion

        #region Engine Initialization

        private void InitEngine(BaseOven oven)
        {
            if (oven == null || oven.IsDestroyed)
                return;

            var netId = oven.net.ID;
            if (_engines.ContainsKey(netId))
                return;

            var instance = new EngineInstance { Oven = oven };

            instance.Generator = FindOrCreateChild<ElectricGenerator>(
                oven, GeneratorPrefab, _config.GeneratorOffset.ToVector3());

            instance.WaterContainer = FindOrCreateChild<LiquidContainer>(
                oven, WaterBarrelPrefab, _config.WaterBarrelOffset.ToVector3());

            instance.Storage = FindOrCreateChild<StorageContainer>(
                oven, StorageBoxPrefab, _config.StorageOffset.ToVector3());

            if (instance.Generator == null || instance.Storage == null)
            {
                PrintError($"SteamEngine: failed to spawn children for oven {netId} " +
                           $"(generator={(instance.Generator != null)}, storage={(instance.Storage != null)})");
                return;
            }

            var boxInv = instance.Storage.inventory;
            if (boxInv != null && boxInv.capacity != EngineCapacity)
            {
                boxInv.capacity = EngineCapacity;
                instance.Storage.SendNetworkUpdate();
            }

            _engines[netId] = instance;
            _storageIndex[instance.Storage.net.ID] = instance;

            MigrateOvenInventory(oven, instance);

            if (oven.IsOn())
            {
                oven.StopCooking();
                oven.SetFlagLocal(BaseEntity.Flags.On, false);
                oven.SendNetworkUpdate();
            }
        }

        // Pre-2.0 engines kept fuel/parts/water in the furnace inventory; move
        // everything into the storage box (dedicated slots when possible).
        private void MigrateOvenInventory(BaseOven oven, EngineInstance instance)
        {
            var ovenInv = oven.inventory;
            var boxInv = EngineInventory(instance);
            if (ovenInv == null || boxInv == null)
                return;

            for (int i = ovenInv.capacity - 1; i >= 0; i--)
            {
                var item = ovenInv.GetSlot(i);
                if (item == null)
                    continue;

                var slot = FindDedicatedSlot(boxInv, item);
                if (slot >= 0 && item.MoveToContainer(boxInv, slot))
                    continue;
                if (item.MoveToContainer(boxInv))
                    continue;
                item.Drop(oven.transform.position + Vector3.up, Vector3.up);
            }
        }

        private T FindOrCreateChild<T>(BaseOven parent, string prefabPath, Vector3 offset)
            where T : BaseEntity
        {
            if (parent.children != null)
            {
                foreach (var child in parent.children)
                {
                    if (child is T typed && !typed.IsDestroyed && typed.PrefabName == prefabPath)
                    {
                        // Snap pre-existing children to the configured offset so
                        // offset changes take effect on reload, not only for
                        // freshly placed engines.
                        if (Vector3.Distance(typed.transform.localPosition, offset) > 0.01f)
                        {
                            typed.transform.localPosition = offset;
                            typed.SendNetworkUpdate();
                        }
                        return typed;
                    }
                }
            }

            return CreateChild<T>(parent, prefabPath, offset);
        }

        private T CreateChild<T>(BaseOven parent, string prefabPath, Vector3 offset)
            where T : BaseEntity
        {
            var child = GameManager.server.CreateEntity(
                prefabPath, parent.transform.position, parent.transform.rotation) as T;
            if (child == null)
            {
                PrintError($"SteamEngine: CreateEntity returned null for {prefabPath}");
                return null;
            }

            // SetParent(worldPositionStays: false) treats the CURRENT transform
            // values as local coordinates -- so the local offset must be applied
            // AFTER parenting. (Pre-2.0 passed world coordinates here, which
            // catapulted the children ~1km away from the furnace.)
            child.SetParent(parent, worldPositionStays: false);
            child.transform.localPosition = offset;
            child.transform.localRotation = Quaternion.identity;

            // Children persist in the save so wiring, hose connections, stored
            // parts/fuel and barrel water survive restarts. They die with the
            // furnace (vanilla child cascade + OnEntityKill cleanup).
            child.enableSaving = true;
            child.Spawn();
            child.SendNetworkUpdate();

            StripUnwantedComponents(child);

            return child;
        }

        private static void StripUnwantedComponents(BaseEntity entity)
        {
            try
            {
                var destroyOnGround = entity.GetComponent<DestroyOnGroundMissing>();
                if (destroyOnGround != null)
                    UnityEngine.Object.DestroyImmediate(destroyOnGround);
            }
            catch { }

            try
            {
                var groundWatch = entity.GetComponent<GroundWatch>();
                if (groundWatch != null)
                    UnityEngine.Object.DestroyImmediate(groundWatch);
            }
            catch { }

            try
            {
                if (entity.transform.parent != null)
                {
                    var rb = entity.GetComponent<Rigidbody>();
                    if (rb != null)
                        UnityEngine.Object.DestroyImmediate(rb);
                }
            }
            catch { }
        }

        #endregion

        #region Inventory Hooks

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (container == null || container.net == null)
                return null;

            // Storage box: native generic loot UI, plus a one-time layout hint.
            if (_storageIndex.ContainsKey(container.net.ID))
            {
                if (player != null && _hintedPlayers.Add(player.userID))
                    PrintToChat(player,
                        "Steam Engine box — slot 1: fuel (wood/charcoal), slots 2-6: car parts, slot 7: water container. " +
                        "Hose the barrel for piped water, wire the generator for power, press E on the furnace to start/stop.");
                return null;
            }

            // The furnace itself is just the on/off switch: pressing E toggles
            // the engine instead of opening the (unused) furnace inventory.
            var oven = container as BaseOven;
            if (!IsEngineOven(oven))
                return null;

            if (!_engines.TryGetValue(oven.net.ID, out var instance))
            {
                InitEngine(oven);
                _engines.TryGetValue(oven.net.ID, out instance);
            }

            if (instance != null && player != null)
            {
                if (instance.Running)
                {
                    StopEngine(instance);
                    PrintToChat(player, "Steam engine stopped.");
                }
                else if (TryStartEngine(instance))
                {
                    PrintToChat(player, $"Steam engine started — output {CalculatePower(instance):0}W.");
                }
                else
                {
                    PrintToChat(player, "Steam engine needs all 5 car parts, fuel (wood/charcoal), and fresh water (hose or container) in the box.");
                }
            }

            return false;
        }

        private bool IsAllowedInEngine(string shortname)
        {
            return AllowedFuel.Contains(shortname)
                || _carPartShortnames.Contains(shortname)
                || _waterContainerShortnames.Contains(shortname);
        }

        private bool IsCorrectSlot(int position, string shortname)
        {
            if (AllowedFuel.Contains(shortname))
                return position == FuelSlot;
            if (_carPartShortnames.Contains(shortname))
                return position >= FirstPartSlot && position <= LastPartSlot;
            if (_waterContainerShortnames.Contains(shortname))
                return position == WaterSlot;
            return false;
        }

        private bool SlotAvailableFor(ItemContainer inv, int slot, Item item)
        {
            var existing = inv.GetSlot(slot);
            if (existing == null || existing == item)
                return true;
            return existing.info == item.info && existing.amount < existing.MaxStackable();
        }

        private int FindDedicatedSlot(ItemContainer inv, Item item)
        {
            var shortname = item.info.shortname;

            if (AllowedFuel.Contains(shortname))
                return SlotAvailableFor(inv, FuelSlot, item) ? FuelSlot : -1;

            if (_waterContainerShortnames.Contains(shortname))
                return SlotAvailableFor(inv, WaterSlot, item) ? WaterSlot : -1;

            if (_carPartShortnames.Contains(shortname))
            {
                for (int i = FirstPartSlot; i <= LastPartSlot; i++)
                    if (SlotAvailableFor(inv, i, item))
                        return i;
            }

            return -1;
        }

        private EngineInstance StorageEngineOf(ItemContainer container)
        {
            var owner = container?.entityOwner;
            if (owner?.net == null)
                return null;
            return _storageIndex.TryGetValue(owner.net.ID, out var instance) ? instance : null;
        }

        private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {
            if (StorageEngineOf(container) == null)
                return null;

            var shortname = item.info.shortname;

            if (targetPos == FuelSlot)
                return AllowedFuel.Contains(shortname);

            if (targetPos >= FirstPartSlot && targetPos <= LastPartSlot)
                return _carPartShortnames.Contains(shortname);

            if (targetPos == WaterSlot)
                return _waterContainerShortnames.Contains(shortname);

            // Auto-placement (shift-click): only when a dedicated slot is free.
            return FindDedicatedSlot(container, item) >= 0;
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            var engine = StorageEngineOf(container);
            if (engine == null)
                return;

            if (IsCorrectSlot(item.position, item.info.shortname))
                return;

            // Shift-clicked items land in the first free slot; relocate them to
            // their dedicated slot so the layout stays fuel/parts/water.
            var target = FindDedicatedSlot(container, item);
            if (target >= 0 && container.GetSlot(target) == null)
            {
                item.position = target;
                item.MarkDirty();
                container.MarkDirty();
                return;
            }

            // No dedicated slot free: bounce the item out instead of letting it
            // squat in a foreign slot.
            NextTick(() =>
            {
                if (item == null || item.parent != container)
                    return;
                if (IsCorrectSlot(item.position, item.info.shortname))
                    return;
                var storage = engine.Storage;
                var dropAt = storage != null && !storage.IsDestroyed
                    ? storage.transform.position + Vector3.up * 1.2f
                    : item.GetOwnerPlayer()?.transform.position ?? Vector3.zero;
                item.Drop(dropAt, Vector3.up);
            });
        }

        private object CanMoveItem(Item item, PlayerInventory inventory,
            uint targetContainerId, int targetSlot, int amount)
        {
            if (item == null)
                return null;

            var rootContainer = item.GetRootContainer();
            if (rootContainer == null)
                return null;

            if (StorageEngineOf(rootContainer) == null)
                return null;

            if (IsAllowedInEngine(item.info.shortname))
                return null;

            return false;
        }

        #endregion

        #region Oven Toggle

        private object OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (oven == null || oven.skinID != _config.SkinId)
                return null;

            if (!_engines.TryGetValue(oven.net.ID, out var instance))
                return null;

            if (instance.Running)
            {
                StopEngine(instance);
            }
            else
            {
                if (!TryStartEngine(instance) && player != null)
                    PrintToChat(player, "Steam engine needs all 5 car parts, fuel (wood/charcoal), and fresh water (hose it in or drop a filled water container in the inventory).");
            }

            return false;
        }

        #endregion

        #region Fuel Control

        private object OnOvenStart(BaseOven oven)
        {
            if (oven == null || oven.skinID != _config.SkinId)
                return null;

            return false;
        }

        private object OnFuelConsume(BaseOven oven, Item fuel, ItemModBurnable burnable)
        {
            if (oven == null || oven.skinID != _config.SkinId)
                return null;

            return true;
        }

        private object OnOvenCook(BaseOven oven, Item item, BasePlayer player)
        {
            if (oven == null || oven.skinID != _config.SkinId)
                return null;

            return false;
        }

        #endregion

        #region Engine Control

        private bool TryStartEngine(EngineInstance instance)
        {
            if (instance.Running)
                return true;

            if (instance.Oven == null || instance.Oven.IsDestroyed)
                return false;

            if (EngineInventory(instance) == null)
                return false;

            if (GetFuelAmount(instance) <= 0)
                return false;

            if (GetWaterAmount(instance) <= 0)
                return false;

            if (!HasAllParts(instance))
                return false;

            instance.Running = true;
            instance.Oven.SetFlagLocal(BaseEntity.Flags.On, true);
            instance.Oven.SendNetworkUpdate();

            instance.TickTimer = timer.Every(_config.TickInterval, () => EngineTick(instance));

            return true;
        }

        private void StopEngine(EngineInstance instance)
        {
            instance.Running = false;
            instance.TickTimer?.Destroy();
            instance.TickTimer = null;

            if (instance.Oven != null && !instance.Oven.IsDestroyed)
            {
                instance.Oven.SetFlagLocal(BaseEntity.Flags.On, false);
                instance.Oven.SendNetworkUpdate();
            }

            SetGeneratorOutput(instance.Generator, 0f);
        }

        private void EngineTick(EngineInstance instance)
        {
            if (!instance.Running)
                return;

            if (instance.Oven == null || instance.Oven.IsDestroyed)
            {
                var deadNetId = instance.Oven?.net.ID ?? default(NetworkableId);
                TeardownEngine(instance, permanent: true);
                if (!deadNetId.Equals(default(NetworkableId)))
                    _engines.Remove(deadNetId);
                return;
            }

            if (GetFuelAmount(instance) <= 0)
            {
                StopEngine(instance);
                return;
            }

            if (GetWaterAmount(instance) <= 0)
            {
                StopEngine(instance);
                return;
            }

            ConsumeFuel(instance);
            ConsumeWater(instance);
            DegradeParts(instance);

            if (!HasAllParts(instance))
            {
                StopEngine(instance);
                return;
            }

            var powerOutput = CalculatePower(instance);
            SetGeneratorOutput(instance.Generator, powerOutput);
        }

        #endregion

        #region Fuel / Water / Power Logic

        private bool TryRemoveItem(Item item)
        {
            try { item.Remove(); return true; }
            catch (Exception ex) { Interface.Oxide.LogError($"SteamEngine: item.Remove failed: {ex.Message}"); return false; }
        }

        private bool TryConsumeItem(Item item, int amount)
        {
            try { item.amount -= amount; item.MarkDirty(); return true; }
            catch (Exception ex) { Interface.Oxide.LogError($"SteamEngine: item consume failed: {ex.Message}"); return false; }
        }

        private bool HasAllParts(EngineInstance instance)
        {
            var inv = EngineInventory(instance);
            if (inv == null) return false;

            var found = new HashSet<string>();
            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null) continue;
                if (IsCarPart(item.info.shortname))
                {
                    foreach (var name in _config.CarPartBaseNames)
                    {
                        if (item.info.shortname.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(name);
                            break;
                        }
                    }
                }
            }

            foreach (var name in _config.CarPartBaseNames)
                if (!found.Contains(name))
                    return false;

            return true;
        }

        private int GetFuelAmount(EngineInstance instance)
        {
            var inv = EngineInventory(instance);
            if (inv == null) return 0;

            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null)
                    continue;
                if (AllowedFuel.Contains(item.info.shortname))
                    return item.amount;
            }
            return 0;
        }

        private Item FindBarrelWaterItem(EngineInstance instance)
        {
            if (instance.WaterContainer == null || instance.WaterContainer.IsDestroyed)
                return null;

            var liquid = instance.WaterContainer.inventory?.GetSlot(0);
            if (liquid != null && liquid.info.shortname == FreshWaterShortname && liquid.amount > 0)
                return liquid;
            return null;
        }

        private Item FindSlotWaterItem(EngineInstance instance)
        {
            var inv = EngineInventory(instance);
            if (inv == null) return null;

            for (int i = 0; i < inv.capacity; i++)
            {
                var vessel = inv.GetSlot(i);
                if (vessel == null || !_waterContainerShortnames.Contains(vessel.info.shortname))
                    continue;

                var liquid = vessel.contents?.GetSlot(0);
                if (liquid != null && liquid.info.shortname == FreshWaterShortname && liquid.amount > 0)
                    return liquid;
            }
            return null;
        }

        private int GetWaterAmount(EngineInstance instance)
        {
            var total = 0;
            total += FindBarrelWaterItem(instance)?.amount ?? 0;
            total += FindSlotWaterItem(instance)?.amount ?? 0;
            return total;
        }

        private void ConsumeFuel(EngineInstance instance)
        {
            var inv = EngineInventory(instance);
            if (inv == null) return;

            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null || !AllowedFuel.Contains(item.info.shortname))
                    continue;

                float toConsume = _config.FuelPerTick;
                if (toConsume >= item.amount)
                    TryRemoveItem(item);
                else
                    TryConsumeItem(item, Mathf.CeilToInt(toConsume));
                inv.MarkDirty();
                return;
            }
        }

        private void ConsumeWater(EngineInstance instance)
        {
            var toConsume = (int)(_config.WaterPerSecond * _config.TickInterval);
            if (toConsume <= 0)
                return;

            toConsume -= DrainWaterItem(FindBarrelWaterItem(instance), toConsume);

            if (toConsume > 0)
            {
                DrainWaterItem(FindSlotWaterItem(instance), toConsume);
                EngineInventory(instance)?.MarkDirty();
            }
        }

        private int DrainWaterItem(Item waterItem, int amount)
        {
            if (waterItem == null || amount <= 0)
                return 0;

            if (amount >= waterItem.amount)
            {
                var drained = waterItem.amount;
                TryRemoveItem(waterItem);
                return drained;
            }

            TryConsumeItem(waterItem, amount);
            return amount;
        }

        private bool IsCarPart(string shortname)
        {
            foreach (var name in _config.CarPartBaseNames)
            {
                if (shortname.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = shortname.Substring(name.Length);
                    if (suffix == "1" || suffix == "2" || suffix == "3")
                        return true;
                }
            }
            return false;
        }

        private void DegradeParts(EngineInstance instance)
        {
            if (_config.PartWearPerTick <= 0f) return;

            var inv = EngineInventory(instance);
            if (inv == null) return;

            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null) continue;
                if (!IsCarPart(item.info.shortname)) continue;
                if (!item.hasCondition) continue;

                item.condition -= _config.PartWearPerTick;
                if (item.condition <= 0f)
                    TryRemoveItem(item);
                else
                    item.MarkDirty();
            }
        }

        private float CalculatePower(EngineInstance instance)
        {
            var inv = EngineInventory(instance);
            if (inv == null) return _config.BasePower;

            float power = _config.BasePower;

            float fuelBonus = 0f;
            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null || !AllowedFuel.Contains(item.info.shortname))
                    continue;

                if (item.info.shortname == "charcoal")
                    fuelBonus = _config.CharcoalPowerBonus;
                else if (item.info.shortname == "wood")
                    fuelBonus = _config.WoodPowerBonus;
                break;
            }
            power += fuelBonus;

            var bestTierPerPart = new Dictionary<string, float>();
            var partBonuses = _config.TierMultipliers;

            for (int i = 0; i < inv.capacity; i++)
            {
                var item = inv.GetSlot(i);
                if (item == null)
                    continue;

                var shortname = item.info.shortname;
                string baseName = null;
                string tier = null;

                foreach (var name in _config.CarPartBaseNames)
                {
                    if (shortname.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    {
                        var suffix = shortname.Substring(name.Length);
                        if (suffix == "1" || suffix == "2" || suffix == "3")
                        {
                            baseName = name;
                            tier = suffix;
                        }
                        break;
                    }
                }

                if (baseName == null || tier == null || !partBonuses.TryGetValue(tier, out var mult))
                    continue;

                if (!bestTierPerPart.TryGetValue(baseName, out var existing) || mult > existing)
                    bestTierPerPart[baseName] = mult;
            }

            var partMultiplier = 1f;
            foreach (var mult in bestTierPerPart.Values)
            {
                if (_config.MultiplyPartBonuses)
                    partMultiplier *= mult;
                else
                    partMultiplier += (mult - 1f);
            }

            power *= partMultiplier;
            return Mathf.Clamp(power, 0f, _config.MaxPower);
        }

        private void SetGeneratorOutput(ElectricGenerator generator, float amount)
        {
            if (generator == null || generator.IsDestroyed || _genEnergyField == null)
                return;

            var intAmount = Mathf.RoundToInt(amount);

            try
            {
                _genEnergyField.SetValue(generator, intAmount);
                generator.MarkDirtyForceUpdateOutputs();
                generator.SendNetworkUpdate();
            }
            catch (Exception ex)
            {
                Interface.Oxide.LogError($"SteamEngine: failed to set generator output: {ex.Message}");
            }
        }

        #endregion

        #region Commands

        [ChatCommand("steamengine.give")]
        private void CmdGive(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, "SteamEngine: admin only.");
                return;
            }

            if (_customItemDef == null)
                TryRegisterCustomItem();

            Item item;
            if (_customItemDef != null)
            {
                item = ItemManager.Create(_customItemDef, 1, _config.SkinId);
            }
            else
            {
                item = ItemManager.CreateByName("furnace", 1, _config.SkinId);
                if (item != null)
                    item.name = "Steam Engine";
            }

            if (item == null)
            {
                SendReply(player, "SteamEngine: failed to create item.");
                return;
            }

            player.GiveItem(item);
            var kind = _customItemDef != null ? $"custom item '{CustomItemShortname}'" : "skinned furnace";
            SendReply(player, $"SteamEngine: given ({kind}, skin={_config.SkinId}). Place as normal furnace.");
        }

        [ChatCommand("steamengine.status")]
        private void CmdStatus(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, "SteamEngine: admin only.");
                return;
            }

            SendReply(player, $"SteamEngine v{Version} — {_engines.Count} engine(s) active.");
            SendReply(player, $"SkinId: {_config.SkinId}, BasePower: {_config.BasePower}, MaxPower: {_config.MaxPower}");
            SendReply(player, $"Fuel types: {string.Join(", ", AllowedFuel)}");
            SendReply(player, $"Car parts: {string.Join(", ", _config.CarPartBaseNames)} (tiers 1-3)");
            SendReply(player, $"Water containers: {string.Join(", ", _config.WaterContainerShortnames)} (or hose into the barrel)");
            SendReply(player, $"Water: {_config.WaterPerSecond} ml/s, Fuel: {_config.FuelPerTick}/tick");
        }

        [ConsoleCommand("steamengine.list")]
        private void CmdConsoleList(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon && arg.Connection != null && arg.Connection.authLevel < 2)
            {
                arg.ReplyWith("SteamEngine: auth level 2 required.");
                return;
            }

            var lines = new List<string>();
            var scanned = 0;
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var oven = entity as BaseOven;
                if (oven == null) continue;
                scanned++;
                var tracked = _engines.TryGetValue(oven.net.ID, out var inst);
                if (oven.skinID != _config.SkinId && !tracked) continue;

                lines.Add(
                    $"net={oven.net.ID} skin={oven.skinID} pos={oven.transform.position} " +
                    $"tracked={tracked} running={(tracked && inst.Running)} " +
                    $"fuel={(tracked ? GetFuelAmount(inst) : -1)} water={(tracked ? GetWaterAmount(inst) : -1)} " +
                    $"parts={(tracked && HasAllParts(inst))} " +
                    $"box={(tracked && inst.Storage != null && !inst.Storage.IsDestroyed ? inst.Storage.net.ID.ToString() : "MISSING")} " +
                    $"barrel={(tracked && inst.WaterContainer != null && !inst.WaterContainer.IsDestroyed)} " +
                    $"gen={(tracked && inst.Generator != null && !inst.Generator.IsDestroyed)}");
            }
            lines.Add($"custom item: {(_customItemDef != null ? $"registered (itemid {_customItemDef.itemid})" : "NOT registered")}");
            lines.Add($"engines tracked: {_engines.Count} (scanned {scanned} ovens)");
            arg.ReplyWith(string.Join("\n", lines));
        }

        [ConsoleCommand("steamengine.reload")]
        private void CmdConsoleReload(ConsoleSystem.Arg arg)
        {
            if (!arg.IsRcon && arg.Connection != null && arg.Connection.authLevel < 2)
            {
                arg.ReplyWith("SteamEngine: auth level 2 required.");
                return;
            }

            foreach (var instance in _engines.Values.ToList())
                StopEngine(instance);
            _engines.Clear();
            _storageIndex.Clear();

            LoadConfig();
            Init();

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var oven = entity as BaseOven;
                if (oven == null || oven.skinID != _config.SkinId)
                    continue;
                InitEngine(oven);
            }

            var resumed = 0;
            foreach (var instance in _engines.Values)
            {
                if (TryStartEngine(instance))
                    resumed++;
            }

            Puts($"SteamEngine config reloaded. {_engines.Count} engines found, {resumed} auto-started.");
            arg.ReplyWith($"SteamEngine config reloaded. {_engines.Count} engines found, {resumed} auto-started.");
        }

        #endregion
    }
}
