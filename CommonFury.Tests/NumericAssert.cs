using Xunit;

namespace CommonFury.Tests;

internal static class NumericAssert
{
    public static void Near(float actual, double expected, string message, double tolerance = 3e-5) =>
        Assert.True(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)), $"{message}: {actual} != {expected}");
}
