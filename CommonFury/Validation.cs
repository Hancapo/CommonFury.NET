namespace CommonFury;

/// <summary>Only <see cref="Error"/> makes a report invalid.</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Whether a diagnostic limits all use of the data or only rewriting it.</summary>
public enum DiagnosticScope
{
    General,
    /// <summary>Limits serialization fidelity only, not interpretation of decoded fields.</summary>
    Rewrite
}

/// <summary>How a report interprets rewrite-only errors.</summary>
public enum ValidationPurpose
{
    /// <summary>Rewrite-only errors stay errors.</summary>
    Export,
    /// <summary>Rewrite-only errors become warnings.</summary>
    Inspection
}

/// <summary>A validation issue with a stable code and, when known, a field path.</summary>
public sealed record Diagnostic(string Code, string Message, string? Path = null,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error, string? Asset = null)
{
    public DiagnosticScope Scope { get; init; }
}

/// <summary>Immutable diagnostics interpreted for one <see cref="ValidationPurpose"/>; switching purpose restores original severities.</summary>
public sealed class ValidationReport
{
    private readonly Diagnostic[] originalDiagnostics;
    /// <summary>In original order, with severities interpreted for <see cref="Purpose"/>.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public ValidationPurpose Purpose { get; }
    /// <summary>No diagnostic is an error for <see cref="Purpose"/>.</summary>
    public bool IsValid { get; }

    /// <summary>Copies <paramref name="diagnostics"/>; later changes to the source collection have no effect.</summary>
    public ValidationReport(IEnumerable<Diagnostic> diagnostics, ValidationPurpose purpose = ValidationPurpose.Export)
        : this(Copy(diagnostics), purpose)
    {
    }

    private ValidationReport(Diagnostic[] diagnostics, ValidationPurpose purpose)
    {
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        originalDiagnostics = diagnostics;
        Purpose = purpose;
        Diagnostic[] interpreted = purpose == ValidationPurpose.Inspection
            ? diagnostics.Select(diagnostic => diagnostic is { Scope: DiagnosticScope.Rewrite, Severity: DiagnosticSeverity.Error }
                ? diagnostic with { Severity = DiagnosticSeverity.Warning } : diagnostic).ToArray()
            : diagnostics;
        Diagnostics = Array.AsReadOnly(interpreted);
        IsValid = !interpreted.Any(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Error);
    }

    private static Diagnostic[] Copy(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostic[] values = diagnostics.ToArray();
        if (values.Any(diagnostic => diagnostic is null))
            throw new ArgumentException("Diagnostics cannot contain null entries.", nameof(diagnostics));
        return values;
    }

    /// <summary>Reinterprets the original severities; returns this instance when the purpose is unchanged.</summary>
    public ValidationReport ForPurpose(ValidationPurpose purpose) => purpose == Purpose ? this : new(originalDiagnostics, purpose);

    /// <summary>Throws <see cref="ValidationException"/> when the report has errors; warnings alone never throw.</summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new ValidationException(this);
    }
}

/// <summary>Thrown by <see cref="ValidationReport.ThrowIfInvalid"/>.</summary>
public sealed class ValidationException : Exception
{
    /// <summary>The full report, including non-blocking diagnostics.</summary>
    public ValidationReport Report { get; }

    /// <summary>The message lists only blocking errors.</summary>
    public ValidationException(ValidationReport report) : base(MessageFor(report)) => Report = report;

    private static string MessageFor(ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string message = string.Join("; ", report.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        return message.Length == 0 ? "Validation failed." : message;
    }
}
