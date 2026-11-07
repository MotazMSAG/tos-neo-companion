using System.Diagnostics;
using System.Globalization;
using Companion.TeaHouse;

// Trains the Kitty Tea House helper's learned board value by self-play (see TeaTrainer).
// Usage: Companion.TeaHouse.Trainer [--minutes 60] [--threads 4] [--rate 0.02] [--dir C:\ProgramData\TOS-companion\teahouse] [--fresh]
var minutes = Arg("--minutes", 60.0);
var threads = (int)Arg("--threads", Math.Max(1, Environment.ProcessorCount / 2));
var baseRate = Arg("--rate", 0.02);
var dir = ArgText("--dir", @"C:\ProgramData\TOS-companion\teahouse");
var fresh = args.Contains("--fresh");
var features = args.Contains("--features");
var netPath = Path.Combine(dir, "net.bin");

// a training run must never slow the game: below-normal priority
using (var self = Process.GetCurrentProcess())
{
    self.PriorityClass = ProcessPriorityClass.BelowNormal;
}

var model = LoadModel(Path.Combine(dir, "model.json"));
TeaNet net = new() { UsesFeatures = features };
if (!fresh && File.Exists(netPath))
{
    using var f = File.OpenRead(netPath);
    net = TeaNet.Load(f) ?? new TeaNet();
}

Console.WriteLine($"training {minutes} min on {threads} threads, rate {baseRate}, from {net.Trained} rounds; odds: " +
    string.Join(" ", TeaKinds.Spawnable.Select(k => $"{k}:{model.Odds(k):P1}")));
var stop = DateTime.UtcNow.AddMinutes(minutes);
var recent = new double[2000];
long rounds = 0;
var gate = new object();
var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
{
    var rng = new Random(Environment.TickCount ^ (t * 7919));
    while (DateTime.UtcNow < stop)
    {
        var rate = baseRate / (1 + (net.Trained / 300000.0));
        var score = TeaTrainer.Round(net, model, rng, rate);
        lock (gate)
        {
            recent[rounds % recent.Length] = score;
            rounds++;
        }
    }
}) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
workers.ForEach(w => w.Start());

var lastSave = DateTime.UtcNow;
double best = 0;
var sw = Stopwatch.StartNew();
while (workers.Any(w => w.IsAlive))
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
    double avg;
    long done;
    double[] last;
    lock (gate)
    {
        done = rounds;
        var n = (int)Math.Min(rounds, recent.Length);
        last = recent.Take(n).ToArray();
        avg = n == 0 ? 0 : last.Average();
    }

    // the user (2026-10-29): "peak is a better criterion, we need to hit 200-250k once": the best rounds as well
    Array.Sort(last);
    double Pct(double q) => last.Length == 0 ? 0 : last[Math.Min(last.Length - 1, (int)(q * last.Length))];
    if (last.Length > 0)
    {
        best = Math.Max(best, last[^1]);
    }

    net.RecentScore = avg;
    Console.WriteLine($"{sw.Elapsed.TotalMinutes:F1} min rounds {done} (total {net.Trained}), last {last.Length}: avg {avg:F0}, median {Pct(0.5):F0}, top 10% {Pct(0.9):F0}, top 1% {Pct(0.99):F0}, best {last.LastOrDefault():F0} (best ever {best:F0})");
    if (DateTime.UtcNow - lastSave > TimeSpan.FromMinutes(5))
    {
        Save();
        lastSave = DateTime.UtcNow;
    }
}

Save();
Console.WriteLine("done");

void Save()
{
    Directory.CreateDirectory(dir);
    var tmp = netPath + ".tmp";
    using (var f = File.Create(tmp))
    {
        net.Save(f);
    }

    File.Move(tmp, netPath, overwrite: true);
    Console.WriteLine($"saved {netPath}");
}

double Arg(string name, double fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

string ArgText(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

// the piece counts and points the helper learned (its model.json), loaded exactly as the helper loads them: a stored
// value never beats a confirmed one (TeaModelFile, 2026-10-31)
static TeaModel LoadModel(string path) => TeaModelFile.Load(File.Exists(path) ? File.ReadAllText(path) : null);
