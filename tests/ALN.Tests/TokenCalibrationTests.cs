using ALN.Runtime;

namespace ALN.Tests;

/// <summary>
/// The estimator is a fit; the provider's own count is a measurement. This is where the second corrects the first — and
/// where the correction is kept from becoming a number nobody can reconstruct.
/// </summary>
public sealed class TokenCalibrationTests
{
    [Fact]
    public void Nothing_measured_means_no_correction()
    {
        var calibration = new TokenCalibration();
        Assert.Equal(1.0, calibration.For("s-1"));
        // A provider that reports nothing, or a request that was estimated at nothing, is not evidence of anything.
        Assert.Equal(1.0, calibration.Observe("s-1", 0, 80_000));
        Assert.Equal(1.0, calibration.Observe("s-1", 10_000, 0));
        Assert.Equal(1.0, calibration.For("s-1"));
    }

    [Fact]
    public void The_first_measurement_is_believed_and_the_next_ones_are_blended()
    {
        var calibration = new TokenCalibration();
        // Predicted 40k, charged 60k: the estimator is low by half.
        Assert.Equal(1.5, calibration.Observe("s-1", 40_000, 60_000), 3);
        // A second, exact measurement pulls it back rather than replacing it — one call is not a new calibration.
        Assert.Equal(1.25, calibration.Observe("s-1", 40_000, 40_000), 3);
        Assert.Equal(1.25, calibration.For("s-1"), 3);
    }

    [Fact]
    public void One_outlier_cannot_move_the_budget_far()
    {
        var calibration = new TokenCalibration();
        // A provider billing a cached prompt differently (or a truncated request) is not a reason to fold everything.
        calibration.Observe("s-1", 1_000, 100_000);
        Assert.Equal(4.0, calibration.For("s-1"), 3);
        // And the other way: nothing makes the estimate collapse to nothing.
        calibration.Observe("s-2", 100_000, 1_000);
        Assert.Equal(0.25, calibration.For("s-2"), 3);
    }

    [Fact]
    public void Sessions_are_measured_apart_and_forgetting_is_possible()
    {
        var calibration = new TokenCalibration();
        calibration.Observe("s-1", 10_000, 20_000);
        Assert.Equal(2.0, calibration.For("s-1"), 3);
        Assert.Equal(1.0, calibration.For("s-2"));
        calibration.Forget("s-1");
        Assert.Equal(1.0, calibration.For("s-1"));
    }

    [Fact]
    public void A_correction_scales_the_estimate_it_is_given()
    {
        Assert.Equal(1_000, TokenEstimator.Corrected(1_000, 1.0));
        Assert.Equal(1_500, TokenEstimator.Corrected(1_000, 1.5));
        // Rounded up, not truncated: half a token over the budget is still over the budget.
        Assert.Equal(1_502, TokenEstimator.Corrected(1_001, 1.5));
        Assert.Equal(1_000, TokenEstimator.Corrected(1_000, 0));
    }
}
