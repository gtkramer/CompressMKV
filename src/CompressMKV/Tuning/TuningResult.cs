namespace CompressMkv;

public sealed class TuningResult
{
    public List<SampleWindow> SampleWindows { get; set; } = new();
    public List<CqAggregate> CqResults { get; set; } = new();
    public Selection Selection { get; set; } = new();

    public List<int> BaseCqList { get; set; } = new();
    public List<int> EffectiveCqList { get; set; } = new();
    public bool HdrCqShiftApplied { get; set; }
    public int HdrCqShiftDelta { get; set; }
}
