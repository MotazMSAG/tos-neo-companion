using System.Globalization;
using System.Text;

namespace Companion.TeaHouse;

/// <summary>A round's outlook: how the practice rounds that stood where this one stands ended.</summary>
/// <param name="TypicalFinal">Their median final score.</param>
/// <param name="Share160">Share of them that reached 160k (0..1).</param>
/// <param name="Share200">Share of them that reached 200k (0..1).</param>
/// <param name="TooEarly">True below <see cref="TeaForecast.EarlyScore"/>: rounds there cannot be told apart yet.</param>
public sealed record TeaOutlook(double TypicalFinal, double Share160, double Share200, bool TooEarly);

/// <summary>
/// The round forecast (2026-10-30, the user: "where do I see if its a bad run or a good one?"): the outlook of a board
/// is read from the practice rounds that once stood at the same point, i.e. with about the same score so far and the
/// same value of the best move (the brain's guess of the points still to come). No formula: their median final score
/// and the shares that reached 160k and 200k are shown as they are. The samples come from self-play with the installed
/// brain (ForecastProbe), so they are only as true as the game model, and they are brain-specific: a new brain needs new
/// samples. Below <see cref="EarlyScore"/> (measured on held-out rounds: the point where look-alike rounds start to end
/// differently) the outlook says it is too early to tell.
/// </summary>
public sealed class TeaForecast
{
    /// <summary>How many look-alike rounds an outlook is read from.</summary>
    public const int Alike = 150;

    /// <summary>Score and value are compared in steps of this many points.</summary>
    private const double Step = 10_000;

    private readonly float[] _score;
    private readonly float[] _value;
    private readonly float[] _final;

    private TeaForecast(float[] score, float[] value, float[] final, double earlyScore)
    {
        _score = score;
        _value = value;
        _final = final;
        EarlyScore = earlyScore;
    }

    /// <summary>The score from which look-alike rounds start to end differently (from the samples file).</summary>
    public double EarlyScore { get; }

    /// <summary>Samples held.</summary>
    public int Count => _score.Length;

    /// <summary>
    /// Reads a samples file: '#' comment lines (one may say "early N"), then "score,value,final" rows. Null when the text
    /// holds fewer than <see cref="Alike"/> rows.
    /// </summary>
    public static TeaForecast? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var score = new List<float>();
        var value = new List<float>();
        var final = new List<float>();
        var early = 0.0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                var at = line.IndexOf("early ", StringComparison.Ordinal);
                if (at >= 0 && double.TryParse(line[(at + 6)..].Split(' ', ';', ',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
                {
                    early = e;
                }

                continue;
            }

            var parts = line.Split(',');
            if (parts.Length == 3
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            {
                score.Add(s);
                value.Add(v);
                final.Add(f);
            }
        }

        return score.Count < Alike ? null : new TeaForecast([.. score], [.. value], [.. final], early);
    }

    /// <summary>Writes a samples file (the format <see cref="Parse"/> reads).</summary>
    public static string Write(IEnumerable<(double Score, double Value, double Final)> samples, double earlyScore, string note)
    {
        var sb = new StringBuilder();
        sb.Append("# Kitty Tea House forecast samples: ").Append(note).Append("; early ").Append(earlyScore.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# score,value,final\n");
        foreach (var (s, v, f) in samples)
        {
            sb.Append(s.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(v.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                .Append(f.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The outlook of a board whose score so far is <paramref name="score"/> and whose best move is worth
    /// <paramref name="value"/> (points now plus the brain's value of what follows, at the quick look of 2 pieces).
    /// </summary>
    public TeaOutlook Predict(double score, double value)
    {
        // the Alike nearest samples (a bounded max-heap on distance)
        var heap = new PriorityQueue<int, double>(Alike + 1, Comparer<double>.Create((a, b) => b.CompareTo(a)));
        for (var i = 0; i < _score.Length; i++)
        {
            var ds = (_score[i] - score) / Step;
            var dv = (_value[i] - value) / Step;
            var d = (ds * ds) + (dv * dv);
            if (heap.Count < Alike)
            {
                heap.Enqueue(i, d);
            }
            else if (heap.TryPeek(out _, out var worst) && d < worst)
            {
                heap.DequeueEnqueue(i, d);
            }
        }

        var finals = new List<float>(heap.Count);
        while (heap.TryDequeue(out var i, out _))
        {
            finals.Add(_final[i]);
        }

        finals.Sort();
        var median = finals.Count == 0 ? 0 : finals[finals.Count / 2];
        var n = Math.Max(1, finals.Count);
        return new TeaOutlook(median, finals.Count(f => f >= 160_000) / (double)n, finals.Count(f => f >= 200_000) / (double)n, score < EarlyScore);
    }
}
