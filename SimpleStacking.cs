using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SpaceCraft;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SimpleStacking;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "local.theplanetcrafter.simplestacking";
    public const string PluginName = "Simple Stacking";
    public const string PluginVersion = "1.0.3";

    private static readonly Dictionary<string, int> GroupCounts = new();
    private static readonly HashSet<int> AllowedContainerInventories = new();
    private static readonly HashSet<int> BlockedInventories = new();
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

    private static MethodInfo onImageClicked;
    private static MethodInfo onDropClicked;
    private static MethodInfo onActionViaGamepad;
    private static MethodInfo onConsumeViaGamepad;
    private static MethodInfo onDropViaGamepad;
    private static MethodInfo addItemInInventory;
    private static Font font;
	

    private void Awake()
    {
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

        onImageClicked = AccessTools.Method(typeof(InventoryDisplayer), "OnImageClicked");
        onDropClicked = AccessTools.Method(typeof(InventoryDisplayer), "OnDropClicked");
        onActionViaGamepad = AccessTools.Method(typeof(InventoryDisplayer), "OnActionViaGamepad");
        onConsumeViaGamepad = AccessTools.Method(typeof(InventoryDisplayer), "OnConsumeViaGamepad");
        onDropViaGamepad = AccessTools.Method(typeof(InventoryDisplayer), "OnDropViaGamepad");
        addItemInInventory = AccessTools.Method(typeof(Inventory), "AddItemInInventory");
        font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        Harmony.CreateAndPatchAll(typeof(Plugin), PluginGuid);
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
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
        if (inventory == null || StackSize.Value <= 1 || BlockedInventories.Contains(inventory.GetId()))
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
        {
            return StackOreExtractors.Value;
        }
        
        if (groupId.StartsWith("WaterCollector"))
        {
            return StackWaterCollectors.Value;
        }
        
        if (groupId.StartsWith("GasExtractor"))
        {
            return StackGasExtractors.Value;
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
        if (!StackBackpack.Value)
        {
            return false;
        }

        try
        {
            PlayersManager players = Managers.GetManager<PlayersManager>();
            if (players == null)
            {
                return false;
            }

            PlayerMainController active = players.GetActivePlayerController();
            if (active?.GetPlayerBackpack()?.GetInventory() == inventory)
            {
                return true;
            }

            foreach (PlayerMainController player in players.playersControllers)
            {
                if (player?.GetPlayerBackpack()?.GetInventory() == inventory)
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool LooksLikeStorage(WorldObject owner)
    {
        string id = owner?.GetGroup()?.GetId();
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        string lower = id.ToLowerInvariant();
        IEnumerable<string> needles = StorageGroupIdContains.Value
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length != 0);

        return needles.DefaultIfEmpty().Any(needle => lower.Contains(needle))
            || DefaultStorageNeedles.Any(needle => lower.Contains(needle));
    }

    private static void AllowContainerInventory(Inventory inventory, WorldObject owner, string source)
    {
        if (!StackOpenedContainers.Value || inventory == null)
        {
            return;
        }

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
            if (worldObject == null)
            {
                continue;
            }

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

    private static int GetStackCount(IEnumerable<WorldObject> items)
    {
        int stackSize = Math.Max(1, StackSize.Value);
        int stacks = 0;
        GroupCounts.Clear();

        foreach (WorldObject item in items)
        {
            if (item == null)
            {
                continue;
            }

            AddToStackCount(GetStackId(item), stackSize, ref stacks);
        }

        return stacks;
    }

    private static bool WouldBeFull(IEnumerable<WorldObject> items, int inventorySize, string incomingStackId)
    {
        int stackSize = Math.Max(1, StackSize.Value);
        int stacks = 0;
        GroupCounts.Clear();

        foreach (WorldObject item in items)
        {
            if (item != null)
            {
                AddToStackCount(GetStackId(item), stackSize, ref stacks);
            }
        }

        if (!string.IsNullOrEmpty(incomingStackId))
        {
            AddToStackCount(incomingStackId, stackSize, ref stacks);
        }

        return stacks > inventorySize;
    }

    private static void AddToStackCount(string stackId, int stackSize, ref int stacks)
    {
        GroupCounts.TryGetValue(stackId, out int count);
        count++;
        if (count == 1 || count > stackSize)
        {
            stacks++;
            count = 1;
        }

        GroupCounts[stackId] = count;
    }

    private static void AddStackDisplay(GameObject slot, int amount)
    {
        GameObject counter = new("StackCounter");
        counter.transform.SetParent(slot.transform, false);

        Text text = counter.AddComponent<Text>();
        text.font = font;
        text.fontSize = FontSize.Value;
        text.fontStyle = FontStyle.Bold;
        text.alignment = AlignLeft.Value
			? TextAnchor.LowerLeft
			: TextAnchor.LowerRight;
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
        BlockedInventories.Remove(inventoryId);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryAssociated), "SetInventory")]
    private static void InventoryAssociated_SetInventory_Post(InventoryAssociated __instance, Inventory inventory)
    {
        if (inventory == null) return;
        
        WorldObject owner = WorldObjectsHandler.Instance?.GetWorldObjectForInventory(inventory);
        if (owner != null && !LooksLikeStorage(owner))
        {
            DebugLog($"InventoryAssociated: inventory {inventory.GetId()} assigned to {owner.GetGroup()?.GetId() ?? "unknown"}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Inventory), "IsFull")]
    private static bool Inventory_IsFull_Pre(Inventory __instance, ref bool __result)
    {
        if (!CanStack(__instance))
        {
            return true;
        }

        __result = GetStackCount(__instance.GetInsideWorldObjects()) >= __instance.GetSize();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Inventory), "AddItem")]
    private static bool Inventory_AddItem_Pre(Inventory __instance, WorldObject worldObject, ref bool __result)
    {
        if (!CanStack(__instance) || worldObject == null)
        {
            return true;
        }

        if (WouldBeFull(__instance.GetInsideWorldObjects(), __instance.GetSize(), GetStackId(worldObject)))
        {
            __result = false;
            return false;
        }

        __result = (bool)addItemInInventory.Invoke(__instance, new object[] { worldObject });
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Inventory), "DropObjectsIfNotEnoughSpace")]
    private static bool Inventory_DropObjectsIfNotEnoughSpace_Pre(Inventory __instance, Vector3 dropPosition, bool removeOnly, ref List<WorldObject> __result)
    {
        if (!CanStack(__instance))
        {
            return true;
        }

        List<WorldObject> dropped = new();
        List<WorldObject> content = __instance.GetInsideWorldObjects().ToList();
        while (GetStackCount(content) > __instance.GetSize() && content.Count > 0)
        {
            WorldObject worldObject = content[content.Count - 1];
            dropped.Add(worldObject);
            __instance.RemoveItem(worldObject);
            content.RemoveAt(content.Count - 1);
            if (!removeOnly)
            {
                WorldObjectsHandler.Instance.DropOnFloor(worldObject, dropPosition, 0f, true, false, 0);
            }
        }

        __result = dropped;
        return false;
    }

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
        VisualsResourcesHandler visuals = (VisualsResourcesHandler)AccessTools.Field(typeof(InventoryDisplayer), "_visualResourcesHandler").GetValue(__instance);
        LogisticManager logisticManager = (LogisticManager)AccessTools.Field(typeof(InventoryDisplayer), "_logisticManager").GetValue(__instance);
        WindowsGamepadHandler gamepadHandler = (WindowsGamepadHandler)AccessTools.Field(typeof(InventoryDisplayer), "_windowsHandlerControllers").GetValue(__instance);
        int selectionIndex = (int)AccessTools.Field(typeof(InventoryDisplayer), "_selectionIndex").GetValue(__instance);

        GameObjects.DestroyAllChildren(grid.gameObject, false);
        GameObject inventoryBlock = visuals.GetInventoryBlock();
        bool isPlayerInventory = IsPlayerBackpack(inventory);
        HashSet<Group> authorizedGroups = inventory.GetAuthorizedGroups();
        Sprite authorizedIcon = authorizedGroups.Count > 0 ? visuals.GetGroupItemCategoriesSprite(authorizedGroups.First()) : null;
        List<List<WorldObject>> slots = CreateInventorySlots(inventoryWorldObjects);

        for (int i = 0; i < inventory.GetSize(); i++)
        {
            GameObject slot = UnityEngine.Object.Instantiate(inventoryBlock, grid.transform);
            InventoryBlock block = slot.GetComponent<InventoryBlock>();
            block.SetAuthorizedGroupIcon(authorizedIcon);

            if (i < slots.Count && slots[i].Count > 0)
            {
                List<WorldObject> stack = slots[i];
                WorldObject item = stack[stack.Count - 1];
                bool showDropIcon = isPlayerInventory && (!(item.GetGroup() is GroupItem groupItem) || !groupItem.GetCantBeDestroyed());
                block.SetDisplay(item, infosDisplayerBlockSwitches, showDropIcon);

                if (stack.Count > 1)
                {
                    AddStackDisplay(slot, stack.Count);
                }

                if (!item.GetIsLockedInInventory())
                {
                    EventsHelpers.AddTriggerEvent(slot, EventTriggerType.PointerClick, data => onImageClicked.Invoke(__instance, new object[] { data }), null, item, 0);
                    EventsHelpers.AddTriggerEvent(block.GetDropIcon(), EventTriggerType.PointerClick, data => onDropClicked.Invoke(__instance, new object[] { data }), null, item, 0);
                    slot.AddComponent<EventGamepadAction>().SetEventGamepadAction(
                        (Action<WorldObject, Group, int>)Delegate.CreateDelegate(typeof(Action<WorldObject, Group, int>), __instance, onActionViaGamepad),
                        item.GetGroup(),
                        item,
                        i,
                        (Action<WorldObject, Group, int>)Delegate.CreateDelegate(typeof(Action<WorldObject, Group, int>), __instance, onConsumeViaGamepad),
                        isPlayerInventory ? (Action<WorldObject, Group, int>)Delegate.CreateDelegate(typeof(Action<WorldObject, Group, int>), __instance, onDropViaGamepad) : null,
                        null);
                }

                if (shouldCheckItemsLogisticsStatus)
                {
                    block.SetLogisticStatus(logisticManager.WorldObjectIsInTasks(item));
                }
            }

            slot.SetActive(true);
            if (enabled && i == selectionIndex)
            {
                gamepadHandler.SelectForController(slot, true, false, true, true, true);
            }
            else if (!enabled)
            {
                Selectable selectable = slot.GetComponentInChildren<Selectable>();
                if (selectable != null)
                {
                    selectable.interactable = false;
                }
            }
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(JsonablesHelper), "JsonableToInventory")]
    private static bool JsonablesHelper_JsonableToInventory_Pre(JsonableInventory __0, Dictionary<int, WorldObject> __1, ref Inventory __result)
    {
        if (StackSize.Value <= 1)
        {
            return true;
        }

        JsonableInventory jsonableInventory = __0;
        Dictionary<int, WorldObject> objectMap = __1;
        List<WorldObject> list = new();
        string ids = jsonableInventory.woIds ?? "";
        foreach (string idText in ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(idText, out int id) && objectMap.TryGetValue(id, out WorldObject worldObject))
            {
                list.Add(worldObject);
            }
        }

        __result = new Inventory(
            jsonableInventory.id,
            jsonableInventory.size,
            list,
            GroupsHandler.GetGroupsViaString(jsonableInventory.supplyGrps, new HashSet<Group>()) as HashSet<Group>,
            GroupsHandler.GetGroupsViaString(jsonableInventory.demandGrps, new HashSet<Group>()) as HashSet<Group>,
            jsonableInventory.priority);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(JsonablesHelper), "InventoryToJsonable")]
    private static bool JsonablesHelper_InventoryToJsonable_Pre(Inventory __0, ref JsonableInventory __result)
    {
        if (StackSize.Value <= 1)
        {
            return true;
        }

        Inventory inventory = __0;
        StringBuilder builder = new();
        foreach (WorldObject worldObject in inventory.GetInsideWorldObjects())
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(worldObject.GetId());
        }

        __result = new JsonableInventory(
            inventory.GetId(),
            builder.ToString(),
            inventory.GetSize(),
            GroupsHandler.GetGroupsStringIds(inventory.GetLogisticEntity().GetDemandGroups()),
            GroupsHandler.GetGroupsStringIds(inventory.GetLogisticEntity().GetSupplyGroups()),
            inventory.GetLogisticEntity().GetPriority());
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InventoriesHandler), "UpdateOrCreateInventoryFromMessage")]
    private static bool InventoriesHandler_UpdateOrCreateInventoryFromMessage_Pre(int size, int inventoryId, int[] content, int[] contentIds, Inventory newInventory, ref Inventory __result)
    {
        if (StackSize.Value <= 1)
        {
            return true;
        }

        Inventory inventory = newInventory ?? new Inventory(inventoryId, size, null, null, null, 0);
        inventory.ClearContent(size);
        List<WorldObject> restoredContent = (List<WorldObject>)AccessTools.Field(typeof(Inventory), "_worldObjectsInInventory").GetValue(inventory);

        for (int i = 0; i < contentIds.Length; i++)
        {
            int worldObjectId = contentIds[i];
            WorldObject worldObject = WorldObjectsHandler.Instance.GetWorldObjectViaId(worldObjectId);
            if (worldObject == null && i < content.Length)
            {
                worldObject = WorldObjectsHandler.Instance.CreateNewWorldObject(GroupsHandler.GetGroupFromHash(content[i]), worldObjectId, null, true);
            }

            if (worldObject != null)
            {
                restoredContent.Add(worldObject);
            }
        }

        __result = inventory;
        return false;
    }
}
