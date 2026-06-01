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

namespace SimpleStacking;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "local.theplanetcrafter.simplesting.v2";
    public const string PluginName = "Simple Stacking";
    public const string PluginVersion = "1.1.1";

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
    private static bool shiftTransferInProgress;
    private static Font font;
    
    // Рефлексия для доступа к protected методам
    private static System.Reflection.MethodInfo onImageClickedMethod;
    private static System.Reflection.MethodInfo onDropClickedMethod;
    private static System.Reflection.MethodInfo onActionViaGamepadMethod;
    private static System.Reflection.MethodInfo onConsumeViaGamepadMethod;
    private static System.Reflection.MethodInfo onDropViaGamepadMethod;

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

        Harmony.CreateAndPatchAll(typeof(Plugin), PluginGuid);
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

        __result = GetStackCount(__instance.GetInsideWorldObjects()) >= __instance.GetSize();
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
}
