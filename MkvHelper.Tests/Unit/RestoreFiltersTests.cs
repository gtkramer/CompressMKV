namespace MkvHelper.Tests.Unit;

/// <summary>
/// Composition tests for the §7.2.3 ffmpeg filter chains.  These tests build
/// the actual strings we hand to ffmpeg's `-vf` and verify they contain the
/// guide-prescribed filters in the right order with the right options.
/// </summary>
[TestFixture]
public class RestoreFiltersTests
{
    [Test]
    public void IvtcChain_TffPropagatesThroughFieldmatchAndBwdif()
    {
        string chain = RestoreFilters.IvtcChain(FieldParity.Tff);

        Assert.That(chain, Does.Contain("fieldmatch=order=tff"));
        Assert.That(chain, Does.Contain("bwdif=mode=send_frame:parity=tff:deint=interlaced"));
        Assert.That(chain, Does.Contain("decimate"));
    }

    [Test]
    public void IvtcChain_BffPropagatesThroughFieldmatchAndBwdif()
    {
        string chain = RestoreFilters.IvtcChain(FieldParity.Bff);

        Assert.That(chain, Does.Contain("fieldmatch=order=bff"));
        Assert.That(chain, Does.Contain("bwdif=mode=send_frame:parity=bff:deint=interlaced"));
        Assert.That(chain, Does.Contain("decimate"));
    }

    [Test]
    public void IvtcChain_AutoParityRendersAuto()
    {
        string chain = RestoreFilters.IvtcChain(FieldParity.Auto);

        Assert.That(chain, Does.Contain("fieldmatch=order=auto"));
        Assert.That(chain, Does.Contain("parity=auto"));
    }

    [Test]
    public void IvtcChain_FilterOrderIsFieldmatchThenBwdifThenDecimate()
    {
        string chain = RestoreFilters.IvtcChain(FieldParity.Tff);
        int posFm = chain.IndexOf("fieldmatch", StringComparison.Ordinal);
        int posBw = chain.IndexOf("bwdif",      StringComparison.Ordinal);
        int posDc = chain.IndexOf("decimate",   StringComparison.Ordinal);

        Assert.That(posFm, Is.GreaterThanOrEqualTo(0));
        Assert.That(posFm, Is.LessThan(posBw), "fieldmatch must come before bwdif");
        Assert.That(posBw, Is.LessThan(posDc), "bwdif must come before decimate");
    }

    [Test]
    public void IvtcChain_UsesCombMatchFull()
    {
        // combmatch=full is what makes fieldmatch verify every frame for residual
        // combing, not just at scene changes.  This is critical for accurate IVTC.
        string chain = RestoreFilters.IvtcChain(FieldParity.Tff);
        Assert.That(chain, Does.Contain("combmatch=full"));
    }

    [Test]
    public void DeinterlaceChain_UsesBwdifAtNativeRate()
    {
        string chain = RestoreFilters.DeinterlaceChain(FieldParity.Tff);
        Assert.That(chain, Does.StartWith("bwdif=mode=send_frame:parity=tff"));
        Assert.That(chain, Does.Not.Contain("decimate"));
        Assert.That(chain, Does.Not.Contain("fieldmatch"));
    }

    [Test]
    public void DeinterlaceChain_DoesNotSendField_WhichWouldDoubleFps()
    {
        // mode=send_frame: one output frame per input frame (preserves rate).
        // mode=send_field: one output frame per FIELD (doubles rate to 60p) —
        // explicitly NOT what we want for the §7.2.3.3 deinterlace path.
        string chain = RestoreFilters.DeinterlaceChain(FieldParity.Tff);
        Assert.That(chain, Does.Contain("mode=send_frame"));
        Assert.That(chain, Does.Not.Contain("mode=send_field"));
    }

    [Test]
    public void IvtcOutputFps_IsExactlyNtsc24()
    {
        // The IVTC chain pins output to 24000/1001 — must be the exact canonical
        // fraction, not 24/1 or 23.976.
        Assert.That(RestoreFilters.IvtcOutputFps, Is.EqualTo(Fps.Ntsc24));
        Assert.That(RestoreFilters.IvtcOutputFps.ToString(), Is.EqualTo("24000/1001"));
    }

}
