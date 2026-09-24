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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.EV.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// The event stream every browser hangs on, as a proxy in front of the
    /// vehicle sees it.
    /// </summary>
    /// <remarks>
    /// Against a vehicle that is started, because what is tested is what goes
    /// over the wire: a header, and what the stream says while nothing happens.
    /// </remarks>
    public class EventStreamTests
    {

        #region Data

        private String  directory  = "";
        private EV?     vehicle;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-event-stream-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task TearDown()
        {

            if (vehicle is not null)
                await vehicle.DisposeAsync();

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


        #region (helper) StartedVehicle()

        /// <summary>
        /// A vehicle listening on a free port of the loopback, and a client
        /// signed in as the account its first start made up.
        /// </summary>
        private async Task<HttpClient> StartedVehicle()
        {

            var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port  = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            vehicle = new EV(
                          HTTPPort:          IPPort.Parse(port),
                          AccountsPath:      Path.Combine(directory, "accounts"),
                          ConfigFile:        new EVConfigFile(Path.Combine(directory, EVConfigFile.DefaultFileName)),
                          CertificatesPath:  Path.Combine(directory, "certificates"),
                          LogToConsole:      false,
                          BridgeDebugLog:    false
                      );

            await vehicle.Start();

            var client = new HttpClient {
                             BaseAddress  = new Uri($"http://127.0.0.1:{port}/"),
                             Timeout      = TimeSpan.FromSeconds(30)
                         };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"root:{vehicle.GeneratedPassword}"))
                                                         );

            return client;

        }

        #endregion

        #region (helper) ReadUntil(Reader, Wanted, Within)

        /// <summary>
        /// Read the stream line by line until a line satisfies the condition,
        /// and say whether one did in time.
        /// </summary>
        private static async Task<Boolean> ReadUntil(StreamReader        Reader,
                                                     Func<String, Boolean>  Wanted,
                                                     TimeSpan            Within)
        {

            using var timeout = new CancellationTokenSource(Within);

            try
            {
                while (await Reader.ReadLineAsync(timeout.Token) is String line)
                {
                    if (Wanted(line))
                        return true;
                }
            }
            catch (OperationCanceledException)
            { }

            return false;

        }

        #endregion


        #region TheStreamAsksAProxyNotToBufferIt()

        /// <summary>
        /// "X-Accel-Buffering: no" on the event stream.
        /// </summary>
        /// <remarks>
        /// nginx buffers what it passes on unless it is told otherwise, and a
        /// buffered event stream reached the browser as nothing at all - not even
        /// its header - until nginx gave up on it after 60 silent seconds. The
        /// Logs page said "reconnecting ..." all the while, and never asked for
        /// its snapshot, which it does when the stream opens.
        /// </remarks>
        [Test]
        public async Task TheStreamAsksAProxyNotToBufferIt()
        {

            using var client    = await StartedVehicle();
            using var response  = await client.GetAsync("api/v1/events", HttpCompletionOption.ResponseHeadersRead);

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode,                                                         Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType,                             Is.EqualTo("text/event-stream"));
                Assert.That(response.Headers.TryGetValues("X-Accel-Buffering", out var values),          Is.True, "the header is there");
                Assert.That(values,                                                                      Is.EqualTo(new[] { "no" }));
            });

        }

        #endregion

        #region ASilentStreamSaysSoAndThenCarriesOn()

        /// <summary>
        /// A comment whenever the stream has been silent for the heartbeat, and
        /// the next entry after it as if nothing had happened.
        /// </summary>
        /// <remarks>
        /// nginx gives up on an upstream that has sent nothing for 60 seconds, and
        /// a vehicle nobody is using says nothing for longer than that. The
        /// second half is the one that could go wrong: the stream waits for the
        /// next entry across the heartbeat instead of asking for it again, and an
        /// entry that arrived during one must neither be lost nor come twice.
        /// </remarks>
        [Test]
        public async Task ASilentStreamSaysSoAndThenCarriesOn()
        {

            using var client    = await StartedVehicle();

            vehicle!.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            using var response  = await client.GetAsync("api/v1/events", HttpCompletionOption.ResponseHeadersRead);
            using var reader    = new StreamReader(await response.Content.ReadAsStreamAsync());

            var heartbeat       = await ReadUntil(reader, line => line == ": keep-alive", TimeSpan.FromSeconds(10));

            var marker          = "A line for the event stream " + Guid.NewGuid().ToString("N")[..8];
            vehicle.Log.Info(marker, "test");

            var lines           = new List<String>();
            var entry           = await ReadUntil(reader, line => { lines.Add(line); return line.Contains(marker); }, TimeSpan.FromSeconds(10));

            // And the one after it, to be sure the stream is still waiting for
            // entries and not only for the heartbeat.
            var second          = marker + " (second)";
            vehicle.Log.Info(second, "test");

            var secondEntry     = await ReadUntil(reader, line => { lines.Add(line); return line.Contains(second); }, TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(heartbeat,                                          Is.True,   "a comment came while nothing was logged");
                Assert.That(entry,                                              Is.True,   "the entry logged after the heartbeat arrived");
                Assert.That(secondEntry,                                        Is.True,   "and so did the one after it");
                Assert.That(lines.Count(line => line.Contains($"\"{marker}\"")), Is.EqualTo(1), "once");
            });

        }

        #endregion

    }

}
