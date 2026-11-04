namespace MainframeEngine.Video.Media.Export;

/// <param name="Fraction">0..1 overall.</param>
/// <param name="Stage">Short human-readable description of the current step.</param>
public readonly record struct ExportProgress(double Fraction, string Stage);
