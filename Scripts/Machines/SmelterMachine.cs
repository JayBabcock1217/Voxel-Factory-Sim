using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;

namespace FactorySim.Machines;

/// <summary>
/// The smelter machine's behavior — the factory's heat-based processor. It is
/// the "ore → ingot" machine that turns raw mined materials into their refined
/// form:
///
///   1. GATHER: pull the selected recipe's INPUT material from adjacent
///      conveyors (any of the six sides) into its INPUT buffer.
///   2. SMELT:  when the input buffer holds at least one of the input material,
///      it consumes one and runs the smelt over its
///      <see cref="MachineDefinition.ProcessTime"/> (the smelt duration), then
///      puts <see cref="MachineDefinition.OutputCount"/> (default 1) of the
///      recipe's OUTPUT material into its OUTPUT buffer.
///   3. The OUTPUT buffer is pulled by adjacent conveyors (any side).
///
/// Which recipe a smelter produces is chosen by the player via the machine UI
/// (right-click → pick a recipe from the definition's paired
/// <c>SmeltInputs</c>/<c>SmeltOutputs</c> lists). Until a recipe is chosen, the
/// smelter idles. A smelter CONSUMES power (its definition's
/// <see cref="MachineDefinition.PowerConsumption"/>) — with no power it idles
/// but keeps its buffers (see <see cref="Machine.HasPower"/>).
///
/// The smelter's recipes are DATA-DRIVEN: they come from the
/// <see cref="MachineDefinition.SmeltableRecipes"/> on its definition, so the
/// user can tune which materials smelt into which in the editor without code
/// changes. A freshly-spawned smelter with no selected recipe defaults to its
/// definition's FIRST recipe (so it works out of the box), unless the player
/// picks a different one (or clears it) via the UI.
///
/// This is a <see cref="Machine"/> subclass selected for <see
/// cref="MachineType.Smelter"/> (see <see cref="MachineBehaviorRegistry"/>). It
/// carries no visual of its own — the mesh comes from the
/// <see cref="MachineDefinition"/> — only the per-tick behavior.
/// </summary>
public partial class SmelterMachine : Machine
{
	/// <summary>The machine's INPUT buffer (the ore/raw material being smelted).</summary>
	public MachineBuffer Input { get; private set; }

	/// <summary>The machine's OUTPUT buffer (the smelted product, pulled by conveyors).</summary>
	public MachineBuffer Output { get; private set; }

	public override MachineBuffer InputBuffer => Input;
	public override MachineBuffer OutputBuffer => Output;

	/// <summary>
	/// The recipe index the smelter is currently set to (into
	/// <see cref="SmeltableRecipes"/>). -1 means "not yet chosen" — the smelter
	/// idles until a recipe is selected (or it auto-selects the first one on
	/// start, see <see cref="StartMachine"/>).
	/// </summary>
	public int RecipeIndex { get; set; } = -1;

	/// <summary>0..1 progress through the current smelt (0 when idle or between smelts).</summary>
	public float SmeltProgress { get; private set; }

	/// <summary>The smelter's available recipes (input, output) pairs from its definition.</summary>
	public IReadOnlyList<(int Input, int Output)> SmeltableRecipes
		=> Definition?.SmeltableRecipes ?? Array.Empty<(int, int)>();

	/// <summary>
	/// The currently selected recipe as (input, output), or (0, 0) when no
	/// recipe is selected. Used by the machine UI to show what the smelter is
	/// set to produce.
	/// </summary>
	public (int Input, int Output) CurrentRecipe
	{
		get
		{
			if (RecipeIndex >= 0 && RecipeIndex < SmeltableRecipes.Count)
			{
				return SmeltableRecipes[RecipeIndex];
			}
			return (0, 0);
		}
	}

	private float _smeltTimer;
	private bool _smelting;

	protected override void StartMachine()
	{
		// Input holds the raw material; output holds the smelted product. The
		// definition's BufferSize (default 0 → 1) is a floor; a smelter wants a
		// few slots so it can hold a queue of ore. Use max(BufferSize, 4).
		int cap = Math.Max(Definition?.BufferSize ?? 0, 4);
		Input = new MachineBuffer(cap);
		Output = new MachineBuffer(cap);
		_smelting = false;
		_smeltTimer = 0f;
		SmeltProgress = 0f;

		// Auto-select the definition's first recipe so a freshly-spawned smelter
		// works out of the box (the player can still change or clear it via the
		// UI, and a restored save overrides this with the saved recipe).
		if (RecipeIndex < 0 && SmeltableRecipes.Count > 0)
		{
			RecipeIndex = 0;
		}
	}

	protected override void ProcessMachine(float delta)
	{
		if (Input == null || Output == null)
		{
			return;
		}
		// A smelter needs a selected recipe and power to run. Without either it
		// idles but KEEPS its buffers (items are not lost).
		var recipe = CurrentRecipe;
		if (recipe.Input == 0 || recipe.Output == 0)
		{
			return;
		}
		if (!HasPower())
		{
			return;
		}

		if (_smelting)
		{
			// Advance the in-progress smelt.
			float smeltTime = Mathf.Max(0.05f, Definition?.ProcessTime ?? 1f);
			_smeltTimer += delta;
			SmeltProgress = Mathf.Clamp(_smeltTimer / smeltTime, 0f, 1f);
			if (_smeltTimer >= smeltTime)
			{
				FinishSmelt(recipe);
			}
			return;
		}

		// Not smelting: pull the input material from adjacent conveyors, then
		// start a smelt if the input buffer holds at least one.
		GatherInput(recipe.Input);
		if (Input.HasAtLeast(recipe.Input, 1))
		{
			// Consume one unit of the input material and start the timer.
			Input.TryExtract(recipe.Input, 1);
			_smelting = true;
			_smeltTimer = 0f;
			SmeltProgress = 0f;
		}
	}

	/// <summary>
	/// Pulls one unit of the selected recipe's input material from the FIRST
	/// adjacent conveyor (any of the six sides) that has it, into the input
	/// buffer. Called each tick while idle so the buffer refills as ore arrives
	/// on the belt. Pulling one unit per tick (like the crafter's gather) keeps
	/// the flow one-by-one and avoids over-filling.
	/// </summary>
	private void GatherInput(int inputMaterial)
	{
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
			var item = output.TryExtract(inputMaterial, 1);
			if (!item.IsEmpty)
			{
				int inserted = Input.TryInsert(item);
				if (inserted < item.Count)
				{
					// Input full: return the remainder to the neighbor.
					output.TryInsert(ItemStack.From(item.MaterialId, item.Count - inserted));
				}
				return; // pulled one unit this tick
			}
		}
	}

	/// <summary>
	/// Completes the in-progress smelt: puts the product into the output buffer
	/// (as much as fits; the rest is lost to a full output, which the player
	/// should prevent by keeping a conveyor draining it), and resets the smelt
	/// state.
	/// </summary>
	private void FinishSmelt((int Input, int Output) recipe)
	{
		int productTotal = Definition?.OutputCount > 0 ? Definition.OutputCount : 1;
		Output.TryInsert(ItemStack.From(recipe.Output, productTotal));
		_smelting = false;
		_smeltTimer = 0f;
		SmeltProgress = 0f;
	}

	// ------------------------------------------------------------------
	// Save/restore: also capture/restore the selected recipe index.
	// ------------------------------------------------------------------

	public override MachineSnapshot TakeSnapshot()
	{
		var snap = base.TakeSnapshot();
		snap.RecipeIndex = RecipeIndex;
		return snap;
	}

	public override void RestoreSnapshot(MachineSnapshot snap)
	{
		base.RestoreSnapshot(snap);
		if (snap != null && snap.HasRecipe && SmeltableRecipes.Count > 0)
		{
			// The saved recipe index is resolved against the live recipe list; if
			// it is out of range (the recipe list changed between saves) it is
			// clamped to the last valid index. Only apply when the list is
			// non-empty (otherwise keep the auto-selected recipe, if any).
			int count = SmeltableRecipes.Count;
			RecipeIndex = System.Math.Min(snap.RecipeIndex, count - 1);
		}
	}
}
