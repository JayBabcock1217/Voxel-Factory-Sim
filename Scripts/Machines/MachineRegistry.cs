using Godot;
using System;
using System.Collections.Generic;

namespace FactorySim.Machines;

/// <summary>
/// The factory that turns a machine item into a placed <see cref="Machine"/>
/// node. It owns the <see cref="MachineDefConfig"/> registry and the
/// item-id → definition mapping, and it decides WHICH <see cref="Machine"/>
/// scene to instantiate for a given machine type:
///
///   1. If the definition has a <see cref="MachineDefinition.MachineSceneOverride"/>
///      (a .tscn path), that scene is loaded and instantiated — this is how a
///      machine with custom logic (a Turret subclass, etc.) plugs in.
///   2. Else the base <see cref="Machine"/> is constructed directly.
///
/// This is the single place the spawner (and any other system that creates
/// machines) goes through, so the "which node for which machine" policy lives
/// in exactly one spot and is easy to extend.
/// </summary>
public static class MachineRegistry
{
	// The shared registry config, set by the MachineWorld (or a test). Null-safe:
	// lookups on a null/empty registry simply find nothing.
	public static MachineDefConfig Config { get; private set; }

	// Cache of loaded override scenes, keyed by the .tscn path.
	private static readonly Dictionary<string, PackedScene> _sceneCache = new();

	/// <summary>Sets (or clears) the active machine registry. The MachineWorld calls this at setup.</summary>
	public static void SetConfig(MachineDefConfig config)
	{
		Config = config;
		_sceneCache.Clear();
	}

	/// <summary>True when a machine registry is active and has at least one definition.</summary>
	public static bool IsReady => Config != null && Config.Machines != null && Config.Machines.Length > 0;

	/// <summary>
	/// The definition for a machine type. Returns the registered definition when
	/// one exists; otherwise a DEFAULT (placeholder) definition for that type, so
	/// the starter machine types are always placeable (rendering a placeholder
	/// cube) even before the user registers a custom one. Only returns null for
	/// ids that aren't machine ids at all.
	/// </summary>
	public static MachineDefinition GetByType(MachineType type)
	{
		var def = Config?.GetByType(type);
		return def ?? DefaultFor(type);
	}

	/// <summary>
	/// The definition for an item id. Returns the registered definition when one
	/// exists; otherwise a DEFAULT (placeholder) definition, so the starter
	/// machine types are always placeable. Returns null only when the id is not
	/// a machine id.
	/// </summary>
	public static MachineDefinition GetByItemId(int itemId)
	{
		var def = Config?.GetByItemId(itemId);
		if (def != null)
		{
			return def;
		}
		// No registered def: fall back to a default if it's a valid machine id.
		if (Enum.TryParse<MachineType>(itemId.ToString(), out var type))
		{
			return DefaultFor(type);
		}
		return null;
	}

	/// <summary>
	/// True when <paramref name="itemId"/> is a machine item id (in the machine
	/// range). The place-ability uses this to decide "place a voxel" vs "place a
	/// machine". Any id in the range is placeable — a registered definition
	/// customizes it, and the starter types work with defaults.
	/// </summary>
	public static bool IsMachineItemId(int itemId)
		=> FactorySim.Voxel.VoxelMaterialIds.IsMachineId(itemId);

	/// <summary>
	/// Builds a minimal DEFAULT definition for a machine type (no mesh → the
	/// Machine renders its placeholder cube; default stats). This is the fallback
	/// that makes every machine type in the enum placeable out of the box.
	/// </summary>
	private static MachineDefinition DefaultFor(MachineType type)
	{
		return new MachineDefinition
		{
			Type = type,
			Name = type.ToString(),
			ItemId = (int)type,
			// No Mesh / MeshScene → placeholder cube. Default stats.
		};
	}

	/// <summary>
	/// Creates a <see cref="Machine"/> node for the given item id, ready to be
	/// added to the tree at <paramref name="voxelPos"/>. Returns null only when
	/// the id isn't a machine id at all (a registered definition customizes the
	/// machine; the starter types use defaults). The node is NOT added to the
	/// tree here — the spawner parents it to the shared "Machines" container and
	/// sets its position.
	///
	/// The node's <c>Initialize(def, voxelPos, voxelSize)</c> is called by the
	/// spawner AFTER it is in the tree (so GlobalPosition works). This method
	/// just constructs the right node type.
	/// </summary>
	public static Machine CreateMachine(int itemId, Vector3I voxelPos)
	{
		var def = GetByItemId(itemId);
		if (def == null)
		{
			return null;
		}
		return CreateMachineForDef(def);
	}

	/// <summary>
	/// Constructs the right <see cref="Machine"/> node for a definition:
	///   1. A custom <see cref="MachineDefinition.MachineSceneOverride"/> scene
	///      ALWAYS wins (the escape hatch for fully custom machine nodes).
	///   2. Else the <see cref="MachineBehaviorRegistry"/> picks the built-in
	///      behavior subclass for the type (conveyor/crafter/power source) or
	///      falls back to the base <see cref="Machine"/> for static machines.
	/// The returned node is not yet in the tree.
	/// </summary>
	public static Machine CreateMachineForDef(MachineDefinition def)
	{
		if (def == null)
		{
			return null;
		}

		// A custom subclass scene wins when provided.
		if (!string.IsNullOrEmpty(def.MachineSceneOverride))
		{
			var scene = LoadScene(def.MachineSceneOverride);
			if (scene != null)
			{
				if (scene.Instantiate() is Machine custom)
				{
					return custom;
				}
				// The scene's root isn't a Machine — fall back to the behavior
				// registry and warn (the user pointed at the wrong scene).
				GD.PushWarning($"[MachineRegistry] Machine scene '{def.MachineSceneOverride}' for {def.Type} is not a Machine; using the behavior registry.");
			}
			else
			{
				GD.PushError($"[MachineRegistry] Failed to load machine scene '{def.MachineSceneOverride}' for {def.Type}; using the behavior registry.");
			}
		}

		// No override: use the built-in behavior for the type (or the base
		// Machine for static types).
		return MachineBehaviorRegistry.CreateForType(def.Type);
	}

	private static PackedScene LoadScene(string path)
	{
		if (_sceneCache.TryGetValue(path, out var cached))
		{
			return cached;
		}
		var scene = GD.Load<PackedScene>(path);
		if (scene != null)
		{
			_sceneCache[path] = scene;
		}
		return scene;
	}
}
