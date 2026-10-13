namespace MainframeEngine.Video.Media;

/// <summary>Min and max sample value within one waveform bucket. Four bytes; there are millions of these.</summary>
public readonly record struct Peak(short Min, short Max);
