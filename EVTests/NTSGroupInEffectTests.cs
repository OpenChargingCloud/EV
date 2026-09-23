/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the Open Charging Cloud EV <https://github.com/OpenChargingCloud/EV>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What a vehicle makes of an "nts" section: the section put into effect
    /// on top of the group of time servers it already has.
    /// </summary>
    /// <remarks>
    /// Beside the tests of the section on its own, because the rule that
    /// matters here - what a section does not mention is left as it is - can
    /// only be seen against something that is already there.
    ///
    /// The vehicles are constructed and never started. The constructor is what
    /// applies the file, and starting would open a port and make up an account
    /// for nothing.
    /// </remarks>
    public class NTSGroupInEffectTests
    {

        #region Data

        private String directory = "";

        private String ConfigurationPath
            => Path.Combine(directory, EVConfigFile.DefaultFileName);

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-nts-group-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public void TearDown()
        {

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


        #region (helper) Vehicle(Configuration = null)

        /// <summary>
        /// A vehicle as it stands after reading this configuration file, or
        /// after reading none.
        /// </summary>
        private EV Vehicle(String? Configuration = null)
        {

            if (Configuration is not null)
                File.WriteAllText(ConfigurationPath, Configuration);

            return new EV(
                       AccountsPath:      Path.Combine(directory, "accounts"),
                       ConfigFile:        new EVConfigFile(ConfigurationPath),
                       CertificatesPath:  Path.Combine(directory, "certificates"),
                       LogToConsole:      false
                   );

        }

        #endregion


        #region AQuorumOnItsOwnHoldsTheServersInEffectToIt()

        /// <summary>
        /// "minServers" without a list is about the servers the vehicle has.
        /// </summary>
        /// <remarks>
        /// It used to count only beside a list or a hostname: the file was read,
        /// the start reported NTS configuration, and the default four went on
        /// being held to two.
        /// </remarks>
        [Test]
        public async Task AQuorumOnItsOwnHoldsTheServersInEffectToIt()
        {

            await using var vehicle = Vehicle("""{ "nts": { "minServers": 3 } }""");

            Assert.Multiple(() =>
            {
                Assert.That(vehicle.TimeSources.Sources.Count(),  Is.EqualTo(4),  "the servers were not mentioned, so they are the default four");
                Assert.That(vehicle.TimeSources.MinServers,       Is.EqualTo(3),  "the quorum was read and changed nothing");
            });

        }

        #endregion

        #region ADeviationOnItsOwnAppliesToTheServersInEffect()

        [Test]
        public async Task ADeviationOnItsOwnAppliesToTheServersInEffect()
        {

            await using var vehicle = Vehicle("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.Multiple(() =>
            {
                Assert.That(vehicle.TimeSources.Sources.Count(),  Is.EqualTo(4));
                Assert.That(vehicle.TimeSources.MinServers,       Is.EqualTo(2));
                Assert.That(vehicle.TimeSources.MaxDeviation,     Is.EqualTo(TimeSpan.FromSeconds(0.5)),  "the deviation was read and changed nothing");
            });

        }

        #endregion

        #region AQuorumTheServersInEffectCannotReachStopsTheStart()

        /// <summary>
        /// Five of four is refused while the file is read, as five of a list of
        /// four always was.
        /// </summary>
        [Test]
        public void AQuorumTheServersInEffectCannotReachStopsTheStart()
        {

            var problem = Assert.Throws<InvalidOperationException>(() => Vehicle("""{ "nts": { "minServers": 5 } }"""));

            Assert.That(problem?.Message,  Does.Contain("minServers"));

        }

        #endregion

        #region AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()

        /// <summary>
        /// And the same from the page: refused, and the file left as it was, so
        /// that the next start does not stop over what was refused.
        /// </summary>
        [Test]
        public async Task AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()
        {

            await using var vehicle = Vehicle();

            Assert.Multiple(() =>
            {

                Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 5 }"""), out var error),  Is.False);
                Assert.That(error,                                                                                   Does.Contain("minServers"));

                Assert.That(!File.Exists(ConfigurationPath) || !File.ReadAllText(ConfigurationPath).Contains("minServers"),
                            Is.True,
                            "a refused quorum was written down all the same");

                Assert.That(vehicle.TimeSources.MinServers,  Is.EqualTo(2));

            });

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 3 }"""), out var unexpected),  Is.True,  unexpected);
            Assert.That(vehicle.TimeSources.MinServers,                                                                  Is.EqualTo(3));

        }

        #endregion

        #region AListAfterALoneHostnameIsHeldToTheQuorumAgain()

        /// <summary>
        /// The quorum a group of one has to settle for is not carried over to
        /// the four that come after it.
        /// </summary>
        /// <remarks>
        /// Read from the file at the next start, the same section holds the four
        /// to two. A running vehicle that held them to one would be a different
        /// vehicle from the one that file describes.
        /// </remarks>
        [Test]
        public async Task AListAfterALoneHostnameIsHeldToTheQuorumAgain()
        {

            await using var vehicle = Vehicle();

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "hostname": "ptbtime1.ptb.de" }"""), out var error),  Is.True,  error);
            Assert.That(vehicle.TimeSources.MinServers,                                                                          Is.EqualTo(1),  "one server cannot be held to two");

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""
                            {
                                "servers": [ "ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                             "ptbtime3.ptb.de", "ptbtime4.ptb.de" ]
                            }
                            """), out error),  Is.True,  error);

            Assert.That(vehicle.TimeSources.MinServers,  Is.EqualTo(2),  "the group of one's quorum was carried over to four");

        }

        #endregion

        #region ADeviationStaysWhenTheServersChange()

        /// <summary>
        /// What a section does not mention is left as it is - the deviation
        /// too, when the servers are replaced.
        /// </summary>
        [Test]
        public async Task ADeviationStaysWhenTheServersChange()
        {

            await using var vehicle = Vehicle("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var error),  Is.True,  error);

            Assert.That(vehicle.TimeSources.MaxDeviation,  Is.EqualTo(TimeSpan.FromSeconds(0.5)));

        }

        #endregion

    }

}
