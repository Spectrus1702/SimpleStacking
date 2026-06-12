using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    public const string PluginGuid = "spectrus.simplestacking";
    public const string PluginName = "SimpleStacking";
    public const string PluginVersion = "1.4.0";

    private static ManualLogSource Log;
    private static ConfigEntry<int> StackSize;
    private static ConfigEntry<int> FontSize;
    private static ConfigEntry<string> CounterPosition;
    private static ConfigEntry<bool> DebugMode;
    private static Plugin Instance;
    private static Harmony _harmony;
    private static bool shiftTransferInProgress;
    private static readonly Dictionary<string, ConfigEntry<bool>> containerOverrides = new();
    private static int addRejectCount;
    private static bool bulkCraftInProgress;

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
    private static System.Reflection.FieldInfo uiWindowCraft_SourceCrafter;
    private static System.Reflection.FieldInfo uiWindowCraft_CanCraft;
    private static System.Reflection.FieldInfo uiWindowCraft_PreviousGroupCrafted;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        
        StackSize = Config.Bind("General", "StackSize", 10, "How many equal items fit into one visible slot.");
        FontSize = Config.Bind("General", "FontSize", 15, "Stack counter font size.");
        CounterPosition = Config.Bind("General", "CounterPosition", "BottomRight", "Stack counter position: BottomRight, BottomLeft, TopRight, TopLeft");
        DebugMode = Config.Bind("General", "DebugMode", false, "Write detailed diagnostic logs.");

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

        uiWindowCraft_SourceCrafter = AccessTools.Field(typeof(UiWindowCraft), "sourceCrafter");
        uiWindowCraft_CanCraft = AccessTools.Field(typeof(UiWindowCraft), "canCraft");
        uiWindowCraft_PreviousGroupCrafted = AccessTools.Field(typeof(UiWindowCraft), "previousGroupCrafted");

        _harmony = Harmony.CreateAndPatchAll(typeof(Plugin), PluginGuid);
        PatchLogisticsStateMachine();

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded for Planet Crafter v2.008");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ConfigFile), "Save")]
    private static void ConfigFile_Save_Post()
    {
        try
        {
            string path = Instance.Config.ConfigFilePath;
            if (!File.Exists(path)) return;

            string[] lines = File.ReadAllLines(path);
            var result = new List<string>();
            bool inSection = false;

            foreach (string line in lines)
            {
                string t = line.TrimStart();
                if (t.StartsWith("["))
                {
                    inSection = (t == "[AllowContainers]");
                    result.Add(line);
                    continue;
                }
                if (inSection && (t.StartsWith("#") || t.StartsWith("##")))
                    continue;
                result.Add(line);
            }

            File.WriteAllLines(path, result.ToArray());
        }
        catch { }
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
            return false;

        WorldObject owner = GetInventoryOwner(inventory);
        string groupId = owner?.GetGroup()?.GetId() ?? "";

        if (!string.IsNullOrEmpty(groupId))
        {
            if (containerOverrides.TryGetValue(groupId, out ConfigEntry<bool> overrideEntry))
                return overrideEntry.Value;

            bool stackingAllowed = (inventory.GetAuthorizedGroups()?.Count ?? 0) == 0;
            ConfigEntry<bool> entry = Instance.Config.Bind("AllowContainers", groupId, stackingAllowed, "");
            containerOverrides[groupId] = entry;
            Instance.Config.Save();
            return stackingAllowed;
        }

        return true;
    }
    
    private static WorldObject GetInventoryOwner(Inventory inventory)
    {
        if (inventory == null) return null;
        return WorldObjectsHandler.Instance?.GetWorldObjectForInventory(inventory);
    }

    private static bool IsPlayerBackpack(Inventory inventory)
    {
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

    private static int CalculateMaxCraftable(Inventory backpack, List<Group> ingredients)
    {
        Dictionary<string, int> available = new();
        foreach (WorldObject wo in backpack.GetInsideWorldObjects())
        {
            if (wo != null)
            {
                string id = wo.GetGroup().GetId();
                available[id] = available.GetValueOrDefault(id) + 1;
            }
        }

        Dictionary<string, int> required = new();
        foreach (Group g in ingredients)
        {
            string id = g.GetId();
            required[id] = required.GetValueOrDefault(id) + 1;
        }

        int max = int.MaxValue;
        foreach (var kv in required)
        {
            int have = available.GetValueOrDefault(kv.Key);
            max = Math.Min(max, have / kv.Value);
        }

        return max == int.MaxValue ? 0 : max;
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

    private static List<WorldObject> FindSlotForStackTransfer(Inventory from, WorldObject clickedItem, string stackId)
    {
        foreach (List<WorldObject> slot in CreateInventorySlots(from.GetInsideWorldObjects()))
        {
            if (slot.Count > 0 && GetStackId(slot[0]) == stackId && slot.Contains(clickedItem))
            {
                return slot;
            }
        }

        return null;
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

        List<WorldObject> sourceSlot = FindSlotForStackTransfer(from, eventData.worldObject, stackId);
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

            // Клиенты не могут напрямую модифицировать инвентари (нет RPC для трансфера стака).
            // Пропускаем shift-click → ванильный OnImageClicked обработает 1 предмет через сеть.
            if (!NetworkManager.Singleton.IsHost)
                return true;

            return !TryTransferStackOnShiftClick(__instance, eventTriggerCallbackData);
        }
        catch (Exception ex)
        {
            Log.LogWarning("Shift stack transfer failed before vanilla click: " + ex);
            return true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UiWindowCraft), "OnImageClicked")]
    private static bool UiWindowCraft_OnImageClicked_Pre(UiWindowCraft __instance, EventTriggerCallbackData _eventTriggerCallbackData)
    {
        try
        {
            if (!IsShiftPressed() || bulkCraftInProgress)
                return true;

            if (_eventTriggerCallbackData.pointerEventData == null ||
                _eventTriggerCallbackData.pointerEventData.button != PointerEventData.InputButton.Left)
                return true;

            GroupItem group = _eventTriggerCallbackData.group as GroupItem;
            if (group == null)
                return true;

            if (group.GetCraftedInWorld())
                return true;

            bool canCraft = (bool)uiWindowCraft_CanCraft.GetValue(__instance);
            bool everythingUnlocked = Managers.GetManager<GameSettingsHandler>().GetCurrentGameSettings().GetEverythingUnlocked();
            if (!canCraft && !everythingUnlocked)
                return false;

            PlayerMainController player = Managers.GetManager<PlayersManager>().GetActivePlayerController();
            if (player == null) return true;

            Inventory backpack = player.GetPlayerBackpack()?.GetInventory();
            if (backpack == null) return true;

            List<Group> ingredients = group.GetRecipe()?.GetIngredientsGroupInRecipe();
            if (ingredients == null || ingredients.Count == 0)
                return true;

            GameSettingsHandler settings = Managers.GetManager<GameSettingsHandler>();
            bool freeCraft = settings.GetCurrentGameSettings().GetFreeCraft();

            int maxCraftable;
            if (freeCraft)
            {
                maxCraftable = Math.Max(1, StackSize.Value);
            }
            else
            {
                maxCraftable = CalculateMaxCraftable(backpack, ingredients);
            }

            if (maxCraftable <= 1)
                return true;

            ActionCrafter sourceCrafter = (ActionCrafter)uiWindowCraft_SourceCrafter.GetValue(__instance);
            if (sourceCrafter == null)
                return true;

            bulkCraftInProgress = true;

            sourceCrafter.CraftAnimation(group);

            List<Group> allIngredients = new(ingredients.Count * maxCraftable);
            for (int i = 0; i < maxCraftable; i++)
                allIngredients.AddRange(ingredients);

            InventoriesHandler.Instance.RemoveItemsFromInventory(allIngredients, backpack, destroy: true, displayInformation: true);

            for (int i = 0; i < maxCraftable; i++)
                InventoriesHandler.Instance.AddItemToInventory(group, backpack, null);

            for (int i = 0; i < maxCraftable; i++)
                WorldObjectsHandler.Instance.AddOneToTotalCraft();

            uiWindowCraft_PreviousGroupCrafted.SetValue(__instance, group);

            if (!player.GetPlayerInputDispatcher().IsPressingAccessibilityKey())
                __instance.CloseAll();

            return false;
        }
        catch (Exception ex)
        {
            DebugLog($"Bulk craft error: {ex}");
            return true;
        }
        finally
        {
            bulkCraftInProgress = false;
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
        text.raycastTarget = false;
        text.color = Color.white;
        text.text = amount.ToString();

        Shadow shadow = counter.AddComponent<Shadow>();
        shadow.effectColor = Color.black;
        shadow.effectDistance = new Vector2(1.5f, -1.5f);

        string pos = CounterPosition.Value;
        float w = FontSize.Value * 1.8f;
        float h = FontSize.Value * 1.5f;
        Vector2 anchor, pivot, offset;

        switch (pos)
        {
            case "BottomLeft":
                anchor = new Vector2(0f, 0f); pivot = new Vector2(0f, 0f); offset = new Vector2(4f, 2f);
                text.alignment = TextAnchor.LowerLeft;
                break;
            case "TopRight":
                anchor = new Vector2(1f, 1f); pivot = new Vector2(1f, 1f); offset = new Vector2(-4f, -4f);
                text.alignment = TextAnchor.UpperRight;
                break;
            case "TopLeft":
                anchor = new Vector2(0f, 1f); pivot = new Vector2(0f, 1f); offset = new Vector2(4f, -4f);
                text.alignment = TextAnchor.UpperLeft;
                break;
            default: // BottomRight
                anchor = new Vector2(1f, 0f); pivot = new Vector2(1f, 0f); offset = new Vector2(-4f, 2f);
                text.alignment = TextAnchor.LowerRight;
                break;
        }

        RectTransform rect = counter.GetComponent<RectTransform>();
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.sizeDelta = new Vector2(w, h);
        rect.anchoredPosition = offset;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Inventory), "IsFull")]
    private static bool Inventory_IsFull_Pre(Inventory __instance, ref bool __result)
    {
        if (!CanStack(__instance)) return true;

        int stackSize = Math.Max(1, StackSize.Value);
        var items = __instance.GetInsideWorldObjects();
        Dictionary<string, int> stackCounts = new();
        int usedSlots = 0;

        foreach (WorldObject item in items)
        {
            if (item == null) continue;
            string stackId = GetStackId(item);
            if (!stackCounts.ContainsKey(stackId))
            {
                stackCounts[stackId] = 0;
                usedSlots++;
            }
            stackCounts[stackId]++;
            if (stackCounts[stackId] > stackSize)
            {
                usedSlots++;
                stackCounts[stackId] -= stackSize;
            }
        }

        // Есть свободные слоты — точно не полный
        if (usedSlots < __instance.GetSize())
        {
            __result = false;
            return false;
        }

        // Все слоты заняты — не полный, если есть неполные стаки
        __result = !stackCounts.Values.Any(c => c < stackSize);
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

		if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
			return true;

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
			if (DebugMode.Value)
			{
				string ownerGroup = GetInventoryOwner(__instance)?.GetGroup()?.GetId() ?? "?";
				addRejectCount++;
				Log.LogInfo($"[STACK REJECT #{addRejectCount}] invId={__instance.GetId()} group={ownerGroup}" +
					$" stackId={newStackId} usedSlots={usedSlots}/{__instance.GetSize()}" +
					$" freeSlots={__instance.GetSize() - usedSlots}" +
					$" hasStack={stackCounts.ContainsKey(newStackId)}");
			}
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
        int selectionIndex = (int)AccessTools.Field(typeof(InventoryDisplayer), "_selectionIndex").GetValue(__instance);
        Inventory inventoryInteracting = (Inventory)AccessTools.Field(typeof(InventoryDisplayer), "_inventoryInteracting").GetValue(__instance);

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
            else if (i == selectionIndex && (inventoryInteracting == null || inventoryInteracting == inventory))
            {
                gamepadHandler.SelectForController(slot, true, false, true, true, true, true);
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
