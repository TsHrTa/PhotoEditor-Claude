using PhotoEditor.Core.Masks;

namespace PhotoEditor.Core.Editing;

/// <summary>Immutable edit operations on <see cref="EditState"/> masks.</summary>
public static class EditStateOperations
{
    public static Mask? FindMask(this EditState state, Guid id) => state.Masks.Find(m => m.Id == id);

    /// <summary>"Mask N" with the lowest N not used yet.</summary>
    public static string NextMaskName(this EditState state)
    {
        for (int n = 1; ; n++)
        {
            var name = $"Mask {n}";
            if (!state.Masks.Exists(m => m.Name == name))
                return name;
        }
    }

    public static EditState AddMask(this EditState state, Mask mask) => state with { Masks = state.Masks.Add(mask) };

    public static EditState RemoveMask(this EditState state, Guid id) =>
        state with { Masks = state.Masks.RemoveAll(m => m.Id == id) };

    /// <summary>Replaces the mask with the given id by <paramref name="update"/>(mask); unchanged if not found.</summary>
    public static EditState UpdateMask(this EditState state, Guid id, Func<Mask, Mask> update)
    {
        int i = state.Masks.FindIndex(m => m.Id == id);
        return i < 0 ? state : state with { Masks = state.Masks.SetItem(i, update(state.Masks[i])) };
    }

    public static Mask AddComponent(this Mask mask, MaskComponent component) =>
        mask with { Components = mask.Components.Add(component) };

    public static Mask ReplaceComponent(this Mask mask, int index, MaskComponent component) =>
        mask with { Components = mask.Components.SetItem(index, component) };

    public static Mask RemoveComponent(this Mask mask, int index) =>
        mask with { Components = mask.Components.RemoveAt(index) };
}
