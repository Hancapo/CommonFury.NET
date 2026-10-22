using Xunit;

namespace CommonFury.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void ReportsProtectPublishedDiagnostics()
    {
        var source = new List<Diagnostic> { new("test.error", "error") };
        var report = new ValidationReport(source);
        source.Clear();
        Assert.True(!report.IsValid && report.Diagnostics.Count == 1, "Reports must own their diagnostic collection.");
        Assert.ThrowsAny<NotSupportedException>(() => ((IList<Diagnostic>)report.Diagnostics).Clear());
        Assert.ThrowsAny<ValidationException>(report.ThrowIfInvalid);
        Assert.True(new ValidationReport([new("test.warning", "warning", Severity: DiagnosticSeverity.Warning)]).IsValid, "Warning became an error.");
    }

    [Fact]
    public void ReportsRestoreOriginalSeveritiesWithoutMutation()
    {
        var issue = new Diagnostic("test.rewrite", "Opaque relocation.") { Scope = DiagnosticScope.Rewrite };
        var report = new ValidationReport([issue]);
        Assert.False(report.IsValid);
        var inspection = report.ForPurpose(ValidationPurpose.Inspection);
        Assert.True(inspection.IsValid);
        Assert.False(inspection.ForPurpose(ValidationPurpose.Export).IsValid);
        Assert.Throws<ValidationException>(report.ThrowIfInvalid);
    }
}
