using PhotoEditor.Core.Adjustments;
using PhotoEditor.Core.Editing;
using PhotoEditor.Core.Masks;
using PhotoEditor.Tests.Masks;

namespace PhotoEditor.Tests.Editing;

public class EditStateOperationsTests
{
    [Fact]
    public void NextMaskName_FillsGaps()
    {
        var s = EditState.Default;
        Assert.Equal("Mask 1", s.NextMaskName());
        s = s.AddMask(new Mask { Name = "Mask 1" }).AddMask(new Mask { Name = "Mask 3" });
        Assert.Equal("Mask 2", s.NextMaskName());
    }

    [Fact]
    public void AddUpdateRemoveMask()
    {
        var mask = new Mask { Name = "A" };
        var s = EditState.Default.AddMask(mask);
        Assert.Same(mask, s.FindMask(mask.Id));

        s = s.UpdateMask(mask.Id, m => m with { Adjustments = new AdjustmentSettings { Exposure = 1 } });
        Assert.Equal(1, s.FindMask(mask.Id)!.Adjustments.Exposure);
        Assert.Equal(s, s.UpdateMask(Guid.NewGuid(), m => m with { Name = "X" }));

        s = s.RemoveMask(mask.Id);
        Assert.Empty(s.Masks);
    }

    [Fact]
    public void ComponentOperations()
    {
        var mask = new Mask()
            .AddComponent(new ValueComponent(1))
            .AddComponent(new ValueComponent(0.5f));
        Assert.Equal(2, mask.Components.Count);

        mask = mask.ReplaceComponent(1, mask.Components[1] with { Mode = MaskMode.Subtract });
        Assert.Equal(MaskMode.Subtract, mask.Components[1].Mode);

        mask = mask.RemoveComponent(0);
        Assert.Equal(new ValueComponent(0.5f) { Mode = MaskMode.Subtract }, mask.Components.Single());
    }
}
