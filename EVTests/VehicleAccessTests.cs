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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// Who may do what on a vehicle: its three resources beside the node's,
    /// its driver and its service beside the node's viewer and administrators
    /// - and a role from the configuration file, heard by the API like every
    /// other.
    /// </summary>
    public class VehicleAccessTests
    {

        #region Data

        private const String  NoTimeServers  = """{ "nts": { "enabled": false } }""";

        private String  directory  = "";
        private EV?     vehicle;
        private Uri?    address;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-access-" + Guid.NewGuid().ToString("N")[..12]);

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


        #region (helper) Vehicle(Configuration)

        /// <summary>
        /// A vehicle with the given configuration file, on a free port of the
        /// loopback - made, and not yet started.
        /// </summary>
        private EV Vehicle(String Configuration = NoTimeServers)
        {

            var port   = TestPorts.Free();

            var file   = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, Configuration);

            vehicle    = new EV(
                             HTTPPort:          IPPort.Parse(port),
                             AccountsPath:      Path.Combine(directory, "accounts"),
                             ConfigFile:        new WWCPConfigFile(file),
                             CertificatesPath:  Path.Combine(directory, "certificates"),
                             LogToConsole:      false,
                             BridgeDebugLog:    false
                         );

            address    = new Uri($"http://127.0.0.1:{port}/");

            return vehicle;

        }

        #endregion

        #region (helper) SignedInAs(Name, Role)

        /// <summary>
        /// A client signed in with a password as an account of the given name,
        /// made for the purpose and put in the group of the given role - made
        /// the way the vehicle makes its first one, so that it may sign in.
        /// </summary>
        private async Task<HttpClient> SignedInAs(String  Name,
                                                  String  Role)
        {

            var password = "correct-horse-battery-" + Guid.NewGuid().ToString("N")[..8];

            Assert.That(vehicle!.ExtAPI.TryGetOrganization(Organization_Id.Parse("Vehicle"), out var organization) &&
                        organization is Organization, Is.True, "the vehicle's organization is not there");

            var account = await vehicle.ExtAPI.CreateUser(
                                    User_Id.Parse(Name),
                                    I18NString.Create(Languages.en, Name),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    Password:                  password,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                                  Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(vehicle.ExtAPI.TryGetUser(User_Id.Parse(Name), out var stored),           Is.True);
            Assert.That(vehicle.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group),  Is.True, $"the vehicle has no group '{Role}'");

            var joined = await vehicle.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

            Assert.That(joined.IsSuccess, Is.True, $"'{Name}' could not be put in '{Role}'");

            var client = new HttpClient {
                             BaseAddress  = address,
                             Timeout      = TimeSpan.FromSeconds(30)
                         };

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                                                             "Basic",
                                                             Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Name}:{password}"))
                                                         );

            return client;

        }

        #endregion

        #region (helper) Put(Client, Path, JSON)

        private static Task<HttpResponseMessage> Put(HttpClient  Client,
                                                     String      Path,
                                                     String      JSON)

            => Client.PutAsync(Path, new StringContent(JSON, Encoding.UTF8, "application/json"));

        #endregion

        #region (helper) VehicleAccessControl()

        /// <summary>
        /// The vehicle's resources and roles, as a node told nothing else puts
        /// them together.
        /// </summary>
        private static AccessControl VehicleAccessControl()
        {

            Assert.That(AccessControl.TryCombine(VehicleAccess.Resources, VehicleAccess.Roles, null, null,
                                                 "electric vehicle", out var access, out _, out var error),
                        Is.True, error);

            return access!;

        }

        #endregion


        #region AVehicleKnowsItsResourcesAndItsFourRoles()

        /// <summary>
        /// The node brings the viewer and the administrators, and the vehicle
        /// its driver and its service - which the node used to bring for every
        /// kind of node, and brings for none now.
        /// </summary>
        [Test]
        public void AVehicleKnowsItsResourcesAndItsFourRoles()
        {

            var ev = Vehicle();

            Assert.Multiple(() => {
                Assert.That(ev.Roles,             Is.EqualTo(new[] { "viewer", "driver", "service", WWCPNode.AdminRole }));
                Assert.That(ev.Access.Resources,  Is.EqualTo(new[] { "configuration", "dns", "nts", "certificates", "vehicle", "v2g", "session" }));
            });

        }

        #endregion

        #region EachRoleMayDoWhatItAlwaysMayDo(Role, Permission, Allowed)

        /// <summary>
        /// What each role could do before roles were data, permission by
        /// permission: the driver charges and asks, the service also repoints,
        /// the viewer looks, and only the administrators touch the
        /// certificates.
        /// </summary>
        [TestCase("viewer",       "dns:read",           true)]
        [TestCase("viewer",       "session:read",       true)]
        [TestCase("viewer",       "dns:edit",           false)]
        [TestCase("viewer",       "dns:run",            false)]
        [TestCase("viewer",       "session:run",        false)]

        [TestCase("driver",       "certificates:read",  true)]
        [TestCase("driver",       "dns:run",            true)]
        [TestCase("driver",       "nts:run",            true)]
        [TestCase("driver",       "v2g:run",            true)]
        [TestCase("driver",       "vehicle:edit",       true)]
        [TestCase("driver",       "session:edit",       true)]
        [TestCase("driver",       "session:run",        true)]
        [TestCase("driver",       "dns:edit",           false)]
        [TestCase("driver",       "nts:edit",           false)]
        [TestCase("driver",       "v2g:edit",           false)]
        [TestCase("driver",       "certificates:edit",  false)]

        [TestCase("service",      "dns:edit",           true)]
        [TestCase("service",      "nts:edit",           true)]
        [TestCase("service",      "v2g:edit",           true)]
        [TestCase("service",      "session:run",        true)]
        [TestCase("service",      "certificates:edit",  false)]

        [TestCase("systemadmin",  "certificates:edit",  true)]
        [TestCase("systemadmin",  "session:run",        true)]
        public void EachRoleMayDoWhatItAlwaysMayDo(String Role, String Permission, Boolean Allowed)
        {

            Assert.That(protocols.WWCP.Node.Web.Permission.TryParse(Permission, out var permission, out var error), Is.True, error);

            Assert.That(VehicleAccessControl().RoleNamed(Role)!.Allows(permission.Resource, permission.Operation), Is.EqualTo(Allowed));

        }

        #endregion


        #region ADriverMayLookAtTheDNSSettingsAndIsToldWhoMayChangeThem()

        /// <summary>
        /// Over the wire, as a browser signed in as a driver sees it: the page
        /// opens, the save is refused with the roles to ask for, and what the
        /// browser is told it may do says the same beforehand.
        /// </summary>
        [Test]
        public async Task ADriverMayLookAtTheDNSSettingsAndIsToldWhoMayChangeThem()
        {

            await Vehicle().Start();

            using var driver  = await SignedInAs("driver1", "driver");

            var looked        = await driver.GetAsync("api/v1/configuration/dns");
            var changed       = await Put(driver, "api/v1/configuration/dns", "{}");
            var refusal       = await changed.Content.ReadAsStringAsync();
            var me            = JObject.Parse(await (await driver.GetAsync("api/v1/auth/me")).Content.ReadAsStringAsync());
            var permissions   = me["permissions"]!.Values<String>().OfType<String>().ToArray();

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,               Is.EqualTo(HttpStatusCode.OK));
                Assert.That(changed.StatusCode,              Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                         Does.Contain("This needs the service or systemadmin role."));
                Assert.That(me["roles"]!.Values<String>(),   Is.EqualTo(new[] { "driver" }));
                Assert.That(permissions,                     Does.Contain("session:run").And.Contain("vehicle:edit").And.Contain("dns:read"));
                Assert.That(permissions,                     Does.Not.Contain("dns:edit").And.Not.Contain("certificates:edit"));
                Assert.That(permissions.Any(permission => permission.StartsWith('*')),
                            Is.False,
                            "spelt out resource by resource, so that a page asking \"dns:read\" need not know what \"*\" is");
            });

        }

        #endregion

        #region ARoleFromTheConfigurationFileIsHeardByTheAPI()

        /// <summary>
        /// A role nobody compiled in: the file names it, the start makes its
        /// group, and a route asking for a permission lets it in or not by what
        /// the file says it carries.
        /// </summary>
        [Test]
        public async Task ARoleFromTheConfigurationFileIsHeardByTheAPI()
        {

            await Vehicle("""
                          {
                            "nts":   { "enabled": false },
                            "roles": { "support": [ "dns:read" ] }
                          }
                          """).Start();

            using var support  = await SignedInAs("supporter", "support");

            var dns            = await support.GetAsync("api/v1/configuration/dns");
            var nts            = await support.GetAsync("api/v1/configuration/nts");
            var refusal        = await nts.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(vehicle!.Roles,    Is.EqualTo(new[] { "viewer", "driver", "service", "support", WWCPNode.AdminRole }));
                Assert.That(dns.StatusCode,    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(nts.StatusCode,    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,           Does.Contain("This needs the viewer or driver or service or systemadmin role."),
                            "the file's role carries dns:read and nothing else, so it is not among the ones to ask for");
            });

        }

        #endregion

    }

}
