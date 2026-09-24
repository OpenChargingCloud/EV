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

        #region ASaveOfPartOfTheSectionLeavesTheRestInEffect()

        /// <summary>
        /// The switch on the page sends "enabled" and nothing else, and the
        /// rest of what the file said stays in effect.
        /// </summary>
        /// <remarks>
        /// It used to be replaced by what was sent: how often to check and who
        /// stands behind the time went back to their defaults, and came back
        /// only when the next start read the file again.
        /// </remarks>
        [Test]
        public async Task ASaveOfPartOfTheSectionLeavesTheRestInEffect()
        {

            await using var vehicle = Vehicle("""{ "nts": { "checkEverySeconds": 600, "legalTimeAuthority": "PTB" } }""");

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "enabled": true }"""), out var error),  Is.True,  error);

            Assert.Multiple(() =>
            {
                Assert.That(vehicle.TimeCheckEvery,      Is.EqualTo(TimeSpan.FromSeconds(600)),  "the interval went back to its default");
                Assert.That(vehicle.LegalTimeAuthority,  Is.EqualTo("PTB"),                      "the authority was forgotten");
            });

        }

        #endregion

        #region AListShorterThanTheQuorumIsRefusedAndNotWritten()

        /// <summary>
        /// A server deleted or switched off below the quorum the file holds is
        /// refused, and the file is left as it was.
        /// </summary>
        /// <remarks>
        /// Each half was fine on its own - the quorum in the file, the list that
        /// was sent - and merged they made a section the next start refuses. A
        /// save that is accepted and then stops the vehicle is the one thing
        /// worse than a save that is refused.
        /// </remarks>
        [Test]
        public async Task AListShorterThanTheQuorumIsRefusedAndNotWritten()
        {

            await using var vehicle = Vehicle("""
                                          { "nts": { "servers": [ "a.example", "b.example", "c.example" ], "minServers": 3 } }
                                          """);

            var before = File.ReadAllText(ConfigurationPath);

            Assert.Multiple(() =>
            {

                Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var deleted),
                            Is.False,
                            "a server was deleted below the quorum");

                Assert.That(deleted,  Does.Contain("minServers"));

                Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""
                                { "servers": [ "a.example", "b.example", { "hostname": "c.example", "enabled": false } ] }
                                """), out _),
                            Is.False,
                            "a server was switched off below the quorum");

                Assert.That(File.ReadAllText(ConfigurationPath),         Is.EqualTo(before),  "a refused save was written down");
                Assert.That(vehicle.TimeSources.Sources.Count(),          Is.EqualTo(3));

            });

        }

        #endregion

        #region EveryServerIsListedWithItsPorts()

        /// <summary>
        /// The list the page edits and sends back whole has every server in it,
        /// the switched-off ones included, with the ports each is asked on.
        /// </summary>
        /// <remarks>
        /// It used to list the bands, which have only the servers switched on:
        /// a page sending back what it was shown would have deleted every
        /// server that was switched off.
        /// </remarks>
        [Test]
        public async Task EveryServerIsListedWithItsPorts()
        {

            await using var vehicle = Vehicle("""
                                          { "nts": { "servers": [ "a.example",
                                                                  { "hostname": "b.example", "ntsKEPort": 4461, "enabled": false } ] } }
                                          """);

            var listed = vehicle.NTSConfigurationJSON()["timeSources"] as JArray;

            Assert.Multiple(() =>
            {
                Assert.That(listed,                                      Has.Count.EqualTo(2),  "the switched-off server is missing");
                Assert.That(listed?[1]?.Value<String>("hostname"),       Is.EqualTo("b.example."));
                Assert.That(listed?[1]?.Value<Boolean>("enabled"),       Is.False);
                Assert.That(listed?[1]?.Value<Int32>("ntsKEPort"),       Is.EqualTo(4461));
                Assert.That(listed?[0]?.Value<Int32>("ntpPort"),         Is.EqualTo(123));

                // There and empty until a key exchange has shown a chain.
                Assert.That(listed?[0]?["rootCA"]?.Type,                 Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

        #region TheQuorumWantedAndTheQuorumHeldAreBothShown()

        /// <summary>
        /// A lone hostname holds the group to one, and the page shows both that
        /// and the two that is wanted, which the next list is held to.
        /// </summary>
        [Test]
        public async Task TheQuorumWantedAndTheQuorumHeldAreBothShown()
        {

            await using var vehicle = Vehicle("""{ "nts": { "hostname": "a.example" } }""");

            var shown = vehicle.NTSConfigurationJSON();

            Assert.Multiple(() =>
            {
                Assert.That(shown["settings"]?.Value<Int32>("minServers"),  Is.EqualTo(2),  "the quorum wanted");
                Assert.That(shown["group"]?.   Value<Int32>("minServers"),  Is.EqualTo(1),  "the quorum one server can be held to");
            });

        }

        #endregion

        #region AChangeToOneServerIsWrittenDown()

        /// <summary>
        /// A server given another priority or a port of its own is a change to
        /// the group, and the log book hears of it.
        /// </summary>
        /// <remarks>
        /// The log compared the names of the servers switched on, in the order
        /// they are asked. A priority that put a server into a band of its own
        /// at the end, and a port of its own, left those just as they were -
        /// and so the group changed without a line saying so.
        /// </remarks>
        [Test]
        public async Task AChangeToOneServerIsWrittenDown()
        {

            await using var vehicle = Vehicle("""{ "nts": { "servers": [ "a.example", "b.example" ] } }""");

            var before = vehicle.Log.LastId;

            Assert.That(vehicle.TryUpdateNTSConfiguration(JObject.Parse("""
                            { "servers": [ "a.example", { "hostname": "b.example", "priority": 5, "ntsKEPort": 4461 } ] }
                            """), out var error),
                        Is.True,
                        error);

            var said = vehicle.Log.Recent(50, before, null).Select(entry => entry.Message).ToArray();

            Assert.That(said,  Has.Some.Contains("time servers = a.example, b.example (priority 5, NTS-KE port 4461)"),
                        String.Join(" | ", said));

        }

        #endregion

        #region TheOverviewNamesTheGroupAndNotTheTestClient()

        /// <summary>
        /// The Configuration page's time card names the servers the clock is
        /// set by - all of them, the one switched off as well - and not the
        /// host of the single client the detailed test starts from.
        /// </summary>
        /// <remarks>
        /// It led with "NTS: ptbtime1.ptb.de." above the servers switched on:
        /// one server, which was the test's, above the group that was asked.
        /// The NTS answer carried the same client as "server", "cookies" and
        /// "keyExchange", and does not any more either.
        /// </remarks>
        [Test]
        public async Task TheOverviewNamesTheGroupAndNotTheTestClient()
        {

            await using var vehicle = Vehicle("""
                                          { "nts": { "servers": [ "a.example",
                                                                  { "hostname": "b.example", "priority": 5 },
                                                                  { "hostname": "c.example", "enabled": false } ] } }
                                          """);

            var time  = vehicle.ConfigurationJSON()["time"] as JObject;
            var nts   = vehicle.NTSConfigurationJSON();

            Assert.Multiple(() =>
            {

                Assert.That(time?.Value<String>("timeServers"),  Is.EqualTo("a.example, b.example (priority 5), c.example (switched off)"));
                Assert.That(time?.Value<Boolean>("ntsEnabled"),  Is.True);
                Assert.That(time?.ContainsKey("nts"),            Is.False,  "the test client's host is named again");

                // There and empty while nothing has been synchronised, so that
                // the card says "-" rather than leaving the line out.
                Assert.That(time?["lastSync"]?.      Type,       Is.EqualTo(JTokenType.Null));
                Assert.That(time?["lastSyncResult"]?.Type,       Is.EqualTo(JTokenType.Null));

                Assert.That(nts.ContainsKey("server"),           Is.False);
                Assert.That(nts.ContainsKey("cookies"),          Is.False);
                Assert.That(nts.ContainsKey("keyExchange"),      Is.False);

            });

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

        #region TheClockIsCheckedAgainstTheGroupAndNotTheTestClient()

        /// <summary>
        /// Against whom the clock is checked, as its JSON says it: the group,
        /// its servers switched on in the order they are asked, and how many of
        /// them have to answer.
        /// </summary>
        /// <remarks>
        /// It used to say "server", with the host of the single client that is
        /// only there for a server's detailed test - one name, the default, for
        /// a check that asks whatever group the file names.
        /// </remarks>
        [Test]
        public async Task TheClockIsCheckedAgainstTheGroupAndNotTheTestClient()
        {

            await using var vehicle = Vehicle("""
                                          { "nts": { "servers": [ { "hostname": "b.example", "priority": 5 },
                                                                  "a.example",
                                                                  { "hostname": "c.example", "enabled": false } ] } }
                                          """);

            var nts = (JObject) vehicle.ClockJSON()["nts"]!;

            Assert.Multiple(() =>
            {

                Assert.That(nts.Value<String>("group"),        Is.EqualTo("legal"));

                Assert.That(nts["servers"]!.Values<String>(),  Is.EqualTo(new[] { "a.example", "b.example" }),
                            "switched on, in the order their bands are asked, and without the root's dot");

                Assert.That(nts.Value<Int32>("minServers"),    Is.EqualTo(2));

                Assert.That(nts.ContainsKey("server"),         Is.False,  "the test client's host is named again");

            });

        }

        #endregion

        #region AClockThatIsNotCheckedNamesNobody()

        /// <summary>
        /// Switched off, the clock is checked against nobody, and says so -
        /// rather than naming servers that are not asked.
        /// </summary>
        [Test]
        public async Task AClockThatIsNotCheckedNamesNobody()
        {

            await using var vehicle = Vehicle("""{ "nts": { "enabled": false } }""");

            var nts = (JObject) vehicle.ClockJSON()["nts"]!;

            Assert.Multiple(() =>
            {
                Assert.That(nts.Value<Boolean>("enabled"),  Is.False);
                Assert.That(nts["group"]?.     Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["servers"]?.   Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["minServers"]?.Type,        Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

    }

}
