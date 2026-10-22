using System;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.Items;

/// <summary>
/// The crafting "database": a registry of every <see cref="CraftingRecipe"/> plus a
/// live view of the total ingredients currently in the player's
/// <see cref="Inventory"/>. It is the single source of truth for "how many of
/// recipe X can I make right now?".
///
/// It is plain C# (not a Node) so it is trivially testable and can be shared by
/// the inventory crafting pane and, later, by placeable machines. The UI keeps a
/// reference to it and calls <see cref="RebuildInventory"/> whenever the
/// inventory changes; <see cref="PossibleCount"/> / <see cref="CanCraft"/> are
/// then O(ingredients) lookups against the cached counts.
/// </summary>
public sealed class CraftingDatabase
{
	/// <summary>
	/// The research level the player currently has. The tech tree does not exist
	/// yet, so this is the access stub: every starter recipe is level 0 and this
	/// is 0, so all recipes are unlocked. When the tech tree lands it updates
	/// this value and <see cref="GetUnlockedRecipes"/> starts filtering.
	/// </summary>
	public const int UnlockedResearchLevel = 0;

	private readonly List<CraftingRecipe> _recipes = new();

	/// <summary>
	/// Cached total count of each material across all inventory slots
	/// (materialId → total). Rebuilt by <see cref="RebuildInventory"/>; empty
	/// until the first rebuild.
	/// </summary>
	private readonly Dictionary<int, int> _inventoryTotals = new();

	/// <summary>All registered recipes (unfiltered, all research levels).</summary>
	public IReadOnlyList<CraftingRecipe> AllRecipes => _recipes;

	// ------------------------------------------------------------------
	// Shared (static) instance
	// ------------------------------------------------------------------

	private static CraftingDatabase _shared;

	/// <summary>
	/// A process-wide shared <see cref="CraftingDatabase"/> holding the default
	/// recipe set, accessible to systems that have no scene node to own their own
	/// (notably placeable machines like the <see cref="Machines.CrafterMachine"/>).
	/// It is built once (lazily) from <see cref="BuildDefaultDatabase"/>. The
	/// inventory UI keeps its OWN instance for the interactive crafting pane
	/// (which tracks the player's live inventory); machines use this shared one
	/// only to look up recipes by product/ingredient — they never call
	/// <see cref="Craft"/> on it (machines consume from their own buffers).
	/// </summary>
	public static CraftingDatabase Shared
	{
		get
		{
			_shared ??= BuildDefaultDatabase();
			return _shared;
		}
	}

	/// <summary>
	/// The recipes the player can currently see/craft (filtered by
	/// <see cref="UnlockedResearchLevel"/>). The tech-tree stub: with the level at
	/// 0 and all starter recipes at 0, this is the full set.
	/// </summary>
	public IReadOnlyList<CraftingRecipe> GetUnlockedRecipes()
	{
		var list = new List<CraftingRecipe>();
		foreach (var r in _recipes)
		{
			if (r.RequiredResearchLevel <= UnlockedResearchLevel)
			{
				list.Add(r);
			}
		}
		return list;
	}

	/// <summary>Adds a recipe to the registry.</summary>
	public void AddRecipe(CraftingRecipe recipe)
	{
		if (recipe != null)
		{
			_recipes.Add(recipe);
		}
	}

	/// <summary>
	/// Re-scans the inventory and rebuilds <see cref="_inventoryTotals"/>. Call
	/// this after any inventory change (the UI wires it to
	/// <see cref="Inventory.InventoryChanged"/> and once at init).
	/// </summary>
	public void RebuildInventory(Inventory inventory)
	{
		_inventoryTotals.Clear();
		if (inventory == null)
		{
			return;
		}

		foreach (var slot in inventory.Slots)
		{
			if (slot.IsEmpty)
			{
				continue;
			}
			_inventoryTotals.TryGetValue(slot.MaterialId, out int existing);
			_inventoryTotals[slot.MaterialId] = existing + slot.Count;
		}
	}

	/// <summary>The total count of a material across all slots (0 when none).</summary>
	public int CountOf(int materialId)
	{
		_inventoryTotals.TryGetValue(materialId, out int count);
		return count;
	}

	/// <summary>
	/// How many times <paramref name="recipe"/> can be crafted with the current
	/// inventory: the minimum of floor(total/required) over every ingredient
	/// (0 when any ingredient is missing).
	/// </summary>
	public int PossibleCount(CraftingRecipe recipe)
	{
		if (recipe == null)
		{
			return 0;
		}

		int possible = int.MaxValue;
		foreach (var (materialId, count) in recipe.Ingredients)
		{
			if (count <= 0)
			{
				continue;
			}
			_inventoryTotals.TryGetValue(materialId, out int have);
			int canMake = have / count;
			if (canMake < possible)
			{
				possible = canMake;
			}
		}

		// A recipe with no (or zero-quantity) ingredients is craftable infinitely;
		// cap it to a sane number so the UI can show a finite "max".
		return possible == int.MaxValue ? 9999 : possible;
	}

	/// <summary>True when at least one of <paramref name="recipe"/> can be crafted.</summary>
	public bool CanCraft(CraftingRecipe recipe) => PossibleCount(recipe) >= 1;

	/// <summary>
	/// The ingredients the player is currently short on for <paramref name="recipe"/>
	/// (materialId, have, needed) — used by the tooltip to highlight missing
	/// inputs in red. Empty when everything is present.
	/// </summary>
	public List<(int MaterialId, int Have, int Needed)> MissingIngredients(CraftingRecipe recipe)
	{
		var missing = new List<(int, int, int)>();
		if (recipe == null)
		{
			return missing;
		}

		foreach (var (materialId, count) in recipe.Ingredients)
		{
			if (count <= 0)
			{
				continue;
			}
			_inventoryTotals.TryGetValue(materialId, out int have);
			if (have < count)
			{
				missing.Add((materialId, have, count));
			}
		}
		return missing;
	}

	/// <summary>
	/// Performs a craft: validates that every ingredient is available, removes
	/// exactly <paramref name="amount"/> worth of ingredients from the inventory
	/// (draining slots), then adds <c>amount × recipe.OutputCount</c> of the
	/// product via <see cref="Inventory.CollectItem"/>. Returns the number of
	/// product stacks' worth actually crafted — 0 when the craft was blocked
	/// (not enough ingredients) or when there was no room for the product.
	///
	/// The inventory is the single source of truth for where items go; this
	/// method never touches the cursor or the UI directly.
	/// </summary>
	public int Craft(CraftingRecipe recipe, int amount, Inventory inventory)
	{
		if (recipe == null || inventory == null || amount <= 0)
		{
			return 0;
		}

		// Make sure the totals reflect the live inventory (the UI should keep it
		// current, but be safe against a stale cache).
		RebuildInventory(inventory);

		int possible = PossibleCount(recipe);
		if (possible <= 0)
		{
			return 0;
		}

		amount = Math.Min(amount, possible);

		// Remove the required ingredients, draining slots.
		foreach (var (materialId, count) in recipe.Ingredients)
		{
			int need = count * amount;
			if (need <= 0)
			{
				continue;
			}
			if (!RemoveIngredients(materialId, need, inventory))
			{
				// Should not happen (we just validated), but bail cleanly.
				return 0;
			}
		}

		// Add the product. CollectItem returns how many actually fit; if the
		// inventory is full it may be less than requested (or 0). We do not
		// refund ingredients in that case — the player simply has a full
		// inventory and should make room (matches "output into inventory").
		int productTotal = amount * recipe.OutputCount;
		int collected = inventory.CollectItem(ItemStack.From(recipe.ProductId, productTotal));

		RebuildInventory(inventory);
		return collected > 0 ? amount : 0;
	}

	/// <summary>
	/// Removes up to <paramref name="count"/> of <paramref name="materialId"/>
	/// from the inventory, draining slots from the end (storage) toward the
	/// front. Returns true when the full amount was removed.
	/// </summary>
	private static bool RemoveIngredients(int materialId, int count, Inventory inventory)
	{
		int remaining = count;
		// Drain from the last slot to the first so the hotbar is touched last.
		for (int i = inventory.Slots.Length - 1; i >= 0 && remaining > 0; i--)
		{
			var slot = inventory.Slots[i];
			if (slot.IsEmpty || slot.MaterialId != materialId)
			{
				continue;
			}
			int take = Math.Min(remaining, slot.Count);
			slot.Count -= take;
			if (slot.Count <= 0)
			{
				inventory.Slots[i] = ItemStack.From(0);
			}
			remaining -= take;
		}

		return remaining == 0;
	}

	// ------------------------------------------------------------------
	// Starter recipe set (Factorio-flavored, all research level 0)
	// ------------------------------------------------------------------

	/// <summary>
	/// Builds the default recipe set for testing. Each recipe produces a distinct
	/// crafted-product material (see <see cref="VoxelMaterialIds"/> ids 18..22) so
	/// the crafting pane shows real, different items. The tech tree / final item
	/// set will replace or extend this later.
	/// </summary>
	public static CraftingDatabase BuildDefaultDatabase()
	{
		var db = new CraftingDatabase();

		db.AddRecipe(new CraftingRecipe(
			productId: VoxelMaterialIds.Plank,
			ingredients: new (int, int)[] { (VoxelMaterialIds.Trunk, 1) },
			outputCount: 4,
			description: "Wooden planks. A basic building material.",
			category: "Wood"));

		db.AddRecipe(new CraftingRecipe(
			productId: VoxelMaterialIds.Coal,
			ingredients: new (int, int)[] { (VoxelMaterialIds.CoalOre, 1) },
			outputCount: 1,
			description: "Burnable fuel, smelted from coal ore.",
			category: "Smelting"));

		db.AddRecipe(new CraftingRecipe(
			productId: VoxelMaterialIds.IronPlate,
			ingredients: new (int, int)[] { (VoxelMaterialIds.IronOre, 1), (VoxelMaterialIds.Coal, 1) },
			outputCount: 1,
			description: "A pressed plate of iron. Used in machines and tools.",
			category: "Smelting"));

		db.AddRecipe(new CraftingRecipe(
			productId: VoxelMaterialIds.StoneBrick,
			ingredients: new (int, int)[] { (VoxelMaterialIds.Stone, 2) },
			outputCount: 1,
			description: "Cut stone bricks. A sturdier building block.",
			category: "Blocks"));

		db.AddRecipe(new CraftingRecipe(
			productId: VoxelMaterialIds.MachineFrame,
			ingredients: new (int, int)[] { (VoxelMaterialIds.IronPlate, 2), (VoxelMaterialIds.StoneBrick, 1) },
			outputCount: 1,
			description: "A rigid frame for mounting machines. A preview of the coming machine system.",
			category: "Machines"));

		return db;
	}

	/// <summary>The distinct category tags present in a recipe set (for the pane's sort tabs).</summary>
	public static List<string> CategoriesOf(IEnumerable<CraftingRecipe> recipes)
	{
		var set = new List<string>();
		foreach (var r in recipes)
		{
			if (!set.Contains(r.Category))
			{
				set.Add(r.Category);
			}
		}
		return set;
	}
}
