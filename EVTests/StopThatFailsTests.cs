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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// A vehicle whose stopping fails is let go of all the same: what is its
    /// own, and then what the node below it holds.
    /// </summary>
    /// <remarks>
    /// It used to stop first and let go afterwards, with nothing between to
    /// catch a stop that threw: the locks of its session and of its
    /// discovery, and everything of the node below - the log file among it -
    /// stayed held.
    /// </remarks>
    public class StopThatFailsTests
    {

        #region Data

        private String                        directory  = "";
        private readonly List<FailingToStop>  made       = [];

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-stop-fails-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task TearDown()
        {

            foreach (var vehicle in made)
            {
                try
                {
                    await vehicle.DisposeAsync();
                }
                catch (InvalidOperationException)
                {
                    // Its stopping fails; that is what it is for.
                }
            }

            made.Clear();

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A temporary directory that outlives one test run is not worth
                // failing the run over.
            }

        }

        #endregion


        #region (helper) Read(File)

        /// <summary>
        /// What a log file says, read beside whoever may still be writing it.
        /// </summary>
        private static String Read(String File)
        {

            using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();

        }

        #endregion


        #region LettingGoOfAVehicleWhoseStopFailsLetsGoOfItAll()

        /// <summary>
        /// Letting go of a vehicle whose stopping fails says that it failed -
        /// and lets go of it all the same: of its own locks, so that neither a
        /// session nor a discovery is begun any more, and of the node below,
        /// so that nothing logged afterwards reaches its log file.
        /// </summary>
        [Test]
        public async Task LettingGoOfAVehicleWhoseStopFailsLetsGoOfItAll()
        {

            var vehicle = await TestPorts.StartedOnFreshPorts(() => {

                var here = Path.Combine(directory, Guid.NewGuid().ToString("N")[..8]);
                var file = Path.Combine(here, WWCPConfigFile.DefaultFileName);

                Directory.CreateDirectory(here);
                File.WriteAllText(file, """{ "nts": { "enabled": false } }""");

                return new FailingToStop(
                           HTTPPort:          IPPort.Parse(TestPorts.Free()),
                           AccountsPath:      Path.Combine(here, "accounts"),
                           ConfigFile:        new WWCPConfigFile(file),
                           CertificatesPath:  Path.Combine(here, "certificates"),
                           LogPath:           Path.Combine(here, "logs")
                       );

            });

            made.Add(vehicle);

            vehicle.Log.Notice("Written while the vehicle runs.", "test");

            var logFile = vehicle.LogFile;

            Assert.That(logFile, Is.Not.Null, "the vehicle wrote no log file");

            Assert.That(async () => await vehicle.DisposeAsync(),
                        Throws.InstanceOf<InvalidOperationException>().With.Message.EqualTo(FailingToStop.Why));

            vehicle.Log.Notice("Written after the vehicle was let go of.", "test");

            var written = Read(logFile!);

            // Asked with a token cancelled beforehand: a vehicle that still has
            // its locks answers by being cancelled, and goes nowhere near a
            // station or the link to do so.
            using var cancelled = new CancellationTokenSource();

            cancelled.Cancel();

            Assert.Multiple(() => {

                Assert.That(async () => await vehicle.RunSessionAsync(CancellationToken: cancelled.Token),
                            Throws.InstanceOf<ObjectDisposedException>(),
                            "the lock of the vehicle's session was not let go of");

                Assert.That(async () => await vehicle.DiscoverAsync  (CancellationToken: cancelled.Token),
                            Throws.InstanceOf<ObjectDisposedException>(),
                            "the lock of the vehicle's discovery was not let go of");

                Assert.That(written, Does.Contain    ("Written while the vehicle runs."));
                Assert.That(written, Does.Not.Contain("Written after the vehicle was let go of."),
                            "the log file of a vehicle that was let go of was still written");

            });

        }

        #endregion


        #region (private class) FailingToStop

        /// <summary>
        /// A vehicle whose stopping fails: what it ends before the server stops
        /// throws instead.
        /// </summary>
        private sealed class FailingToStop(IPPort          HTTPPort,
                                           String          AccountsPath,
                                           WWCPConfigFile  ConfigFile,
                                           String          CertificatesPath,
                                           String          LogPath)

            : EV(HTTPPort:          HTTPPort,
                 AccountsPath:      AccountsPath,
                 ConfigFile:        ConfigFile,
                 CertificatesPath:  CertificatesPath,
                 LogToConsole:      false,
                 LogPath:           LogPath,
                 BridgeDebugLog:    false)

        {

            /// <summary>
            /// What its stopping says.
            /// </summary>
            public const String Why = "What this vehicle holds open would not end.";

            /// <summary>
            /// The file its log is written to, once something was.
            /// </summary>
            public String? LogFile
                => fileLog?.CurrentFile;

            protected override Task OnStopping()
                => Task.FromException(new InvalidOperationException(Why));

        }

        #endregion

    }

}
