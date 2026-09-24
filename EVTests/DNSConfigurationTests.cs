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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What the "dns" section may say about the name servers - and what it may
    /// not, said as a sentence about the file rather than as an exception.
    /// </summary>
    /// <remarks>
    /// The README showed a name server as "udp://192.168.1.1:53", which is how
    /// the log names one and not a form this section has ever taken - in this
    /// vehicle, or in the charging station and the energy meter whose sections
    /// it shares. A vehicle given it stopped at its start with an exception out
    /// of IPAddress.TryParse. The README now shows what is read, and what is
    /// not read is refused.
    /// </remarks>
    public class DNSConfigurationTests
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

            directory = Path.Combine(Path.GetTempPath(), "ev-dns-" + Guid.NewGuid().ToString("N")[..12]);

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


        #region TheREADMEsExampleIsOneNameServerOverUDP()

        /// <summary>
        /// The section as the README writes it: an address, which is a name
        /// server asked over UDP on port 53.
        /// </summary>
        [Test]
        public void TheREADMEsExampleIsOneNameServerOverUDP()
        {

            var section = JObject.Parse("""{ "enabled": true, "servers": [ "192.168.1.1" ] }""");

            Assert.That(DNSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var server = read!.Servers!.Single();

            Assert.Multiple(() =>
            {
                Assert.That(read.Enabled,                  Is.True);
                Assert.That(server.IPAddress?.ToString(),  Is.EqualTo("192.168.1.1"));
                Assert.That(server.Port.ToUInt16(),        Is.EqualTo(53));
                Assert.That(server.Transport,              Is.EqualTo(DNSTransport.UDP));
            });

        }

        #endregion

        #region TheLongFormIsWhatThePageWritesBack()

        /// <summary>
        /// The object the README shows for a server with more to say about it
        /// is read, and written back as it was - which is what makes it the
        /// form the DNS page saves the list in.
        /// </summary>
        [Test]
        public void TheLongFormIsWhatThePageWritesBack()
        {

            var entry = JObject.Parse("""{ "address": "192.168.1.1", "port": 53, "transport": "UDP", "queryTimeoutSeconds": 2 }""");

            Assert.That(DNSConfiguration.TryParseServer(entry, out var server, out var error),  Is.True,  error);

            var written = DNSConfiguration.ServerJSON(server!);

            Assert.Multiple(() =>
            {
                Assert.That(written.Value<String>("address"),              Is.EqualTo("192.168.1.1"));
                Assert.That(written.Value<Int32> ("port"),                 Is.EqualTo(53));
                Assert.That(written.Value<String>("transport"),            Is.EqualTo("UDP"));
                Assert.That(written.Value<Double>("queryTimeoutSeconds"),  Is.EqualTo(2));
            });

        }

        #endregion

        #region TheFormTheLogNamesAServerInIsRefusedWithASentence(Entry)

        /// <summary>
        /// How the log names a name server, and an address with its port: not
        /// forms the section takes, so refused - with the entry named, and not
        /// with an exception out of the parser, which is what all three were.
        /// </summary>
        [TestCase("udp://213.133.98.98:53")]
        [TestCase("udp://[2a01:4f8:0:1::add:1010]:53")]
        [TestCase("213.133.98.98:53")]
        public void TheFormTheLogNamesAServerInIsRefusedWithASentence(String Entry)
        {

            var                section  = new JObject(new JProperty("servers", new JArray(Entry)));
            var                parsed   = true;
            DNSConfiguration?  read     = null;
            String?            error    = null;

            Assert.That(() => parsed = DNSConfiguration.TryParse(section, out read, out error),  Throws.Nothing);

            Assert.Multiple(() =>
            {
                Assert.That(parsed,  Is.False);
                Assert.That(read,    Is.Null);
                Assert.That(error,   Does.Contain("'dns.servers'").And.Contain(Entry));
            });

        }

        #endregion

        #region AVehicleWhoseFileSaysSoStopsWithASentence()

        /// <summary>
        /// And at a start: the vehicle stops over the file the way it stops over
        /// any file it cannot read, saying what is wrong and where - rather than
        /// with an ArgumentException, which is how it stopped before.
        /// </summary>
        /// <remarks>
        /// Constructed and never started, like the vehicles of the NTS tests:
        /// the constructor is what reads the file.
        /// </remarks>
        [Test]
        public void AVehicleWhoseFileSaysSoStopsWithASentence()
        {

            File.WriteAllText(ConfigurationPath, """{ "dns": { "servers": [ "udp://213.133.98.98:53" ] } }""");

            var problem = Assert.Throws<InvalidOperationException>(() => new EV(
                                                                             AccountsPath:      Path.Combine(directory, "accounts"),
                                                                             ConfigFile:        new EVConfigFile(ConfigurationPath),
                                                                             CertificatesPath:  Path.Combine(directory, "certificates"),
                                                                             LogToConsole:      false
                                                                         ));

            Assert.That(problem?.Message,  Does.Contain("udp://213.133.98.98:53").And.Contain(ConfigurationPath));

        }

        #endregion

    }

}
