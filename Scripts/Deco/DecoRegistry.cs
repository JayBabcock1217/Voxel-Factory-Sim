using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.Deco;

/// <summary>
/// The static, world-wide lookup for <see cref="DecoDefinition"/>s — the
/// deco-system counterpart of <see cref="Machines.MachineRegistry"/>. It owns
/// the active <see cref="DecoDefConfig"/> (published by the
/// <see cref="VoxelWorld.DecoConfig"/> export) and answers "which definition
/// for this deco type / item id".
///
/// The spawner (<see cref="DecoSpawner"/>), the tool's placement path, and the
/// item registry all go through this, so the "which definition for which
/// deco" policy lives in exactly one spot. A null/empty registry simply finds
/// nothing — decos then fall back to their built-in default look (e.g. grass's
/// green crossed planes), so the starter types always work.
/// </summary>
public static class DecoRegistry
{
	/// <summary>The active deco registry config (null = no decos registered).</summary>
	public static DecoDefConfig Config { get; private set; }

	/// <summary>
	/// Sets (or clears) the active deco registry. The <see cref="VoxelWorld"/>
	/// calls this in _Ready and whenever its <see cref="VoxelWorld.DecoConfig"/>
	/// export is reassigned in the inspector.
	/// </summary>
	public static void SetConfig(DecoDefConfig config)
		=> Config = config;

	/// <summary>True when a deco registry is active and has at least one definition.</summary>
	public static bool IsReady
		=> Config != null && Config.Decos != null && Config.Decos.Length > 0;

	/// <summary>
	/// The definition for a deco type, or null when no definition is registered
	/// for it. The spawner passes this to the DecoVoxel so it can apply the
	/// editor-assigned material/mesh (falling back to the built-in look when
	/// null).
	/// </summary>
	public static DecoDefinition GetByType(DecoType type)
		=> Config?.GetByType(type);

	/// <summary>
	/// The definition for a deco item id, or null when no definition uses that
	/// id. This is the lookup the tool's placement path uses: the player holds
	/// a deco item, its MaterialId is the deco id, and this resolves it to the
	/// type (and definition) that should be spawned.
	/// </summary>
	public static DecoDefinition GetByItemId(int itemId)
		=> Config?.GetByItemId(itemId);

	/// <summary>
	/// Resolves a held deco item id to the <see cref="DecoType"/> to spawn.
	/// When a definition is registered for the id, its type is used; otherwise
	/// it falls back to the id's built-in mapping (id <c>DecoFirst + (int)type</c>),
	/// so the starter types are always placeable even before a definition is
	/// registered. Returns null when the id is not a deco id.
	/// </summary>
	public static DecoType? TypeForItemId(int itemId)
	{
		if (!VoxelMaterialIds.IsDecoId(itemId))
		{
			return null;
		}
		var def = GetByItemId(itemId);
		if (def != null)
		{
			return def.Type;
		}
		// Built-in mapping: the id range starts at DecoFirst, offset by the
		// type value (DecoGrass = 500 = DecoFirst + (int)DecoType.Grass).
		int asType = itemId - VoxelMaterialIds.DecoFirst;
		return Enum.IsDefined(typeof(DecoType), asType) ? (DecoType?)asType : null;
	}
}
