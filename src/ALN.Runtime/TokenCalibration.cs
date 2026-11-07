using System.Collections.Concurrent;

namespace ALN.Runtime;

/// <summary>
/// What the provider actually charged, compared with what <see cref="TokenEstimator"/> predicted for the same request.
///
/// The estimator fits a character count to tokens, and a fit is a guess about a particular model, prompt set and mix of
/// content. The provider tells us the true number with every answer, so there is no reason for the guess to stay
/// unexamined: each completed call is a measurement, and the next request is estimated with the ratio the last one
/// produced.
///
/// Three properties make this safe to leave running:
/// <list type="bullet">
/// <item>One ratio per Session, held in memory. A number that survives a restart would be a number with no evidence
/// behind it — the deployment that produced it is not the one running now.</item>
/// <item>Clamped, because a ratio outside this range is not a better fit: it is a provider reporting something else
/// (a cached prompt billed differently, a truncation, an estimate that was nearly zero). Folding that in would move the
/// budget for a reason nobody could reconstruct.</item>
/// <item>Smoothed, because one round of "read a big file" is not a new calibration. Half the old, half the new: it
/// follows a drift over a few turns without chasing a single outlier.</item>
/// </list>
/// </summary>
public sealed class TokenCalibration
{
    /// <summary>A single observation may not move the correction further than this, in either direction.</summary>
    private const double MinRatio = 0.25;
    private const double MaxRatio = 4.0;

    /// <summary>How much of one observation is believed. Half: follows drift, ignores one outlier.</summary>
    private const double Weight = 0.5;

    private readonly ConcurrentDictionary<string, double> _ratios = new(StringComparer.Ordinal);

    /// <summary>The correction to apply to this Session's next estimate. 1.0 until something has been measured.</summary>
    public double For(string sessionId) =>
        _ratios.TryGetValue(sessionId, out var ratio) ? ratio : 1.0;

    /// <summary>
    /// Records what one request cost against what was predicted for it, and answers with the correction now in force.
    /// Reasons to ignore the pair (nothing predicted, nothing charged) leave the correction where it was: an
    /// unusable measurement is not evidence of anything.
    /// </summary>
    public double Observe(string sessionId, int estimated, long actual)
    {
        if (estimated <= 0 || actual <= 0) return For(sessionId);
        var observed = Math.Clamp((double)actual / estimated, MinRatio, MaxRatio);
        if (!_ratios.TryGetValue(sessionId, out var current))
        {
            _ratios[sessionId] = observed;
            return observed;
        }
        var blended = current * (1 - Weight) + observed * Weight;
        _ratios[sessionId] = Math.Clamp(blended, MinRatio, MaxRatio);
        return _ratios[sessionId];
    }

    /// <summary>Forgets one Session's measurement. Called when its timeline is replaced wholesale — a checkpoint keeps
    /// the conversation but not the request sizes that produced the ratio.</summary>
    public void Forget(string sessionId) => _ratios.TryRemove(sessionId, out _);
}
