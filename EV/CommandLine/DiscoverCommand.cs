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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.EV.ISO15118;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.EV.CommandLine
{

    /// <summary>
    /// Look for a charging station: an SDP request to ff02::1 on one interface.
    /// </summary>
    /// <remarks>
    /// The same thing the ISO 15118 page does with 'Look for a station', and the
    /// same thing --sdp does once at a start. All three end in
    /// <see cref="EV.DiscoverAsync"/>, which is also where the one-at-a-time
    /// rule lives - a second discovery while one is running is answered with
    /// what is happening rather than queued behind it.
    /// </remarks>
    /// <param name="CLI">The command line of the vehicle to ask.</param>
    public class DiscoverCommand(VehicleCLI CLI) : ACLICommand<VehicleCLI>(CLI),
                                                   ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as. Taken from the class name, as everywhere
        /// else: "DiscoverCommand" without its last seven characters.
        /// </summary>
        public static readonly String CommandName = nameof(DiscoverCommand)[..^7].ToLowerFirstChar();

        #endregion

        #region Suggest(Arguments)

        /// <summary>
        /// Complete the command, and then complete its one argument from the
        /// interfaces this machine actually has.
        /// </summary>
        /// <remarks>
        /// The argument is where this earns its keep. An interface is called
        /// "enp0s5" on the vehicle's Linux and "vEthernet (Default Switch)" on a
        /// Windows workstation, and nobody types either of those from memory
        /// twice. The vehicle already knows which of them could carry V2G
        /// traffic, so Tab can say it.
        ///
        /// Names with spaces in them are offered quoted, because the command
        /// line is split on whitespace unless quotes say otherwise.
        /// </remarks>
        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            #region The command itself - and, once it is whole, the interfaces

            if (Arguments.Length == 1)
            {

                // The interfaces as soon as the command is whole, and not only
                // once something of one has been typed. The command line keeps
                // no empty word at its end, so "discover " arrives here as
                // "discover" alone, and a Tab that answered with the command it
                // already was never showed the list it is there for.
                if (CommandName.Equals(Arguments[0], StringComparison.CurrentCultureIgnoreCase))
                {

                    var all = V2GLink.Candidates().
                                  Select(candidate => SuggestionResponse.ParameterPrefix($"{CommandName} {Quoted(candidate.Name)}")).
                                  ToArray();

                    return all.Length > 0
                               ? all
                               : [ SuggestionResponse.CommandCompleted(CommandName) ];

                }

                if (CommandName.StartsWith(Arguments[0], StringComparison.CurrentCultureIgnoreCase))
                    return [ SuggestionResponse.CommandCompleted(CommandName) ];

                return [];

            }

            #endregion

            #region ... and the interface to broadcast on

            // A name with a space in it is typed in quotes, and until its closing
            // quote is there the command line splits it at the spaces: what Tab
            // itself fills in for "vEthernet (Default Switch)" and
            // "vEthernet (WSL)" is `discover "vEthernet (`, which arrives as
            // three words. Put back together it is the beginning of one name,
            // without the quote that opened it.
            if (Arguments.Length > 2 && Arguments[1].StartsWith('"'))
                Arguments = [ Arguments[0], String.Join(" ", Arguments[1..]) ];

            if (Arguments.Length == 2 &&
                CommandName.Equals(Arguments[0], StringComparison.CurrentCultureIgnoreCase))
            {

                var typed = Arguments[1].TrimStart('"');
                var list  = new List<SuggestionResponse>();

                foreach (var candidate in V2GLink.Candidates())
                {

                    if (!candidate.Name.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase))
                        continue;

                    var name = Quoted(candidate.Name);

                    list.Add(
                        candidate.Name.Equals(typed, StringComparison.CurrentCultureIgnoreCase)
                            ? SuggestionResponse.ParameterCompleted($"{CommandName} {name}")
                            : SuggestionResponse.ParameterPrefix   ($"{CommandName} {name}")
                    );

                }

                return list;

            }

            #endregion

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 2)
                return [ $"Usage: {Help()}" ];

            // What 'Look for a station' needs on the page.
            if (!cli.MayDo(Permission.Run(VehicleAccess.V2G), CommandName, out var refused))
                return [ refused ];

            var wanted = Arguments.Length == 2 ? Arguments[1] : null;

            // Said before the discovery rather than after it, and said at all
            // because the log is a log book: the SDP entries below record what
            // went out on the wire, and nothing in them says who asked for it.
            //
            // The line the web interface writes when 'Look for a station' is
            // pressed, with the same tags and "cli" where it says "web" - and
            // "ssh" beside it over SSH. There it names the account that pressed
            // the button; over SSH the account signed in, and at the console
            // whoever is there, which the vehicle cannot tell apart.
            cli.Vehicle.Log.Info(
                $"{cli.Caller.Asker} asked this vehicle to look for a station " +
                $"{(wanted is null ? "on its configured interface" : $"on '{wanted}'")}.",
                cli.Caller.Tags("15118", "sdp", "test")
            );

            var discovery = await cli.Vehicle.DiscoverAsync(
                                      wanted,
                                      CancellationToken
                                  );

            // The whole of it is in the log and on the ISO 15118 page either
            // way, so what belongs here is the one line that says how it went.
            return [ Outcome(discovery) ];

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<interface>] - look for a charging station; without an interface, the configured one";

        #endregion

        #region Outcome(Discovery)

        /// <summary>
        /// How a discovery went, in one line: what this command answers, and
        /// what a program says on its console after --sdp at a start. The whole
        /// of it is in the log either way.
        /// </summary>
        /// <param name="Discovery">What <see cref="EV.DiscoverAsync"/> answered.</param>
        public static String Outcome(JObject Discovery)
        {

            var outcome = Discovery.Value<String>("outcome");

            if (outcome == "found" && Discovery["secc"] is JObject secc)
                return $"a station at [{secc.Value<String>("address")}]:{secc.Value<Int32>("port")} " +
                       $"({(secc.Value<String>("security") == "tls" ? "TLS" : "no TLS")}), " +
                       $"after {Discovery.Value<Int32>("attempts")} request(s) in {Discovery.Value<Double>("elapsed_ms"):F0} ms";

            return outcome switch {
                       "rejected"     => "something answered, and none of the answers was usable - see the log",
                       "timeout"      => $"nothing answered after {Discovery.Value<Int32>("attempts")} request(s)",
                       "noInterface"  => Discovery.Value<String>("error") ?? "there was nothing to broadcast on",
                       "cancelled"    => "cancelled",
                       _              => Discovery.Value<String>("error") ?? "the discovery failed - see the log"
                   };

        }

        #endregion


        #region (private static) Quoted(Name)

        /// <summary>
        /// An interface name as it has to be typed: in quotes when it has a
        /// space in it, because the command line is split on whitespace unless
        /// quotes say otherwise.
        /// </summary>
        private static String Quoted(String Name)

            => Name.Contains(' ')
                   ? $"\"{Name}\""
                   : Name;

        #endregion

    }

}
