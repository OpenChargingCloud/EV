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

using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.EV.CommandLine;
using cloud.charging.open.EV.ISO15118;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// The vehicle's command line, which is the vehicle's and not EVCLI's:
    /// what it says at the console and over SSH, and discover, the one command
    /// only a vehicle has - its arguments, who may run it, what it writes in
    /// the log, its one line of an answer and what Tab offers for it.
    /// </summary>
    /// <remarks>
    /// Nothing here goes out on the wire. A discovery is asked for on an
    /// interface this machine does not have, which the vehicle answers before
    /// it sends anything; that a discovery on a real interface goes as it
    /// should is the ISO 15118 page's to show, and the SDP tests'.
    /// </remarks>
    public class VehicleCLITests
    {

        #region Data

        private const String  NoTimeServers      = """{ "nts": { "groups": { "legal": { "enabled": false } } } }""";

        /// <summary>
        /// No machine has an interface called this, so a discovery on it ends
        /// before anything is sent.
        /// </summary>
        private const String  NoSuchInterface    = "no-such-interface-0";

        private static readonly SshPty Terminal  = new ("xterm", new SshWindowSize(120, 40), new Dictionary<Byte, UInt32>());

        private String          directory  = "";
        private EV?             vehicle;
        // A test's own, made new with each vehicle: a vehicle of the test
        // before may still log while it goes.
        private List<LogEntry>  entries    = [];

        #endregion

        #region (class) Screen

        /// <summary>
        /// What the shell writes, as it arrives.
        /// </summary>
        private sealed class Screen
        {

            private readonly StringBuilder  received = new ();
            private readonly Lock           padlock  = new ();

            public Screen(SshClientShell Shell)
                => _ = Task.Run(async () => {
                       var buffer = new Byte[4096];
                       try
                       {
                           Int32 read;
                           while ((read = await Shell.Output.ReadAsync(buffer)) > 0)
                               lock (padlock) { received.Append(Encoding.UTF8.GetString(buffer, 0, read)); }
                       }
                       catch { }
                   });

            public String Text
            {
                get { lock (padlock) { return received.ToString(); } }
            }

            /// <summary>
            /// Wait until the given text has been written after what was there
            /// when Since was taken.
            /// </summary>
            public async Task<String> WaitFor(String What, Int32 Since = 0, Int32 Seconds = 10)
            {

                var until = DateTime.UtcNow.AddSeconds(Seconds);

                while (DateTime.UtcNow < until)
                {
                    var text = Text;
                    if (text.IndexOf(What, Math.Min(Since, text.Length), StringComparison.Ordinal) >= 0)
                        return text[Math.Min(Since, text.Length)..];
                    await Task.Delay(20);
                }

                Assert.Fail($"Waited in vain for '{What}'. The session said:\n{Text}");
                return "";

            }

        }

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {

            directory = Path.Combine(Path.GetTempPath(), "ev-cli-" + Guid.NewGuid().ToString("N")[..12]);

            Directory.CreateDirectory(directory);

            File.WriteAllText(Path.Combine(directory, WWCPConfigFile.DefaultFileName), NoTimeServers);

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


        #region (helper) Vehicle(SSH = false) / Logged(Message)

        /// <summary>
        /// A vehicle on a free port of the loopback, with a log of its own -
        /// made, and not yet started - and with an SSH server where asked.
        /// Every call makes it this test's vehicle.
        /// </summary>
        private EV Vehicle(Boolean SSH = false)
        {

            List<LogEntry> entries = this.entries = [];

            var log = new EventLog();
            log.OnLogged += entry => { lock (entries) { entries.Add(entry); } };

            vehicle = new EV(
                          HTTPPort:          IPPort.Parse(TestPorts.Free()),
                          AccountsPath:      Path.Combine(directory, "accounts"),
                          ConfigFile:        new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                          CertificatesPath:  Path.Combine(directory, "certificates"),
                          Log:               log,
                          LogToConsole:      false,
                          BridgeDebugLog:    false,
                          SSH:               SSH
                                                 ? new SSHSettings(Enabled: true, Port: IPPort.Parse(TestPorts.Free()))
                                                 : null
                      );

            return vehicle;

        }

        /// <summary>
        /// What the log said with the given message, taken under its lock.
        /// </summary>
        private LogEntry[] Logged(String Message)
        {
            lock (entries)
                return [.. entries.Where(entry => entry.Message == Message)];
        }

        #endregion

        #region (helper) SignIn(Account, Role) / Open(Client)

        /// <summary>
        /// Sign in over SSH as the given account - made, in the given role,
        /// where it is not root - with a key of its own.
        /// </summary>
        private async Task<SshClient> SignIn(String Account, String? Role = null)
        {

            if (Account != WWCPNode.DefaultAdminUser)
            {

                await vehicle!.ExtAPI.AddUser(new User(User_Id.Parse(Account), I18NString.Create(Languages.en, Account), SimpleEMailAddress.Parse($"{Account}@localhost")),
                                              SkipDefaultNotifications: true, SkipNewUserEMail: true, SkipNewUserNotifications: true);

                vehicle.ExtAPI.TryGetUser(User_Id.Parse(Account), out var stored);
                vehicle.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role!), out var group);

                await vehicle.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

            }

            var key   = SshHostKey.GenerateEd25519();
            var added = await vehicle!.AuthorizeSSHKey(Account, SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

            return await SshClient.ConnectAsync("127.0.0.1",
                                                vehicle.SSHPort!.Value.ToUInt16(),
                                                new SshClientOptions {
                                                    Username       = Account,
                                                    VerifyHostKey  = blob => vehicle.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                    Credentials    = [ key ]
                                                });

        }

        private static async Task<(SshClientShell Shell, Screen Screen)> Open(SshClient Client)
        {
            var shell  = await Client.OpenShellAsync(Terminal);
            var screen = new Screen(shell);
            return (shell, screen);
        }

        #endregion


        #region AVehicleHandsItsOwnCommandLineToSSH

        /// <summary>
        /// Whoever signs in over SSH gets the vehicle's command line, with
        /// discover beside the node's commands, without the program that runs
        /// the vehicle having to say so - as here, where no EVCLI runs it.
        /// </summary>
        [Test]
        public async Task AVehicleHandsItsOwnCommandLineToSSH()
        {

            var ev = Vehicle();

            Assert.That(ev.CommandLines, Is.Not.Null, "a session over SSH would get the node's command line only");

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = ev.CommandLines!(terminal, CLICaller.Console);

            Assert.That(cli,                                                 Is.InstanceOf<VehicleCLI>());
            Assert.That(((VehicleCLI) cli).Vehicle,                          Is.SameAs(ev));
            Assert.That((await cli.Execute("discover a b")).Single(),        Does.StartWith("Usage: discover [<interface>]"));
            Assert.That(String.Join(" | ", await cli.Execute("syncNTS x")),  Does.Not.StartWith("Unknown command"));

        }

        #endregion

        #region DiscoverTakesOneInterfaceAtMost

        /// <summary>
        /// discover takes an interface or nothing, and with two says how it is
        /// used - without looking for anything or writing to the log.
        /// </summary>
        [Test]
        public async Task DiscoverTakesOneInterfaceAtMost()
        {

            using var cli    = new VehicleCLI(Vehicle());
            var       lines  = await new DiscoverCommand(cli).Execute([ "discover", "eth0", "eth1" ], CancellationToken.None);

            Assert.That(lines.Single(), Is.EqualTo("Usage: discover [<interface>] - look for a charging station; without an interface, the configured one"));

            lock (entries)
                Assert.That(entries.Select(entry => entry.Message), Has.None.Contains("asked this vehicle to look for a station"));

        }

        #endregion

        #region DiscoverOnAnInterfaceThisMachineHasNotSaysSo

        /// <summary>
        /// At the console, discover on an interface the machine does not have
        /// answers in one line why nothing was looked for - and the log says
        /// first who asked, as the ISO 15118 page's button does, with "cli"
        /// where the page says "web".
        /// </summary>
        [Test]
        public async Task DiscoverOnAnInterfaceThisMachineHasNotSaysSo()
        {

            using var cli    = new VehicleCLI(Vehicle());
            var       lines  = await new DiscoverCommand(cli).Execute([ "discover", NoSuchInterface ], CancellationToken.None);

            Assert.That(lines.Single(), Does.StartWith($"'{NoSuchInterface}' is not an interface of this machine that could carry V2G traffic."));

            var asked = Logged($"Somebody at the command line asked this vehicle to look for a station on '{NoSuchInterface}'.");

            Assert.That(asked,           Has.Length.EqualTo(1));
            Assert.That(asked[0].Level,  Is.EqualTo(LogLevel.Info));
            Assert.That(asked[0].Tags,   Is.EqualTo(new[] { "15118", "sdp", "test", "cli" }));

        }

        #endregion

        #region OverSSHTheVehicleSaysItsNameAndADriverMayDiscover

        /// <summary>
        /// Over SSH the prompt is the vehicle's name, Tab completes discover,
        /// and a driver may run it: the log names the account, and says "ssh"
        /// beside "cli".
        /// </summary>
        [Test]
        public async Task OverSSHTheVehicleSaysItsNameAndADriverMayDiscover()
        {

            var ev = await TestPorts.StartedOnFreshPorts(() => Vehicle(SSH: true));

            await using var client  = await SignIn("driver1", VehicleAccess.Driver.Name);
            var (shell, screen)     = await Open(client);

            await screen.WaitFor($"{ev.VehicleName}> ");

            var mark = screen.Text.Length;

            await shell.WriteAsync("disc\t");
            await screen.WaitFor("discover", mark);

            await shell.WriteAsync($" {NoSuchInterface}\r");

            var answer = await screen.WaitFor("could carry V2G traffic", mark);

            Assert.That(answer, Does.Contain($"'{NoSuchInterface}' is not an interface of this machine"));

            var asked = Logged($"'driver1' at the command line over SSH asked this vehicle to look for a station on '{NoSuchInterface}'.");

            Assert.That(asked,          Has.Length.EqualTo(1));
            Assert.That(asked[0].Tags,  Is.EqualTo(new[] { "15118", "sdp", "test", "cli", "ssh" }));

        }

        #endregion

        #region OverSSHAViewerMayNotDiscover

        /// <summary>
        /// A viewer may look and not run: discover is refused, with the roles
        /// that would do, before anything is looked for - and the refusal is
        /// in the log.
        /// </summary>
        [Test]
        public async Task OverSSHAViewerMayNotDiscover()
        {

            var ev = await TestPorts.StartedOnFreshPorts(() => Vehicle(SSH: true));

            await using var client  = await SignIn("aviewer", Role.Viewer.Name);
            var (shell, screen)     = await Open(client);

            await screen.WaitFor($"{ev.VehicleName}> ");

            var mark = screen.Text.Length;
            await shell.WriteAsync($"discover {NoSuchInterface}\r");

            var answer = await screen.WaitFor("may not do that here", mark);

            Assert.That(answer, Does.Contain($"needs the ").And.Contain(VehicleAccess.Driver.Name));

            lock (entries)
            {
                Assert.That(entries.Select(entry => entry.Message), Has.Some.StartsWith("'aviewer' was refused v2g:run on 'discover' at the command line over SSH"));
                Assert.That(entries.Select(entry => entry.Message), Has.None.Contains("asked this vehicle to look for a station"));
            }

        }

        #endregion


        #region OutcomeSaysInOneLineHowADiscoveryWent(Discovery, Expected)

        /// <summary>
        /// The one line discover answers, and EVCLI says after --sdp, for each
        /// way a discovery can end.
        /// </summary>
        [TestCase("""{ "outcome": "found", "attempts": 2, "elapsed_ms": 412.6, "secc": { "address": "fe80::1%3", "port": 15118, "security": "tls" } }""",
                  "a station at [fe80::1%3]:15118 (TLS), after 2 request(s) in 413 ms")]
        [TestCase("""{ "outcome": "found", "attempts": 1, "elapsed_ms": 9,     "secc": { "address": "fe80::2",   "port": 61851, "security": "none" } }""",
                  "a station at [fe80::2]:61851 (no TLS), after 1 request(s) in 9 ms")]
        [TestCase("""{ "outcome": "rejected" }""",                                     "something answered, and none of the answers was usable - see the log")]
        [TestCase("""{ "outcome": "timeout", "attempts": 50 }""",                      "nothing answered after 50 request(s)")]
        [TestCase("""{ "outcome": "noInterface", "error": "'x' is not one." }""",      "'x' is not one.")]
        [TestCase("""{ "outcome": "noInterface" }""",                                  "there was nothing to broadcast on")]
        [TestCase("""{ "outcome": "cancelled" }""",                                    "cancelled")]
        [TestCase("""{ "outcome": "busy", "error": "A discovery is already running." }""", "A discovery is already running.")]
        [TestCase("""{ "outcome": "failed" }""",                                       "the discovery failed - see the log")]
        public void OutcomeSaysInOneLineHowADiscoveryWent(String Discovery, String Expected)
        {
            Assert.That(DiscoverCommand.Outcome(JObject.Parse(Discovery)), Is.EqualTo(Expected));
        }

        #endregion

        #region TabCompletesDiscoverAndOffersTheInterfaces

        /// <summary>
        /// Tab completes the command, and once it is whole offers the
        /// interfaces that could carry V2G traffic - quoted where a name has a
        /// space in it. Which interfaces those are is this machine's to say, so
        /// what is offered is held against what the vehicle finds itself.
        /// </summary>
        [Test]
        public void TabCompletesDiscoverAndOffersTheInterfaces()
        {

            using var cli         = new VehicleCLI(Vehicle());
            var       discover    = new DiscoverCommand(cli);
            var       candidates  = V2GLink.Candidates().Select(candidate => candidate.Name).ToArray();

            static String Quoted(String Name)
                => Name.Contains(' ') ? $"\"{Name}\"" : Name;

            Assert.Multiple(() => {

                Assert.That(discover.Suggest([ "disc" ]),                   Is.EqualTo(new[] { SuggestionResponse.CommandCompleted("discover") }));
                Assert.That(discover.Suggest([ "sync" ]),                   Is.Empty);
                Assert.That(discover.Suggest([ "discover", "zz-none" ]),    Is.Empty);

                Assert.That(discover.Suggest([ "discover" ]),
                            Is.EqualTo(candidates.Length > 0
                                           ? candidates.Select(name => SuggestionResponse.ParameterPrefix($"discover {Quoted(name)}")).ToArray()
                                           : new[] { SuggestionResponse.CommandCompleted("discover") }));

                foreach (var name in candidates)
                    Assert.That(discover.Suggest([ "discover", name ]),
                                Has.Member(SuggestionResponse.ParameterCompleted($"discover {Quoted(name)}")),
                                name);

            });

        }

        #endregion

    }

}
