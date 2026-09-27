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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What a TLS root is for, over the wire: said at the upload, changed
    /// afterwards, taken back to every use - and a usage the vehicle does not
    /// know refused where it is typed.
    /// </summary>
    public class CertificateUsagesTests
    {

        #region Data

        private String       directory  = "";
        private EV?          vehicle;
        private HttpClient?  client;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public async Task Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-certificate-usages-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

            var probe  = new TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port   = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, """{ "nts": { "enabled": false } }""");

            vehicle    = new EV(
                             HTTPPort:          IPPort.Parse(port),
                             AccountsPath:      Path.Combine(directory, "accounts"),
                             ConfigFile:        new WWCPConfigFile(file),
                             CertificatesPath:  Path.Combine(directory, "certificates"),
                             LogToConsole:      false,
                             BridgeDebugLog:    false
                         );

            await vehicle.Start();

            client     = new HttpClient {
                             BaseAddress  = new Uri($"http://127.0.0.1:{port}/"),
                             Timeout      = TimeSpan.FromSeconds(30)
                         };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"root:{vehicle.GeneratedPassword}"))
                                                         );

        }

        [TearDown]
        public async Task TearDown()
        {

            client?.Dispose();

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


        #region (helpers) RootPem(Name) / Send(Method, Path, JSON)

        /// <summary>
        /// A self-signed certificate, as the text of a PEM file base64-encoded -
        /// which is what an upload from the browser turns into.
        /// </summary>
        private static String RootPem(String Name)
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request    = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return Convert.ToBase64String(Encoding.ASCII.GetBytes(root.ExportCertificatePem()));

        }

        private async Task<(HttpStatusCode Status, JObject JSON)> Send(HttpMethod  Method,
                                                                      String      Path,
                                                                      JObject?    JSON = null)
        {

            using var request   = new HttpRequestMessage(Method, Path);

            if (JSON is not null)
                request.Content = new StringContent(JSON.ToString(), Encoding.UTF8, "application/json");

            using var response  = await client!.SendAsync(request);
            var text            = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 ? JObject.Parse(text) : new JObject());

        }

        #endregion


        #region ARootIsUploadedForTheUsesItIsFor()

        [Test]
        public async Task ARootIsUploadedForTheUsesItIsFor()
        {

            var (created, entry) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                  new JProperty("kind",     "tlsRoot"),
                                                  new JProperty("content",  RootPem("Our Clocks' Root")),
                                                  new JProperty("usages",   new JArray("nts"))
                                              ));

            var (_, store)       = await Send(HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {

                Assert.That(created,                                                  Is.EqualTo(HttpStatusCode.Created), entry.ToString());
                Assert.That(entry["usages"]!.Values<String>(),                        Is.EqualTo(new[] { "nts" }));

                Assert.That(store["usages"]!.Values<String>(),                        Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer");
                Assert.That(store["kinds"]!["tlsRoot"]!["hasUsages"]!.Value<Boolean>(),  Is.True);
                Assert.That(store["kinds"]!["v2gRoot"]!["hasUsages"]!.Value<Boolean>(),  Is.False);
                Assert.That(store["certificates"]!["tlsRoot"]![0]!["usages"]!.Values<String>(),  Is.EqualTo(new[] { "nts" }));
                Assert.That(store["certificates"]!["v2gRoot"]!.Children().Any(),      Is.False);

                Assert.That(store["trustAnchors"]!.Values<String>(),                  Does.Contain("tlsRoot"));
                Assert.That(store["recognised"]!.Values<String>(),                    Is.EqualTo(new[] { "tlsServer" }),
                            "a server certificate is recognised, neither believed nor presented");
                Assert.That(store["credentials"]!.Values<String>(),                   Does.Not.Contain("tlsServer").And.Contain("tlsIdentity"));

            });

        }

        #endregion

        #region WhatARootIsForIsChangedAndTakenBackToEveryUse()

        [Test]
        public async Task WhatARootIsForIsChangedAndTakenBackToEveryUse()
        {

            var (_, entry)        = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                   new JProperty("kind",     "tlsRoot"),
                                                   new JProperty("content",  RootPem("Our Resolvers' Root")),
                                                   new JProperty("usages",   new JArray("dns"))
                                               ));

            var path              = $"api/v1/certificates/{entry["id"]}";

            var (both,  forBoth)  = await Send(HttpMethod.Patch, path, new JObject(new JProperty("usages", new JArray("nts", "dns"))));
            var (label, relabel)  = await Send(HttpMethod.Patch, path, new JObject(new JProperty("label",  "Our Root")));
            var (every, forAll)   = await Send(HttpMethod.Patch, path, new JObject(new JProperty("usages", JValue.CreateNull())));

            Assert.Multiple(() => {
                Assert.That(both,                                      Is.EqualTo(HttpStatusCode.OK), forBoth.ToString());
                Assert.That(forBoth["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(relabel["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }), "a PATCH without them leaves them alone");
                Assert.That(every,                                     Is.EqualTo(HttpStatusCode.OK), forAll.ToString());
                Assert.That(forAll["usages"]!.Type,                    Is.EqualTo(JTokenType.Null),    "null is every use again");
                Assert.That(vehicle!.Log.Recent(200, Tag: "security").Any(line => line.Message.Contains("is now for every use")),
                            Is.True,
                            "a change of what a root vouches for is a matter of security, and said as one");
            });

        }

        #endregion

        #region WhatIsNotAUsageIsRefusedWhereItIsTyped()

        [Test]
        public async Task WhatIsNotAUsageIsRefusedWhereItIsTyped()
        {

            var (unknown, said)     = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Some Root")),
                                                     new JProperty("usages",   new JArray("ntp"))
                                                 ));

            var (onV2G, v2gSaid)    = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "v2gRoot"),
                                                     new JProperty("content",  RootPem("A V2G Root")),
                                                     new JProperty("usages",   new JArray("nts"))
                                                 ));

            var (notAList, listSaid) = await Send(HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Another Root")),
                                                     new JProperty("usages",   "dns")
                                                 ));

            var (_, store)          = await Send(HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(unknown,                       Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(said.ToString(),               Does.Contain("'ntp' is not a usage this electric vehicle knows").And.Contain("dns, nts"));
                Assert.That(onV2G,                         Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(v2gSaid.ToString(),            Does.Contain("only a TLS root and a server certificate"));
                Assert.That(notAList,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(listSaid.ToString(),           Does.Contain("has to be a list of usages"));
                Assert.That(store["certificates"]!.Values().SelectMany(kind => kind.Children()).Any(),
                            Is.False,
                            "nothing refused was half-imported");
            });

        }

        #endregion

    }

}
