using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SpaceCraft;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Unity.Netcode;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Reflection.Emit;
 
namespace SimpleStacking;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "local.theplanetcrafter.simplesting.v2";
    public const string PluginName = "Simple Stacking";
    public const string PluginVersion = "1.1.5";

    private static readonly HashSet<int> AllowedContainerInventories = new();
    private static readonly string[] DefaultStorageNeedles = { "container", "storage", "locker", "chest" };

    private static ManualLogSource Log;
    private static ConfigEntry<int> StackSize;
    private static ConfigEntry<int> FontSize;
    private static ConfigEntry<float> OffsetX;
    private static ConfigEntry<float> OffsetY;
    private static ConfigEntry<bool> StackBackpack;
    private static ConfigEntry<bool> StackOpenedContainers;
    private static ConfigEntry<string> StorageGroupIdContains;
    private static ConfigEntry<bool> DebugMode;
    private static ConfigEntry<bool> AlignLeft;
    private static ConfigEntry<float> CounterWidth;
    
    private static ConfigEntry<bool> StackOreExtractors;
    private static ConfigEntry<bool> StackWaterCollectors;
    private static ConfigEntry<bool> StackGasExtractors;

    private static Plugin Instance;
    private static Harmony _harmony;
    private static bool shiftTransferInProgress;
    private static readonly Dictionary<int, string> AllowFullMiningBackpackForStackId = new();
    private static Font font;
    
    // Рефлексия для доступа к protected методам
    private static System.Reflection.MethodInfo onImageClickedMethod;
    private static System.Reflection.MethodInfo onDropClickedMethod;
    private static System.Reflection.MethodInfo onActionViaGamepadMethod;
    private static System.Reflection.MethodInfo onConsumeViaGamepadMethod;
    private static System.Reflection.MethodInfo onDropViaGamepadMethod;

    private static System.Reflection.MethodInfo addNewItemClientRpcMethod;
    private static System.Reflection.MethodInfo dirtyInventoryMethod;
    private static System.Reflection.FieldInfo logisticTaskPrioritiesField;
    private static System.Reflection.FieldInfo logisticDemandInventoriesField;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        
        StackSize = Config.Bind("General", "StackSize", 10, "How many equal items fit into one visible slot.");
        FontSize = Config.Bind("General", "FontSize", 15, "Stack counter font size.");
        OffsetX = Config.Bind("General", "OffsetX", -2f, "Move stack counter horizontally.");
        OffsetY = Config.Bind("General", "OffsetY", 2f, "Move stack counter vertically.");
        AlignLeft = Config.Bind("General", "AlignLeft", false, "Align stack counter to the left instead of the right.");
        StackBackpack = Config.Bind("General", "StackBackpack", true, "Allow stacking in the player backpack.");
        StackOpenedContainers = Config.Bind("General", "StackOpenedContainers", true, "Allow stacking in opened storage/container inventories.");
        StorageGroupIdContains = Config.Bind("General", "StorageGroupIdContains", "container,storage,locker,chest", "Comma-separated owner group-id fragments treated as storage.");
        DebugMode = Config.Bind("General", "DebugMode", false, "Write detailed diagnostic logs.");
        CounterWidth = Config.Bind("General", "CounterWidth", 40f, "Width of stack counter area.");
        
        StackOreExtractors = Config.Bind("Machines", "StackOreExtractors", true, "Allow stacking in Ore Extractors (OreExtractor1, OreExtractor2, OreExtractor3).");
        StackWaterCollectors = Config.Bind("Machines", "StackWaterCollectors", true, "Allow stacking in Water Collectors (WaterCollector1, WaterCollector2).");
        StackGasExtractors = Config.Bind("Machines", "StackGasExtractors", true, "Allow stacking in Gas Extractors (GasExtractor1, GasExtractor2).");

        font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        
        // Получаем методы через рефлексию (они protected)
        onImageClickedMethod = AccessTools.Method(typeof(InventoryDisplayer), "OnImageClicked");
        onDropClickedMethod = AccessTools.Method(typeof(InventoryDisplayer), "OnDropClicked");
        onActionViaGamepadMethod = AccessTools.Method(typeof(InventoryDisplayer), "OnActionViaGamepad");
        onConsumeViaGamepadMethod = AccessTools.Method(typeof(InventoryDisplayer), "OnConsumeViaGamepad");
        onDropViaGamepadMethod = AccessTools.Method(typeof(InventoryDisplayer), "OnDropViaGamepad");

        addNewItemClientRpcMethod = AccessTools.Method(typeof(InventoriesHandler), "AddNewItemClientRpc");
        dirtyInventoryMethod = AccessTools.Method(typeof(InventoriesHandler), "DirtyInventory");
        logisticTaskPrioritiesField = AccessTools.Field(typeof(LogisticManager), "_taskPriorities");
        logisticDemandInventoriesField = AccessTools.Field(typeof(LogisticManager), "_demandInventories");

        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin), PluginGuid);
        PatchLogisticsStateMachine();

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded for Planet Crafter v2.008");
        Logger.LogInfo($"Stacking enabled for: Ore Extractors={StackOreExtractors.Value}, Water Collectors={StackWaterCollectors.Value}, Gas Extractors={StackGasExtractors.Value}");
    }

    private static void DebugLog(string message)
    {
        if (DebugMode.Value)
        {
            Log.LogInfo(message);
        }
    }

    private static bool CanStack(Inventory inventory)
    {
        if (inventory == null || StackSize.Value <= 1)
        {
            return false;
        }

        if (IsPlayerBackpack(inventory))
        {
            return StackBackpack.Value;
        }

        if (AllowedContainerInventories.Contains(inventory.GetId()))
        {
            return StackOpenedContainers.Value;
        }

        WorldObject owner = GetInventoryOwner(inventory);
        if (owner == null)
        {
            return true;
        }

        string groupId = owner.GetGroup()?.GetId() ?? "";
        
        if (groupId.StartsWith("OreExtractor"))
            return StackOreExtractors.Value;
        if (groupId.StartsWith("WaterCollector"))
            return StackWaterCollectors.Value;
        if (groupId.StartsWith("GasExtractor"))
            return StackGasExtractors.Value;

        return true;
    }
    
    private static WorldObject GetInventoryOwner(Inventory inventory)
    {
        if (inventory == null) return null;
        return WorldObjectsHandler.Instance?.GetWorldObjectForInventory(inventory);
    }

    private static bool IsPlayerBackpack(Inventory inventory)
    {
        if (!StackBackpack.Value) return false;

        try
        {
            PlayersManager players = Managers.GetManager<PlayersManager>();
            if (players == null) return false;

            PlayerMainController active = players.GetActivePlayerController();
            if (active?.GetPlayerBackpack()?.GetInventory() == inventory)
                return true;

            foreach (PlayerMainController player in players.playersControllers)
            {
                if (player?.GetPlayerBackpack()?.GetInventory() == inventory)
                    return true;
            }
        }
        catch { return false; }

        return false;
    }

    private static bool LooksLikeStorage(WorldObject owner)
    {
        string id = owner?.GetGroup()?.GetId();
        if (string.IsNullOrEmpty(id)) return false;

        string lower = id.ToLowerInvariant();
        var needles = StorageGroupIdContains.Value
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length != 0);

        return needles.DefaultIfEmpty().Any(needle => lower.Contains(needle))
            || DefaultStorageNeedles.Any(needle => lower.Contains(needle));
    }

    private static void AllowContainerInventory(Inventory inventory, WorldObject owner, string source)
    {
        if (!StackOpenedContainers.Value || inventory == null) return;

        if (LooksLikeStorage(owner))
        {
            AllowedContainerInventories.Add(inventory.GetId());
            DebugLog($"Allowed inventory {inventory.GetId()} from {source}, owner group {owner.GetGroup().GetId()}");
        }
    }

    private static string GetStackId(WorldObject worldObject)
    {
        string id = worldObject.GetGroup().GetId();
        
        if (id == "GeneticTrait")
        {
            StringBuilder builder = new(48);
            builder.Append(id).Append('_');
            AppendTraitInfo(worldObject.GetGeneticTraitType(), worldObject.GetGeneticTraitValue(), worldObject.GetColor(), builder);
            return builder.ToString();
        }

        if (id == "DNASequence")
        {
            StringBuilder builder = new(128);
            Inventory inventory = InventoriesHandler.Instance.GetInventoryById(worldObject.GetLinkedInventoryId());
            if (inventory != null)
            {
                builder.Append(id);
                List<WorldObject> traits = inventory.GetInsideWorldObjects().ToList();
                traits.Sort((a, b) => a.GetGeneticTraitType().CompareTo(b.GetGeneticTraitType()));
                foreach (WorldObject trait in traits)
                {
                    builder.Append('_');
                    AppendTraitInfo(trait.GetGeneticTraitType(), trait.GetGeneticTraitValue(), trait.GetColor(), builder);
                }
            }
            return builder.ToString();
        }

        if (id == "BlueprintT1")
        {
            List<Group> linkedGroups = worldObject.GetLinkedGroups();
            return linkedGroups != null && linkedGroups.Count > 0 ? id + "_" + linkedGroups[0].GetId() : id;
        }

        return id;
    }

    private static void AppendTraitInfo(DataConfig.GeneticTraitType type, int value, Color color, StringBuilder builder)
    {
        builder.Append(type).Append('_');
        if (type == DataConfig.GeneticTraitType.ColorA || type == DataConfig.GeneticTraitType.ColorB || type == DataConfig.GeneticTraitType.PatternColor)
        {
            builder.Append(((int)(color.r * 255f)).ToString("X2"));
            builder.Append(((int)(color.g * 255f)).ToString("X2"));
            builder.Append(((int)(color.b * 255f)).ToString("X2"));
        }
        else
        {
            builder.Append(value);
        }
    }

    private static List<List<WorldObject>> CreateInventorySlots(IEnumerable<WorldObject> worldObjects)
    {
        int max = Math.Max(1, StackSize.Value);
        Dictionary<string, List<WorldObject>> openStacks = new();
        List<List<WorldObject>> slots = new();

        foreach (WorldObject worldObject in worldObjects)
        {
            if (worldObject == null) continue;

            string stackId = GetStackId(worldObject);
            if (!openStacks.TryGetValue(stackId, out List<WorldObject> stack))
            {
                stack = new List<WorldObject>(max) { worldObject };
                openStacks[stackId] = stack;
                slots.Add(stack);
                continue;
            }

            stack.Add(worldObject);
            if (stack.Count >= max)
            {
                openStacks.Remove(stackId);
            }
        }

        return slots;
    }

    private static bool IsShiftPressed()
    {
        Keyboard keyboard = Keyboard.current;
        return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
    }

    private static int GetPartialStackSpace(Inventory inventory, string stackId, int stackSize)
    {
        int space = 0;
        foreach (List<WorldObject> slot in CreateInventorySlots(inventory.GetInsideWorldObjects()))
        {
            if (slot.Count > 0 && slot.Count < stackSize && GetStackId(slot[0]) == stackId)
            {
                space += stackSize - slot.Count;
            }
        }

        return space;
    }

    private static bool HasFreeVisibleSlot(Inventory inventory)
    {
        return CreateInventorySlots(inventory.GetInsideWorldObjects()).Count < inventory.GetSize();
    }

    private static Inventory GetOtherOpenInventory(Inventory from, InventoryDisplayer displayer)
    {
        if (from == null)
        {
            return null;
        }

        try
        {
            WindowsHandler windowsHandler = Managers.GetManager<WindowsHandler>();
            UiWindowContainer container = windowsHandler?.GetWindowViaUiId(windowsHandler.GetOpenedUi()) as UiWindowContainer;
            Inventory other = container?.GetOtherInventory(from);
            if (other != null && other != from)
            {
                return other;
            }
        }
        catch (Exception ex)
        {
            DebugLog("Unable to resolve other inventory from opened container: " + ex.Message);
        }

        Inventory fallback = (Inventory)AccessTools.Field(typeof(InventoryDisplayer), "_inventoryInteracting").GetValue(displayer);
        return fallback != from ? fallback : null;
    }

    private static List<WorldObject> FindSlotForStackTransfer(Inventory from, WorldObject clickedItem, string stackId, bool preferFullStack)
    {
        int stackSize = Math.Max(1, StackSize.Value);
        List<WorldObject> clickedSlot = null;
        List<WorldObject> fullSlot = null;

        foreach (List<WorldObject> slot in CreateInventorySlots(from.GetInsideWorldObjects()))
        {
            if (slot.Count == 0 || GetStackId(slot[0]) != stackId)
            {
                continue;
            }

            if (slot.Contains(clickedItem))
            {
                clickedSlot = slot;
            }

            if (fullSlot == null && slot.Count >= stackSize)
            {
                fullSlot = slot;
            }
        }

        if (preferFullStack && clickedSlot != null && clickedSlot.Count < stackSize && fullSlot != null)
        {
            return fullSlot;
        }

        return clickedSlot ?? fullSlot;
    }

    private static bool TryTransferStackOnShiftClick(InventoryDisplayer displayer, EventTriggerCallbackData eventData)
    {
        if (Instance == null || displayer == null || eventData?.worldObject == null)
        {
            return false;
        }

        if (eventData.pointerEventData != null && eventData.pointerEventData.button != PointerEventData.InputButton.Left)
        {
            return false;
        }

        Inventory from = (Inventory)AccessTools.Field(typeof(InventoryDisplayer), "_inventory").GetValue(displayer);
        Inventory to = GetOtherOpenInventory(from, displayer);
        if (!CanStack(from) || !CanStack(to))
        {
            return false;
        }

        if (shiftTransferInProgress)
        {
            DebugLog("Shift stack transfer skipped: another transfer is still running");
            return true;
        }

        int stackSize = Math.Max(1, StackSize.Value);
        string stackId = GetStackId(eventData.worldObject);
        bool targetHasFreeSlot = HasFreeVisibleSlot(to);
        int partialSpace = GetPartialStackSpace(to, stackId, stackSize);
        int targetCapacityForClick = targetHasFreeSlot ? stackSize : partialSpace;
        if (targetCapacityForClick <= 0)
        {
            DebugLog($"Shift stack transfer skipped: target inventory {to.GetId()} has no room for {stackId}");
            return true;
        }

        List<WorldObject> sourceSlot = FindSlotForStackTransfer(from, eventData.worldObject, stackId, targetHasFreeSlot);
        if (sourceSlot == null || sourceSlot.Count == 0)
        {
            return true;
        }

        int amount = Math.Min(sourceSlot.Count, targetCapacityForClick);
        if (amount <= 0)
        {
            return true;
        }

        List<WorldObject> itemsToTransfer = sourceSlot.Take(amount).ToList();
        DebugLog($"Shift stack transfer: {amount}x {stackId} from {from.GetId()} to {to.GetId()}");
        Instance.StartCoroutine(TransferItems(from, to, itemsToTransfer, stackId));
        return true;
    }

    private static bool CanReceiveStackItem(Inventory inventory, string stackId, int stackSize)
    {
        return HasFreeVisibleSlot(inventory) || GetPartialStackSpace(inventory, stackId, stackSize) > 0;
    }

    private static bool TryAddStackedWorldObject(Inventory inventory, WorldObject worldObject, bool resetPositionAndRotation)
    {
        if (!CanStack(inventory) || worldObject == null)
        {
            return false;
        }

        string stackId = GetStackId(worldObject);
        int stackSize = Math.Max(1, StackSize.Value);
        if (!CanReceiveStackItem(inventory, stackId, stackSize))
        {
            return false;
        }

        return inventory.AddItem(worldObject, resetPositionAndRotation);
    }

    private static bool CanReceiveWorldObjectInExistingStack(Inventory inventory, WorldObject worldObject)
    {
        if (!CanStack(inventory) || worldObject == null)
        {
            return false;
        }

        return GetPartialStackSpace(inventory, GetStackId(worldObject), Math.Max(1, StackSize.Value)) > 0;
    }

    private static IEnumerator TransferItems(Inventory from, Inventory to, List<WorldObject> items, string stackId)
    {
        shiftTransferInProgress = true;
        int stackSize = Math.Max(1, StackSize.Value);

        try
        {
            foreach (WorldObject item in items)
            {
                if (item == null || !from.ContainWorldObject(item) || !CanReceiveStackItem(to, stackId, stackSize))
                {
                    continue;
                }

                bool removed = from.RemoveItem(item);
                if (!removed)
                {
                    DebugLog($"Shift stack transfer stopped: source did not contain {GetStackId(item)}");
                    break;
                }

                bool added = to.AddItem(item, false);
                if (!added)
                {
                    DebugLog($"Shift stack transfer stopped: target rejected {GetStackId(item)}, returning item to source");
                    from.AddItem(item, false);
                    break;
                }

                yield return null;
            }
        }
        finally
        {
            shiftTransferInProgress = false;
            from.RefreshDisplayerContent();
            to.RefreshDisplayerContent();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryDisplayer), "OnImageClicked")]
    private static bool InventoryDisplayer_OnImageClicked_Pre(InventoryDisplayer __instance, EventTriggerCallbackData eventTriggerCallbackData)
    {
        try
        {
            if (!IsShiftPressed())
            {
                return true;
            }

            return !TryTransferStackOnShiftClick(__instance, eventTriggerCallbackData);
        }
        catch (Exception ex)
        {
            Log.LogWarning("Shift stack transfer failed before vanilla click: " + ex);
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActionMinable), "OnAction")]
    private static void ActionMinable_OnAction_Pre(ActionMinable __instance)
    {
        try
        {
            WorldObject worldObject = __instance.GetComponent<WorldObjectAssociated>()?.GetWorldObject();
            Inventory backpack = Managers.GetManager<PlayersManager>()?.GetActivePlayerController()?.GetPlayerBackpack()?.GetInventory();
            if (backpack != null)
            {
                AllowFullMiningBackpackForStackId.Remove(backpack.GetId());
            }

            if (CanReceiveWorldObjectInExistingStack(backpack, worldObject))
            {
                AllowFullMiningBackpackForStackId[backpack.GetId()] = GetStackId(worldObject);
                DebugLog($"Allow mining into visually full backpack {backpack.GetId()} for {GetStackId(worldObject)}");
            }
        }
        catch (Exception ex)
        {
            DebugLog("Unable to prepare mining stack allowance: " + ex.Message);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoriesHandler), "AddWorldObjectToInventory")]
    private static void InventoriesHandler_AddWorldObjectToInventory_Pre(WorldObject worldObject, Inventory inventory)
    {
        try
        {
            if (!CanStack(inventory) || worldObject == null)
            {
                return;
            }

            // Очищаем старый флаг
            AllowFullMiningBackpackForStackId.Remove(inventory.GetId());

            // Если можно добавить в существующий неполный стак, устанавливаем флаг
            if (CanReceiveWorldObjectInExistingStack(inventory, worldObject))
            {
                AllowFullMiningBackpackForStackId[inventory.GetId()] = GetStackId(worldObject);
                DebugLog($"Allow adding item into visually full inventory {inventory.GetId()} for {GetStackId(worldObject)}");
            }
        }
        catch (Exception ex)
        {
            DebugLog("Unable to prepare stack allowance for AddWorldObjectToInventory: " + ex.Message);
        }
    }

	private static int GetStackCount(ReadOnlyCollection<WorldObject> items)
	{
		int stackSize = Math.Max(1, StackSize.Value);
		Dictionary<string, int> stackCounts = new Dictionary<string, int>();
		int occupiedSlots = 0;
		
		foreach (WorldObject item in items)
		{
			if (item == null) continue;
			
			string stackId = GetStackId(item);
			
			if (!stackCounts.ContainsKey(stackId))
			{
				stackCounts[stackId] = 0;
				occupiedSlots++;
			}
			stackCounts[stackId]++;
			
			// Если стак переполнен, увеличиваем количество слотов
			if (stackCounts[stackId] > stackSize)
			{
				occupiedSlots++;
				stackCounts[stackId] -= stackSize;
			}
		}
		
		return occupiedSlots;
	}

	public static int GetStackSlotCount(ReadOnlyCollection<WorldObject> items)
	{
		return GetStackCount(items);
	}

    private static bool IsExtractorInventory(Inventory inventory)
    {
        if (inventory == null) return false;
        WorldObject owner = GetInventoryOwner(inventory);
        if (owner == null) return false;
        string groupId = owner.GetGroup()?.GetId() ?? "";
        return groupId.StartsWith("OreExtractor") ||
               groupId.StartsWith("WaterCollector") ||
               groupId.StartsWith("GasExtractor");
    }

    private static bool IsSingleItemMachine(Inventory inventory)
    {
        if (inventory == null) return false;
        WorldObject owner = GetInventoryOwner(inventory);
        if (owner?.GetGameObject() == null) return false;
        GameObject go = owner.GetGameObject();
        return go.GetComponent<MachineConvertRecipe>() != null ||
               go.GetComponent<MachineGrowerVegetationHarvestable>() != null ||
               go.GetComponent<MachineGrowerIfLinkedGroup>() != null ||
               go.GetComponent<MachineGrowerBase>() != null;
    }

    private static void AddStackDisplay(GameObject slot, int amount)
    {
        GameObject counter = new("StackCounter");
        counter.transform.SetParent(slot.transform, false);

        Text text = counter.AddComponent<Text>();
        text.font = font;
        text.fontSize = FontSize.Value;
        text.fontStyle = FontStyle.Bold;
        text.alignment = AlignLeft.Value ? TextAnchor.LowerLeft : TextAnchor.LowerRight;
        text.raycastTarget = false;
        text.color = Color.white;
        text.text = amount.ToString();

        Shadow shadow = counter.AddComponent<Shadow>();
        shadow.effectColor = Color.black;
        shadow.effectDistance = new Vector2(1.5f, -1.5f);

        RectTransform rect = counter.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.sizeDelta = new Vector2(CounterWidth.Value, 30f);
        rect.anchoredPosition = new Vector2(-4f + OffsetX.Value, 2f + OffsetY.Value);
    }

    // Патчи для контейнеров
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActionOpenable), "OpenInventories")]
    private static void ActionOpenable_OpenInventories_Pre(ActionOpenable __instance, Inventory objectInventory, WorldObject worldObject)
    {
        if (!__instance.notAContainer)
        {
            AllowContainerInventory(objectInventory, worldObject, "ActionOpenable");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UiWindowContainer), "SetInventories")]
    private static void UiWindowContainer_SetInventories_Post(Inventory inventoryRight)
    {
        try
        {
            WorldObject owner = WorldObjectsHandler.Instance?.GetWorldObjectForInventory(inventoryRight);
            AllowContainerInventory(inventoryRight, owner, "UiWindowContainer");
        }
        catch (Exception ex)
        {
            DebugLog("Unable to resolve container owner: " + ex.Message);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoriesHandler), "DestroyInventory")]
    private static void InventoriesHandler_DestroyInventory_Post(int inventoryId)
    {
        AllowedContainerInventories.Remove(inventoryId);
    }

    // Патч для IsFull - учитывает стаки
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Inventory), "IsFull")]
    private static bool Inventory_IsFull_Pre(Inventory __instance, ref bool __result)
    {
        if (!CanStack(__instance)) return true;

        if (AllowFullMiningBackpackForStackId.TryGetValue(__instance.GetId(), out string stackId) &&
            GetPartialStackSpace(__instance, stackId, Math.Max(1, StackSize.Value)) > 0)
        {
            __result = false;
            return false;
        }

        // Для экстракторов (руда/вода/газ) — total capacity (size * stackSize)
        if (IsExtractorInventory(__instance))
        {
            int maxStack = Math.Max(1, StackSize.Value);
            __result = __instance.GetInsideWorldObjects().Count >= __instance.GetSize() * maxStack;
            return false;
        }

        // Для всего остального — визуальные слоты (size)
        int usedSlots = GetStackSlotCount(__instance.GetInsideWorldObjects());
        __result = usedSlots >= __instance.GetSize();
        return false;
    }

    // Патч для AddItemInInventory - корректное добавление с учётом стаков
	[HarmonyPrefix]
	[HarmonyPatch(typeof(Inventory), "AddItemInInventory")]
	private static bool Inventory_AddItemInInventory_Pre(
		Inventory __instance, 
		WorldObject worldObject, 
		bool resetPositionAndRotation, 
		ref bool __result)
	{
		if (!CanStack(__instance) || worldObject == null)
		{
			return true;
		}

		// Для однослотовых машин (Vegetube, Flower Seeder) — без стаков, vanilla логика
		if (IsSingleItemMachine(__instance))
		{
			return true;
		}

		int stackSize = Math.Max(1, StackSize.Value);
		string newStackId = GetStackId(worldObject);
		
		// Получаем текущее состояние
		var worldObjectsList = (List<WorldObject>)AccessTools.Field(typeof(Inventory), "_worldObjectsInInventory").GetValue(__instance);
		
		// Считаем текущие стаки
		Dictionary<string, int> stackCounts = new Dictionary<string, int>();
		int usedSlots = 0;
		
		foreach (WorldObject item in worldObjectsList)
		{
			if (item == null) continue;
			string stackId = GetStackId(item);
			
			if (!stackCounts.ContainsKey(stackId))
			{
				stackCounts[stackId] = 0;
				usedSlots++;
			}
			stackCounts[stackId]++;
			
			// Если переполнение - учитываем дополнительные слоты
			if (stackCounts[stackId] > stackSize)
			{
				usedSlots++;
				stackCounts[stackId] -= stackSize;
			}
		}
		
		// Проверяем, можно ли добавить
		bool canAdd = false;
		
		if (stackCounts.ContainsKey(newStackId))
		{
			// Есть такой тип - проверяем текущий размер стака
			int currentCount = 0;
			// Нужно посчитать актуальное количество предметов этого типа
			foreach (WorldObject item in worldObjectsList)
			{
				if (item != null && GetStackId(item) == newStackId)
					currentCount++;
			}
			
			// Если текущий стак не полный (по остатку от деления на stackSize)
			if (currentCount % stackSize != 0)
			{
				canAdd = true;
			}
		}
		
		// Если нет такого типа или не можем добавить в существующий - проверяем свободные слоты
		if (!canAdd)
		{
			// Свободные слоты = общий размер - использованные слоты
			int freeSlots = __instance.GetSize() - usedSlots;
			if (freeSlots > 0)
			{
				canAdd = true;
			}
		}
		
		if (!canAdd)
		{
			__result = false;
			return false;
		}
		
		// Добавляем предмет
		if (!worldObjectsList.Contains(worldObject))
		{
			worldObjectsList.Add(worldObject);
		}
		
		if (resetPositionAndRotation)
		{
			worldObject.ResetPositionAndRotation();
		}
		worldObject.SetLockInInventoryTime(0f);
		
		__result = true;
		return false;
	}

    // Основной патч для отображения инвентаря со стаками
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoryDisplayer), "SetInventoryBlocks")]
    private static bool InventoryDisplayer_SetInventoryBlocks_Pre(
        InventoryDisplayer __instance,
        ReadOnlyCollection<WorldObject> inventoryWorldObjects,
        GroupInfosDisplayerBlocksSwitches infosDisplayerBlockSwitches,
        bool shouldCheckItemsLogisticsStatus,
        bool enabled)
    {
        Inventory inventory = (Inventory)AccessTools.Field(typeof(InventoryDisplayer), "_inventory").GetValue(__instance);
        
        if (!CanStack(inventory))
        {
            return true;
        }

        GridLayoutGroup grid = (GridLayoutGroup)AccessTools.Field(typeof(InventoryDisplayer), "_grid").GetValue(__instance);
        VisualsResourcesHandler visuals = Managers.GetManager<VisualsResourcesHandler>();
        LogisticManager logisticManager = Managers.GetManager<LogisticManager>();
        WindowsGamepadHandler gamepadHandler = Managers.GetManager<WindowsGamepadHandler>();

        GameObjects.DestroyAllChildren(grid.gameObject, false);
        GameObject inventoryBlock = visuals.GetInventoryBlock();
        bool isPlayerInventory = IsPlayerBackpack(inventory);
        HashSet<Group> authorizedGroups = inventory.GetAuthorizedGroups();
        Sprite authorizedGroupIcon = authorizedGroups.Count > 0 ? visuals.GetGroupItemCategoriesSprite(authorizedGroups.First()) : null;
        
        List<List<WorldObject>> slots = CreateInventorySlots(inventoryWorldObjects);

        for (int i = 0; i < inventory.GetSize(); i++)
        {
            GameObject slot = UnityEngine.Object.Instantiate(inventoryBlock, grid.transform);
            InventoryBlock block = slot.GetComponent<InventoryBlock>();
            block.SetAuthorizedGroupIcon(authorizedGroupIcon);

            if (i < slots.Count && slots[i].Count > 0)
            {
                List<WorldObject> stack = slots[i];
                WorldObject item = stack[stack.Count - 1];
                
                bool showDropIcon = isPlayerInventory && 
                    (!(item.GetGroup() is GroupItem groupItem) || !groupItem.GetCantBeDestroyed());
                
                block.SetDisplay(item, infosDisplayerBlockSwitches, showDropIcon);

                if (stack.Count > 1)
                {
                    AddStackDisplay(slot, stack.Count);
                }

                if (!item.GetIsLockedInInventory())
                {
                    EventTriggerCallbackData eventData = new EventTriggerCallbackData(item);
                    eventData.intValue = i;
                    
                    EventsHelpers.AddTriggerEvent(slot, EventTriggerType.PointerClick, 
                        (Action<EventTriggerCallbackData>)Delegate.CreateDelegate(typeof(Action<EventTriggerCallbackData>), __instance, onImageClickedMethod), 
                        eventData);
                    
                    EventsHelpers.AddTriggerEvent(block.GetDropIcon(), EventTriggerType.PointerClick, 
                        (Action<EventTriggerCallbackData>)Delegate.CreateDelegate(typeof(Action<EventTriggerCallbackData>), __instance, onDropClickedMethod), 
                        eventData);
                    
                    var actionDelegate = (Action<EventTriggerCallbackData>)Delegate.CreateDelegate(typeof(Action<EventTriggerCallbackData>), __instance, onActionViaGamepadMethod);
                    var consumeDelegate = (Action<EventTriggerCallbackData>)Delegate.CreateDelegate(typeof(Action<EventTriggerCallbackData>), __instance, onConsumeViaGamepadMethod);
                    var dropDelegate = showDropIcon ? (Action<EventTriggerCallbackData>)Delegate.CreateDelegate(typeof(Action<EventTriggerCallbackData>), __instance, onDropViaGamepadMethod) : null;
                    
                    slot.AddComponent<EventGamepadAction>().SetEventGamepadAction(actionDelegate, eventData, consumeDelegate, dropDelegate, null);
                }

                if (shouldCheckItemsLogisticsStatus)
                {
                    block.SetLogisticStatus(logisticManager.WorldObjectIsInTasks(item));
                }
            }
            else
            {
                slot.AddComponent<EventGamepadAction>().SetEventGamepadAction(null, new EventTriggerCallbackData(i), null, null, null);
            }

            slot.SetActive(true);
            
            if (!enabled)
            {
                var selectable = slot.GetComponentInChildren<Selectable>();
                if (selectable != null)
                    selectable.interactable = false;
            }
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoriesHandler), "AddNewItemToInventoryServerRpc")]
    private static bool AddNewItemToInventoryServerRpc_Pre(
        InventoriesHandler __instance,
        int groupHash,
        int inventoryId,
        RpcParams rpcParams)
    {
        var stageField = AccessTools.Field(typeof(NetworkBehaviour), "__rpc_exec_stage");
        var stageEnumType = stageField.FieldType;
        var sendValue = Enum.Parse(stageEnumType, "Send");
        var executeValue = Enum.Parse(stageEnumType, "Execute");

        if (!stageField.GetValue(__instance).Equals(executeValue))
            return true;

        var inventory = InventoriesHandler.Instance.GetInventoryById(inventoryId);
        if (inventory == null || !CanStack(inventory))
            return true;

        var group = GroupsHandler.GetGroupFromHash(groupHash);
        var newWo = WorldObjectsHandler.Instance.CreateNewWorldObject(group, 0, null, true);
        int woId = newWo.GetId();
        bool result = inventory.AddItem(newWo, true);

        if (result)
        {
            inventory.PropagateModification(newWo, true);
            inventory.RefreshDisplayerContent();
            dirtyInventoryMethod.Invoke(__instance, new object[] { inventoryId, rpcParams.Receive.SenderClientId, false });
        }
        else
        {
            WorldObjectsHandler.Instance.DestroyWorldObject(newWo, false);
            DebugLog($"Destroyed orphan: {group.GetId()} (WO#{woId}) from inventory {inventoryId}");
        }

        var clientParams = NetworkUtils.GetSenderClientParams(rpcParams);
        var args = new object[] { result, woId, clientParams };

        stageField.SetValue(__instance, sendValue);
        addNewItemClientRpcMethod.Invoke(__instance, args);

        stageField.SetValue(__instance, executeValue);
        addNewItemClientRpcMethod.Invoke(__instance, args);

        return false;
    }

    private static void PatchLogisticsStateMachine()
    {
        var setLogisticTasks = AccessTools.Method(typeof(LogisticManager), "SetLogisticTasks");
        var attr = setLogisticTasks?.GetCustomAttribute<IteratorStateMachineAttribute>();
        var stateType = attr?.StateMachineType;
        if (stateType == null) return;

        var moveNext = AccessTools.Method(stateType, "MoveNext");
        if (moveNext == null) return;

        _harmony.Patch(moveNext, transpiler: new HarmonyMethod(
            AccessTools.Method(typeof(Plugin), nameof(SetLogisticTasks_MoveNext_Transpiler))));
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogisticManager), "AddNewDemandInventory")]
    private static bool AddNewDemandInventory_Pre(LogisticManager __instance, Inventory inventory)
    {
        if (inventory == null || !CanStack(inventory))
            return true;

        var priorities = (Dictionary<int, Dictionary<int, int>>)logisticTaskPrioritiesField.GetValue(__instance);
        var demands = (List<Inventory>)logisticDemandInventoriesField.GetValue(__instance);

        int usedSlots = GetStackSlotCount(inventory.GetInsideWorldObjects());
        int freeSlots = Math.Max(0, inventory.GetSize() - usedSlots - inventory.GetLogisticEntity().waitingDemandSlots);

        if (freeSlots > 0 && priorities.ContainsKey(inventory.GetLogisticEntity().GetPlanetHash()))
        {
            foreach (Group demandGroup in inventory.GetLogisticEntity().GetDemandGroups())
            {
                int num;
                if (!priorities[inventory.GetLogisticEntity().GetPlanetHash()].TryGetValue(demandGroup.stableHashCode, out num) || num < inventory.GetLogisticEntity().GetPriority())
                    priorities[inventory.GetLogisticEntity().GetPlanetHash()][demandGroup.stableHashCode] = inventory.GetLogisticEntity().GetPriority();
            }
        }

        for (int index = 0; index < demands.Count; ++index)
        {
            if (inventory.GetLogisticEntity().GetPriority() >= demands[index].GetLogisticEntity().GetPriority())
            {
                demands.Insert(index, inventory);
                return false;
            }
        }
        demands.Add(inventory);
        return false;
    }

    private static IEnumerable<CodeInstruction> SetLogisticTasks_MoveNext_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        var getInsideWorldObjects = AccessTools.Method(typeof(Inventory), "GetInsideWorldObjects");
        var getCount = typeof(ReadOnlyCollection<WorldObject>).GetMethod("get_Count");
        var stackCount = AccessTools.Method(typeof(Plugin), nameof(GetStackSlotCount));

        for (int i = 0; i < codes.Count - 2; i++)
        {
            if (codes[i].opcode == OpCodes.Callvirt &&
                codes[i].operand is System.Reflection.MethodBase m1 &&
                m1 == getInsideWorldObjects &&
                codes[i + 1].opcode == OpCodes.Callvirt &&
                codes[i + 1].operand is System.Reflection.MethodBase m2 &&
                m2 == getCount &&
                codes[i + 2].opcode == OpCodes.Sub)
            {
                codes[i + 1] = new CodeInstruction(OpCodes.Call, stackCount);
            }
        }
        return codes;
    }
}
