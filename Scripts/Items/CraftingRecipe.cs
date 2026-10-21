using System.Collections.Generic;

namespace FactorySim.Items;

/// <summary>
/// A single crafting recipe in the Factorio model: having the required
/// <see cref="Ingredients"/> in the inventory lets you produce <see cref="OutputCount"/>
/// of <see cref="ProductId"/>. There is no crafting grid or arrangement — just
/// "inputs present → output available".
///
/// This is a plain data class (like <see cref="ItemStack"/>): it is copied by
/// value freely and never a Godot Resource. The display name / description of
/// the product come from the world's material config (looked up by
/// <see cref="ProductId"/>); <see cref="Description"/> is an optional per-recipe
/// blurb that overrides the product's own description in the tooltip.
/// </summary>
public sealed class CraftingRecipe
{
	/// <summary>The product material id produced by this recipe.</summary>
	public int ProductId { get; }

	/// <summary>How many product items one craft produces (default 1).</summary>
	public int OutputCount { get; }

	/// <summary>
	/// The required ingredients as (materialId, count) pairs. Every ingredient
	/// must be present in at least <c>count</c> total (across all inventory
	/// slots) for the recipe to be craftable.
	/// </summary>
	public IReadOnlyList<(int MaterialId, int Count)> Ingredients { get; }

	/// <summary>
	/// The research level required to see/craft this recipe. The tech tree does
	/// not exist yet, so the access stub treats the player as level
	/// <see cref="CraftingDatabase.UnlockedResearchLevel"/> and every starter
	/// recipe is 0 (unlocked). When the tech tree lands, this is the field it
	/// gates on.
	/// </summary>
	public int RequiredResearchLevel { get; }

	/// <summary>Optional per-recipe description shown in the tooltip (empty = use the product's material description).</summary>
	public string Description { get; }

	/// <summary>Category tag used by the crafting pane's sort tabs (e.g. "Blocks", "Ores").</summary>
	public string Category { get; }

	/// <summary>Seconds to craft ONE batch (the recipe's OutputCount). The crafting queue advances one job at a time over this duration.</summary>
	public float CraftTime { get; }

	public CraftingRecipe(int productId, IReadOnlyList<(int MaterialId, int Count)> ingredients,
		int outputCount = 1, int requiredResearchLevel = 0, string description = "", string category = "General",
		float craftTime = 1f)
	{
		ProductId = productId;
		Ingredients = ingredients;
		OutputCount = outputCount <= 0 ? 1 : outputCount;
		RequiredResearchLevel = requiredResearchLevel;
		Description = description ?? "";
		Category = string.IsNullOrEmpty(category) ? "General" : category;
		CraftTime = craftTime <= 0f ? 1f : craftTime;
	}
}
