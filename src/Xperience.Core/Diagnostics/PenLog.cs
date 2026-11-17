using System.Globalization;
using Xperience.Core.Pen;

namespace Xperience.Core.Diagnostics;

/// <summary>
/// Turns the pen's stream of reports into lines for the record. The pen reports a few hundred times a second,
/// so reports are not written one by one: each visit of the pen to the surface and each stroke becomes one
/// line with what it reached (pressure, position, tilt, how often it reported), and a button or a change of
/// tool is written the moment it happens.
/// </summary>
public sealed class PenLog(ILog log, Func<long> ticks, long ticksPerSecond)
{
    Span _visit, _stroke;
    PenSample _last;
    bool _started;

    public PenLog(ILog log) : this(log, System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency)
    {
    }

    /// <summary>Takes one report, as the kernel completed it.</summary>
    public void Take(PenSample sample)
    {
        long now = ticks();
        var before = _started ? _last : default;
        _started = true;
        _last = sample;

        if (sample.InRange && !before.InRange)
        {
            _visit = Span.Begin(now);
            log.Write("pen", sample.Eraser ? "came near the surface as its eraser" : "came near the surface");
        }
        else if (sample.InRange && before.InRange && sample.Eraser != before.Eraser)
            log.Write("pen", sample.Eraser ? "became its eraser" : "became the pen again");
        if (sample.LowerButton != before.LowerButton)
            log.Write("pen", sample.LowerButton ? "lower button down" : "lower button up");
        if (sample.UpperButton != before.UpperButton)
            log.Write("pen", sample.UpperButton ? "upper button down" : "upper button up");

        if (sample.Touching && !before.Touching)
            _stroke = Span.Begin(now);
        if (sample.InRange)
            _visit.Add(sample, now);
        if (sample.Touching)
            _stroke.Add(sample, now);

        if (!sample.Touching && before.Touching)
            log.Write("pen", $"stroke{(before.Eraser ? " with the eraser" : "")}: {_stroke.Describe(ticksPerSecond, pressure: true)}");
        if (!sample.InRange && before.InRange)
            log.Write("pen", $"left the surface: {_visit.Describe(ticksPerSecond, pressure: false)}");
    }

    /// <summary>What a run of reports reached.</summary>
    struct Span
    {
        long _from, _to, _pressureSum;
        int _count, _minPressure, _maxPressure, _minX, _maxX, _minY, _maxY, _minTiltX, _maxTiltX, _minTiltY, _maxTiltY;
        bool _lower, _upper;

        public static Span Begin(long now) => new()
        {
            _from = now, _to = now, _minPressure = int.MaxValue, _minX = int.MaxValue, _minY = int.MaxValue,
            _minTiltX = int.MaxValue, _minTiltY = int.MaxValue, _maxTiltX = int.MinValue, _maxTiltY = int.MinValue,
        };

        public void Add(PenSample s, long now)
        {
            _to = now;
            _count++;
            _pressureSum += s.Pressure;
            (_minPressure, _maxPressure) = (Math.Min(_minPressure, s.Pressure), Math.Max(_maxPressure, s.Pressure));
            (_minX, _maxX) = (Math.Min(_minX, s.X), Math.Max(_maxX, s.X));
            (_minY, _maxY) = (Math.Min(_minY, s.Y), Math.Max(_maxY, s.Y));
            (_minTiltX, _maxTiltX) = (Math.Min(_minTiltX, s.TiltX), Math.Max(_maxTiltX, s.TiltX));
            (_minTiltY, _maxTiltY) = (Math.Min(_minTiltY, s.TiltY), Math.Max(_maxTiltY, s.TiltY));
            _lower |= s.LowerButton;
            _upper |= s.UpperButton;
        }

        public readonly string Describe(long ticksPerSecond, bool pressure)
        {
            if (_count == 0)
                return "no reports";
            double seconds = (_to - _from) / (double)ticksPerSecond;
            string rate = seconds > 0.05 ? string.Create(CultureInfo.InvariantCulture, $" ({(_count - 1) / seconds:0}/s)") : "";
            string text = string.Create(CultureInfo.InvariantCulture, $"{seconds:0.00} s, {_count} reports{rate}");
            if (pressure)
                text += string.Create(CultureInfo.InvariantCulture, $", pressure {_minPressure}..{_maxPressure} mean {_pressureSum / _count}");
            text += string.Create(CultureInfo.InvariantCulture, $", x {_minX}..{_maxX}, y {_minY}..{_maxY}, tilt x {_minTiltX}..{_maxTiltX} y {_minTiltY}..{_maxTiltY}");
            if (_lower || _upper)
                text += ", with " + (_lower && _upper ? "both buttons" : _lower ? "the lower button" : "the upper button") + " held";
            return text;
        }
    }
}
