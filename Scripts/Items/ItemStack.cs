using FactorySim.Machines;

namespace FactorySim.Items;

/// <summary>
/// A stack of identical items: a voxel material id plus a count. A plain data
/// class (POCO), not a Resource — it is copied by value freely between the
/// inventory, the cursor, and item drops. Material id 0 (or a count of 0) is
/// an empty stack.
///
/// A stack may also carry a <see cref="ToolType"/>: when <c>MaterialId</c> is a
/// tool id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsToolId"/>), the
/// <see cref="ToolType"/> tells the world/tool system what kind of tool this is
/// (shovel, pickaxe, ...). Tool stacks never hold more than one (Count is 1) —
/// a tool is not stackable like a material.
/// </summary>
public class ItemStack
{
	/// <summary>Maximum items per stack — used for drops and the inventory alike.</summary>
	public const int StackLimit = 64;

	/// <summary>The voxel material id of the item (byte 1-255; 0 = air/empty).</summary>
	public int MaterialId { get; set; }

	/// <summary>How many items are in this stack.</summary>
	public int Count { get; set; }

	// The kind fields are stored explicitly (set by FromTool/FromMachine/
	// FromWeapon/FromAmmo), BUT they are also DERIVED from the MaterialId as a
	// fallback. This is critical for save/load: the save file stores only
	// (MaterialId, Count), so a loaded stack has no explicit kind field — but
	// the kind is fully determined by the MaterialId range (tools 100-103,
	// machines 200-299, weapons 300-399, ammo 400-499), so the derived getter
	// reconstructs it. See the individual property docs.

	/// <summary>
	/// The tool kind this stack represents, when <see cref="MaterialId"/> is a
	/// tool id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsToolId"/>).
	/// Returns the explicit value if set, otherwise derives it from
	/// <see cref="MaterialId"/> (so a stack loaded from a save — which stores
	/// only the id — is still recognized as the right tool).
	/// </summary>
	public ToolType? ToolType
	{
		get => _toolType ?? (_isToolId ? (ToolType?)MaterialId : null);
		set => _toolType = value;
	}
	private ToolType? _toolType;
	private bool _isToolId => FactorySim.Voxel.VoxelMaterialIds.IsToolId(MaterialId);

	/// <summary>
	/// The machine kind this stack represents, when <see cref="MaterialId"/> is
	/// a machine id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsMachineId"/>).
	/// Returns the explicit value if set, otherwise derives it from
	/// <see cref="MaterialId"/> (so a saved/loaded stack is still recognized).
	/// When set, placing this item spawns a <see cref="FactorySim.Machines.Machine"/>
	/// node (its imported mesh) instead of a voxel.
	/// </summary>
	public MachineType? MachineType
	{
		get => _machineType ?? (_isMachineId ? (MachineType?)MaterialId : null);
		set => _machineType = value;
	}
	private MachineType? _machineType;
	private bool _isMachineId => FactorySim.Voxel.VoxelMaterialIds.IsMachineId(MaterialId);

	/// <summary>
	/// The weapon kind this stack represents, when <see cref="MaterialId"/> is a
	/// weapon id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsWeaponId"/>).
	/// Returns the explicit value if set, otherwise derives it from
	/// <see cref="MaterialId"/> (so a saved/loaded stack is still recognized as a
	/// weapon). A weapon is a single, non-stacking item (Count is 1) that, when
	/// selected in the hotbar, becomes the active weapon the player can fire with
	/// the primary mouse button. Its loaded ammo lives in the weapon's own ammo
	/// slot (see the ammo feature), not on this stack.
	/// </summary>
	public WeaponType? WeaponType
	{
		get => _weaponType ?? (_isWeaponId ? (WeaponType?)MaterialId : null);
		set => _weaponType = value;
	}
	private WeaponType? _weaponType;
	private bool _isWeaponId => FactorySim.Voxel.VoxelMaterialIds.IsWeaponId(MaterialId);

	/// <summary>
	/// The ammo kind this stack represents, when <see cref="MaterialId"/> is an
	/// ammo id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsAmmoId"/>).
	/// Returns the explicit value if set, otherwise derives it from
	/// <see cref="MaterialId"/> (so a saved/loaded stack is still recognized as
	/// ammo). Ammo is a STACKABLE consumable (Count up to <see cref="StackLimit"/>)
	/// whose only use is being loaded into a compatible weapon's ammo slot and
	/// consumed as the weapon fires.
	/// </summary>
	public AmmoType? AmmoType
	{
		get => _ammoType ?? (_isAmmoId ? (AmmoType?)MaterialId : null);
		set => _ammoType = value;
	}
	private AmmoType? _ammoType;
	private bool _isAmmoId => FactorySim.Voxel.VoxelMaterialIds.IsAmmoId(MaterialId);

	/// <summary>
	/// The BLOCK LIGHT LEVEL this item emits when placed as a voxel (0-15).
	/// 0 = not a light source (the default for every ordinary material). A
	/// non-zero value marks the item as a light source: when it is placed, the
	/// world stores this level on that voxel and the sky-light flood fill seeds
	/// the surrounding air from it, so the block lights its neighbourhood the
	/// way a Minecraft torch or glowstone does. The level doubles as the
	/// approximate reach in blocks (15 = bright, 7 = dim). Set per item
	/// (e.g. a crafted "torch" item sets this to 14) so the SAME material can be
	/// a light source or not depending on which item placed it.
	/// </summary>
	public byte LightLevel { get; set; }

	/// <summary>True when the stack holds nothing.</summary>
	public bool IsEmpty => MaterialId == 0 || Count <= 0;

	/// <summary>True when this stack is a tool (carries a non-null <see cref="ToolType"/>).</summary>
	public bool IsTool => ToolType.HasValue;

	/// <summary>True when this stack is a machine item (carries a non-null <see cref="MachineType"/>).</summary>
	public bool IsMachine => MachineType.HasValue;

	/// <summary>True when this stack is a weapon item (carries a non-null <see cref="WeaponType"/>).</summary>
	public bool IsWeapon => WeaponType.HasValue;

	/// <summary>True when this stack is an ammo item (carries a non-null <see cref="AmmoType"/>).</summary>
	public bool IsAmmo => AmmoType.HasValue;

	/// <summary>Creates a stack of <paramref name="count"/> of a material.</summary>
	public static ItemStack From(int materialId, int count = 1)
		=> new() { MaterialId = materialId, Count = count };

	/// <summary>Creates a single tool stack of the given <paramref name="type"/>.</summary>
	public static ItemStack FromTool(ToolType type)
		=> new()
		{
			MaterialId = (int)type,
			Count = 1,
			ToolType = type,
		};

	/// <summary>Creates a single machine item of the given <paramref name="type"/>.</summary>
	public static ItemStack FromMachine(MachineType type)
		=> new()
		{
			MaterialId = (int)type,
			Count = 1,
			MachineType = type,
		};

	/// <summary>Creates a single weapon item of the given <paramref name="type"/> (non-stacking, no ammo loaded).</summary>
	public static ItemStack FromWeapon(WeaponType type)
		=> new()
		{
			MaterialId = (int)type,
			Count = 1,
			WeaponType = type,
		};

	/// <summary>Creates a stack of <paramref name="count"/> of the given <paramref name="type"/> of ammo.</summary>
	public static ItemStack FromAmmo(AmmoType type, int count)
		=> new()
		{
			MaterialId = (int)type,
			Count = count,
			AmmoType = type,
		};
}
