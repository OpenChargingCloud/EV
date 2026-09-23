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

namespace cloud.charging.open.EV.Logging
{

    /// <summary>
    /// The event log on disk, for after the fact.
    /// </summary>
    /// <remarks>
    /// The console shows what is happening to whoever is watching, and the web
    /// interface keeps the last two thousand entries for whoever asks. Both are
    /// gone when the process is: a console that was not being read kept
    /// nothing, and the ring buffer empties with the vehicle. Everything that
    /// wants answering afterwards - what a station replied at four in the
    /// morning, what the clock did last week, which discovery preceded the
    /// session that failed - needs a third place, and this is it.
    ///
    /// One file per day, named for the date, appended to and flushed after
    /// every entry. Flushing every time costs a system call per entry and buys
    /// the property that matters here: a vehicle that is killed, or that
    /// crashes, has its last lines on disk rather than in a buffer. The volume
    /// this writes - a busy charging session is a few thousand lines - makes
    /// that trade an easy one.
    ///
    /// Nothing is ever deleted. A simulator that quietly threw away the
    /// evidence of the run somebody is asking about would be worse than one
    /// that needs a directory emptied now and then.
    /// </remarks>
    public sealed class FileLog : IDisposable
    {

        #region Data

        private readonly EventLog          log;
        private readonly Action<LogEntry>  handler;
        private readonly Lock              padlock = new();

        /// <summary>
        /// The day the open file belongs to, so that midnight is noticed
        /// without asking the file system anything.
        /// </summary>
        private DateOnly      openFor;
        private StreamWriter? writer;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the files are written to.
        /// </summary>
        public String    Directory      { get; }

        /// <summary>
        /// Entries below this level are not written. Debug by default, which is
        /// everything: the console is where a level is chosen for readability,
        /// and a file nobody is reading has no such problem.
        /// </summary>
        public LogLevel  MinimumLevel   { get; }

        /// <summary>
        /// The file being written at the moment, or null before the first entry.
        /// </summary>
        public String?   CurrentFile    { get; private set; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Write the entries of the given log to a file per day below the given
        /// directory.
        /// </summary>
        /// <param name="Log">The event log to follow.</param>
        /// <param name="Directory">Where the files go. Made if it is not there.</param>
        /// <param name="MinimumLevel">Entries below this level are not written.</param>
        public FileLog(EventLog  Log,
                       String    Directory,
                       LogLevel  MinimumLevel   = LogLevel.Debug)
        {

            this.log           = Log;
            this.Directory     = Path.GetFullPath(Directory);
            this.MinimumLevel  = MinimumLevel;

            System.IO.Directory.CreateDirectory(this.Directory);

            this.handler       = Write;

            log.OnLogged      += handler;

        }

        #endregion


        #region (private) Write(Entry)

        private void Write(LogEntry Entry)
        {

            if (Entry.Level < MinimumLevel)
                return;

            lock (padlock)
            {

                try
                {

                    var day = DateOnly.FromDateTime(Entry.Timestamp.UtcDateTime);

                    if (writer is null || day != openFor)
                    {

                        writer?.Dispose();

                        openFor      = day;
                        CurrentFile  = Path.Combine(Directory, $"ev-{day:yyyy-MM-dd}.log");
                        writer       = new StreamWriter(CurrentFile, append: true);

                    }

                    // The timestamp in full and in UTC, unlike the console's
                    // local time of day: a file outlives the session that wrote
                    // it and is read in another time zone often enough.
                    writer.Write    (Entry.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    writer.Write    (' ');
                    writer.Write    (Entry.LevelName.PadRight(8));

                    if (Entry.Tags.Count > 0)
                    {
                        writer.Write('[');
                        writer.Write(String.Join(" ", Entry.Tags));
                        writer.Write("] ");
                    }

                    writer.WriteLine(Entry.Message);

                    // Every entry, not every buffer: see the remarks above.
                    writer.Flush();

                }
                catch (Exception e)
                {

                    // A log that takes the vehicle down with it when a disk
                    // fills up would be the more expensive failure. Said once
                    // on the console - going through the log would come back
                    // here and fail again.
                    Console.Error.WriteLine($"The log file in '{Directory}' could not be written: {e.Message}");

                    writer?.Dispose();
                    writer = null;

                }

            }

        }

        #endregion

        #region Dispose()

        /// <summary>
        /// Stop writing and close the file.
        /// </summary>
        public void Dispose()
        {

            log.OnLogged -= handler;

            lock (padlock)
            {
                writer?.Dispose();
                writer = null;
            }

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
