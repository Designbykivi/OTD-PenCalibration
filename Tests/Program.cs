using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using OpenTabletDriver.Plugin.Tablet;
using PenCalibration;
using PenCalibration.App;

int failures = 0;

void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok)
        failures++;
}

static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

// Pen error on a pen display: offset, slight rotation and scale, plus a little keystone.
var penError = new Transform2D(new[] { 1.012, 0.006, 9.0, -0.004, 0.991, -6.0, 2e-6, -1e-6, 1 });
// Window is 1920x1080 points on a 2x display that sits right of a 2560px monitor in OTD's space.
var windowToOtd = Transform2D.ScaleTranslate(2, 2, 2560, 0);
const double W = 1920, H = 1080;

// ---- Fitting ----------------------------------------------------------------------------

{
    var src = new[] { (100.0, 100.0), (3000, 120), (2900, 2000), (80, 2100), (1500, 1100), (700, 1600) };
    var dst = src.Select(p => penError.Apply(p.Item1, p.Item2)).ToArray();

    var exact4 = Transform2D.FitPerspective(src.Take(4).ToList(), dst.Take(4).ToList());
    Check(src.Zip(dst).All(p => Dist(exact4.Apply(p.First.Item1, p.First.Item2), p.Second) < 1e-6), "perspective fit is exact with 4 points");

    var ls = Transform2D.FitPerspective(src, dst);
    Check(src.Zip(dst).All(p => Dist(ls.Apply(p.First.Item1, p.First.Item2), p.Second) < 1e-6), "perspective fit recovers transform with 6 points");

    var affine = new Transform2D(new[] { 0.98, 0.02, 15, -0.03, 1.01, -4, 0, 0, 1 });
    var adst = src.Select(p => affine.Apply(p.Item1, p.Item2)).ToArray();
    var af = Transform2D.FitAffine(src, adst);
    Check(af.Values.Zip(affine.Values).All(v => Math.Abs(v.First - v.Second) < 1e-9), "affine fit recovers transform");

    var inv = penError.Then(penError.Inverse());
    Check(inv.Values.Zip(Transform2D.Identity.Values).All(v => Math.Abs(v.First - v.Second) < 1e-9), "inverse composes to identity");

    var parsed = Transform2D.Parse(penError.Serialize().Split(' '));
    Check(parsed.Values.SequenceEqual(penError.Values), "serialize round-trips exactly");

    bool threw = false;
    try { Transform2D.FitPerspective(new[] { (0.0, 0.0), (1, 1), (2, 2), (3, 3) }, new[] { (0.0, 0.0), (1, 1), (2, 2), (3, 3) }); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw, "collinear points are rejected");
}

// ---- Full calibration simulation -----------------------------------------------------------

List<Sample> Simulate(int count, double jitter, int seed)
{
    var rng = new Random(seed);
    var otdToWindow = windowToOtd.Inverse();
    return CalibrationSolver.TargetLayout(count).Select(f =>
    {
        // The pen tip lands on the crosshair (give or take hand jitter); the driver reports it with the pen error.
        var target = (f.X * W, f.Y * H);
        var tip = (target.Item1 + (rng.NextDouble() - 0.5) * jitter, target.Item2 + (rng.NextDouble() - 0.5) * jitter);
        var tipOtd = windowToOtd.Apply(tip.Item1, tip.Item2);
        var raw = penError.Apply(tipOtd.X, tipOtd.Y);
        var click = otdToWindow.Apply(raw.X, raw.Y);
        return new Sample(target, click, raw);
    }).ToList();
}

foreach (var (count, mode) in new[] { (4, FitMode.Perspective), (5, FitMode.Perspective), (9, FitMode.Perspective), (16, FitMode.Perspective), (25, FitMode.Perspective), (9, FitMode.Affine), (4, FitMode.Affine) })
{
    var result = CalibrationSolver.Solve(Simulate(count, 0, 1), mode, W, H);
    // Check over the whole screen, not just at the targets.
    var rng = new Random(2);
    double worst = 0;
    for (int i = 0; i < 200; i++)
    {
        var tip = windowToOtd.Apply(rng.NextDouble() * W, rng.NextDouble() * H);
        var raw = penError.Apply(tip.X, tip.Y);
        worst = Math.Max(worst, Dist(result.Correction.Apply(raw.X, raw.Y), tip));
    }

    // Affine can't model the keystone part of the pen error, so allow it a little slack.
    double limit = mode == FitMode.Perspective ? 0.01 : 10;
    Check(worst < limit && result.Problem == null && result.ErrorBefore.Average() > 5,
        $"{count} targets, {mode}: error {result.ErrorBefore.Average():F1} -> {result.ErrorAfter.Average():F2} px, worst on screen {worst:F3} OTD px");
}

{
    var grid = CalibrationSolver.TargetLayout(25);
    Check(grid.Distinct().Count() == 25 && grid.Zip(grid.Skip(1)).All(p => Dist(p.First, p.Second) < 0.25 + 1e-9),
        "25-target grid has distinct targets and never jumps more than one cell");

    var noisy25 = CalibrationSolver.Solve(Simulate(25, 3, 7), FitMode.Perspective, W, H);
    var noisy9 = CalibrationSolver.Solve(Simulate(9, 3, 7), FitMode.Perspective, W, H);
    Console.WriteLine($"      hand jitter 3 px: worst on-screen error 9 targets {WorstOnScreen(noisy9):F2}, 25 targets {WorstOnScreen(noisy25):F2} OTD px");

    var noisy = CalibrationSolver.Solve(Simulate(9, 3, 7), FitMode.Perspective, W, H);
    Check(noisy.ErrorAfter.Average() < 1.5 && noisy.Problem == null, $"9 targets with 3 px hand jitter: residual {noisy.ErrorAfter.Average():F2} px");

    var missed = Simulate(9, 0, 1);
    missed[3] = missed[3] with { Target = (missed[3].Target.X - 700, missed[3].Target.Y) };
    var bad = CalibrationSolver.Solve(missed, FitMode.Perspective, W, H);
    Check(bad.Problem != null, "a badly missed target is flagged: " + bad.Problem);

    var desync = Simulate(9, 0, 1).Select((s, i) => i == 2 ? s with { Click = (s.Click.X + 40, s.Click.Y) } : s).ToList();
    Check(CalibrationSolver.Solve(desync, FitMode.Perspective, W, H).Problem?.Contains("disagree") == true, "cursor/driver mismatch is flagged");
}

double WorstOnScreen(CalibrationResult result)
{
    var rng = new Random(3);
    double worst = 0;
    for (int i = 0; i < 400; i++)
    {
        var tip = windowToOtd.Apply(rng.NextDouble() * W, rng.NextDouble() * H);
        var raw = penError.Apply(tip.X, tip.Y);
        worst = Math.Max(worst, Dist(result.Correction.Apply(raw.X, raw.Y), tip));
    }
    return worst;
}

// ---- Plugin <-> app over the real socket -----------------------------------------------------

await Task.Run(async () =>
{
    var store = Environment.GetEnvironmentVariable("OTD_PENCALIBRATION_FILE");
    if (store == null || !store.Contains("pencal-test"))
        throw new InvalidOperationException("Set OTD_PENCALIBRATION_FILE to a temp path (run via run-tests.sh).");
    if (Protocol.Port == Protocol.DefaultPort)
        throw new InvalidOperationException("Set OTD_PENCALIBRATION_PORT so the tests don't talk to a running OpenTabletDriver (run via run-tests.sh).");
    if (File.Exists(store))
        File.Delete(store);

    var filter = new PenCalibrationFilter();
    filter.OnLoad();
    Vector2 output = default;
    filter.Emit += r => output = ((IAbsolutePositionReport)r).Position;
    void Report(float x, float y) => filter.Consume(new FakeReport { Position = new Vector2(x, y) });

    using var client = new DriverClient();
    for (int attempt = 0; ; attempt++)
    {
        try { await client.ConnectAsync(); break; }
        catch when (attempt < 20) { await Task.Delay(50); }
    }

    Report(100, 200);
    Check(output == new Vector2(100, 200), "no calibration: positions pass through");

    var pos = await client.GetPositionAsync();
    Check(pos.X == 100 && pos.Y == 200 && pos.AgeMs < 250 && pos.Tablet == "Unknown tablet", $"POS returns raw position ({pos.X}, {pos.Y}, {pos.AgeMs} ms, '{pos.Tablet}')");

    await client.ApplyAsync(new CalibrationRecord
    {
        Tablet = pos.Tablet, Matrix = Transform2D.ScaleTranslate(1, 1, 5, -3).Values, Targets = 9, Mode = "Perspective",
        AverageErrorBefore = 12.3, WorstErrorBefore = 20, AverageErrorAfter = 0.4, WorstErrorAfter = 0.9, CentreShift = new[] { 5.0, -3.0 },
    });
    Report(100, 200);
    Check(output == new Vector2(105, 197), "APPLY takes effect immediately");
    Check(CalibrationStore.Load(pos.Tablet) != null, "APPLY persists to disk");
    var details = PenCalibrationFilter.CalibrationDetails.ToList();
    Console.WriteLine("      Filters tab shows: " + string.Join(" | ", details));
    Check(details.Count == 6 && details[0] == PenCalibrationFilter.DETAILS_HEADER && details[1].StartsWith("✓ Calibrated")
          && details.Any(d => d.Contains("0.4") || d.Contains("0,4")),
        "saved calibration details are listed for the Filters tab");

    await client.BeginAsync();
    Report(100, 200);
    Check(output == new Vector2(100, 200), "BEGIN bypasses the correction");
    Check(CalibrationServer.Bypass, "bypass is on while calibrating");

    client.Dispose();
    for (int i = 0; i < 40 && CalibrationServer.Bypass; i++)
        await Task.Delay(25);
    Report(100, 200);
    Check(!CalibrationServer.Bypass && output == new Vector2(105, 197), "app disconnecting mid-calibration restores the correction");

    var reloaded = new PenCalibrationFilter();
    reloaded.OnLoad();
    reloaded.Emit += r => output = ((IAbsolutePositionReport)r).Position;
    reloaded.Consume(new FakeReport { Position = new Vector2(10, 10) });
    Check(output == new Vector2(15, 7), "saved calibration loads on the next start");

    using var again = new DriverClient();
    await again.ConnectAsync();
    await again.ClearAsync(pos.Tablet);
    reloaded.Consume(new FakeReport { Position = new Vector2(10, 10) });
    Check(output == new Vector2(10, 10) && CalibrationStore.Load(pos.Tablet) == null, "CLEAR removes the calibration");

    // The test process has no OTD display list, so SCREEN must fail cleanly rather than hang or crash.
    bool screenRefused = false;
    try { await again.GetScreenAsync(); }
    catch (DriverException) { screenRefused = true; }
    Check(screenRefused, "SCREEN without OTD's display list answers ERR");

    bool rejected = false;
    try { await again.ApplyAsync(new CalibrationRecord { Tablet = "", Matrix = Transform2D.Identity.Values }); }
    catch (DriverException) { rejected = true; }
    Check(rejected, "malformed APPLY is rejected with ERR");

    rejected = false;
    try { await again.ApplyAsync(new CalibrationRecord { Tablet = "X", Matrix = new double[] { 1, 2 } }); }
    catch (DriverException) { rejected = true; }
    Check(rejected && CalibrationStore.Load("X") == null, "APPLY with a broken matrix is rejected and not saved");

    Check(PenCalibrationFilter.CalibrationDetails.Last().StartsWith("✗ Not calibrated"), "Filters tab says when nothing is calibrated");

    // Files written by the first version held only the matrix.
    File.WriteAllText(store, "{ \"Old tablet\": [1, 0, 2, 0, 1, 3, 0, 0, 1] }");
    Check(CalibrationStore.Load("Old tablet") is { } old && old.Apply(new Vector2(0, 0)) == new Vector2(2, 3)
          && PenCalibrationFilter.CalibrationDetails.ElementAt(1) == "✓ Calibrated"
          && PenCalibrationFilter.CalibrationDetails.Last().StartsWith("Recalibrate"),
        "old calibration files still load");
});

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

class FakeReport : IAbsolutePositionReport
{
    public byte[] Raw { set; get; } = Array.Empty<byte>();
    public Vector2 Position { set; get; }
}
