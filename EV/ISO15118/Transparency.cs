/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EV <https://github.com/OpenChargingCloud/EV>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Globalization;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.Metering;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.EV.ISO15118
{

    /// <summary>
    /// What this vehicle can say about a station's clock and meter from its own side of a session:
    /// the lines it writes while the session runs, and the block the result carries afterwards.
    /// </summary>
    /// <remarks>
    /// The first slice of "Chargy in the Vehicle": the clock comparison, and the station's meter
    /// readings beside the vehicle's own count. The measuring is in <c>WWCP_ISO15118_Session</c> -
    /// <see cref="ClockComparison"/>, <see cref="StationReading"/>, <see cref="EvMeter"/> - and what is
    /// here is only how it is said.
    ///
    /// <b>Nothing here verifies a signature.</b> A reading is reported as signed or unsigned, which is
    /// what the wire shows; whether the signature is the meter's is a later step, and a page that
    /// drew a tick for "present" would be claiming something nobody checked.
    ///
    /// <b>Agreement between the two counts is arithmetic, not evidence, in a simulator.</b> Both
    /// figures come from the same modelled charge loop, so they agree unless a field, a unit or an
    /// exponent is wrong - which is worth catching, and is not a statement about a real meter.
    /// </remarks>
    public static class Transparency
    {

        #region Data

        /// <summary>
        /// How far a station's clock may be from this vehicle's before it is called out.
        /// </summary>
        /// <remarks>
        /// Five seconds is this vehicle's choice and nothing more: neither ISO 15118 nor a tariff is
        /// being quoted. What a comparison cannot resolve - the whole second of a timestamp, half the
        /// round trip - is allowed for on top of it, by <see cref="ClockComparison.Agrees"/>.
        /// </remarks>
        public static readonly TimeSpan  ClockTolerance = TimeSpan.FromSeconds(5);

        #endregion


        #region SayFirstClockSample(Whose, Sample, Log)

        /// <summary>
        /// The clock comparison, said once: when the first timestamp of a peer has been read.
        /// </summary>
        /// <param name="Whose">"station" or "meter".</param>
        public static void SayFirstClockSample(String       Whose,
                                               ClockSample  Sample,
                                               EventLog     Log)
        {

            var within = Sample.Offset.Duration() <= ClockTolerance + Sample.Uncertainty;

            var text   = String.Create(CultureInfo.InvariantCulture,
                             $"Clock: the {Whose}'s clock is {Seconds(Sample.Offset)} against this vehicle's " +
                             $"(resolved to ±{Sample.Uncertainty.TotalSeconds:F1} s)");

            if (within)
                Log.Notice ($"{text}.", "15118", "clock");
            else
                Log.Warning($"{text} - more than the {ClockTolerance.TotalSeconds:F0} s this vehicle tolerates. " +
                             "Timestamps of this session, a time-of-use tariff included, are off by that much.",
                            "15118", "clock");

        }

        #endregion

        #region IsNews(Readings, Reading)

        /// <summary>
        /// Whether a reading is worth a line of its own: the first of a session, or one whose
        /// difference to the vehicle's count is not what the reading before it showed.
        /// </summary>
        /// <param name="Readings">Every reading so far, <paramref name="Reading"/> included as the last.</param>
        /// <param name="Reading">The reading that just arrived.</param>
        public static Boolean IsNews(IReadOnlyList<StationReading>  Readings,
                                     StationReading                 Reading)

            => Readings.Count < 2 ||
               Readings[^2].DifferenceWh != Reading.DifferenceWh;

        #endregion

        #region SayReading(Reading, Log)

        /// <summary>
        /// One meter reading of the station's beside this vehicle's count, as it arrives.
        /// </summary>
        public static void SayReading(StationReading  Reading,
                                      EventLog        Log)

            => Log.Info(String.Create(CultureInfo.InvariantCulture,
                            $"Meter: {Reading.MeterId} reads {Reading.StationWh} Wh " +
                            $"({(Reading.Signed ? "signed, not verified" : "unsigned")}), this vehicle counted " +
                            $"{Reading.VehicleWh:F0} Wh - difference {Reading.DifferenceWh:+0;-0;0} Wh."),
                        "15118", "meter");

        #endregion

        #region SaySummary(StationClock, Readings, Log)

        /// <summary>
        /// What the session added up to, for somebody who only reads the end of the log.
        /// </summary>
        public static void SaySummary(ClockComparison?               StationClock,
                                      IReadOnlyList<StationReading>  Readings,
                                      EventLog                       Log)
        {

            // Only a drift the two samples can tell from a second ticking over.
            if (StationClock is { Drifted: true, Drift: { } drift })
                Log.Info(String.Create(CultureInfo.InvariantCulture,
                             $"Clock: over {StationClock.Samples} responses the station's clock moved {Seconds(drift)} against this vehicle's."),
                         "15118", "clock");

            if (Readings.Count == 0)
            {
                Log.Notice("Meter: the station reported no meter reading, so there is nothing to put beside this vehicle's own count.",
                           "15118", "meter");
                return;
            }

            var last      = Readings[^1];

            var relative  = last.RelativeDifference is { } ratio
                                ? String.Create(CultureInfo.InvariantCulture, $" ({ratio:+0.00%;-0.00%;0.00%})")
                                : "";

            Log.Notice(String.Create(CultureInfo.InvariantCulture,
                           $"Meter: {Readings.Count} reading(s); at the last one the station had {last.StationWh} Wh " +
                           $"and this vehicle {last.VehicleWh:F0} Wh - difference {last.DifferenceWh:+0;-0;0} Wh{relative}."),
                       "15118", "meter");

        }

        #endregion

        #region JSON(StationClock, MeterClock, Readings, VehicleWh)

        /// <summary>
        /// The block the result carries, as the web interface reads it.
        /// </summary>
        /// <param name="StationClock">The station's clock, where the protocol shows one: -20 only.</param>
        /// <param name="MeterClock">The meter's clock.</param>
        /// <param name="Readings">Every reading, paired.</param>
        /// <param name="VehicleWh">This vehicle's own count at the end of the session.</param>
        public static JObject JSON(ClockComparison?               StationClock,
                                   ClockComparison                MeterClock,
                                   IReadOnlyList<StationReading>  Readings,
                                   Double                         VehicleWh)

            => new (
                   new JProperty("clockTolerance_ms",  ClockTolerance.TotalMilliseconds),
                   new JProperty("stationClock",       ClockJSON(StationClock)),
                   new JProperty("meterClock",         ClockJSON(MeterClock)),
                   new JProperty("vehicleWh",          Math.Round(VehicleWh, 0)),
                   new JProperty("readings",           new JArray(Readings.Select(reading => new JObject(
                                                           new JProperty("sample",        reading.Sample),
                                                           new JProperty("meterId",       reading.MeterId),
                                                           new JProperty("stationWh",     reading.StationWh),
                                                           new JProperty("vehicleWh",     Math.Round(reading.VehicleWh, 0)),
                                                           new JProperty("differenceWh",  Math.Round(reading.DifferenceWh, 0)),
                                                           new JProperty("signed",        reading.Signed),
                                                           new JProperty("meterTime",     reading.MeterTime?.ToString("o"))
                                                       ))))
               );

        #endregion


        #region (private static) ClockJSON(Clock)

        private static JObject? ClockJSON(ClockComparison? Clock)

            => Clock?.First is not { } first || Clock.Last is not { } last
                   ? null
                   : new JObject(
                         new JProperty("samples",         Clock.Samples),
                         new JProperty("unreadable",      Clock.Unreadable),
                         new JProperty("firstOffset_ms",  Math.Round(first.Offset.     TotalMilliseconds, 0)),
                         new JProperty("offset_ms",       Math.Round(last. Offset.     TotalMilliseconds, 0)),
                         new JProperty("uncertainty_ms",  Math.Round(last. Uncertainty.TotalMilliseconds, 0)),
                         new JProperty("drift_ms",        Clock.Drift is { } drift ? Math.Round(drift.TotalMilliseconds, 0) : null),
                         new JProperty("drifted",         Clock.Drifted),
                         new JProperty("agrees",          Clock.Agrees(ClockTolerance))
                     );

        #endregion

        #region (private static) Seconds(Span)

        /// <summary>An offset in words: "0.3 s ahead", "90.2 s behind".</summary>
        private static String Seconds(TimeSpan Span)

            => String.Create(CultureInfo.InvariantCulture,
                             $"{Span.Duration().TotalSeconds:F1} s {(Span < TimeSpan.Zero ? "behind" : "ahead")}");

        #endregion

    }

}
