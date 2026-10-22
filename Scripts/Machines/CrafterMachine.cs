using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;

namespace FactorySim.Machines;

/// <summary>
/// The crafter machine's behavior — a placeable, automated crafting station. It
/// is the "first processor" that proves the item flow end-to-end:
///
///   1. GATHER: pull recipe ingredients from adjacent conveyors (any of the six
///      sides) into its INPUT buffer.
///   2. CRAFT:  when the input buffer holds a full set of ingredients for one of
///      the recipes it is set to, it runs that recipe over its
///      <see cref="MachineDefinition.ProcessTime"/> (the craft duration), then
///      puts the product into its OUTPUT buffer.
///   3. The OUTPUT buffer is pulled by adjacent conveyors (any side).
///
/// Which recipe a crafter produces is chosen by the player via the machine UI
/// (right-click → pick a recipe). Until a recipe is chosen, the crafter idles.
/// A crafter CONSUMES power (its definition's <see cref="MachineDefinition.PowerConsumption"/>) —
/// with no power it idles but keeps its buffers (see <see cref="Machine.HasPower"/>).
///
/// The crafter reuses the existing <see cref="CraftingDatabase"/>/
/// <see cref="CraftingRecipe"/> system: a crafter *is* a real crafting machine,
/// just driven by conveyor-fed ingredients instead of the player's inventory.
/// </summary>
public partial class CrafterMachine : Machine
{
	/// <summary>The machine's INPUT buffer (ingredients gathered from conveyors).</summary>
	public MachineBuffer Input { get; private set; }

	/// <summary>The machine's OUTPUT buffer (crafted product, pulled by conveyors).</summary>
	public MachineBuffer Output { get; private set; }

	public override MachineBuffer InputBuffer => Input;
	public override MachineBuffer OutputBuffer => Output;

	/// <summary>The recipe the crafter is currently set to produce (null = idle, no recipe chosen).</summary>
	public CraftingRecipe Recipe { get; set; }

	/// <summary>0..1 progress through the current craft (0 when idle or between crafts).</summary>
	public float CraftProgress { get; private set; }

	private float _craftTimer;
	private bool _crafting;

	protected override void StartMachine()
	{
		// Input buffer holds the gathered ingredients; output holds the product.
		// The definition's BufferSize (default 0 → 1) is a floor; a crafter wants
		// a few slots so it can hold a full recipe set. Use max(BufferSize, 4).
		int cap = Math.Max(Definition?.BufferSize ?? 0, 4);
		Input = new MachineBuffer(cap);
		Output = new MachineBuffer(cap);
		_crafting = false;
		_craftTimer = 0f;
		// (The base StartMachine already sets IsRunning = true.)
	}

	protected override void ProcessMachine(float delta)
	{
		if (Input == null || Output == null || Recipe == null)
		{
			return;
		}
		// A crafter needs power to run. Without power it idles but KEEPS its
		// buffers (items are not lost).
		if (!HasPower())
		{
			return;
		}

		if (_crafting)
		{
			// Advance the in-progress craft.
			float craftTime = Mathf.Max(0.05f, Recipe.CraftTime);
			_craftTimer += delta;
			CraftProgress = Mathf.Clamp(_craftTimer / craftTime, 0f, 1f);
			if (_craftTimer >= craftTime)
			{
				FinishCraft();
			}
			return;
		}

		// Not crafting: try to gather ingredients, then start a craft if we have
		// a full set.
		GatherIngredients();
		if (Input.CanCraftRecipe(Recipe))
		{
			// Consume the ingredients from the input buffer and start the timer.
			RemoveRecipeIngredients(Recipe);
			_crafting = true;
			_craftTimer = 0f;
			CraftProgress = 0f;
		}
	}

	/// <summary>
	/// Pulls up to one item of each missing ingredient from adjacent conveyors
	/// into the input buffer, any side. Called each tick while idle so the
	/// buffer refills as items arrive on the belt.
	/// </summary>
	private void GatherIngredients()
	{
		foreach (var (materialId, count) in Recipe.Ingredients)
		{
			if (count <= 0)
			{
				continue;
			}
			// Only pull if we are still short of this ingredient.
			if (Input.CountOf(materialId) >= count)
			{
				continue;
			}
			foreach (var neighborPos in NeighborPositions())
			{
				var neighbor = NeighborMachine(neighborPos);
				if (neighbor == null || neighbor == this)
				{
					continue;
				}
				var output = neighbor.OutputBuffer;
				if (output == null || output.IsEmpty)
				{
					continue;
				}
				var item = output.TryExtract(materialId, 1);
				if (!item.IsEmpty)
				{
					int inserted = Input.TryInsert(item);
					if (inserted < item.Count)
					{
						// Input full: return the remainder to the neighbor.
						output.TryInsert(ItemStack.From(item.MaterialId, item.Count - inserted));
					}
				}
			}
		}
	}

	/// <summary>Removes exactly one recipe's worth of ingredients from the input buffer.</summary>
	private void RemoveRecipeIngredients(CraftingRecipe recipe)
	{
		foreach (var (materialId, count) in recipe.Ingredients)
		{
			if (count <= 0)
			{
				continue;
			}
			Input.TryExtract(materialId, count);
		}
	}

	/// <summary>
	/// Completes the in-progress craft: puts the product into the output buffer
	/// (as much as fits; the rest is lost to a full output, which the player
	/// should prevent by keeping a conveyor draining it), and resets the craft
	/// state.
	/// </summary>
	private void FinishCraft()
	{
		int productTotal = Recipe.OutputCount;
		int inserted = Output.TryInsert(ItemStack.From(Recipe.ProductId, productTotal));
		_crafting = false;
		_craftTimer = 0f;
		CraftProgress = 0f;
	}

	/// <summary>
	/// The list of recipes this crafter can be set to (from the shared crafting
	/// database). The machine UI shows this so the player can pick a product.
	/// </summary>
	public List<CraftingRecipe> AvailableRecipes => new(CraftingDatabase.Shared.AllRecipes);
}
