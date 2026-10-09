using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>ADR 0168: the Forest's colour grade, film lens and photo-shot depth of field.</summary>
public sealed class ForestGradeTests
{
    private static string ForestRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void TheCommittedLutIsTheGenerator()
    {
        var committed = CubeLut.Load(Path.Combine(ForestRoot, ForestGrade.LutPath));
        var generated = ForestGrade.CreateLut();
        Assert.Equal(ForestGrade.LutSize, committed.Size);
        Assert.Equal(generated.Title, committed.Title);
        for (var i = 0; i < generated.Entries.Length; i++)
            Assert.True(Vector3.Distance(generated.Entries[i], committed.Entries[i]) < 2e-6f, $"entry {i} differs: run --write-scenes");
    }

    [Fact]
    public void TheGradeIsWarmSoftAndKeepsItsOrder()
    {
        // Monotonic in brightness, blacks lifted but dark, whites rolled off but bright.
        var last = -1f;
        for (var v = 0f; v <= 1f; v += 1f / 64f)
        {
            var y = Vector3.Dot(ForestGrade.Grade(new Vector3(v)), new Vector3(0.2126f, 0.7152f, 0.0722f));
            Assert.True(y >= last, $"the grade darkens at {v}");
            last = y;
        }

        var black = ForestGrade.Grade(Vector3.Zero);
        var white = ForestGrade.Grade(Vector3.One);
        Assert.InRange(black.Y, 0.005f, 0.03f);
        Assert.InRange(white.Y, 0.95f, 0.99f);
        var grey = ForestGrade.Grade(new Vector3(0.5f));
        Assert.True(grey.X > grey.Z, $"mid grey is not warm: {grey}");
        var sky = ForestGrade.Grade(new Vector3(0.45f, 0.6f, 0.85f));
        Assert.True(sky.Z > sky.X + 0.3f, $"the sky lost its blue: {sky}");
    }

    [Fact]
    public void TheEnvironmentGradesAndOnlyThePhotoShotsHaveDepthOfField()
    {
        var environment = ForestScene.CreateEnvironment();
        Assert.True(environment.PostProcess!.AdjustmentEnabled);
        Assert.NotNull(environment.PostProcess.AdjustmentColorCorrection); // the forest-morning LUT
        var look = environment.PostProcessSettings;
        Assert.InRange(look.VignetteIntensity, 0.01f, 0.5f); // a subtle vignette (tuned in forest-lens.mres)
        Assert.InRange(look.FilmGrainIntensity, 0.001f, 0.05f); // and a touch of grain
        Assert.False(look.DofEnabled); // never while walking
        Assert.Equal(0f, look.ChromaticAberrationIntensity);

        foreach (var shot in ValleyLayout.Shots)
        {
            var lens = ForestDev.PhotoLens(shot);
            var photo = shot.Name is "r4-vista" or "r5-floor";
            Assert.Equal(photo, lens is not null);
            if (lens is null)
                continue;
            var settings = lens.ApplyTo(PostProcessSettings.Default);
            Assert.True(settings.DofEnabled);
            Assert.Equal(DepthOfFieldQuality.High, settings.DofQuality);
            Assert.Equal(ForestGrade.VignetteIntensity, settings.VignetteIntensity); // the film lens comes along
        }
    }
}
